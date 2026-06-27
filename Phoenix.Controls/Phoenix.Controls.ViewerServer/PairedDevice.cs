using System;

namespace Phoenix.Controls.ViewerServer;

/// <summary>
/// One paired remote viewer. The bearer token presented over the wire
/// is never stored — only the PBKDF2 hash + salt. <see cref="DeviceId"/>
/// is the shared key the viewer's credentials.json caches alongside its
/// token to identify itself on subsequent connects.
/// </summary>
public sealed class PairedDevice
{
    public string         DeviceId    { get; init; } = "";
    public string         Label       { get; init; } = "";
    public DateTimeOffset PairedAt    { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen    { get; set;  } = DateTimeOffset.UtcNow;
    public bool           Revoked     { get; set;  }

    /// <summary>
    /// When this device's bearer token was issued. Used together with
    /// <see cref="LastUsedUtc"/> for the rotation policy: tokens older
    /// than the absolute max (default 30 days from <see cref="IssuedAtUtc"/>)
    /// or stale longer than the idle max (default 7 days since
    /// <see cref="LastUsedUtc"/>) are rejected by
    /// <c>PairedDeviceStore.Verify</c>, forcing a re-pair.
    /// </summary>
    public DateTimeOffset IssuedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Last time the bearer token actually verified against the store.
    /// Updated on each successful <c>Verify</c>; distinct from
    /// <see cref="LastSeen"/>, which the server bumps on socket-level
    /// activity. Drives the idle expiry half of the rotation policy.
    /// </summary>
    public DateTimeOffset LastUsedUtc { get; set;  } = DateTimeOffset.UtcNow;
}
