using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace AsyncToolkit
{
    public abstract class FutureScheduler
    {
        public struct FutureSchedulerAwaiter : INotifyCompletion
        {
            private readonly FutureScheduler _scheduler;

            public bool IsCompleted => false;

            public FutureSchedulerAwaiter(FutureScheduler scheduler)
            {
                _scheduler = scheduler;
            }

            public void GetResult()
            { }

            public void OnCompleted(Action continuation)
            {
                _scheduler.ScheduleContinuation(continuation);
            }
        }

        public static FutureScheduler Inline { get; } = new SynchronousScheduler();
        public static FutureScheduler NewThread { get; } = new NewThreadScheduler();
        public static FutureScheduler ThreadPool { get; } = new ThreadPoolScheduler();

        internal abstract void ScheduleContinuation(Action continuation);

        internal virtual void ScheduleCallback(WaitCallback callback, object state)
        {
            ScheduleContinuation(() => callback(state));
        }

        public FutureSchedulerAwaiter GetAwaiter()
        {
            return new FutureSchedulerAwaiter(this);
        }
    }

    public class SynchronousScheduler : FutureScheduler
    {
        internal override void ScheduleContinuation(Action continuation)
        {
            continuation();
        }
    }

    public class ThreadPoolScheduler : FutureScheduler
    {
        internal override void ScheduleContinuation(Action continuation)
        {
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(state => state.Cast<Action>().Invoke(), continuation);
        }

        internal override void ScheduleCallback(WaitCallback callback, object state)
        {
            System.Threading.ThreadPool.UnsafeQueueUserWorkItem(callback, state);
        }
    }

    public class NewThreadScheduler : FutureScheduler
    {
        public string ThreadName { get; }

        public NewThreadScheduler()
            : this("NewThreadScheduler")
        { }

        public NewThreadScheduler(string threadName)
        {
            ThreadName = threadName;
        }

        internal override void ScheduleContinuation(Action continuation)
        {
            Thread t = new Thread(Run);
            t.IsBackground = true;
            t.Name = ThreadName;
            t.Start(continuation);
        }

        private static void Run(object state)
        {
            Action action = (Action)state;
            action();
        }
    }
}
