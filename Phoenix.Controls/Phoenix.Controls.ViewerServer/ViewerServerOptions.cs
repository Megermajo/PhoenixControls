using System.Net;

namespace Phoenix.Controls.ViewerServer;

/// <summary>
/// Configuration for <see cref="ViewerServer"/>. Defaults match the
/// design package: loopback bind, port 18090, LAN off.
///
/// <para><b>Endpoint-reachability design intent</b> (P1-U2 hardening note):
/// <c>GET /v/api/snapshot</c> is intentionally LAN-reachable. Paired
/// phones / tablets on the same network are the primary consumer; they
/// fetch the bootstrap payload from a non-loopback origin after pairing.
/// Bearer-token auth (issued via the PIN flow) gates access — LAN
/// reachability alone is not a privilege grant. The live pairing PIN
/// itself is never served over HTTP: the former loopback-only
/// <c>GET /v/api/pin</c> route was removed (no Hub UI ever consumed it,
/// and an unauthenticated PIN-disclosure endpoint is a liability).</para>
/// </summary>
public sealed class ViewerServerOptions
{
    /// <summary>Channel name shown in the viewer top bar (e.g. "megermajo").</summary>
    public string Channel { get; set; } = "channel";

    /// <summary>
    /// Bind address. <see cref="IPAddress.Loopback"/> = 127.0.0.1 (default,
    /// LAN unreachable). Use <see cref="IPAddress.Any"/> together with
    /// <see cref="LanModeEnabled"/> to expose on the LAN.
    /// </summary>
    public IPAddress BindAddress { get; set; } = IPAddress.Loopback;

    /// <summary>HTTP+WS port. 18090 is the design-package default.</summary>
    public int Port { get; set; } = 18090;

    /// <summary>
    /// When true, the server binds the wildcard <c>+</c> prefix so other
    /// devices on the LAN can reach it. Pairs with <see cref="BindAddress"/>
    /// = <see cref="IPAddress.Any"/>.
    /// </summary>
    public bool LanModeEnabled { get; set; }

    /// <summary>
    /// Filesystem root for the static web bundle (HTML/CSS/JS/assets).
    /// Resolved at <see cref="ViewerServer.StartAsync"/> time relative to
    /// the host's working directory. The matching project is
    /// <c>Phoenix.Controls.WebViewer</c>; downstream consumers
    /// ProjectReference it with <c>OutputItemType="Content"</c> so its
    /// <c>dist/</c> tree lands at this path under the host's bin.
    /// </summary>
    public string AssetsRoot { get; set; } = "ViewerAssets";

    /// <summary>
    /// Where to persist paired-device tokens. Default null = the server
    /// resolves to <c>%LOCALAPPDATA%/PhoenixControls/Hub/viewer_devices.json</c>.
    /// </summary>
    public string? PairedDevicesPath { get; set; }

    /// <summary>How long an unclaimed PIN remains valid.</summary>
    public TimeSpan PinTtl { get; set; } = TimeSpan.FromSeconds(60);
}
