using System.Drawing;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Visualist.Core
{
    /// <summary>
    /// DisplaySinkNode — the auto-injected, non-removable terminal node every
    /// per-trigger graph carries. Receives the final <see cref="SocketDataType.Image"/>
    /// socket and defines what renders into the widget rect.
    /// </summary>
    public static class DisplaySinkNode
    {
        public const string Title    = "Display";
        public const string Category = "Sink";

        public static Node Build()
        {
            return new Node
            {
                Title       = Title,
                Category    = Category,
                Location    = new Point(420, 120),
                Size        = new Size(180, 60),
                HeaderColor = Color.FromArgb(64, 90, 64),
                Sockets     =
                {
                    // Manifesto §4.6 — Image sockets are hot pink (235,80,170).
                    // Earlier builds had this socket painted in the Scalar light
                    // blue, which made the type lie about itself: the data
                    // flowing in is an Image handle, not a number, so the pin
                    // glyph and wire colour both have to read pink for the user
                    // to recognise compatible upstream sockets at a glance.
                    new Socket { Name = "Image", Type = SocketType.Input, DataType = SocketDataType.Image, Color = Color.FromArgb(235, 80, 170) },
                },
            };
        }

        public static bool Is(Node node) =>
            node.Title == Title && string.Equals(node.Category, Category, System.StringComparison.OrdinalIgnoreCase);
    }
}
