using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // P0-3 (sweep 0.10.0 ) — reverse-audit caller.
    //
    // CommandManifest.VerifyAllHubCommandsRegistered (Shared/Core/CommandManifest.cs)
    // answers the reverse-direction question that the existing
    // VerifyCommandManifest in ScriptManager.cs does NOT: "does every command the
    // Hub actually registered via _engine.RegisterCommand have a matching
    // entry in CommandManifest.All?"
    //
    //   * Forward direction (existing VerifyCommandManifest)
    //         manifest.Keys  →  _engine.HasCommand
    //         catches: a descriptor names a command the Hub forgot to wire up
    //
    //   * Reverse direction (THIS file — VerifyHubCommandsAgainstManifest)
    //         _engine.RegisteredCommandNames  →  manifest.ContainsKey
    //         catches: Hub registers a command that has no manifest schema
    //                  (so CommandBinder.BindArgs can never produce a typed
    //                  dict and the script-window introspection lies)
    //
    // Both audits run at the END of RegisterHubCommands so the engine's
    // command surface is fully populated before either is consulted.
    //
    // Logged + thrown (not just logged). A registration drift is a
    // three-way-contract bug per the project conventions — it is the kind of failure that
    // should refuse to boot rather than silently surface at first script-call.
    // VerifyCommandManifest throws on the same severity tier; we match it
    // here for consistency.
    public partial class ScriptManager
    {
        /// <summary>
        /// P0-3 — reverse-direction manifest audit. Pulls the snapshot of
        /// currently-registered command names off the engine ('s
        /// <see cref="ScriptEngine.RegisteredCommandNames"/>) and asks the
        /// manifest to flag any name that has no schema entry. Throws on a
        /// non-empty result so a registration drift fails startup loudly.
        /// </summary>
        private void VerifyHubCommandsAgainstManifest()
        {
            IReadOnlyCollection<string> missing =
                CommandManifest.VerifyAllHubCommandsRegistered(_engine.RegisteredCommandNames);

            if (missing.Count == 0) return;

            var ordered = missing.OrderBy(n => n, StringComparer.Ordinal).ToList();
            string detail = string.Join("\n  ", ordered);
            string message =
                "ScriptManager: the following commands are registered on the engine "
                + "but have no entry in CommandManifest (reverse-audit drift):\n  "
                + detail;

            // GlobalLogger.Error for the recorded-trail; throw for the
            // hard-stop. Mirrors VerifyCommandManifest's posture in
            // ScriptManager.cs — manifest drift is a build-time bug, not
            // something to paper over at startup.
            GlobalLogger.Log(message, "ScriptManager", LogLevel.CriticalError);
            throw new InvalidOperationException(message);
        }
    }
}
