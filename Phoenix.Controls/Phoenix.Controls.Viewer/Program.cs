using System;
using System.IO;
using System.Windows.Forms;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Viewer;

/// <summary>
/// Phoenix Controls — Viewer (WebView2 desktop shell).
///
/// Replaces the retired WinForms Viewer per the v2 redesign. The whole
/// UI is the web bundle in <c>Phoenix.Controls.WebViewer</c>; this exe
/// is just a chromeless container that points WebView2 at the same
/// URL a browser would hit (default <c>http://127.0.0.1:18090/v/{channel}</c>).
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Explicit PerMonitorV2 prelude — WebView2 hosts on multi-monitor
        // setups with mixed DPI need this to keep the chrome from re-virtualizing
        // the browser surface during DPI changes. Must precede
        // ApplicationConfiguration.Initialize() and any window construction;
        // the source-generated default is SystemAware otherwise.
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);

        ApplicationConfiguration.Initialize();

        // Carry the AppData migration shim forward so a user upgrading
        // from the WinForms Viewer keeps any existing Hub state pinned
        // to the Phoenix Controls retail brand.
        AppDataMigrator.RunOnce();

        IconFontResolver.Init();
        IconFontResolver.ShowMissingFontWarningIfNeeded();

        string url = ResolveUrl(args);
        using var win = new ViewerWindow(url);
        Application.Run(win);
    }

    /// <summary>
    /// Resolution order:
    /// <list type="number">
    ///   <item>First positional arg if it parses as a URI.</item>
    ///   <item><c>%LOCALAPPDATA%/PhoenixControls/Viewer/url.txt</c> first non-empty line.</item>
    ///   <item>Default <c>http://127.0.0.1:18090/v/channel</c>.</item>
    /// </list>
    /// </summary>
    private static string ResolveUrl(string[] args)
    {
        if (args.Length > 0 && Uri.TryCreate(args[0], UriKind.Absolute, out _))
            return args[0];

        string urlFile = Path.Combine(Paths.LocalAppData("Viewer"), "url.txt");
        try
        {
            if (File.Exists(urlFile))
            {
                foreach (var line in File.ReadAllLines(urlFile))
                {
                    var t = line.Trim();
                    if (t.Length > 0 && !t.StartsWith('#') &&
                        Uri.TryCreate(t, UriKind.Absolute, out _))
                        return t;
                }
            }
        }
        catch (Exception ex)
        {
            // Previously swallowed silently — a malformed or
            // permission-locked url.txt would leave the user wondering
            // why the Viewer points at the default port. Surface it
            // through the central log so SystemLog / LiveFeed pick it up.
            GlobalLogger.Error(
                "Viewer",
                $"Failed to read Viewer url.txt at '{urlFile}' — falling back to default URL",
                ex);
        }

        return "http://127.0.0.1:18090/v/channel";
    }
}
