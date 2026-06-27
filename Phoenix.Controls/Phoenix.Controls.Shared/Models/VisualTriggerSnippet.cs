namespace Phoenix.Controls.Shared.Models
{
    /// <summary>
    /// Cross-pillar clipboard payload for "I copied a Visualist trigger
    /// reference; paste it in Architect as a Visual.Trigger node".
    ///
    /// The Visualist's TriggerInspectorPanel writes both plain text
    /// (<c>visual.trigger("layer", "widget", "name", {})</c>) AND this
    /// payload under <see cref="ClipboardFormat"/>. Architect's
    /// Canvas.PasteSelection detects the payload first and spawns a
    /// fully-attributed Visual.Trigger node at the paste position; if the
    /// payload is absent the canvas falls through to its existing sub-graph
    /// paste path, and a paste into a text editor still gets the readable
    /// snippet from the plain-text channel.
    /// </summary>
    public sealed record VisualTriggerSnippet(
        string LayerID,
        string WidgetID,
        string TriggerName,
        bool   Queued)
    {
        public const string ClipboardFormat = "PhoenixControls.VisualTriggerSnippet";
    }
}
