using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// Loads Phoenix Controls's identity / brand assets (phoenix_app, pillar_*)
    /// from each pillar's <c>Assets/Icons/</c> folder. Different from
    /// <c>IconCache</c>: that one slices 3-state hover/pressed strips for
    /// interactive command icons; this one returns a single image for
    /// non-interactive identity surfaces (taskbar Form.Icon, caption bar
    /// brand glyph, About box, etc.).
    ///
    /// Filename convention (matches MAJO_ICON_DESIGN_QUEUE.md):
    /// <c>{role}-{size}.png</c> for explicit sizes, <c>{role}.png</c> for the
    /// 256-size master.
    ///
    /// Both bitmaps and Icons are cached for the process lifetime — these
    /// assets are immutable on disk and the cache cost is bounded
    /// (~5 sizes × handful of roles × per-pillar = a few hundred KB).
    /// </summary>
    public static class IdentityIconLoader
    {
        public const string AssetsFolder = "Assets/Icons";

        // Sizes recognised in the multi-image .ico builder, smallest-first so
        // the ICONDIRENTRY order matches what Windows shell prefers when it
        // searches for "the smallest entry that fits this slot".
        private static readonly int[] FormIconSizes = { 16, 24, 32, 48, 256 };

        private static readonly ConcurrentDictionary<(string Role, int Size), Bitmap?> _bitmaps = new();
        private static readonly ConcurrentDictionary<string, Icon?> _formIcons = new(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>
        /// Loads the 16×16 / 24×24 / 32×32 / 48×48 single-icon PNG for
        /// <paramref name="role"/>, or null if the file is absent. Returned
        /// bitmap is cached and shared — callers must not Dispose it.
        /// </summary>
        public static Bitmap? LoadCaptionBitmap(string role, int sizePx)
        {
            if (string.IsNullOrEmpty(role) || sizePx <= 0) return null;
            return _bitmaps.GetOrAdd((role, sizePx), key =>
            {
                string filename = $"{key.Role}-{key.Size}.png";
                string path = Path.Combine(AppContext.BaseDirectory, AssetsFolder, filename);
                if (!File.Exists(path)) return null;
                try { return new Bitmap(path); }
                catch { return null; }
            });
        }

        /// <summary>
        /// Builds a multi-image <see cref="Icon"/> from every available pre-
        /// rendered size of <paramref name="role"/> on disk
        /// (<c>{role}-16/24/32/48.png</c> + the <c>{role}.png</c> 256 master).
        /// Windows shell picks the entry that matches each surface's request
        /// (~16 for Alt-Tab small, 32 for taskbar, 48 for shell large, 256
        /// for jump lists / extra-large), so the icon stays sharp without
        /// runtime downscaling. Falls back to a single-image icon built from
        /// the 256 master when no sized variants are present, and to null
        /// when the role has no assets at all. Returned icon is cached for
        /// the process lifetime — callers must not Dispose it.
        /// </summary>
        public static Icon? LoadFormIcon(string role)
        {
            if (string.IsNullOrEmpty(role)) return null;
            return _formIcons.GetOrAdd(role, BuildFormIcon);
        }

        private static Icon? BuildFormIcon(string role)
        {
            var entries = new List<(int Size, byte[] Png)>(FormIconSizes.Length);
            foreach (int size in FormIconSizes)
            {
                string filename = size == 256 ? $"{role}.png" : $"{role}-{size}.png";
                string path = Path.Combine(AppContext.BaseDirectory, AssetsFolder, filename);
                if (!File.Exists(path)) continue;
                try { entries.Add((size, File.ReadAllBytes(path))); }
                catch { /* skip unreadable size */ }
            }
            if (entries.Count == 0) return null;

            try { return ComposeIco(entries); }
            catch
            {
                // Last-resort: single-image icon from whatever we did read.
                // GetHicon scales when the surface asks for a non-matching
                // size, but at least we ship the brand.
                return BuildSingleImageIcon(entries[entries.Count - 1].Png);
            }
        }

        // Assembles an in-memory .ico (PNG-encoded entries — supported by
        // Windows since Vista for any size, by all targeted runtimes here).
        // Layout: ICONDIR (6) + ICONDIRENTRY × N (16 each) + image bytes.
        private static Icon ComposeIco(List<(int Size, byte[] Png)> entries)
        {
            int dirSize = 6 + 16 * entries.Count;
            int totalImageBytes = 0;
            for (int i = 0; i < entries.Count; i++) totalImageBytes += entries[i].Png.Length;

            using var ms = new MemoryStream(dirSize + totalImageBytes);
            using var bw = new BinaryWriter(ms);

            // ICONDIR
            bw.Write((ushort)0);                  // reserved
            bw.Write((ushort)1);                  // type = icon
            bw.Write((ushort)entries.Count);      // image count

            // ICONDIRENTRY × N
            int offset = dirSize;
            for (int i = 0; i < entries.Count; i++)
            {
                int size = entries[i].Size;
                int byteCount = entries[i].Png.Length;
                bw.Write((byte)(size >= 256 ? 0 : size)); // width  (0 means 256)
                bw.Write((byte)(size >= 256 ? 0 : size)); // height (0 means 256)
                bw.Write((byte)0);                        // palette colours (0 = no palette)
                bw.Write((byte)0);                        // reserved
                bw.Write((ushort)1);                      // colour planes
                bw.Write((ushort)32);                     // bits per pixel
                bw.Write((uint)byteCount);                // image byte count
                bw.Write((uint)offset);                   // offset from .ico start
                offset += byteCount;
            }

            // Image data (PNG bytes, each entry contiguously)
            for (int i = 0; i < entries.Count; i++) bw.Write(entries[i].Png);

            ms.Position = 0;
            return new Icon(ms);
        }

        private static Icon BuildSingleImageIcon(byte[] pngBytes)
        {
            using var src = new MemoryStream(pngBytes);
            using var bmp = new Bitmap(src);
            IntPtr hIcon = bmp.GetHicon();
            try { return (Icon)Icon.FromHandle(hIcon).Clone(); }
            finally { DestroyIcon(hIcon); }
        }
    }
}
