using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;
using Phoenix.Controls.Visualist.Core;
using Windows.UI;

namespace Phoenix.Controls.Visualist.WinUI.Controls;

/// <summary>
/// Sprint A — live preview thumbnail surface for Visualist nodes. The
/// host of a <see cref="WidgetGraphNodeView"/> hands this control a
/// <see cref="NodeEvaluator.PreviewSnapshot"/> produced by
/// <see cref="NodeEvaluator.EvaluatePreviews"/> and the control flips
/// between bitmap / colour swatch / placeholder / error tint without
/// performing any graph work of its own.
///
/// Chrome stays per-pillar (see feedback_visualist_architect_chrome_independence.md):
/// the control lives under Phoenix.Controls.Visualist.WinUI.Controls and
/// MUST NOT be promoted to Shared. Architect deliberately has no
/// analogue — Fusion-style "the node IS the image" is Visualist-only.
///
/// All calls into this control are expected to occur on the UI thread
/// (the dispatcher that owns the canvas); bitmap loading is best-effort
/// and silent on failure (no modal dialogs per
/// feedback_no_modal_dialogs_for_repeatable_rejections.md — degraded
/// previews route through GlobalLogger instead).
/// </summary>
public sealed partial class ThumbnailHost : UserControl
{
    public ThumbnailHost()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Apply a snapshot. Passing <c>null</c> or a snapshot whose
    /// <see cref="NodeEvaluator.PreviewKind"/> is <c>Empty</c> reverts the
    /// host to its neutral placeholder state — the caller is responsible
    /// for collapsing the row entirely if it wants zero-pixel layout
    /// (the host always renders some pixels because it sits inside a
    /// sized cell on the node body).
    /// </summary>
    public void SetSnapshot(NodeEvaluator.PreviewSnapshot? snapshot)
    {
        try
        {
            // Default: collapse all overlays + clear error tint. Each
            // case below opts back in to the visuals it needs.
            PreviewImage.Source     = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewSwatch.Visibility = Visibility.Collapsed;
            PreviewHint.Visibility   = Visibility.Collapsed;
            PreviewHint.Text         = string.Empty;
            ToolTipService.SetToolTip(this, null);
            // Reset placeholder back to neutral coal (Error case overrides
            // this with the muted-red tint below).
            PlaceholderRect.Fill = (Brush)Application.Current.Resources["CoalSurfaceBrush"];

            var kind = snapshot?.Kind ?? NodeEvaluator.PreviewKind.Empty;

            switch (kind)
            {
                case NodeEvaluator.PreviewKind.Image:
                {
                    if (TryLoadBitmap(snapshot?.Source, out var bmp))
                    {
                        PreviewImage.Source = bmp;
                        PreviewImage.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        // Bitmap couldn't be resolved at design time
                        // (relative path, network failure, malformed URI).
                        // Degrade to the Unloaded look with a stable hint
                        // so the strip stays informative.
                        PreviewHint.Text = snapshot?.Hint ?? "(image unavailable)";
                        PreviewHint.Visibility = Visibility.Visible;
                    }
                    break;
                }

                case NodeEvaluator.PreviewKind.Color:
                {
                    if (TryParseHex(snapshot?.ColorHex, out var c))
                    {
                        PreviewSwatch.Fill = new SolidColorBrush(c);
                        PreviewSwatch.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        PreviewHint.Text = snapshot?.Hint ?? "(invalid color)";
                        PreviewHint.Visibility = Visibility.Visible;
                    }
                    break;
                }

                case NodeEvaluator.PreviewKind.Unloaded:
                {
                    PreviewHint.Text = snapshot?.Hint ?? "(unloaded)";
                    PreviewHint.Visibility = Visibility.Visible;
                    break;
                }

                case NodeEvaluator.PreviewKind.Error:
                {
                    // Muted-red tint so the strip reads as "evaluator
                    // surfaced an error" without screaming. StatusRedTint
                    // is the project's pre-baked low-alpha red overlay —
                    // see PhoenixDark.xaml; reused so no new brush is
                    // introduced just for the preview-host error state.
                    PlaceholderRect.Fill = (Brush)Application.Current.Resources["StatusRedTintBrush"];
                    PreviewHint.Text = snapshot?.Hint ?? "(preview error)";
                    PreviewHint.Visibility = Visibility.Visible;
                    if (!string.IsNullOrWhiteSpace(snapshot?.Hint))
                    {
                        // Full hint via tooltip so the trimmed inline text
                        // doesn't hide the underlying message.
                        ToolTipService.SetToolTip(this, snapshot!.Hint);
                    }
                    break;
                }

                case NodeEvaluator.PreviewKind.Empty:
                default:
                    // Empty leaves only PlaceholderRect visible — the
                    // host renders its neutral coal panel. The consumer
                    // typically collapses the wrapping row entirely on
                    // Empty so this branch is rarely seen in practice.
                    break;
            }
        }
        catch (Exception ex)
        {
            // Defensive: any unexpected failure (theme key missing, etc.)
            // should degrade silently rather than tear down the canvas.
            try
            {
                PreviewImage.Visibility  = Visibility.Collapsed;
                PreviewSwatch.Visibility = Visibility.Collapsed;
                PreviewHint.Visibility   = Visibility.Collapsed;
            }
            catch { /* swallow */ }
            GlobalLogger.Error("ThumbnailHost", "SetSnapshot", ex);
        }
    }

