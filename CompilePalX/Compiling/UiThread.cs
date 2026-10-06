using System;
using System.Threading;

namespace CompilePalX.Compiling
{
    /// <summary>
    /// The UI thread, for the compile code that has to hand results back to it.
    ///
    /// The compile loop used to reach for WPF's Dispatcher directly - through
    /// <c>MainWindow.ActiveDispatcher</c> - which tied the code that runs compiles to the window that
    /// shows them. This holds a plain <see cref="SynchronizationContext"/> instead, captured once on the
    /// UI thread at startup, so the compile code no longer needs any WPF type to do it. It is the first
    /// step towards that code being usable without the WPF window at all; see docs/native-port.md.
    ///
    /// With nothing captured - in tests - work runs on the calling thread, which is what a
    /// single-threaded caller wants anyway.
    /// </summary>
    internal static class UiThread
    {
        private static SynchronizationContext? context;
        private static int threadId = -1;

        /// <summary>
        /// Records the calling thread as the UI thread, and <paramref name="ui"/> as the way to reach it.
        /// Call once, from that thread, as the application starts.
        /// </summary>
        public static void Capture(SynchronizationContext ui)
        {
            context = ui;
            threadId = Environment.CurrentManagedThreadId;
        }

        /// <summary>Whether there is a UI thread to hand work to, which there is not in tests.</summary>
        public static bool IsCaptured => context is not null;

        private static bool OnUiThread => context is null || Environment.CurrentManagedThreadId == threadId;

        /// <summary>
        /// Runs <paramref name="action"/> on the UI thread and waits for it, as Dispatcher.Invoke did.
        /// Inline when already there: sending to your own thread's context would deadlock some contexts.
        /// </summary>
        public static void Invoke(Action action)
        {
            if (OnUiThread)
                action();
            else
                context!.Send(_ => action(), null);
        }

        /// <summary>Queues <paramref name="action"/> on the UI thread without waiting for it.</summary>
        public static void Post(Action action)
        {
            if (context is null)
                action();
            else
                context.Post(_ => action(), null);
        }
    }
}
