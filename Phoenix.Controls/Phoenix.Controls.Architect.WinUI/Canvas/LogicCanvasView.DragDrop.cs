using System;
using System.IO;
using System.Linq;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// Drag-drop partial — accepts drops from the LeftRail's RailSection items and
// from Explorer (.phxg only). Drag source for the rail is RailSection
// (CanDrag on each ItemsRepeater item, see RailSection.OnRailItemDragStarting),
// which writes phx-rail-kind / phx-rail-id / phx-rail-name into the
// DataPackage. We translate those to a concrete spawn:
//
//   kind=macro    → Macro.Call    with Attributes["MacroId"]   = id
//   kind=process  → Process.Spawn with Attributes["ProcessId"] = id
//   kind=variable → Var.Get       with Attributes["VarName"]   = name
//
// Polish:
//   * Drop coordinate is centred on the cursor (was top-left, so a wide
//     Macro.Call landed far to the right of where the user aimed).
//   * Multi-file drop is first-wins on .phxg with a System log line for
//     each discarded file so the user can tell why the rest were ignored.
//   * DragOver re-asserts the .phxg extension check via GetStorageItemsAsync
//     when WinUI lets us read the items early; non-.phxg drags fall through
//     to AcceptedOperation=None so the gold drop overlay never appears for
//     unsupported file types.
public sealed partial class LogicCanvasView
{
    /// <summary>
    /// Fires when the user drops a .phxg file onto the canvas from Explorer.
    /// MainView subscribes and routes the path through ArchitectViewModel.Open.
    /// </summary>
    public event System.EventHandler<string>? FileOpenRequested;

    /// <summary>
    /// Fires when the user drops a .phxlayer file onto the canvas.
    /// Architect can't open Visualist documents itself, so this surface lets
    /// a host that knows the pillar-launch contract (MainView or future
    /// HubBootstrapper-aware shell) hand the path off to Visualist. If no
    /// host subscribes, the drop is logged via GlobalLogger (System tier)
    /// and the file is otherwise ignored — never accidentally opened in
    /// Architect.
    /// </summary>
    public event System.EventHandler<string>? LayerFileOpenRequested;

    private void HookDragDrop()
    {
        HostRoot.AllowDrop = true;
        HostRoot.DragEnter += OnHostDragEnter;
        HostRoot.DragOver  += OnHostDragOver;
        HostRoot.Drop      += OnHostDrop;
    }

    // Symmetric unhook for HookDragDrop — pre-fix
    // the DragOver/Drop subscriptions on HostRoot were never removed, leaking
    // the handler (and through it, this canvas) once 0.10.0 multi-window
    // restore recycles SubGraphWindow / sibling-window canvases. Called from
    // OnUnloaded (LogicCanvasView.xaml.cs).
    private void UnhookDragDrop()
    {
        try
        {
            HostRoot.DragEnter -= OnHostDragEnter;
            HostRoot.DragOver  -= OnHostDragOver;
            HostRoot.Drop      -= OnHostDrop;
        }
        catch { /* shutdown best-effort */ }
    }

    // ── Gesture-scoped cache of the Explorer-file accept probe ──
    // GetStorageItemsAsync is an async shell round-trip; DragOver fires per
    // pointer-move, so re-probing every event paid a deferral + storage query
    // ~dozens of times per second. The item set can't change mid-gesture, so
    // the FIRST successful probe of a gesture caches the accept/reject verdict
    // (+ caption) and every subsequent DragOver applies it synchronously.
    // Invalidated on DragEnter (a new gesture may carry a different
    // DataPackage), DragLeave, and Drop; a locked-DataView probe failure is
    // deliberately NOT cached so the next DragOver retries, exactly like the
    // pre-cache behaviour.
    private bool   _fileDragDecisionValid;
    private bool   _fileDragAccepted;
    private string _fileDragCaption = string.Empty;

    private void OnHostDragEnter(object sender, DragEventArgs e)
    {
        // Fresh gesture (or re-entry with a possibly different payload) —
        // force the first DragOver to re-run the async probe.
        _fileDragDecisionValid = false;
    }

    private async void OnHostDragOver(object sender, DragEventArgs e)
    {
      try
      {
        if (e.DataView.Properties.ContainsKey("phx-rail-kind"))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            TrySetDragUiOverride(e, $"Spawn {e.DataView.Properties["phx-rail-kind"]}");
            ShowDropOverlay();
            return;
        }

        // Databank table drag.
        if (e.DataView.Properties.ContainsKey("phx-databank-table"))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            TrySetDragUiOverride(e,
                $"Spawn DB.RowCount ({e.DataView.Properties["phx-databank-table"]})");
            return;
        }

