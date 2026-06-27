using System;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Abstract caption-text source. Production: <see cref="LiveCaptionService"/> reads from
    /// Windows 11's built-in LiveCaptions via UI Automation. Tests: drive captions in
    /// directly through a fake implementation. Consumers (Hub) only depend on
    /// this interface so the UIA hook can be skipped in unit tests.
    /// </summary>
    public interface ICaptionSource
    {
        /// <summary>Raised when the caption text changes (debounced upstream of the event).</summary>
        event Action<string>? CaptionChanged;

        /// <summary>Most recently observed caption text (empty if none yet).</summary>
        string LatestCaption { get; }

        /// <summary>Begins watching for caption changes. Idempotent.</summary>
        void Start();

        /// <summary>Stops watching and releases UIA / OS resources. Idempotent.</summary>
        void Stop();
    }
}
