using System.Threading;
using System.Threading.Tasks;

namespace Phoenix.Controls.Hub.Core.Translation
{
    /// <summary>
    /// PassthroughTranslator — default implementation that returns the input text unchanged.
    /// Lets the rest of the CC pipeline run without configuring a real translator. Users wire a
    /// real backend (DeepL, Google, Azure) by switching <c>AppConfig.TranslationProvider</c> to
    /// "http" and pointing <c>TranslationHttpEndpoint</c> at their service.
    /// </summary>
    public sealed class PassthroughTranslator : ITranslator
    {
        public string Name => "passthrough";

        public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
            => Task.FromResult(text ?? "");
    }
}
