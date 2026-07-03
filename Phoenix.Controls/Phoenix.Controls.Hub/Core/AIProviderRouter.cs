using System;

namespace Phoenix.Controls.Hub.Core
{
    /// <summary>
    /// Resolves an authored AI model string to a provider + outbound model
    /// pair. Lifted to a static helper so the routing contract has a
    /// unit-testable surface.
    /// </summary>
    /// <remarks>
    /// Provider prefixes (case-insensitive):
    /// <list type="bullet">
    ///   <item><description><c>claude-&lt;…&gt;</c> → <see cref="AIProvider.Anthropic"/> (no prefix strip — claude IS the model name). The hyphen is required: every shipped Anthropic model is <c>claude-&lt;family&gt;-…</c>, and the hyphen pins out attacker-crafted strings like <c>claudeai</c> or <c>claude_internal_debug</c> that would otherwise route Anthropic-keyed traffic on a permissive <c>claude…</c> match.</description></item>
    ///   <item><description><c>ollama/&lt;name&gt;</c> → <see cref="AIProvider.Ollama"/>, prefix stripped.</description></item>
    ///   <item><description><c>cerebras/&lt;name&gt;</c> → <see cref="AIProvider.Cerebras"/>, prefix stripped.</description></item>
    ///   <item><description>anything else → <see cref="AIProvider.OpenAI"/>.</description></item>
    /// </list>
    /// The explicit-prefix shape (for non-Anthropic / non-OpenAI providers)
    /// means an authored Model = "llama3" still hits OpenAI (and probably
    /// 404). That's the right failure: scripts can never accidentally
    /// route a confidential prompt to a public API or vice versa just
    /// because someone renamed a model.
    /// </remarks>
    public static class AIProviderRouter
    {
        public static (AIProvider Provider, string OutboundModel) Resolve(string model)
        {
            if (string.IsNullOrEmpty(model))
                return (AIProvider.OpenAI, "");

            // Tighten the Anthropic prefix. Every shipped Claude
            // model name is "claude-…"; requiring the hyphen rejects fuzzy
            // matches like "claudeai" or "claude_attacker" that would
            // otherwise burn the Anthropic key on a misrouted call. Bare
            // "claude" (the family name on its own) is not a valid model
            // identifier in any Anthropic API path, so dropping it is safe.
            if (model.StartsWith("claude-", StringComparison.OrdinalIgnoreCase))
                return (AIProvider.Anthropic, model);

            if (model.StartsWith("ollama/", StringComparison.OrdinalIgnoreCase))
                return (AIProvider.Ollama, model.Substring("ollama/".Length));

            if (model.StartsWith("cerebras/", StringComparison.OrdinalIgnoreCase))
                return (AIProvider.Cerebras, model.Substring("cerebras/".Length));

            return (AIProvider.OpenAI, model);
        }
    }

    public enum AIProvider
    {
        OpenAI,
        Anthropic,
        Ollama,
        Cerebras,
    }
}
