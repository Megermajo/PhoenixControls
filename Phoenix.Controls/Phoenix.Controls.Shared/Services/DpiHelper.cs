using System;
using System.Drawing;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    ///  — DPI scaling helper for code paths that compute pixel sizes
    /// outside the WinForms AutoScale pipeline. Use this for:
    /// <list type="bullet">
    ///   <item><description>Canvas rendering math (pixel-perfect drawing where the
    ///     control's <c>DeviceDpi</c> drives the size).</description></item>
    ///   <item><description>One-shot bitmap allocations sized in logical units.</description></item>
    ///   <item><description>Ad-hoc layout math in <c>OnLoad</c> / <c>OnLayout</c> handlers
    ///     where AutoScaleMode wouldn't fire on the constants.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// Form-level layout (size, padding, child positions on the design surface)
    /// is already DPI-aware via <c>AutoScaleMode = AutoScaleMode.Dpi</c> on every
    /// dialog in the suite ( confirmed the audit; previously
    /// <c>VarChainTraceForm</c> was the only outlier and was fixed in the same
    /// sprint). This helper is for the residual code-path cases AutoScale doesn't
    /// reach.
    /// <para>
    /// The helper is stateless and takes a DPI value rather than a Form
    /// reference — call sites that have a <c>Control.DeviceDpi</c> handy
    /// (any form / control on the visible tree) pass that directly.
    /// 96 DPI is the logical baseline (the historical Windows "100%" scale).
    /// </para>
    /// </remarks>
    public static class DpiHelper
    {
        /// <summary>The logical baseline DPI (Windows historical "100%" scale).</summary>
        public const int LogicalDpi = 96;

        /// <summary>Scales a logical-pixel value to device pixels at the given DPI. 96 DPI is identity.</summary>
        public static int Scale(int value, int deviceDpi)
        {
            if (deviceDpi <= 0) return value;
            return (int)Math.Round(value * (deviceDpi / (double)LogicalDpi));
        }

        /// <summary>Scales a <see cref="Size"/> component-wise.</summary>
        public static Size Scale(Size size, int deviceDpi)
            => new Size(Scale(size.Width, deviceDpi), Scale(size.Height, deviceDpi));

        /// <summary>Scales a <see cref="Point"/> component-wise.</summary>
        public static Point Scale(Point point, int deviceDpi)
            => new Point(Scale(point.X, deviceDpi), Scale(point.Y, deviceDpi));

        /// <summary>Returns the scale factor for a given device DPI (<c>deviceDpi / 96.0</c>).</summary>
        public static double ScaleFactor(int deviceDpi)
        {
            if (deviceDpi <= 0) return 1.0;
            return deviceDpi / (double)LogicalDpi;
        }
    }
}
