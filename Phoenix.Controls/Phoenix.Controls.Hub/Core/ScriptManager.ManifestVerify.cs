using System;
using System.Collections.Generic;
using System.Linq;
using Phoenix.Controls.Shared.Core;
using Phoenix.Controls.Shared.Models;
using Phoenix.Controls.Shared.Services;

namespace Phoenix.Controls.Hub.Core
{
    // Reverse-audit caller.
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
        /// Reverse-direction manifest audit. Pulls the snapshot of
        /// currently-registered command names off the engine (via
        /// <see cref="ScriptEngine.RegisteredCommandNames"/>) and asks the
        /// manifest to flag any name that has no schema entry. Throws on a
        /// non-empty result so a registration drift fails startup loudly.
        /// </summary>
        private void VerifyHubCommandsAgainstManifest()
        {
            IReadOnlyCollection<string> missing =
                CommandManifest.VerifyAllHubCommandsRegistered(_engine.RegisteredCommandNames);

            // The 2026-08 tool-node cut's 22 retired shims are engine-registered but
            // deliberately have NO manifest entry — the manifest describes the LIVE
            // surface only. They are exempted by the shim file's own ledger rather
            // than by name here, so the two can never drift apart; every name outside
            // that ledger still hard-stops the boot. (The five V4 overlay shims kept
            // their manifest entries instead, so they never reach this filter.)
            var drift = missing.Where(n => !RetiredToolCommandNames.Contains(n)).ToList();
            if (drift.Count == 0) return;

            var ordered = drift.OrderBy(n => n, StringComparer.Ordinal).ToList();
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
