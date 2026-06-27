using System.Drawing;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// AudioSinkNode — second sink kind alongside <see cref="DisplaySinkNode"/>.
    /// Receives a single <see cref="SocketDataType.Audio"/> input (typically wired
    /// from an <c>Audio.Load</c> node) and triggers playback browser-side via
    /// <c>compositor.js</c>'s <c>evalAudioPlay</c> sink-visit pass.
    ///
    /// Unlike Display, Audio.Play is not auto-injected — graphs that don't
    /// author one simply produce no audio. The "one Display per trigger"
    /// invariant has a sibling here: only one Audio.Play per trigger graph
    /// (additional drops are rejected by <see cref="WidgetGraphCanvas"/>).
    /// </summary>
    public static class AudioSinkNode
    {
        public const string Title    = "Audio.Play";
        public const string Category = "Sink";

        public static Node Build()
        {
            return new Node
            {
                Title       = Title,
                Category    = Category,
                Location    = new Point(420, 220),
                Size        = new Size(180, 80),
                HeaderColor = Color.FromArgb(90, 64, 96),
                Sockets     =
                {
                    new Socket { Name = "Audio", Type = SocketType.Input, DataType = SocketDataType.Audio, Color = Color.FromArgb(190, 140, 240) },
                },
                Attributes  =
                {
                    ["Volume"] = "1.0",
                    ["Loop"]   = "false",
                },
            };
        }

        public static bool Is(Node node) =>
            node.Title == Title && string.Equals(node.Category, Category, System.StringComparison.OrdinalIgnoreCase);
    }
}
