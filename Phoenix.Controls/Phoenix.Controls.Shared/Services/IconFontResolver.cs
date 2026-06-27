using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Phoenix.Controls.Shared.Services
{
    /// <summary>
    /// Detects which Microsoft icon font is installed on the host and exposes the
    /// chosen family name for <c>IconCache</c> to draw glyphs with. Phoenix
    /// Controls' <c>Glyphs</c> codepoints come from Segoe Fluent Icons
    /// (Windows 10 build 19041+) which is PUA-compatible with Segoe MDL2 Assets
    /// on older builds — the same string renders the same shape on either font,
    /// so falling back to MDL2 Assets covers every Windows install since 8.1.
    ///
    /// The resolver is single-shot and lock-free: <c>Init</c> is idempotent and
    /// the field is published via volatile-write ordering on the first call.
    /// Callers can use either <see cref="FontFamilyName"/> or
    /// <see cref="IsResolved"/> from any thread without taking a lock.
    /// </summary>
    public static class IconFontResolver
    {
        private static readonly string[] PreferredFamilies =
        {
            "Segoe Fluent Icons",   // Win10 19041+ / Win11
            "Segoe MDL2 Assets",    // Win8.1+ — PUA-compatible fallback
        };

        // Microsoft's reference doc for Segoe Fluent Icons. Most users who land
        // here have an older Windows install; the doc explains availability and
        // links to the relevant Windows update path.
        private const string HelpUrl =
            "https://learn.microsoft.com/en-us/windows/apps/design/style/segoe-fluent-icons-font";

        private static volatile bool _initialized;
        private static string _fontFamily = string.Empty;

        //  One-shot guard for ShowMissingFontWarningIfNeeded. The
        // method docstring promised "exactly once per process" but the
        // implementation relied solely on the caller honouring that — every
        // pillar's Program.cs calls it at startup, and any future caller
        // could re-trigger the modal. Interlocked.CompareExchange makes the
        // guarantee structural: only the first caller into the popup branch
        // proceeds; everyone else short-circuits.
        private static int _warningShown;

        /// <summary>
        /// Last-resort family name returned by <see cref="FontFamilyOrFallback"/>
        /// when no icon font is installed. <c>Segoe UI</c> ships with every
        /// Windows since Vista, so <c>new Font(Fallback, …)</c> is guaranteed
        /// not to throw — it just renders PUA codepoints as missing glyphs.
        /// </summary>
        private const string Fallback = "Segoe UI";

        /// <summary>The resolved icon font family, or empty if no candidate is installed.</summary>
        public static string FontFamilyName
        {
            get { Init(); return _fontFamily; }
        }

        /// <summary>
        /// Same as <see cref="FontFamilyName"/> but never empty — callers that
        /// build <c>new Font(…)</c> at static-init time should use this so the
        /// constructor can't throw on a stripped Windows. When no icon font is
        /// installed the result is <see cref="Fallback"/> ("Segoe UI"); glyphs
        /// won't render but the UI still loads.
        /// </summary>
        public static string FontFamilyOrFallback
        {
            get { string f = FontFamilyName; return string.IsNullOrEmpty(f) ? Fallback : f; }
        }

        /// <summary>True once <see cref="Init"/> has run AND found an installed icon font.</summary>
        public static bool IsResolved => !string.IsNullOrEmpty(FontFamilyName);

        /// <summary>
        /// Walks installed font families and picks the first match from
        /// <see cref="PreferredFamilies"/>. Idempotent — repeated calls are
        /// cheap. Safe to call from any thread; the first caller wins and
        /// subsequent calls observe the cached result.
        /// </summary>
        public static void Init()
        {
            if (_initialized) return;

            var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (FontFamily fam in FontFamily.Families)
                    available.Add(fam.Name);
            }
            catch
            {
                // GDI+ font enumeration can throw on locked-down kiosk profiles.
                // Treat as "no fonts found" — the popup will inform the user.
            }

            foreach (string name in PreferredFamilies)
            {
                if (available.Contains(name))
                {
                    _fontFamily = name;
                    break;
                }
            }

            _initialized = true;
        }

        /// <summary>
        /// If no icon font is installed, shows a one-shot informational dialog
        /// at app startup with a link to Microsoft's Segoe Fluent Icons
        /// reference. Safe to call multiple times — only the first invocation
        /// that hits the missing-font branch will surface the modal
        /// (Interlocked.CompareExchange guard, ). The dialog is
        /// non-blocking past dismissal: the suite continues to run with empty
        /// glyph slots in place of icons.
        /// </summary>
        public static void ShowMissingFontWarningIfNeeded()
        {
            Init();
            if (IsResolved) return;

            // First-caller wins; all subsequent invocations no-op.
            if (Interlocked.CompareExchange(ref _warningShown, 1, 0) != 0) return;

            DialogResult result = MessageBox.Show(
                "Phoenix Controls uses Microsoft's Segoe Fluent Icons (or Segoe MDL2 " +
                "Assets on older builds) to render UI icons. Neither font is installed on " +
                "this system, so some icons will display as empty boxes.\n\n" +
                "This is rare on Windows 10/11 — typically caused by a stripped, server, " +
                "or LTSC install. Updating Windows to the May 2020 update (build 19041) or " +
                "later resolves it.\n\n" +
                "The suite will continue to work — only the icon glyphs are affected.\n\n" +
                "Open the Microsoft documentation for more info?",
                "Phoenix Controls — icon font missing",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);

            if (result == DialogResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(HelpUrl) { UseShellExecute = true });
                }
                catch
                {
                    // Best effort — if Process.Start fails (no default browser, etc.),
                    // the URL is still in the dialog text the user can copy by hand.
                }
            }
        }
    }
}
