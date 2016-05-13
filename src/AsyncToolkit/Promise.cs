using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace AsyncToolkit
{
    public abstract class Promise
    {
        protected static readonly object SucceededState = new object();

        public abstract bool IsCompleted { get; }

        public abstract bool IsCanceled { get; }

        public abstract bool IsSucceeded { get; }

        public abstract bool IsFailed { get; }

        public abstract Exception Exception { get; }

        /// <summary>
        /// Warning: The ExecutionContext is not captured by this method.
        /// To flow the ExecutionContext, you must 'await promise.Future'.
        /// </summary>
        /// <param name="continuation">An action to invoke when the promise is completed, this delegate must not throw an exception.</param>
        /// <param name="scheduler">The FutureScheduler used to invoke the continuation, null will call it synchronously.</param>
        internal abstract void ContinueWith(Action continuation, FutureScheduler scheduler = null);

        public abstract void ThrowIfFailed();

        public static Promise<T> FromValue<T>(T value)
        {
            var promise = new Promise<T>();
            promise.SetValue(value);
            return promise;
        }

        public static Promise<T> FromException<T>(T value)
        {
            var promise = new Promise<T>();
            promise.SetValue(value);
            return promise;
        }

        public static Promise<T> FromCanceled<T>()
        {
            var promise = new Promise<T>();
            promise.SetCanceled();
            return promise;
        }
    }

    public class Promise<T> : Promise
    {
        private class ContinuationList
        {
            private const int MaximumRecursion = 12;

            private readonly object _continuation;
            private readonly ContinuationList _previous;
            private readonly int _count;

            public ContinuationList(object continuation, ContinuationList previous = null)
            {
                _continuation = continuation;
                _previous = previous;

                if (previous == null)
                    _count = 1;
                else
                    _count = previous._count + 1;
            }

            [Pure]
            public ContinuationList Add(object continuation)
            {
                return new ContinuationList(continuation, this);
            }

            public void InvokeContinuations()
            {
                if (_count < MaximumRecursion)
                {
                    RecursiveInvokeContinuations();
                }
                else
                {
                    var continuations = ToArray();

                    for (int i = 0; i < continuations.Length; i++)
                        InvokeContinuation(continuations[i]);
                }
            }

            private object[] ToArray()
            {
                List<object> list = new List<object>(_count);

                var current = this;

                do
                {
                    list.Add(current._continuation);
                    current = current._previous;
                }
                while (current._previous != null);

                list.Reverse();
                return list.ToArray();
            }

            private void RecursiveInvokeContinuations()
            {
                _previous?.RecursiveInvokeContinuations();
                InvokeContinuation(_continuation);
            }

            private static void InvokeContinuation(object continuationObject)
            {
                Action continuation = continuationObject as Action;
                if (continuation != null)
                {
                    continuation();
                }
                else
                {
                    var scheduledContinuation = (ScheduledContinuation)continuationObject;
                    scheduledContinuation.Invoke();
                }
            }
        }

        private class ScheduledContinuation
        {
            private readonly Action _continuation;

            public FutureScheduler Scheduler { get; }

            public ScheduledContinuation(Action continuation, FutureScheduler scheduler)
            {
                _continuation = continuation;
                Scheduler = scheduler;
            }

            public void Invoke()
            {
                Scheduler.ScheduleContinuation(_continuation);
            }
        }

        private T _unsafeValue;
        private object _unsafeState;

        public override bool IsCompleted
        {
            get
            {
                object state = GetCurrentState();
                return IsCompletedState(state);
            }
        }

        public override bool IsCanceled
        {
            get
            {
                object state = GetCurrentState();
                ThrowIfNotCompleted(state);
                return IsCanceledState(state);
            }
        }

        public override bool IsSucceeded
        {
            get
            {
                object state = GetCurrentState();
                ThrowIfNotCompleted(state);
                return IsSucceededState(state);
            }
        }

        public override bool IsFailed
        {
            get
            {
                object state = GetCurrentState();
                ThrowIfNotCompleted(state);
                return IsFailedState(state);
            }
        }

        public override Exception Exception
        {
            get
            {
                object state = GetCurrentState();
                ThrowIfNotCompleted(state);

                if (IsCanceledState(state))
                    return (OperationCanceledException)state;

                ExceptionDispatchInfo exception = state as ExceptionDispatchInfo;
                return exception?.SourceException;
            }
        }

        public T Value
        {
            get
            {
                object state = GetCurrentState();
                ThrowIfNotCompleted(state);
                ThrowIfFailed(state);

                Thread.MemoryBarrier();
                return _unsafeValue;
            }
        }

        public Future<T> Future => new Future<T>(this);

        public Future<T> GetAwaiter()
        {
            return new Future<T>(this);
        }

        public bool SetValue(T value, FutureScheduler scheduler = null)
        {
            // Setting the value before changing the state to completed
            _unsafeValue = value;

            // Explicit MemoryBarrier to force _value to be visible by other cores before updating the state to Succeeded.
            Thread.MemoryBarrier();

            return TryCompletePromise(SucceededState, scheduler);
        }

        public bool SetCanceled(FutureScheduler scheduler = null)
        {
            return TryCompletePromise(new OperationCanceledException(), scheduler);
        }

        public bool SetException(Exception exception, FutureScheduler scheduler = null)
        {
            return TryCompletePromise(ExceptionDispatchInfo.Capture(exception), scheduler);
        }

        public override void ThrowIfFailed()
        {
            object state = GetCurrentState();

            ThrowIfNotCompleted(state);
            ThrowIfFailed(state);
        }

        /// <summary>
        /// Warning: The ExecutionContext is not captured by this method.
        /// To flow the ExecutionContext, you must 'await promise.Future'.
        /// </summary>
        /// <param name="continuation">An action to invoke when the promise is completed, this delegate must not throw an exception.</param>
        /// <param name="scheduler">The FutureScheduler used to invoke the continuation, null will call it synchronously.</param>
        internal override void ContinueWith(Action continuation, FutureScheduler scheduler = null)
        {
            if (scheduler is SynchronousScheduler)
                scheduler = null;

            while (true)
            {
                object state = GetCurrentState();

                if (IsCompletedState(state))
                {
                    // This will be true if the promise is completed after 'IsCompleted' is checked but before
                    // 'ContinueWith' is called.
                    //
                    // It may also be completed if 'synchronousIfCompleted' was set to false
                    // in Future<T>.ContinueOn.

                    if (scheduler != null)
                        scheduler.ScheduleContinuation(continuation);
                    else
                        continuation();
                }

                object newState = AddContinuation(state, continuation, scheduler);

                if (TryUpdateState(state, newState))
                    break;
            }
        }

        private static void InvokeContinuations(object continuations, FutureScheduler scheduler)
        {
            if (continuations == null)
                return;

            if (scheduler != null)
            {
                // Don't schedule twice if the scheduler is the same.
                var singleScheduledContinuation = continuations as ScheduledContinuation;
                if (singleScheduledContinuation != null && singleScheduledContinuation.Scheduler.Equals(scheduler))
                {
                    singleScheduledContinuation.Invoke();
                    return;
                }

                scheduler.ScheduleCallback(InternalInvokeContinuations, continuations);
                return;
            }

            InternalInvokeContinuations(continuations);
        }

        private static readonly WaitCallback InternalInvokeContinuations = (object continuations) =>
        {
            Action singleContinuation = continuations as Action;
            if (singleContinuation != null)
            {
                singleContinuation();
                return;
            }

            var singleScheduledContinuation = continuations as ScheduledContinuation;
            if (singleScheduledContinuation != null)
            {
                singleScheduledContinuation.Invoke();
                return;
            }

            ContinuationList continuationList = (ContinuationList)continuations;
            continuationList.InvokeContinuations();
        };

        private static object AddContinuation(object existingContinuations, Action newContinuation, FutureScheduler scheduler)
        {
            // The existing continuations can be in one of these form:
            // - Null if there was no registered continuations
            // - Action
            // - ScheduledContinuation
            // - ContinuationList where each item is either an Action or a SchedulerContinuation

            object continuationObject;

            if (scheduler == null)
                continuationObject = newContinuation;
            else
                continuationObject = new ScheduledContinuation(newContinuation, scheduler);

            // If _continuations was null, just assign it with the new continuation
            if (existingContinuations == null)
                return continuationObject;

            // If _continuations was already a List, add the new continuation

            ContinuationList continuationList = existingContinuations as ContinuationList;
            if (continuationList != null)
                return continuationList.Add(continuationObject);

            // If we are here, _continuations was assigned to a single continuation,
            // we reassign _continuations with a list containing the old and the new continuations

            continuationList = new ContinuationList(existingContinuations).Add(continuationObject);

            return continuationList;
        }

        private object GetCurrentState()
        {
            return Volatile.Read(ref _unsafeState);
        }

        private bool TryUpdateState(object expectedOldState, object newState)
        {
            var originalState = Interlocked.CompareExchange(ref _unsafeState, newState, expectedOldState);

            return ReferenceEquals(originalState, expectedOldState);
        }

        private bool TryCompletePromise(object completedState, FutureScheduler scheduler)
        {
            object continuations;
            bool updated = false;

            while (true)
            {
                object currentState = GetCurrentState();

                if (IsCompletedState(currentState))
                    return false;

                continuations = currentState;

                // Thread.Abort protection, if the state is set to completed, the continuations MUST be invoked
                try
                { }
                finally
                {
                    if (TryUpdateState(currentState, completedState))
                    {
                        InvokeContinuations(continuations, scheduler);
                        updated = true;
                    }
                }

                if (updated)
                    return true;
            }
        }

        private static bool IsCompletedState(object state)
        {
            return IsSucceededState(state) || IsCanceledState(state) || IsFailedState(state);
        }

        private static bool IsSucceededState(object state)
        {
            return ReferenceEquals(SucceededState, state);
        }

        private static bool IsCanceledState(object state)
        {
            return state is OperationCanceledException;
        }

        private static bool IsFailedState(object state)
        {
            return state is ExceptionDispatchInfo;
        }

        private static void ThrowIfNotCompleted(object state)
        {
            if (!IsCompletedState(state))
                throw new InvalidOperationException("The Future is not completed.");
        }

        private void ThrowIfFailed(object state)
        {
            var canceledException = state as OperationCanceledException;
            if (canceledException != null)
                throw canceledException;

            ExceptionDispatchInfo exception = state as ExceptionDispatchInfo;
            exception?.Throw();
        }
    }
}
