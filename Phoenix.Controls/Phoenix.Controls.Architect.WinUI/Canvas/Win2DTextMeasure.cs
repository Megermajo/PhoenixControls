using System;
using System.Collections.Generic;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

/// <summary>
/// Shared resolver for the Win2D canvas font families. The GPU renderer draws
/// node text with the app's EXACT Inter / JetBrains Mono faces loaded from
/// their .ttf files via a <c>file://</c> URI + "#Family" (Phoenix is
/// UNPACKAGED, so the packaged "ms-appx:///" form does not resolve for Win2D
/// and DrawText would throw on it). Resolution is cached process-wide so the
/// per-canvas <c>EnsureTextFormats</c> and the measurement oracle in
/// <see cref="Win2DTextMeasure"/> always agree on the faces in use.
/// </summary>
internal static class Win2DCanvasFonts
{
    private const string FallbackSans = "Segoe UI Variable Text";
    private const string FallbackMono = "Consolas";

    private static readonly object s_gate = new();
    private static (string Sans, string Mono, bool Exact)? s_resolved;

    /// <summary>
    /// Resolve (and cache) the sans / mono font-family strings for Win2D text.
    /// The trial <see cref="CanvasTextLayout"/> forces font resolution against
    /// <paramref name="rc"/>; if the file fonts can't load, the system
    /// fallbacks are cached instead so the hot draw path can never throw on an
    /// unresolvable family.
    /// </summary>
    internal static (string Sans, string Mono, bool Exact) Resolve(ICanvasResourceCreator rc)
    {
        lock (s_gate)
        {
            if (s_resolved is { } cached) return cached;

            string sans = FallbackSans;
            string mono = FallbackMono;
            bool exact = false;
            try
            {
                string fontsDir = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts");
                string interTtf = System.IO.Path.Combine(fontsDir, "Inter-Regular.ttf");
                string jbTtf    = System.IO.Path.Combine(fontsDir, "JetBrainsMono-Regular.ttf");
                if (System.IO.File.Exists(interTtf) && System.IO.File.Exists(jbTtf))
                {
                    string sansFam = new Uri(interTtf).AbsoluteUri + "#Inter";
                    string monoFam = new Uri(jbTtf).AbsoluteUri + "#JetBrains Mono";
                    // Trial-load: force font resolution; if it can't load, this
                    // throws and we keep the system fallback.
                    using (var tf = new CanvasTextFormat { FontFamily = sansFam, FontSize = 13 })
                    using (var tl = new CanvasTextLayout(rc, "Ag", tf, 200, 50)) { _ = tl.LayoutBounds; }
                    using (var tf2 = new CanvasTextFormat { FontFamily = monoFam, FontSize = 11 })
                    using (var tl2 = new CanvasTextLayout(rc, "Ag", tf2, 200, 50)) { _ = tl2.LayoutBounds; }
                    sans = sansFam; mono = monoFam; exact = true;
                }
            }
            catch { sans = FallbackSans; mono = FallbackMono; exact = false; }

            s_resolved = (sans, mono, exact);
            return s_resolved.Value;
        }
    }
}

/// <summary>
/// Measures text with the SAME DirectWrite engine + faces the Win2D canvas
/// draws with. <c>NodeGeometry.EstimateTextWidth</c> measures via a XAML
/// <c>TextBlock</c> — a different text engine whose advance widths drift a few
/// px from the Win2D <c>CanvasTextFormat</c> layout. Every width budget
/// (node intrinsic width, pill rect, wrap decision) derived from the XAML
/// number, so whenever DirectWrite needed slightly more room than the estimate
/// plus its slack, the GPU renderer's CharacterEllipsis trimmed the last
/// letters off the pill even though the row had reserved space (Majo:
/// "auto-scaling of text pills still cut off the last couple letters").
/// <see cref="NodeGeometry.EstimateTextWidth"/> consults this oracle and takes
/// the MAX of both engines, so every consumer stays in lockstep and the
/// renderer can never need more width than the geometry reserved.
/// </summary>
internal static class Win2DTextMeasure
{
    private static readonly object s_gate = new();

    // One format per (size, bold, mono) — the canvas uses ~6 combinations
    // (11 mono pills, 12 sans labels, 13 bold titles, 10 italic definers).
    // CanvasTextFormat is device-independent, so device loss doesn't
    // invalidate these.
    private static readonly Dictionary<(double Size, bool Bold, bool Mono), CanvasTextFormat> s_formats = new();

    // Fail-safe: headless test hosts / GPU-less sessions can't create the
    // shared CanvasDevice. If the oracle has NEVER produced a measurement and
    // fails a few times in a row, it disables itself for the process and
    // callers stay on the XAML estimate (the retained-canvas behavior, correct
    // for that environment). Once it HAS succeeded, a failure is treated as
    // transient device loss — no latch, the next call simply retries against
    // the recovered shared device.
    private static int s_failStreak;
    private static bool s_everSucceeded;
    private static bool s_disabled;

    /// <summary>
    /// True when the oracle has latched off for this process (no Win2D device
    /// could ever be created — headless hosts). Lets
    /// <see cref="NodeGeometry.EstimateTextWidth"/> cache its XAML-only value
    /// as final instead of re-measuring forever.
    /// </summary>
    internal static bool IsPermanentlyUnavailable => s_disabled;

    /// <summary>
    /// Width (px) of <paramref name="text"/> as the Win2D renderer will lay it
    /// out, including trailing whitespace (pill values can end in spaces).
    /// Returns a negative value when the oracle is unavailable — callers fall
    /// back to their own estimate.
    /// </summary>
    internal static double MeasureWidth(string text, double fontSize, bool bold, bool mono)
    {
        if (s_disabled || string.IsNullOrEmpty(text)) return -1;
        try
        {
            lock (s_gate)
            {
                var device = CanvasDevice.GetSharedDevice();
                var (sans, monoFam, _) = Win2DCanvasFonts.Resolve(device);
                var key = (fontSize, bold, mono);
                if (!s_formats.TryGetValue(key, out var fmt))
                {
                    fmt = new CanvasTextFormat
                    {
                        FontFamily = mono ? monoFam : sans,
                        FontSize = (float)fontSize,
                        FontWeight = new Windows.UI.Text.FontWeight { Weight = (ushort)(bold ? 700 : 400) },
                        WordWrapping = CanvasWordWrapping.NoWrap,
                    };
                    s_formats[key] = fmt;
                }
                using var layout = new CanvasTextLayout(device, text, fmt, 1_000_000f, 100f);
                double w = layout.LayoutBoundsIncludingTrailingWhitespace.Width;
                s_failStreak = 0;
                s_everSucceeded = true;
                return w;
            }
        }
        catch
        {
            // Only an environment that has NEVER produced a device latches off
            // (unit tests / GPU-less session — stop paying for the throw on
            // every unique string). A post-success failure is transient device
            // loss: report unavailable for this call and retry on the next.
            if (!s_everSucceeded && ++s_failStreak >= 3) s_disabled = true;
            return -1;
        }
    }
}
