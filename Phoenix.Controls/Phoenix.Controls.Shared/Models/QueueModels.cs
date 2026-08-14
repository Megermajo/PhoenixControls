namespace Phoenix.Controls.Shared.Models
{
    // The row model of a NAMED queue — the persistent half of the generic Queue.*
    // node band.
    //
    // Two stores sit behind that band, and the split is deliberate:
    //
    //   * The LEGACY unnamed queue (Queue.Push/Pop/Length/Clear with no Name) is a
    //     pipe-delimited string in the Var global._event_queue. Untouched, forever:
    //     live scripts read that var directly, and migrating it would regress them.
    //   * A NAMED queue lives in the OPEN "Queues" user table, one row per entry,
    //     ordered by priority DESC then insertion order. Open (not a system table)
    //     so db.* scripts can read the line the same way they read Counters/Quotes.
    //
    // This model is the in-memory projection of one such row, shared by DB.Queues.cs,
    // NamedQueueService, the User-Management viewer-queue panel and the overlay
    // publish. The viewer queue IS a named queue — the tool's !join is exactly
    // queue.push(login, display, "<its queue name>", weight) — which is what makes
    // Architect-first parity hold by construction rather than by a parallel API.
    public sealed class QueueEntry
    {
        /// <summary>The open table's rowid — stable identity of this entry, and what
        /// every mutation scopes itself by so duplicate entry names in a hand-built
        /// table are never double-mutated.</summary>
        public long RowId { get; set; }

        /// <summary>Which named queue this row belongs to.</summary>
        public string Queue { get; set; } = "";

        /// <summary>The entry's identity — what Queue.Position / Queue.Remove match on
        /// (case-insensitive). For the viewer queue this is the platform login.</summary>
        public string Entry { get; set; } = "";

        /// <summary>Free-form data carried with the entry. For the viewer queue this is
        /// the viewer's display name, so the overlay and the chat replies can print the
        /// spelling the viewer actually uses.</summary>
        public string Payload { get; set; } = "";

        /// <summary>Higher sorts EARLIER. Ties fall back to insertion order (rowid), so
        /// a queue with every priority at 0 is a plain FIFO.</summary>
        public long Priority { get; set; }

        /// <summary>1-based position in the queue as of the read that produced this row.
        /// Not stored — derived by the ordered read, because a position is only ever
        /// true relative to the rest of the line.</summary>
        public int Position { get; set; }
    }
}