        // Databank column drag (forward-compat for the column-header drag
        // source). Payload format is
        // phx-databank-column={tableName}|{columnName}|{columnType}. The
        // actual node-template choice (Get/Set/Increment/FindRow/GetColumn)
        // happens on Drop via a MenuFlyout; DragOver just signals acceptance
        // and previews the table.column pair in the drag cursor caption.
        if (e.DataView.Properties.ContainsKey("phx-databank-column"))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            string payload = e.DataView.Properties["phx-databank-column"] as string ?? string.Empty;
            // Parse only enough to render a nice cursor caption; the Drop
            // path re-parses authoritatively. A malformed payload still
            // shows up cleanly with the raw string as fallback.
            string caption = payload;
            var firstPipe = payload.IndexOf('|');
            if (firstPipe > 0)
            {
                var secondPipe = payload.IndexOf('|', firstPipe + 1);
                int colEnd = secondPipe > 0 ? secondPipe : payload.Length;
                caption = $"{payload[..firstPipe]}.{payload[(firstPipe + 1)..colEnd]}";
            }
            TrySetDragUiOverride(e, $"Spawn DB node ({caption})");
            ShowDropOverlay();
            return;
        }

        // Explorer file drop — accept only when at least one .phxg is in the
        // dropped items. WinUI 3 surfaces the items via GetStorageItemsAsync;
        // we take a Deferral so the async probe finishes before the operation
        // is finalised. If the probe is rejected (locked DataView under
        // animation), fall back to a provisional accept so the user can still
        // drop and let Drop-time validation reject; logs the discrepancy.
        if (!e.DataView.Contains(StandardDataFormats.StorageItems))
            return;

        // Cached verdict from this gesture's first successful probe —
        // apply it synchronously (no deferral, no shell round-trip).
        if (_fileDragDecisionValid)
        {
            if (_fileDragAccepted)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                TrySetDragUiOverride(e, _fileDragCaption);
                ShowDropOverlay();
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
            }
            return;
        }

        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            bool hasPhxg     = items.OfType<StorageFile>().Any(f =>
                f.Path.EndsWith(".phxg",     StringComparison.OrdinalIgnoreCase));
            // Accept .phxlayer too — Visualist documents that drop
            // on Architect get routed via LayerFileOpenRequested instead of
            // being silently swallowed.
            bool hasPhxLayer = items.OfType<StorageFile>().Any(f =>
                f.Path.EndsWith(".phxlayer", StringComparison.OrdinalIgnoreCase));
            // .phx drops are accepted at DragOver
            // so the user gets the gold drop overlay. Drop-time resolves the
            // sibling .phxg and either opens it or surfaces the missing-file
            // banner. Without this DragOver accept, a .phx-only payload
            // would fall through to AcceptedOperation=None and the user
            // couldn't release the drag at all.
            bool hasPhx = items.OfType<StorageFile>().Any(f =>
                !f.Path.EndsWith(".phxg", StringComparison.OrdinalIgnoreCase) &&
                f.Path.EndsWith(".phx",   StringComparison.OrdinalIgnoreCase));
            if (hasPhxg || hasPhxLayer || hasPhx)
            {
                e.AcceptedOperation = DataPackageOperation.Copy;
                string caption = hasPhxg     ? "Open .phxg"
                               : hasPhxLayer ? "Open .phxlayer in Visualist"
                                             : "Open matching .phxg";
                TrySetDragUiOverride(e, caption);
                ShowDropOverlay();
                _fileDragAccepted      = true;
                _fileDragCaption       = caption;
                _fileDragDecisionValid = true;
            }
            else
            {
                e.AcceptedOperation = DataPackageOperation.None;
                _fileDragAccepted      = false;
                _fileDragCaption       = string.Empty;
                _fileDragDecisionValid = true;
            }
        }
        catch
        {
            // DataView temporarily locked — provisionally accept;
            // Drop's explicit extension filter still gates the actual open.
            // Wrap the DragUIOverride writes in their own try/catch so a
            // second failure (DragUIOverride nulled by a fast cursor-leave)
            // can't escape and skip the deferral.Complete() in `finally`.
            e.AcceptedOperation = DataPackageOperation.Copy;
            TrySetDragUiOverride(e, "Open .phxg");
            ShowDropOverlay();
        }
        finally
        {
            deferral.Complete();
        }
      }
      catch (Exception ex)
      {
          // DragOver fires repeatedly during a
          // drag; an unobserved throw from any branch (rail/databank/file
          // probe) would surface as an async-void crash. Log and swallow so
          // the drag stays alive and the app doesn't tear down.
          GlobalLogger.Error("Architect", "OnHostDragOver threw", ex);
      }
    }

    /// <summary>
    /// DragUIOverride is nullable per WinUI 3 and can be nulled by
    /// the framework mid-DragOver during fast cursor-leave / cancel races.
    /// Reading it into a local and short-circuiting on null plus an inner
    /// try/catch around the writes guarantees the outer caller can always
    /// reach deferral.Complete() without a secondary exception escaping.
    /// </summary>
    private static void TrySetDragUiOverride(DragEventArgs e, string caption)
    {
        var over = e.DragUIOverride;
        if (over is null) return;
        try
        {
            over.Caption          = caption;
            over.IsCaptionVisible = true;
            over.IsContentVisible = false;
        }
        catch
        {
            // DragUIOverride got nulled mid-write or surfaced a COM error
            // from the shell drag-source — fall through so the caller's
            // finally clause still completes the deferral.
        }
    }

    // Wired in LogicCanvasView.xaml on HostRoot's DragLeave attribute.
    private void OnHostDragLeave(object sender, DragEventArgs e)
    {
        HideDropOverlay();
        _fileDragDecisionValid = false; // gesture left the canvas — drop the cached probe verdict
    }

    private void ShowDropOverlay()
    {
        try { if (DropOverlay is not null) DropOverlay.Visibility = Visibility.Visible; }
        catch { /* design-time / pre-realised */ }
    }

    private void HideDropOverlay()
    {
        try { if (DropOverlay is not null) DropOverlay.Visibility = Visibility.Collapsed; }
        catch { /* design-time / pre-realised */ }
    }

    private async void OnHostDrop(object sender, DragEventArgs e)
    {
      try
      {
        HideDropOverlay();
        _fileDragDecisionValid = false; // gesture is ending — next drag re-probes
        if (_vm is null) return;

        // Databank table drop — spawns a DB.RowCount node bound to the
        // table name. Checked before the rail
        // branch since both use Properties[].
        if (e.DataView.Properties.TryGetValue("phx-databank-table", out var tableObj)
            && tableObj is string tableName
            && !string.IsNullOrEmpty(tableName))
        {
            // Per-branch guard — this branch runs
            // BEFORE the StorageItems try/catch, so a throw from SpawnDbRowCount
            // / AddNodeCentered (NodeRegistry, socket-sync) would otherwise only
            // hit the outer async-void catch with no branch-specific feedback.
            try
            {
                var canvasPos = HostToCanvas(e.GetPosition(HostRoot));
                var dbNode = SpawnDbRowCount(tableName, canvasPos);
                if (dbNode is not null) AddNodeCentered(dbNode, canvasPos);
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LogicCanvasView",
                    $"dragdrop.databank-table.spawn-failed '{tableName}'", ex);
            }
            return;
        }

        // Databank column drop (forward-compat for the column-header drag source).
        // Payload: phx-databank-column={tableName}|{columnName}|{columnType}.
        // Surfaces a MenuFlyout at the drop point with the five DB node
        // templates that consume a column (Get / Set / Increment / FindRow /
        // GetColumn) and spawns the picked node with Attributes["TableName"]
        // + Attributes["Column"] pre-bound. The pre-bind set is harmless
        // until the column-header drag source is wired.
        if (e.DataView.Properties.TryGetValue("phx-databank-column", out var colPayloadObj)
            && colPayloadObj is string colPayload
            && !string.IsNullOrEmpty(colPayload))
        {
            var parts = colPayload.Split('|');
            string colTable  = parts.Length > 0 ? parts[0] : string.Empty;
            string colName   = parts.Length > 1 ? parts[1] : string.Empty;
            string colType   = parts.Length > 2 ? parts[2] : string.Empty;
            if (string.IsNullOrEmpty(colTable) || string.IsNullOrEmpty(colName))
            {
                GlobalLogger.Log(
                    $"dragdrop.discard.databank-column.malformed:'{colPayload}'",
                    source: "Architect.LogicCanvasView",
                    level: LogLevel.System);
                e.AcceptedOperation = DataPackageOperation.None;
                return;
            }
            // Per-branch guard — a throw from
            // ShowDatabankColumnSpawnFlyout (MenuFlyout build / ShowAt) would
            // otherwise escape to the outer async-void catch with no branch-
            // specific feedback.
            try
            {
                var canvasPos = HostToCanvas(e.GetPosition(HostRoot));
                ShowDatabankColumnSpawnFlyout(colTable, colName, colType, e.GetPosition(HostRoot), canvasPos);
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LogicCanvasView",
                    $"dragdrop.databank-column.spawn-failed '{colTable}.{colName}'", ex);
            }
            return;
        }

        // Rail-spawn drop branch — phx-rail-kind / phx-rail-id / phx-rail-name
        // dictate which Node template to instantiate at the drop point.
        // Normalise kind to invariant lowercase before the switch
        // so a future RailSection refactor (or a pillar that writes
        // "Macro" / "MACRO") still routes correctly instead of silently
        // falling through to the no-op default arm.
        if (e.DataView.Properties.TryGetValue("phx-rail-kind", out var kindObj)
            && kindObj is string kind)
        {
            e.DataView.Properties.TryGetValue("phx-rail-id",   out var idObj);
            e.DataView.Properties.TryGetValue("phx-rail-name", out var nameObj);
            var id   = idObj   as string ?? string.Empty;
            var name = nameObj as string ?? string.Empty;

            // Per-branch guard — the rail spawn runs
            // BEFORE the StorageItems try/catch, so a throw from SpawnMacroCall /
            // SpawnProcessSpawn / SpawnVarSet / SpawnVarGet / AddNodeCentered
            // (NodeRegistry, socket-sync, RefreshMacroCallSockets) would
            // otherwise only reach the outer async-void catch with no branch-
            // specific feedback.
            try
            {
                var canvasPos = HostToCanvas(e.GetPosition(HostRoot));

                // Ctrl held during a variable drag flips Var.Get → Var.Set
                // so users can write the variable as easily as they read it.
                // Pre-T15 the WinForms rail honoured this same chord. WinUI 3's
                // DragEventArgs has no direct modifier accessor that's reliable
                // across drag sources, so we read the live keyboard state through
                // InputKeyboardSource — same pattern the keyboard partial uses.
                bool ctrlHeld = (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                                 & CoreVirtualKeyStates.Down) == CoreVirtualKeyStates.Down;

                Node? node = (kind ?? string.Empty).ToLowerInvariant() switch
                {
                    "macro"    => SpawnMacroCall(id, name, canvasPos),
                    "process"  => SpawnProcessSpawn(id, name, canvasPos),
                    "variable" => ctrlHeld ? SpawnVarSet(id, canvasPos) : SpawnVarGet(id, canvasPos),
                    _          => null,
                };
                if (node is not null) AddNodeCentered(node, canvasPos);
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LogicCanvasView",
                    $"dragdrop.rail-spawn.spawn-failed kind='{kind}' id='{id}'", ex);
            }
            return;
        }

        // Explorer file-drop branch — first-wins on .phxg / .phxlayer.
        // Non-matching files and additional .phxg files after the first are
        // logged via GlobalLogger.Log (System) so the user can see why they
        // were ignored without a modal interrupt.
        //
        // .phxlayer routes through LayerFileOpenRequested so a
        // pillar-launch-aware host (MainView, future Hub shell) can hand it
        // off to Visualist. No subscriber → discrete System log line + TODO
        // marker so the user knows the path was recognised but not actioned.
        // .phx (Hub runtime output) is explicitly rejected with its own log
        // line because the user may have grabbed the wrong file extension.
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            // Winner is a resolved .phxg PATH string
            // rather than a StorageFile, because a .phx drop redirects to a
            // sibling .phxg that isn't in the original drop payload. The
            // first-wins pool spans both direct .phxg drops AND .phx-redirected
            // siblings — same downstream behaviour for the user (one graph
            // opens) and same FileOpenRequested signature.
            string? phxgWinnerPath  = null;
            StorageFile? phxLayerWinner = null;
            int discarded = 0;
            // Track missing-.phxg-for-.phx siblings so we can surface ALL of
            // them in the banner (not just the first) — the user dropped a
            // batch and deserves to see every unresolved name.
            var missingPhxgSiblings = new System.Collections.Generic.List<string>();
            foreach (var item in items)
            {
                if (item is not StorageFile file)
                {
                    GlobalLogger.Log(
                        $"dragdrop.discard.non-file:{item?.Name ?? "<null>"}",
                        source: "Architect.LogicCanvasView",
                        level: LogLevel.System);
                    continue;
                }
                bool isPhxg     = file.Path.EndsWith(".phxg",     StringComparison.OrdinalIgnoreCase);
                bool isPhxLayer = file.Path.EndsWith(".phxlayer", StringComparison.OrdinalIgnoreCase);
                bool isPhx      = !isPhxg && file.Path.EndsWith(".phx", StringComparison.OrdinalIgnoreCase);

                if (isPhx)
                {
                    // .phx redirects to its sibling .phxg
                    // instead of being rejected outright. .phx is the Hub-
                    // runtime exporter output (NEVER hand-edit), but its
                    // same-stemmed .phxg
                    // sitting next to it is the authored graph the user
                    // almost certainly meant. If the sibling exists, fold it
                    // into the first-wins pool. If not, surface a clear
                    // "Missing .phxg file" banner + Warning log so the user
                    // knows what happened without a modal.
                    string siblingPath = Path.ChangeExtension(file.Path, ".phxg");
                    if (File.Exists(siblingPath))
                    {
                        if (phxgWinnerPath is null)
                        {
                            phxgWinnerPath = siblingPath;
                            GlobalLogger.Log(
                                $"dragdrop.phx-redirect:{file.Name} → {Path.GetFileName(siblingPath)}",
                                source: "Architect.LogicCanvasView",
                                level: LogLevel.System);
                        }
                        else
                        {
                            discarded++;
                            GlobalLogger.Log(
                                $"dragdrop.discard.extra-phx-redirect:{file.Name} (resolved {Path.GetFileName(siblingPath)})",
                                source: "Architect.LogicCanvasView",
                                level: LogLevel.System);
                        }
                    }
                    else
                    {
                        missingPhxgSiblings.Add(file.Name);
                        // Log each unresolved .phx individually at Warning
                        // tier so the SystemLog WARN chip picks them up.
                        GlobalLogger.Log(
                            $"dragdrop.missing-phxg:{file.Name} — '{Path.GetFileName(siblingPath)}' not found alongside the .phx",
                            source: "Architect.LogicCanvasView",
                            level: LogLevel.Warning);
                    }
                    continue;
                }
                if (!isPhxg && !isPhxLayer)
                {
                    GlobalLogger.Log(
                        $"dragdrop.discard.wrong-ext:{file.Name}",
                        source: "Architect.LogicCanvasView",
                        level: LogLevel.System);
                    continue;
                }
                if (isPhxg)
                {
                    if (phxgWinnerPath is null)
                    {
                        phxgWinnerPath = file.Path;
                    }
                    else
                    {
                        discarded++;
                        GlobalLogger.Log(
                            $"dragdrop.discard.extra-phxg:{file.Name}",
                            source: "Architect.LogicCanvasView",
                            level: LogLevel.System);
                    }
                }
                else  // isPhxLayer
                {
                    if (phxLayerWinner is null) phxLayerWinner = file;
                    else
                    {
                        discarded++;
                        GlobalLogger.Log(
                            $"dragdrop.discard.extra-phxlayer:{file.Name}",
                            source: "Architect.LogicCanvasView",
                            level: LogLevel.System);
                    }
                }
            }

            // .phxg takes precedence — Architect can open it directly. A
            // mixed .phxg + .phxlayer drop is unusual but plausible (user
            // grabs everything in a folder); honouring .phxg matches the
            // pre-fix "first-wins on .phxg" contract callers expect.
            if (phxgWinnerPath is not null)
            {
                FileOpenRequested?.Invoke(this, phxgWinnerPath);
                if (phxLayerWinner is not null)
                {
                    discarded++;
                    GlobalLogger.Log(
                        $"dragdrop.discard.coexisting-phxlayer:{phxLayerWinner.Name}",
                        source: "Architect.LogicCanvasView",
                        level: LogLevel.System);
                }
                e.AcceptedOperation = DataPackageOperation.Copy;
                // Even when a winner opened, if any
                // sibling .phx had no .phxg the user still needs to see why
                // those entries didn't contribute. Surface the banner so the
                // partial success is visible.
                if (missingPhxgSiblings.Count > 0)
                {
                    ShowMissingPhxgBanner(missingPhxgSiblings);
                }
            }
            else if (phxLayerWinner is not null)
            {
                // No Architect file in the drop — route the layer file to
                // whichever host subscribed. When no subscriber is wired the
                // route is the only feedback (per task spec — TODO follow-up
                // once the pillar-launch surface is plumbed end-to-end).
                var handler = LayerFileOpenRequested;
                if (handler is not null)
                {
                    handler.Invoke(this, phxLayerWinner.Path);
                    GlobalLogger.Log(
                        $"dragdrop.route.phxlayer:{phxLayerWinner.Name} → Visualist",
                        source: "Architect.LogicCanvasView",
                        level: LogLevel.System);
                }
                else
                {
                    // TODO: once a Hub-shell-aware host is in place, route
                    // .phxlayer drops via the pillar-launch surface so the
                    // user doesn't have to alt-tab to Visualist.
                    GlobalLogger.Log(
                        $"dragdrop.unrouted.phxlayer:{phxLayerWinner.Name} — open in Visualist",
                        source: "Architect.LogicCanvasView",
                        level: LogLevel.System);
                }
                e.AcceptedOperation = DataPackageOperation.Copy;
            }
            else if (missingPhxgSiblings.Count > 0)
            {
                // Drop contained only .phx files with no
                // surviving .phxg siblings (and nothing else openable).
                // Surface the banner so the user understands why nothing
                // opened. AcceptedOperation stays None because no graph was
                // routed.
                ShowMissingPhxgBanner(missingPhxgSiblings);
            }
            else if (discarded == 0)
            {
                // No recognised file in the payload at all — log once so it's
                // discoverable why the drop didn't do anything.
                GlobalLogger.Log(
                    "dragdrop.discard.no-phxg-or-phxlayer",
                    source: "Architect.LogicCanvasView",
                    level: LogLevel.System);
            }
        }
        catch (Exception ex)
        {
            GlobalLogger.Error("LogicCanvasView", "OnHostDrop StorageItems", ex);
        }
        finally
        {
            deferral.Complete();
        }
      }
      catch (Exception ex)
      {
          // Outer guard around the whole async-void
          // drop handler. The rail/databank spawn branches above run BEFORE the
          // StorageItems try/catch, so a throw there (NodeRegistry / socket-sync
          // / flyout) would otherwise escape as an unobserved-task crash. Log and
          // leave the canvas consistent (overlay already hidden at method top).
          GlobalLogger.Error("Architect", "OnHostDrop threw", ex);
      }
    }

    // Missing-.phxg banner. Lives on the LogicCanvasView
    // surface (DropStatusInfoBar in the XAML) so the user gets feedback
    // anchored to the canvas they just dropped on. Auto-dismisses after 5
    // seconds via DispatcherTimer — same pattern used elsewhere in
    // LogicCanvasView (e.g. _crossFileSyncTimer in the .xaml.cs partial).
    // The banner is non-modal (repeatable rejections log rather than pop a
    // modal); the same
    // names are also logged at LogLevel.Warning so they show up in the
    // SystemLog WARN chip.
    private DispatcherTimer? _dropStatusDismissTimer;

    private void ShowMissingPhxgBanner(System.Collections.Generic.IReadOnlyList<string> missingPhxNames)
    {
        if (missingPhxNames is null || missingPhxNames.Count == 0) return;
        // Build the message — singular vs. plural. For a single file we use
        // the exact wording the spec asks for; multi-file collapses the list
        // into the message body so the InfoBar stays compact.
        string title;
        string message;
        if (missingPhxNames.Count == 1)
        {
            string phxName = missingPhxNames[0];
            string phxgName = Path.ChangeExtension(phxName, ".phxg");
            title   = "Missing .phxg file";
            message = $"'{phxgName}' not found alongside the .phx — drop the .phxg instead.";
        }
        else
        {
            title   = $"Missing .phxg file ({missingPhxNames.Count})";
            string joined = string.Join(", ", missingPhxNames.Select(n => Path.ChangeExtension(n, ".phxg")));
            message = $"No matching .phxg next to these .phx files: {joined}.";
        }

        try
        {
            if (DropStatusInfoBar is null) return;
            DropStatusInfoBar.Severity = Microsoft.UI.Xaml.Controls.InfoBarSeverity.Warning;
            DropStatusInfoBar.Title    = title;
            DropStatusInfoBar.Message  = message;
            DropStatusInfoBar.IsOpen   = true;

            // Auto-dismiss after 5 seconds. Reuse the same timer instance
            // across calls so a rapid second drop just resets the window
            // rather than stacking timers.
            if (_dropStatusDismissTimer is null)
            {
                _dropStatusDismissTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(5),
                };
                _dropStatusDismissTimer.Tick += OnDropStatusDismissTick;
            }
            _dropStatusDismissTimer.Stop();
            _dropStatusDismissTimer.Start();
        }
        catch (Exception ex)
        {
            // Banner surface failed (control nulled mid-realisation, etc.) —
            // the per-file Warning log lines remain so the user still has
            // a discoverable record of the failure.
            GlobalLogger.Error("LogicCanvasView", "ShowMissingPhxgBanner", ex);
        }
    }

    private void OnDropStatusDismissTick(object? sender, object e)
    {
        try
        {
            // Stop + fully release the timer after a
            // single fire so it doesn't linger with a live Tick subscription
            // between drops. A subsequent banner re-creates it in
            // ShowMissingPhxgBanner (guarded on null), so nulling here is safe.
            if (_dropStatusDismissTimer is not null)
            {
                _dropStatusDismissTimer.Stop();
                _dropStatusDismissTimer.Tick -= OnDropStatusDismissTick;
                _dropStatusDismissTimer = null;
            }
            if (DropStatusInfoBar is not null) DropStatusInfoBar.IsOpen = false;
        }
        catch
        {
            // Control unrealised — nothing to dismiss.
        }
    }

    // Tear down the auto-dismiss banner timer on
    // unload so a SubGraphWindow / tab-swap doesn't leak the DispatcherTimer +
    // its Tick subscription. Mirrors the _crossFileSyncTimer / _flashSweepTimer
    // teardown in OnUnloaded (LogicCanvasView.xaml.cs). Called from OnUnloaded.
    private void UnhookDropStatusDismissTimer()
    {
        if (_dropStatusDismissTimer is null) return;
        try
        {
            _dropStatusDismissTimer.Stop();
            _dropStatusDismissTimer.Tick -= OnDropStatusDismissTick;
        }
        catch { /* shutdown best-effort */ }
        _dropStatusDismissTimer = null;
    }

    /// <summary>
    /// Add a spawned node centred on the cursor instead of top-left
    /// anchored. NodeRegistry's templates carry their authored
    /// <c>Size</c>; we subtract half of each axis so the body lands under
    /// the cursor.
    /// </summary>
    private void AddNodeCentered(Node node, Point cursorCanvasPos)
    {
        double w = node.Size.Width  > 0 ? node.Size.Width  : 160;
        double h = node.Size.Height > 0 ? node.Size.Height : 60;
        double x = cursorCanvasPos.X - w / 2.0;
        double y = cursorCanvasPos.Y - h / 2.0;
        AddNode(node, x, y);
    }

    private Node? SpawnMacroCall(string macroId, string macroName, Point canvasPos)
    {
        if (_vm is null) return null;

        // Front-load macro-id validation at spawn time (the pre-T15
        // WinForms rail validated the id before node creation). The rail builds
        // its list from _vm.Graph.Macros, so a macro dragged from the LOCAL
        // section resolves here; a macro that lives only in the canonical
        // GlobalMacros library is referenced by id (LeftRail._globalMacroIds)
        // and is not present as a full object on this graph. The canvas has no
        // resolvable GlobalMacros object library to auto-import from in the
        // WinUI architecture, so we keep the node for the global-by-id case
        // (the macro-editor open path materialises the macro on demand) but
        // log the unresolved reference so an orphaned id is discoverable
        // instead of silently producing a non-functional call node.
        var macro = _vm.Graph.Macros.FirstOrDefault(m => m.MacroId == macroId);
        if (macro is null && string.IsNullOrEmpty(macroId))
        {
            // No usable id at all — this can only be an authoring slip; reject.
            GlobalLogger.Log(
                "dragdrop.macro-call.reject: empty MacroId — node not created.",
                source: "Architect.LogicCanvasView",
                level: LogLevel.System);
            return null;
        }

        var node = NodeRegistry.CreateNode("Macro.Call",
            new System.Drawing.Point((int)canvasPos.X, (int)canvasPos.Y));
        if (node is null) return null;
        node.Attributes["MacroId"]   = macroId;
        node.Attributes["MacroName"] = macroName;

        if (macro is not null)
        {
            // Sync the spawned call node's sockets to the macro's
            // actual Entry/Exit signature so a macro with custom inputs/outputs
            // is wireable immediately (pre-fix the node kept only the template's
            // default Flow-in/Flow-out signature). Reuses the ViewModel's
            // existing public RefreshMacroCallSockets (the canonical macro→call
            // socket-sync path) rather than duplicating its logic.
            _vm.RefreshMacroCallSockets(node, macro);
        }
        else
        {
            GlobalLogger.Log(
                $"dragdrop.macro-call.unresolved: MacroId '{macroId}' not in graph " +
                "(global-by-id reference kept; sockets sync when the macro is opened).",
                source: "Architect.LogicCanvasView",
                level: LogLevel.System);
        }
        return node;
    }

    private Node? SpawnProcessSpawn(string processId, string processName, Point canvasPos)
    {
        if (_vm is null) return null;

        // Mirror SpawnMacroCall's validation for Process.Spawn. There
        // is no export-time orphan check for Process.Spawn (CheckMacroCallOrphans
        // covers Macro.Call only), so front-loading the diagnostic here is the
        // only place a dangling process reference surfaces before runtime.
        var proc = _vm.Graph.Processes.FirstOrDefault(p => p.ProcessId == processId);
        if (proc is null && string.IsNullOrEmpty(processId))
        {
            GlobalLogger.Log(
                "dragdrop.process-spawn.reject: empty ProcessId — node not created.",
                source: "Architect.LogicCanvasView",
                level: LogLevel.System);
            return null;
        }

        var node = NodeRegistry.CreateNode("Process.Start",
            new System.Drawing.Point((int)canvasPos.X, (int)canvasPos.Y));
        if (node is null) return null;
        node.Attributes["ProcessId"]   = processId;
        node.Attributes["ProcessName"] = processName;

        if (proc is not null)
        {
            SyncProcessSpawnNodeSockets(node, proc);
        }
        else
        {
            GlobalLogger.Log(
                $"dragdrop.process-spawn.unresolved: ProcessId '{processId}' not in graph " +
                "(reference kept; sockets sync when the process is opened).",
                source: "Architect.LogicCanvasView",
                level: LogLevel.System);
        }
        return node;
    }

    // ── Inline target-picker binding (NodeView middle-attr ▾) ───────────────
    // Bridges the NodeView process / macro picker to the existing socket-sync
    // paths so picking a target from the chevron binds the call node exactly
    // like the rail drag-drop spawn does. PUBLIC so NodeView (the materialized
    // editor surface) can drive the bind back onto the canvas.

    /// <summary>
    /// Bind a Process.Spawn node to <paramref name="process"/> from the inline
    /// process picker. Sets ProcessId, re-syncs ProcessName + the spawn's
    /// var-in/var-out sockets to the process Entry/Exit signature (the SAME path
    /// <see cref="SpawnProcessSpawn"/> uses), rebuilds the node's row VMs, and
    /// marks the graph mutated (one undo entry; canvas repaints + .phx
    /// re-exports). No-op for a null / non-Process.Spawn node. Binding the id —
    /// not just the display name — is what makes the spawn actually resolve at
    /// export time (free-text ProcessName left ProcessId empty → "not found").
    /// </summary>
    public void BindProcessSpawnNode(Node spawnNode, Phoenix.Controls.Shared.Models.Process process)
    {
        if (_vm is null || spawnNode is null || process is null) return;
        if (!string.Equals(spawnNode.Title, "Process.Start", StringComparison.Ordinal)) return;
        PushUndoForInlineEdit();
        spawnNode.Attributes ??= new(); // mirror RefreshMacroCallSockets' defensive guard
        spawnNode.Attributes["ProcessId"] = process.ProcessId;
        SyncProcessSpawnNodeSockets(spawnNode, process); // also sets ProcessName + sockets/links
        _vm.Nodes.FirstOrDefault(n => n.Id == spawnNode.Id)?.RebuildSockets();
        _vm.OnGraphMutated();
    }

    /// <summary>
    /// Macro.Call counterpart of <see cref="BindProcessSpawnNode"/> — binds the
    /// call node to <paramref name="macro"/> from the inline macro picker, reusing
    /// the canonical <see cref="LogicCanvasViewModel.RefreshMacroCallSockets"/>
    /// socket-sync the drag-drop spawn uses.
    /// </summary>
    public void BindMacroCallNode(Node callNode, Macro macro)
    {
        if (_vm is null || callNode is null || macro is null) return;
        if (!string.Equals(callNode.Title, "Macro.Call", StringComparison.Ordinal)) return;
        PushUndoForInlineEdit();
        callNode.Attributes ??= new(); // mirror RefreshMacroCallSockets' defensive guard
        callNode.Attributes["MacroId"] = macro.MacroId;
        // MacroName is set by RefreshMacroCallSockets below (matches BindProcessSpawnNode,
        // which leaves ProcessName to SyncProcessSpawnNodeSockets) — no redundant pre-set.
        _vm.RefreshMacroCallSockets(callNode, macro);
        _vm.Nodes.FirstOrDefault(n => n.Id == callNode.Id)?.RebuildSockets();
        _vm.OnGraphMutated();
    }

    // ── Process.Spawn socket-signature sync ─────────────────────────────────
    // Restores the pre-T15 RefreshProcessSpawnSockets behaviour the
    // WinUI port dropped: a freshly-spawned Process.Spawn should carry the
    // referenced process's actual Entry/Exit socket signature, not just the
    // template default. The macro counterpart already exists as the public
    // LogicCanvasViewModel.RefreshMacroCallSockets, which the macro spawn path
    // reuses; there is NO process equivalent on the ViewModel, so this self-
    // contained method (this file owns it) mirrors that logic for Process.Spawn.
    // Header/row spacing match the ViewModel's macro constants (24 + 22) so the
    // two stay visually consistent until the next intrinsic-size pass.
    private const int ProcessSpawnHeaderH   = 24;
    private const int ProcessSpawnRowSpacing = 22;

    /// <summary>
    /// Sync <paramref name="spawnNode"/>'s data sockets to <paramref name="process"/>'s
    /// Process.Entry outputs (→ spawn inputs) and Process.Exit inputs (→ spawn
    /// outputs). Existing sockets matched by name+direction keep their Id so
    /// live wires survive an idempotent re-sync; names no longer in the
    /// signature are dropped along with their links. Flow and the fixed
    /// Done / InstanceId outputs are always preserved.
    /// </summary>
    private void SyncProcessSpawnNodeSockets(Node spawnNode, Phoenix.Controls.Shared.Models.Process process)
    {
        if (spawnNode is null || process is null || _vm is null) return;
        spawnNode.Attributes["ProcessName"] = process.Name;

        var entryNode = process.Graph.Nodes.FirstOrDefault(n => n.Title == "Process.Entry");
        var exitNode  = process.Graph.Nodes.FirstOrDefault(n => n.Title == "Process.Exit");

        var entrySockets = entryNode?.Sockets
            .Where(s => s.Type == SocketType.Output && !s.IsPlaceholder && s.Name != "Flow")
            .ToList() ?? new System.Collections.Generic.List<Socket>();
        var exitSockets = exitNode?.Sockets
            .Where(s => s.Type == SocketType.Input && !s.IsPlaceholder && s.Name != "Flow")
            .ToList() ?? new System.Collections.Generic.List<Socket>();

        var desiredInputNames  = new System.Collections.Generic.HashSet<string>(entrySockets.Select(s => s.Name));
        var desiredOutputNames = new System.Collections.Generic.HashSet<string>(exitSockets.Select(s => s.Name));
        var fixedOutputNames   = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
            { "Done", "InstanceId" };

        // Step 1 — drop data sockets whose names left the signature, plus links
        // touching them. Flow + the fixed Done/InstanceId outputs survive.
        var dropped = spawnNode.Sockets.Where(s =>
            s.Name != "Flow" &&
            !fixedOutputNames.Contains(s.Name) &&
            !((s.Type == SocketType.Input  && desiredInputNames.Contains(s.Name))
           || (s.Type == SocketType.Output && desiredOutputNames.Contains(s.Name)))).ToList();
        if (dropped.Count > 0)
        {
            foreach (var rs in dropped)
                _vm.Graph.Links.RemoveAll(l => l.FromSocketId == rs.Id || l.ToSocketId == rs.Id);
            spawnNode.Sockets.RemoveAll(s => dropped.Contains(s));
        }

        int width = spawnNode.Size.Width > 0 ? spawnNode.Size.Width : 200;

        // Step 2 — ensure each input socket is present + colour/type refreshed.
        for (int i = 0; i < entrySockets.Count; i++)
        {
            var src = entrySockets[i];
            var existing = spawnNode.Sockets.FirstOrDefault(s => s.Type == SocketType.Input && s.Name == src.Name);
            var off = new System.Drawing.Point(-6, ProcessSpawnHeaderH + 6 + (i + 1) * ProcessSpawnRowSpacing);
            if (existing is not null)
            {
                existing.Color    = src.Color;
                existing.DataType = src.DataType;
                existing.Offset   = off;
            }
            else
            {
                spawnNode.Sockets.Add(new Socket
                {
                    Id       = Guid.NewGuid().ToString(),
                    Name     = src.Name,
                    Type     = SocketType.Input,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = off,
                });
            }
        }

        // Step 3 — same for the variable outputs (right edge). The fixed
        // Done/InstanceId outputs the template already carries are untouched.
        for (int i = 0; i < exitSockets.Count; i++)
        {
            var src = exitSockets[i];
            var existing = spawnNode.Sockets.FirstOrDefault(s => s.Type == SocketType.Output && s.Name == src.Name);
            var off = new System.Drawing.Point(width - 14, ProcessSpawnHeaderH + 6 + (i + 1) * ProcessSpawnRowSpacing);
            if (existing is not null)
            {
                existing.Color    = src.Color;
                existing.DataType = src.DataType;
                existing.Offset   = off;
            }
            else
            {
                spawnNode.Sockets.Add(new Socket
                {
                    Id       = Guid.NewGuid().ToString(),
                    Name     = src.Name,
                    Type     = SocketType.Output,
                    Color    = src.Color,
                    DataType = src.DataType,
                    Offset   = off,
                });
            }
        }

        int totalRows = Math.Max(1 + entrySockets.Count, 1 + exitSockets.Count);
        spawnNode.Size = new System.Drawing.Size(width, ProcessSpawnHeaderH + 14 + totalRows * ProcessSpawnRowSpacing);
        _vm.Graph.MarkStructuralChange();
    }

    private Node? SpawnVarGet(string varName, Point canvasPos)
    {
        var node = NodeRegistry.CreateNode("Var.Get",
            new System.Drawing.Point((int)canvasPos.X, (int)canvasPos.Y));
        if (node is null) return null;
        node.Attributes["VarName"] = varName;
        return node;
    }

    /// <summary>
    /// Var.Set companion to <see cref="SpawnVarGet"/>. Used when
    /// Ctrl is held during a rail variable drag so the user gets a writer
    /// instead of a reader. The NodeRegistry's Var.Set template uses
    /// "VariableName" as the attribute key (see
    /// NodeRegistry.Templates.RemainingBands.cs Var.Get/Set entries); we
    /// write to BOTH "VarName" (rail convention, matches Var.Get for any
    /// downstream tooling that reads either spelling) and "VariableName"
    /// (the template's authoritative key, read by the exporter and the
    /// inline attribute editor) so the spawned node lands fully bound.
    /// </summary>
    private Node? SpawnVarSet(string varName, Point canvasPos)
    {
        var node = NodeRegistry.CreateNode("Var.Set",
            new System.Drawing.Point((int)canvasPos.X, (int)canvasPos.Y));
        if (node is null) return null;
        node.Attributes["VarName"]       = varName;
        node.Attributes["VariableName"]  = varName;
        return node;
    }

    /// <summary>
    /// Databank column drop dispatcher. Pops a small MenuFlyout at
    /// the drop point listing the five DB node templates that consume a
    /// (table, column) pair. The user picks one; we spawn it with
    /// Attributes["TableName"] + Attributes["Column"] pre-bound. The matching
    /// drag source on the Databank side feeds this; the right-click
    /// parity path on a column header is routed through the public shell
    /// entry-point <see cref="ShowDatabankColumnSpawnMenuFromShell"/> so
    /// both gestures share the same menu builder.
    ///
    /// DB.Increment is included for parity with the menu list, but
    /// the runtime template (NodeRegistry.Templates.Databank.cs) doesn't
    /// model a Column socket — Increment's row addressing goes through the
    /// "Key" socket. We still write Attributes["Column"] so the user's
    /// drop intent is preserved in graph JSON; once the
    /// actual increment-by-column semantics ship, the attribute lookup is
    /// already in place. See the DB.Increment TODO below.
    /// </summary>
    private void ShowDatabankColumnSpawnFlyout(
        string tableName,
        string columnName,
        string columnType,
        Point hostPoint,
        Point canvasPos)
    {
        // Drag-drop path: anchor at HostRoot/hostPoint, spawn at the cursor
        // canvas position. The shared builder is used so the menu definition
        // is identical to the right-click parity path opened from Databank.
        BuildAndShowDatabankColumnSpawnMenu(
            tableName, columnName, columnType,
            anchor:     HostRoot,
            anchorPos:  hostPoint,
            spawnPos:   canvasPos);
    }

    /// <summary>
    /// Shell entry-point so the Databank column-header
    /// right-click can open the same Get / Set / Increment / FindRow /
    /// GetColumn menu as the drag-drop path, anchored on the column-header
    /// element (live in the Databank tab) rather than on the canvas host.
    /// Spawn position resolves to the canvas view-centre because the canvas
    /// isn't visible during the right-click; the user sees the freshly-bound
    /// node when they swap back to the Logic tab.
    /// </summary>
    /// <param name="tableName">Table the column belongs to.</param>
    /// <param name="columnName">Column the spawned node will read or write.</param>
    /// <param name="columnType">SQL type (for forward-compat — currently
    /// unused by the menu but logged to match the drag payload schema).</param>
    /// <param name="anchor">UI element to anchor the flyout on (typically
    /// the right-clicked column header).</param>
    /// <param name="anchorPos">Position within <paramref name="anchor"/>
    /// for the flyout's preferred placement.</param>
    public void ShowDatabankColumnSpawnMenuFromShell(
        string tableName,
        string columnName,
        string columnType,
        FrameworkElement anchor,
        Point anchorPos)
    {
        if (anchor is null) return;
        if (string.IsNullOrEmpty(tableName) || string.IsNullOrEmpty(columnName))
        {
            GlobalLogger.Log(
                $"databank-column.shell.spawn-menu.skip:'{tableName}|{columnName}' (empty)",
                source: "Architect.LogicCanvasView",
                level: LogLevel.System);
            return;
        }

        // Resolve a stable spawn point on the canvas. When the canvas is
        // measured we land the node at the visible viewport centre so the
        // user finds it on their next Logic-tab swap; if the canvas hasn't
        // been measured yet (rare — Logic tab never activated this session)
        // fall back to (0,0) graph-space and let the next zoom-to-fit pick
        // it up.
        Point spawnPos;
        if (HostRoot is not null && HostRoot.ActualWidth > 0 && HostRoot.ActualHeight > 0)
        {
            var hostCenter = new Point(HostRoot.ActualWidth / 2.0, HostRoot.ActualHeight / 2.0);
            spawnPos = HostToCanvas(hostCenter);
        }
        else
        {
            spawnPos = new Point(0, 0);
        }

        BuildAndShowDatabankColumnSpawnMenu(
            tableName, columnName, columnType,
            anchor:    anchor,
            anchorPos: anchorPos,
            spawnPos:  spawnPos);
    }

    /// <summary>
    /// Shared menu builder — single source of truth for the
    /// Get / Set / Increment / FindRow / GetColumn label-to-template list.
    /// The drag-drop path and the Databank right-click parity path both
    /// funnel here so a future tweak (rename, reorder) edits exactly one
    /// list.
    /// </summary>
    private void BuildAndShowDatabankColumnSpawnMenu(
        string tableName,
        string columnName,
        string columnType,
        FrameworkElement anchor,
        Point anchorPos,
        Point spawnPos)
    {
        // TODO DB.Increment: NodeRegistry's DB.Increment
        // template has TableName but no Column attribute default; the
        // Attributes["Column"] write here is silently preserved on the
        // node but unread by the current exporter. A follow-up can either
        // retrofit DB.Increment with a Column socket or drop it from this
        // menu — both are acceptable closures.
        var menu = new MenuFlyout();
        AddDatabankColumnSpawnItem(menu, "DB.GetCell",     "Get",               tableName, columnName, spawnPos);
        AddDatabankColumnSpawnItem(menu, "DB.SetCell",     "Set",               tableName, columnName, spawnPos);
        AddDatabankColumnSpawnItem(menu, "DB.Increment",   "Increment",         tableName, columnName, spawnPos);
        AddDatabankColumnSpawnItem(menu, "DB.FindRow",     "Find row",          tableName, columnName, spawnPos);
        AddDatabankColumnSpawnItem(menu, "DB.GetColumn",   "Get column (list)", tableName, columnName, spawnPos);

        // ShowAt's FlyoutShowOptions.Position uses anchor-relative
        // coordinates per WinUI 3 docs — same convention used by
        // ShowSpawnPalette. The anchor is HostRoot for the drag path
        // (releases inside the canvas) and the column-header Border for
        // the right-click parity path (releases inside the Databank tab).
        menu.ShowAt(anchor, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position  = anchorPos,
            ShowMode  = Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowMode.Standard,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.Auto,
        });
    }

    private void AddDatabankColumnSpawnItem(
        MenuFlyout menu,
        string templateTitle,
        string label,
        string tableName,
        string columnName,
        Point canvasPos)
    {
        var item = new MenuFlyoutItem { Text = $"{label} ({templateTitle})" };
        item.Click += (_, _) =>
        {
            try
            {
                var node = NodeRegistry.CreateNode(templateTitle,
                    new System.Drawing.Point((int)canvasPos.X, (int)canvasPos.Y));
                if (node is null)
                {
                    GlobalLogger.Log(
                        $"dragdrop.databank-column.spawn-failed:{templateTitle} not in NodeRegistry",
                        source: "Architect.LogicCanvasView",
                        level: LogLevel.System);
                    return;
                }
                node.Attributes["TableName"] = tableName;
                node.Attributes["Column"]    = columnName;
                AddNodeCentered(node, canvasPos);
            }
            catch (Exception ex)
            {
                GlobalLogger.Error("LogicCanvasView",
                    $"databank-column spawn {templateTitle} failed", ex);
            }
        };
        menu.Items.Add(item);
    }

    /// <summary>
    /// Spawn a DB.RowCount node pre-wired to <paramref name="tableName"/>.
    /// </summary>
    private Node? SpawnDbRowCount(string tableName, Point canvasPos)
    {
        var node = NodeRegistry.CreateNode("DB.RowCount",
            new System.Drawing.Point((int)canvasPos.X, (int)canvasPos.Y));
        if (node is null) return null;
        node.Attributes["TableName"] = tableName;
        return node;
    }
}
