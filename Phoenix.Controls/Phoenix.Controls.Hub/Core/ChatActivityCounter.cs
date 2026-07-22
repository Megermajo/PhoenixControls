using System.Threading;
namespace Phoenix.Controls.Hub.Core
{
    /// <summary>Process-wide monotonic count of inbound (non-bot) chat messages since Hub
    /// start. Backs the Chat.MessageCount value node — a streamer stores it in a State/Var
    /// and compares deltas to build chat-activity gates (e.g. "only if >=N new messages").
    /// Monotonic since Hub launch (delta-based use is the intent); not reset per stream.</summary>
    public static class ChatActivityCounter
    {
        private static long _count;
        public static void Increment() => Interlocked.Increment(ref _count);
        public static long Current => Interlocked.Read(ref _count);
    }
}
