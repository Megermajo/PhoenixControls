using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Viewer;

/// <summary>
/// Slim WinForms host for the WebView2 control. The browser process
/// owns its user-data folder under <c>%LOCALAPPDATA%/PhoenixControls/Viewer/WebView2</c>
/// so cookies and pairing tokens survive across launches.
///
/// When WinUI 3 parity is reached this class can be swapped for a
/// <c>WinUI.Window</c> hosting the same <see cref="WebView2"/> with no
/// behavior change.
/// </summary>
public sealed class ViewerWindow : Form
{
    private readonly WebView2 _web = new();
    private readonly string _url;

    public ViewerWindow(string url)
    {
        _url = url;
        Text = "Phoenix Controls — Viewer";
        ClientSize = new System.Drawing.Size(1480, 920);
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = System.Drawing.Color.FromArgb(11, 9, 7); // coal-0

        _web.Dock = DockStyle.Fill;
        _web.DefaultBackgroundColor = BackColor;
        Controls.Add(_web);

        Load += async (_, _) => await InitializeWebViewAsync();
    }

    private async Task InitializeWebViewAsync()
    {
        try
        {
            string userDataFolder = Paths.LocalAppData(Path.Combine("Viewer", "WebView2"));
            await Task.Run(() => Directory.CreateDirectory(userDataFolder)).ConfigureAwait(false);
            var env = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null,
                userDataFolder: userDataFolder);

            // Window may have been closed while we awaited environment
            // creation — touching _web after disposal throws ObjectDisposedException
            // through the async void Load handler and crashes the shutdown path.
            if (IsDisposed || !_web.IsHandleCreated || _web.IsDisposed) return;

            await _web.EnsureCoreWebView2Async(env);

            // Same race after the EnsureCoreWebView2Async await —
            // CoreWebView2 is non-null here, but Settings/Source assignment
            // against a disposing control will throw.
            if (IsDisposed || _web.IsDisposed || _web.CoreWebView2 == null) return;

            _web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _web.Source = new Uri(_url);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race — nothing to surface to the user, the window is gone.
        }
        catch (Exception ex)
        {
            // Route the failure through GlobalLogger before showing the
            // modal so SystemLog / LiveFeed pick it up alongside the user-facing
            // message. The dialog stays — Viewer is end-user-facing and the
            // WebView2 runtime gap is the most actionable failure mode.
            GlobalLogger.Error("Viewer", "WebView2 initialization failed", ex);

            if (IsDisposed) return;

            MessageBox.Show(
                this,
                "Could not initialize WebView2.\n\n" +
                "Phoenix Controls — Viewer needs the Microsoft Edge WebView2 Runtime, which ships with Windows 11 and most Windows 10 systems. " +
                "If you're on a stripped-down install, download it from https://developer.microsoft.com/microsoft-edge/webview2/.\n\n" +
                ex.Message,
                "Phoenix Controls — Viewer",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }
}
