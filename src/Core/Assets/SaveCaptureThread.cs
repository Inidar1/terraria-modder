using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Terraria;

namespace TerrariaModder.Core.Assets
{
    // Save capture runs between game updates. Native file I/O stays on its caller.
    // Call before taking Player.IOLock; never dispatch from inside native save I/O.
    internal static class SaveCaptureThread
    {
        private static int _threadId;
        internal static int ThreadId => Volatile.Read(ref _threadId);

        internal static void ObserveUpdateThread()
        {
            int current = Thread.CurrentThread.ManagedThreadId;
            int owner = Interlocked.CompareExchange(ref _threadId, current, 0);
            if (owner != 0 && owner != current)
                throw new InvalidOperationException("Terraria update thread changed during save capture lifetime");
        }

        internal static T Capture<T>(Func<T> capture)
        {
            if (capture == null) throw new ArgumentNullException(nameof(capture));
            int owner = ThreadId;
            if (owner == 0)
                throw new IOException("Cannot capture player save before Terraria's first update");
            if (owner == Thread.CurrentThread.ManagedThreadId) return capture();

            var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Main.QueueMainThreadAction(() =>
            {
                try { completion.TrySetResult(capture()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task.GetAwaiter().GetResult();
        }
    }
}
