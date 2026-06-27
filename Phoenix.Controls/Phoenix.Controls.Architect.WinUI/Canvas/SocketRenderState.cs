using System.Collections.Concurrent;

namespace Phoenix.Controls.Architect.WinUI.Canvas;

// 0.11.5 canvas-polish r3 — process-wide map from Socket.Id → measured row-
// centre Y in NodeRoot's outer-top coordinate space, populated by NodeView's
// per-row Loaded / SizeChanged handlers and consumed by both the pin overlay
// (NodeView.RepositionAllPins) and the wire-anchor math
// (LinkViewModel.RecomputePath) so multi-line pill rows stay glued to the
// painted pin centre.
//
// The map exists because LinkViewModel only has the raw Graph POCO (no
// SocketViewModel reference), so it can't read SocketViewModel.MeasuredRowCenterY
// directly. Routing through Socket.Id keeps render-only concerns out of the
// data model AND out of LogicCanvasViewModel's API surface — a single static
// service writes once per layout pass and reads once per wire recompute.
//
// Lifetime: entries are keyed by Socket.Id (Guid string), which is unique
// per socket across the whole process. Stale entries on socket deletion sit
// harmlessly; the map's footprint is bounded by the total number of socket
// instances ever measured, which in a single Architect session caps around
// the low thousands even with heavy graph churn. NodeView's UnhookPinOverlay
// removes entries for sockets it owned so a closed .phxg's measurements
// don't bleed into a future open of the same file (sockets get fresh Ids
// on each Graph load).
public static class SocketRenderState
{
    private static readonly ConcurrentDictionary<string, double> _measuredY = new();

    /// <summary>Write the measured row-centre Y for <paramref name="socketId"/>.
    /// Y is in NodeRoot outer-top coordinates (i.e. y=0 is the top of the
    /// node body's outer border).</summary>
    public static void SetMeasuredRowCenterY(string socketId, double y)
    {
        if (string.IsNullOrEmpty(socketId)) return;
        _measuredY[socketId] = y;
    }

    /// <summary>Read the measured row-centre Y for <paramref name="socketId"/>,
    /// or null if NodeView hasn't measured this socket yet (first paint, or
    /// the socket lives off-screen on a virtualised path).</summary>
    public static double? TryGetMeasuredRowCenterY(string socketId)
    {
        if (string.IsNullOrEmpty(socketId)) return null;
        return _measuredY.TryGetValue(socketId, out var y) ? y : null;
    }

    /// <summary>Drop the measurement for <paramref name="socketId"/> — called
    /// from NodeView.UnhookPinOverlay when the node leaves the visual tree
    /// or its socket list rebuilds.</summary>
    public static void Forget(string socketId)
    {
        if (string.IsNullOrEmpty(socketId)) return;
        _measuredY.TryRemove(socketId, out _);
    }
}
