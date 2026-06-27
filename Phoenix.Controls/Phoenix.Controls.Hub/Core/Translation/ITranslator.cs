using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.Core.Translation
{
    /// <summary>
    /// ITranslator — pluggable translation backend. Implementations are selected by
    /// <c>AppConfig.TranslationProvider</c> via <see cref="TranslationService"/>.
    /// Implementations should be thread-safe and short-circuit when target language is empty.
    /// </summary>
    public interface ITranslator
    {
        /// <summary>Stable identifier matching <c>AppConfig.TranslationProvider</c> (e.g. "passthrough", "http").</summary>
        string Name { get; }

        /// <summary>
        /// Translates <paramref name="text"/> into <paramref name="targetLanguage"/> (BCP-47).
        /// When <paramref name="targetLanguage"/> is empty or equals the detected source, returns
        /// the input unchanged. Implementations should never throw on transient failure — return
        /// the original text and log instead.
        /// </summary>
        Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default);
    }
}
