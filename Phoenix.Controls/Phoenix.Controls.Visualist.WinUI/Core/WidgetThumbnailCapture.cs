using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using Phoenix.Controls.Shared.Services;
using Windows.Storage.Streams;
using Windows.Graphics.Imaging;

namespace Phoenix.Controls.Visualist.WinUI.Core;

/// <summary>
/// WinUI render-to-bitmap utility
/// for capturing a per-widget thumbnail at save time. Renders the supplied
/// <see cref="UIElement"/> via <see cref="RenderTargetBitmap"/>, downscales to
/// fit inside <c>maxDim</c>, re-encodes as PNG, and returns the base64-encoded
/// payload to embed into <c>LayerWidget.Thumbnail</c> (round-trips through
/// LayerSerializer unchanged — see LayerWidget.cs:27).
///
/// This utility is
/// Visualist-only and lives under <c>Visualist.WinUI/Core/</c>; Architect has
/// no analogue (the logic canvas has nothing to thumbnail). Failures degrade
/// silently — null return, GlobalLogger.Error breadcrumb, no modals.
///
/// All entry points must be called on the UI thread that owns
/// <paramref name="target"/> — <see cref="RenderTargetBitmap.RenderAsync"/> is
/// dispatcher-affined.
/// </summary>
public static class WidgetThumbnailCapture
{
    /// <summary>
    /// Capture the supplied element into a PNG-encoded base64 payload sized
    /// to fit inside an <paramref name="maxDim"/>×<paramref name="maxDim"/>
    /// box (aspect preserved). Returns <c>null</c> on any failure — the layer
    /// save path then writes the existing Thumbnail (or null) unchanged so a
    /// bad capture never corrupts the .phxlayer.
    /// </summary>
    /// <param name="target">Visualist widget root (typically a
    /// <c>WidgetView</c> instance already mounted on the layer canvas).
    /// Must have a non-zero <c>ActualWidth</c> / <c>ActualHeight</c>;
    /// off-screen elements return null.</param>
    /// <param name="maxDim">Long-edge clamp in DIPs. 256 by default — small
    /// enough that 50 widgets embed under ~1 MB of base64 JSON.</param>
    public static async Task<string?> CaptureBase64Async(UIElement target, int maxDim = 256)
    {
        if (target is null)
        {
            return null;
        }

        try
        {
            // RenderTargetBitmap will silently produce a 0×0 result for an
            // element with no measured size (designer / collapsed / un-attached).
            // Guard up front so we don't go through the full pipeline for
            // nothing — and so callers know to skip writing the field.
            if (target is FrameworkElement fe)
            {
                // A widget added programmatically
                // immediately before a save may not have been through a layout
                // pass yet, so ActualWidth/Height are still 0 and the capture
                // would be skipped — the new widget then persists with a null
                // Thumbnail until the *next* save. UpdateLayout() synchronously
                // forces the Measure + Arrange pass on this element's subtree
                // (DispatcherQueue.TryEnqueue only defers to the next cycle and
                // does NOT guarantee layout completion — see the verifier note),
                // so a content-bearing widget mounted in the visual tree gets a
                // valid size before the guard below. A still-zero result here is
                // a genuinely sizeless/detached element and is correctly skipped.
                try { fe.UpdateLayout(); }
                catch { /* element detached mid-layout — fall through to the size guard */ }

                if (fe.ActualWidth <= 0 || fe.ActualHeight <= 0)
                {
                    return null;
                }
            }

            var rtb = new RenderTargetBitmap();
            await rtb.RenderAsync(target);

            int srcW = rtb.PixelWidth;
            int srcH = rtb.PixelHeight;
            if (srcW <= 0 || srcH <= 0)
            {
                return null;
            }

            var pixelBuffer = await rtb.GetPixelsAsync();
            byte[] pixels = pixelBuffer.ToArray();

            // Compute the downscale target preserving aspect. The encoder
            // performs the actual resample via BitmapTransform.ScaledWidth /
            // ScaledHeight on the GetPixelDataAsync call below.
            int dstW = srcW;
            int dstH = srcH;
            int longEdge = Math.Max(srcW, srcH);
            if (longEdge > maxDim)
            {
                double scale = (double)maxDim / longEdge;
                dstW = Math.Max(1, (int)Math.Round(srcW * scale));
                dstH = Math.Max(1, (int)Math.Round(srcH * scale));
            }

            using var ms = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ms);
            encoder.SetPixelData(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                (uint)srcW,
                (uint)srcH,
                96.0,
                96.0,
                pixels);

            if (dstW != srcW || dstH != srcH)
            {
                encoder.BitmapTransform.ScaledWidth = (uint)dstW;
                encoder.BitmapTransform.ScaledHeight = (uint)dstH;
                encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            }

            await encoder.FlushAsync();

            // Pull the encoded PNG bytes out of the in-memory stream.
            ms.Seek(0);
            using var reader = new DataReader(ms.GetInputStreamAt(0));
            uint size = (uint)ms.Size;
            await reader.LoadAsync(size);
            byte[] png = new byte[size];
            reader.ReadBytes(png);

            // Embed as base64 with a data-URI prefix so the field is
            // self-describing — a future viewer can recognise the MIME from
            // the prefix alone without sniffing magic bytes.
            return "data:image/png;base64," + Convert.ToBase64String(png);
        }
        catch (Exception ex)
        {
            // Render-to-bitmap can throw COM exceptions when the element is
            // detached mid-render or the GPU is busy (screen-lock, RDP).
            // Log + null so the layer save path falls back to the previous
            // Thumbnail value unchanged.
            GlobalLogger.Error("WidgetThumbnailCapture", "CaptureBase64Async", ex);
            return null;
        }
    }
}
