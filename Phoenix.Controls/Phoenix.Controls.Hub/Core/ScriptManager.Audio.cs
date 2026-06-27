using System;
using System.Globalization;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Partial split: audio.* command registrations. Three RegisterCommand
    // bodies (audio.play / audio.play_tts / audio.set_volume) lifted out of
    // RegisterHubCommands so the omnibus registration method stays smaller
    // and per-domain handlers can grow without lengthening the central call.
    // No behavior change — every body is byte-for-byte identical to the
    // pre-split inline version. Wired in via RegisterAudioCommands() called
    // from RegisterHubCommands at the same spot the inline block lived.
    public partial class ScriptManager
    {
        private void RegisterAudioCommands()
        {
            // P3 — audio.play(path, volume?) — fire-and-forget local playback.
            // result.audio_error mirrors file.* contract: empty on success, exception
            // message on failure. Volume is clamped 0..1 and multiplied against
            // AudioService's global base volume (settable via audio.set_volume).
            _engine.RegisterCommand("audio.play", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string path = bound?.GetOrDefault<string>("Path", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                // Volume is registered as ArgType.Float — binder coerces to System.Single.
                double volume = (bound != null && bound.ContainsKey("Volume") ? (double)bound.Get<float>("Volume") : (double?)null)
                    ?? (args.Length >= 2 && double.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.0);
                try
                {
                    await AudioService.PlayAsync(path, volume, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.audio_error", "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.audio_error", ex.Message);
                    GlobalLogger.Log($"audio.play({path}) failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // P3 — audio.play_tts(text, voice?, rate?, volume?) — Windows TTS.
            // Fire-and-forget; new calls do NOT stop prior synthesizers (alerts
            // overlap by design). Unknown voice names log + fall back to default.
            _engine.RegisterCommand("audio.play_tts", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                string text = bound?.GetOrDefault<string>("Text", ArgOrEmpty(args, 0)) ?? ArgOrEmpty(args, 0);
                string voice = bound?.GetOrDefault<string>("Voice", ArgOrEmpty(args, 1)) ?? ArgOrEmpty(args, 1);
                int rate = (bound != null && bound.ContainsKey("Rate"))
                    ? bound.Get<int>("Rate")
                    : (args.Length >= 3 && int.TryParse(args[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r : 0);
                // Volume is ArgType.Float — binder coerces to System.Single.
                double volume = (bound != null && bound.ContainsKey("Volume") ? (double)bound.Get<float>("Volume") : (double?)null)
                    ?? (args.Length >= 4 && double.TryParse(args[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.0);
                try
                {
                    await AudioService.PlayTtsAsync(text, string.IsNullOrWhiteSpace(voice) ? null : voice, rate, volume, _engine.ExecutionToken).ConfigureAwait(false);
                    await _engine.SetScriptVarAsync("result.audio_error", "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.audio_error", ex.Message);
                    GlobalLogger.Log($"audio.play_tts failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });

            // P3 — audio.set_volume(volume) — sets the in-process global multiplier
            // applied on top of each audio.play / audio.play_tts call. No DB write.
            _engine.RegisterCommand("audio.set_volume", async (args) =>
            {
                var bound = _engine.CurrentBoundArgs;
                // Volume is ArgType.Float — binder coerces to System.Single.
                double volume = (bound != null && bound.ContainsKey("Volume") ? (double)bound.Get<float>("Volume") : (double?)null)
                    ?? (args.Length >= 1 && double.TryParse(args[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 1.0);
                try
                {
                    AudioService.SetBaseVolume(volume);
                    await _engine.SetScriptVarAsync("result.audio_error", "");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    await _engine.SetScriptVarAsync("result.audio_error", ex.Message);
                    GlobalLogger.Log($"audio.set_volume failed: {ex.Message}", "Script", LogLevel.CriticalError);
                }
                return null;
            });
        }
    }
}
