using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Phoenix.Controls.Architect.Core;
using Phoenix.Controls.Architect.WinUI.ViewModels;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Architect.WinUI.Services;

/// <summary>
/// Periodic autosave of the active Architect graph. Writes a sibling
/// <c>.phxg.autosave</c> file alongside the loaded <c>.phxg</c> (or, for
/// unsaved graphs, into <c>%AppData%/PhoenixControls/architect_autosave/</c>).
/// The autosave is removed on a successful manual save so the recovery scan
/// on next boot only surfaces genuine survivors.
///
/// Architect UX review P0-3 — pre-fix a crash, forced Hub close, or any
/// pillar-swap path that bypassed PromptSaveBeforeCloseAsync wiped every
/// edit since the last manual Ctrl+S. Hub backed up the *generated* .phx
/// (LogicWatcher rotation), but the source .phxg — the only thing the user
/// actually authored — had no rotation and no recovery story.
/// </summary>
public sealed class AutosaveService : IDisposable
{
    private const int DefaultIntervalSeconds = 60;
    public const string AutosaveExtension    = ".autosave";
    private const string SessionPrefix       = "session-";

    private readonly ArchitectViewModel _vm;
    private readonly DispatcherQueueTimer _timer;
    private readonly string _sessionId;
    private bool _disposed;
    private int _consecutiveFailures;

    /// <summary>
    ///  Raised after a successful autosave write
    /// (carries the autosave file name). The host surfaces a transient
    /// status-bar confirmation so the user can see autosave is alive.
    /// </summary>
    public event Action<string>? AutosaveSucceeded;

    /// <summary>
    ///  Raised on a failed autosave write, with the
    /// running count of CONSECUTIVE failures and the exception. The host shows a
    /// persistent warning after N consecutive failures so a read-only path /
    /// full disk doesn't leave the user trusting a stale recovery file.
    /// </summary>
    public event Action<int, Exception>? AutosaveFailed;

    public AutosaveService(ArchitectViewModel vm, DispatcherQueue queue, int? intervalSecondsOverride = null)
    {
        _vm        = vm ?? throw new ArgumentNullException(nameof(vm));
        _sessionId = SessionPrefix + DateTime.UtcNow.ToString("yyyyMMddHHmmss");

        _timer          = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(intervalSecondsOverride ?? DefaultIntervalSeconds);
        _timer.IsRepeating = true;
        _timer.Tick    += async (_, _) => await TickAsync();

        // Drop the autosave on a clean manual save — the recovery scan
        // should only flag genuine survivors (newer .autosave than .phxg).
        _vm.PropertyChanged += OnVmPropertyChanged;
    }

    /// <summary>Returns the autosave folder for untitled graphs (creates it on first use).</summary>
    public static string UntitledAutosaveDir
    {
        get
        {
            var dir = Paths.RoamingAppData("architect_autosave");
            try { Directory.CreateDirectory(dir); } catch { /* read-only fallback */ }
            return dir;
        }
    }

    public void Start() => _timer.Start();
    public void Stop()  => _timer.Stop();

    private async Task TickAsync()
    {
        if (_disposed) return;
        if (!_vm.IsDirty) return;
        if (_vm.LogicCanvas?.Graph is null) return;

        try
        {
            string target = ResolveAutosavePath();
            await GraphSerializer.SaveGraphAsync(_vm.LogicCanvas.Graph, target);
            //  reset the failure streak + notify.
            _consecutiveFailures = 0;
            try { AutosaveSucceeded?.Invoke(Path.GetFileName(target)); } catch { /* host hook best-effort */ }
        }
        catch (Exception ex)
        {
            // Autosave must never crash the editor. Log and keep ticking.
            GlobalLogger.Error("Architect.Autosave", "tick write failed", ex);
            //  surface a persistent failure to the
            // host so a stuck autosave (read-only path / full disk) isn't silent.
            _consecutiveFailures++;
            try { AutosaveFailed?.Invoke(_consecutiveFailures, ex); } catch { /* host hook best-effort */ }
        }
    }

    /// <summary>Resolves the autosave path: sibling to the loaded .phxg, or a session-named file in the untitled folder.</summary>
    private string ResolveAutosavePath()
    {
        var loaded = _vm.LoadedFilePath;
        if (!string.IsNullOrEmpty(loaded))
            return loaded + AutosaveExtension;
        return Path.Combine(UntitledAutosaveDir, _sessionId + ".phxg" + AutosaveExtension);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (_disposed) return;
        // When the graph saves cleanly (IsDirty flips false on a real save),
        // remove the current autosave so the next-boot recovery scan stays
        // accurate. Also remove if LoadedFilePath changes (Open / NewGraph
        // re-targets the autosave to the new file).
        if (e.PropertyName == nameof(ArchitectViewModel.IsDirty) && !_vm.IsDirty)
            TryDelete(ResolveAutosavePath());
        else if (e.PropertyName == nameof(ArchitectViewModel.LoadedFilePath))
            TryDelete(ResolveAutosavePath());
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _timer.Stop(); } catch { }
        _vm.PropertyChanged -= OnVmPropertyChanged;
    }
}
