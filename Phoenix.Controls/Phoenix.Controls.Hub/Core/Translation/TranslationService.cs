using System;
using System.Threading;
using System.Threading.Tasks;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core.Translation
{
    /// <summary>
    /// TranslationService — singleton wrapper that resolves the active <see cref="ITranslator"/>
    /// from <c>AppConfig.TranslationProvider</c>. Defaults to <see cref="PassthroughTranslator"/>
    /// so the CC pipeline always has a working translator, even when nothing is configured.
    ///
    /// Tests inject a fake by setting <see cref="Active"/> directly.
    /// </summary>
    public sealed class TranslationService
    {
        private static TranslationService? _instance;
        public static TranslationService Instance => _instance ??= new TranslationService();

        private ITranslator _active;
        private readonly object _lock = new();

        /// <summary>Currently active translator. Tests can swap this directly.</summary>
        public ITranslator Active
        {
            get { lock (_lock) return _active; }
            set
            {
                // The previous translator was dropped without
                // disposal — HttpTranslator owns a per-instance HttpClient + SemaphoreSlim,
                // so every assignment/Reload leaked them. Capture the old reference under
                // the lock, swap, then dispose outside the lock (Dispose may block briefly
                // cancelling in-flight requests; holding _lock there would stall readers).
                var next = value ?? new PassthroughTranslator();
                ITranslator old;
                lock (_lock)
                {
                    old = _active;
                    _active = next;
                }
                if (!ReferenceEquals(old, next))
                    (old as IDisposable)?.Dispose();
            }
        }

        public TranslationService()
        {
            _active = ResolveFromConfig();
        }

        /// <summary>
        /// Re-reads <c>AppConfig.Current.TranslationProvider</c> and rebuilds the active translator.
        /// Call after settings changes; safe to call any time.
        /// </summary>
        public void Reload()
        {
            var next = ResolveFromConfig();
            // Dispose the outgoing translator (HttpTranslator
            // holds an HttpClient + SemaphoreSlim) instead of leaking it. Swap under the
            // lock, dispose outside it.
            ITranslator old;
            lock (_lock)
            {
                old = _active;
                _active = next;
            }
            if (!ReferenceEquals(old, next))
                (old as IDisposable)?.Dispose();
            GlobalLogger.Log($"TranslationService: active translator = '{next.Name}'",
                "Translation", LogLevel.System);
        }

        public Task<string> TranslateAsync(string text, string targetLanguage, CancellationToken ct = default)
        {
            ITranslator t;
            lock (_lock) t = _active;
            return t.TranslateAsync(text, targetLanguage, ct);
        }

        private static ITranslator ResolveFromConfig()
        {
            var cfg = ConfigManager.Current ?? new AppConfig();
            string name = (cfg.TranslationProvider ?? "passthrough").Trim().ToLowerInvariant();
            return name switch
            {
                "http" => new HttpTranslator(cfg.TranslationHttpEndpoint ?? "", cfg.TranslationApiKey ?? ""),
                _      => new PassthroughTranslator(),
            };
        }
    }
}