    // Best-effort bitmap loader. Accepts absolute paths and http(s) URIs, AND
    // (R21) media-root-relative paths — which is what MediaPickerDialog stores
    // for Image.Load nodes. Previously a relative path fell straight through to
    // false, so the node-body preview was permanently blank for the most common
    // image source. We resolve relative paths against the Hub media root and
    // load the file if it exists.
    private static bool TryLoadBitmap(string? source, out BitmapImage? bmp)
    {
        bmp = null;
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            // Absolute path or http(s) URI — load directly (unchanged behavior).
            if (Uri.TryCreate(source, UriKind.Absolute, out var uri))
            {
                bmp = new BitmapImage(uri);
                return true;
            }
            // R21 — relative media path (e.g. "images/foo.png"), relative to the
            // Hub media root that MediaPickerDialog / Import write into. Resolve
            // in the WinUI host (not the engine) so both the OwnPath and the
            // UpstreamImage NodeEvaluator branches render.
            string rel  = source.Replace('/', System.IO.Path.DirectorySeparatorChar);
            string full = System.IO.Path.Combine(Phoenix.Controls.Shared.Core.Paths.HubMedia, rel);
            if (System.IO.File.Exists(full))
            {
                bmp = new BitmapImage(new Uri(full));
                return true;
            }
            // K-P2 (audit live-preview lane): the relative media path resolved
            // but the file isn't on disk. The node strip falls back to the
            // generic "(image unavailable)" hint, which hides WHY — was the
            // HubMedia root wrong, or is the file truly missing? Emit a breadcrumb
            // with the resolved full path so a user diagnosing a media-import
            // failure can see exactly where we looked. Debug level + no modal,
            // per feedback_no_modal_dialogs_for_repeatable_rejections.
            GlobalLogger.Log(
                $"Image load: relative path '{source}' resolved to '{full}' but file not found.",
                "ThumbnailHost", LogLevel.Debug);
            return false;
        }
        catch
        {
            bmp = null;
            return false;
        }
    }

    // Hex parser tolerating "#rrggbb" / "rrggbb" / "#rrggbbaa" / "rrggbbaa".
    // The 8-character form is interpreted as r,g,b,a (engine convention) —
    // matches the prior inline parser so .phxlayer round-trip stays
    // unchanged.
    private static bool TryParseHex(string? hex, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim();
        if (s.StartsWith("#")) s = s[1..];
        try
        {
            byte a, r, g, b;
            if (s.Length == 6)
            {
                a = 0xFF;
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
            }
            else if (s.Length == 8)
            {
                r = Convert.ToByte(s.Substring(0, 2), 16);
                g = Convert.ToByte(s.Substring(2, 2), 16);
                b = Convert.ToByte(s.Substring(4, 2), 16);
                a = Convert.ToByte(s.Substring(6, 2), 16);
            }
            else
            {
                return false;
            }
            color = Color.FromArgb(a, r, g, b);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
