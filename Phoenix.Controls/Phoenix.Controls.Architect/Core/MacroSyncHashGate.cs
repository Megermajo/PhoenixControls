using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Phoenix.Controls.Shared.Models;

namespace Phoenix.Controls.Architect.Core
{
    /// <summary>
    /// Sweep 18 (Macro audit P1 #2 — perf-only) — gates Architect's
    /// <c>MainView.OnMacroSync</c> cascade-refresh on a per-macro signature hash.
    ///
    /// Background: every Hub-broadcast <c>MACRO_SYNC</c> call previously walked
    /// every Macro.Call node in the open document via
    /// <c>Canvas.RefreshGlobalMacro → RefreshAllCallNodesForMacro →
    /// RefreshMacroCallSockets</c>. Sweep 11 made the inner refresh diff-based and
    /// idempotent, so it's correct to call on an unchanged macro — just wasted
    /// CPU. The gate skips the refresh when the macro's
    /// <see cref="BuildSignature">signature</see> hash matches the last applied
    /// hash for that <c>MacroId</c>.
    ///
    /// Signature inputs (all that <c>RefreshMacroCallSockets</c> reads):
    /// <list type="bullet">
    ///   <item><c>macro.Name</c> (written to the Macro.Call node's <c>MacroName</c> attribute).</item>
    ///   <item>Macro.Entry's output sockets (Name + DataType, declaration order, excluding placeholders and the Flow socket).</item>
    ///   <item>Macro.Exit's input sockets (Name + DataType, declaration order, excluding placeholders and the Flow socket).</item>
    /// </list>
    /// Internal node positions, IDs, colors, and any non-Entry/Exit nodes are
    /// explicitly excluded — they do NOT change the Call-node layout, so they
    /// must NOT trigger a refresh.
    ///
    /// Hash function: 64-bit FNV-1a. Chosen over SHA256 because we're detecting
    /// accidental signature drift (not adversarial), and the BCL doesn't ship
    /// xxHash. The output is 8 bytes — way more than enough entropy for the
    /// cardinality of macro signatures in one Architect session.
    ///
    /// First-sync semantics: when <c>hashCache</c> has no entry for the macro's
    /// <c>MacroId</c>, the gate ALWAYS applies (and seeds the cache). This is
    /// the correct behavior for a fresh session or a newly-published macro.
    ///
    /// Removal semantics: callers should evict entries from <c>hashCache</c> for
    /// any macro IDs returned by <c>MacroOps.ApplyCanonicalGlobalMacros</c>'
    /// removed list. Otherwise a re-add of the same MacroId would silently skip
    /// because of the stale cache hit.
    /// </summary>
    public static class MacroSyncHashGate
    {
        /// <summary>
        /// Walks <paramref name="incoming"/> and invokes <paramref name="apply"/>
        /// only for macros whose <see cref="ComputeHash">signature hash</see>
        /// differs from <paramref name="hashCache"/>'s last-applied entry (or is
        /// missing — first-sync). Updates <paramref name="hashCache"/> with the
        /// new hash for every macro <paramref name="apply"/> ran on.
        /// </summary>
        /// <returns>The number of macros that were actually refreshed.</returns>
        public static int Apply(
            IEnumerable<Macro> incoming,
            IDictionary<string, ulong> hashCache,
            Action<Macro> apply)
        {
            if (incoming is null) throw new ArgumentNullException(nameof(incoming));
            if (hashCache is null) throw new ArgumentNullException(nameof(hashCache));
            if (apply is null) throw new ArgumentNullException(nameof(apply));

            int refreshed = 0;
            foreach (var m in incoming)
            {
                if (m is null || string.IsNullOrEmpty(m.MacroId)) continue;
                ulong hash = ComputeHash(m);
                if (hashCache.TryGetValue(m.MacroId, out var prev) && prev == hash)
                    continue;  // signature unchanged — skip the cascade.
                apply(m);
                hashCache[m.MacroId] = hash;
                refreshed++;
            }
            return refreshed;
        }

        /// <summary>
        /// Builds a deterministic signature string for the macro and 64-bit FNV-1a hashes it.
        /// </summary>
        public static ulong ComputeHash(Macro macro)
            => Fnv1a64(BuildSignature(macro));

        /// <summary>
        /// Deterministic signature string. Public for diagnostic / test inspection.
        /// </summary>
        public static string BuildSignature(Macro macro)
        {
            if (macro is null) return string.Empty;

            var sb = new StringBuilder();
            sb.Append(macro.Name ?? string.Empty);
            sb.Append('\x1f');
            sb.Append("ENTRY:");

            var entry = macro.Graph?.Nodes?.FirstOrDefault(n => n.Title == "Macro.Entry");
            if (entry?.Sockets != null)
            {
                foreach (var s in entry.Sockets)
                {
                    if (s.Type != SocketType.Output) continue;
                    if (s.IsPlaceholder) continue;
                    if (s.Name == "Flow") continue;
                    sb.Append(s.Name ?? string.Empty);
                    sb.Append('\x1e');
                    sb.Append(s.DataType);
                    sb.Append('\x1f');
                }
            }

            sb.Append("EXIT:");
            var exit = macro.Graph?.Nodes?.FirstOrDefault(n => n.Title == "Macro.Exit");
            if (exit?.Sockets != null)
            {
                foreach (var s in exit.Sockets)
                {
                    if (s.Type != SocketType.Input) continue;
                    if (s.IsPlaceholder) continue;
                    if (s.Name == "Flow") continue;
                    sb.Append(s.Name ?? string.Empty);
                    sb.Append('\x1e');
                    sb.Append(s.DataType);
                    sb.Append('\x1f');
                }
            }

            return sb.ToString();
        }

        // FNV-1a 64-bit. Reference: https://en.wikipedia.org/wiki/Fowler%E2%80%93Noll%E2%80%93Vo_hash_function
        //
        // PERF (perf/architect-blockers, HIGH): pre-cache, every call allocated
        // a fresh byte[] via Encoding.UTF8.GetBytes(s). MACRO_SYNC fires per
        // macro publish + per cross-instance broadcast — with 50 macros × ~500
        // bytes of signature = ~25 KB of per-broadcast garbage. Switch to
        // Encoding.UTF8.GetByteCount + GetBytes(span, span) into a stack-
        // allocated buffer for the common short-signature case, with a heap
        // fallback for outliers. Net allocation drops to zero on the common
        // path.
        private const ulong Fnv64Offset = 14695981039346656037UL;
        private const ulong Fnv64Prime  = 1099511628211UL;
        private const int StackBufferSize = 512;

        private static ulong Fnv1a64(string s)
        {
            if (string.IsNullOrEmpty(s)) return Fnv64Offset;

            int byteCount = Encoding.UTF8.GetByteCount(s);
            Span<byte> buffer = byteCount <= StackBufferSize
                ? stackalloc byte[StackBufferSize]
                : new byte[byteCount];
            int written = Encoding.UTF8.GetBytes(s, buffer);

            ulong h = Fnv64Offset;
            for (int i = 0; i < written; i++)
            {
                h ^= buffer[i];
                h *= Fnv64Prime;
            }
            return h;
        }
    }
}
