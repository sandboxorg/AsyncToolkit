using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace AsyncToolkit
{
    public abstract class Promise
    {
        public abstract bool IsCompleted { get; }

        public abstract bool IsCanceled { get; }

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
        private class ScheduledContinuation
        {
            private readonly Action _continuation;
            private readonly FutureScheduler _scheduler;

            public ScheduledContinuation(Action continuation, FutureScheduler scheduler)
            {
                _continuation = continuation;
                _scheduler = scheduler;
            }

            public void Invoke()
            {
                _scheduler.ScheduleContinuation(_continuation);
            }
        }

        // This field is polymorphic to reduce the size of the Promise class.
        // It contains continuations callback to invoke when the promise is completed.
        // It can be in three form:
        // - Action
        // - ScheduledContinuation
        // - List<object> where each item is either an Action or a SchedulerContinuation
        //
        // We could keep the continuation list in a raw array to save one allocation,
        // but the hot path should be the single continuation case, revise if needed.
        //
        // This field must always be accessed through the instance lock.
        private object _continuations;

        private T _value;
        private volatile ExceptionDispatchInfo _exception;
        private volatile bool _isCanceled;
        private volatile bool _isCompleted;
        
        public override bool IsCompleted => _isCompleted;

        public override bool IsCanceled => _isCanceled;

        public override Exception Exception
        {
            get
            {
                lock (this)
                {
                    if (!_isCompleted)
                        throw new InvalidOperationException("The Future is not completed.");

                    return _exception.SourceException;
                }
            }
        }

        public T Value
        {
            get
            {
                ExceptionDispatchInfo exception;
                T value;
                lock (this)
                {
                    if (!_isCompleted)
                        throw new InvalidOperationException("The Future is not completed.");

                    exception = _exception;
                    value = _value;
                }

                exception?.Throw();
                return value;
            }
        }

        public Future<T> Future => new Future<T>(this);

        public bool SetValue(T value, FutureScheduler scheduler = null)
        {
            object continuations;
            lock (this)
            {
                if (_isCompleted)
                    return false;

                _value = value;
                _isCompleted = true;
                continuations = _continuations;
                _continuations = null;
            }

            InvokeContinuations(continuations, scheduler);
            return true;
        }

        public bool SetCanceled(FutureScheduler scheduler = null)
        {
            object continuations;
            lock (this)
            {
                if (_isCompleted)
                    return false;

                _isCanceled = true;
                _exception = ExceptionDispatchInfo.Capture(new OperationCanceledException());
                _isCompleted = true;
                continuations = _continuations;
                _continuations = null;
            }

            InvokeContinuations(continuations, scheduler);
            return true;
        }

        public bool SetException(Exception exception, FutureScheduler scheduler = null)
        {
            object continuations;
            lock (this)
            {
                if (_isCompleted)
                    return false;

                _exception = ExceptionDispatchInfo.Capture(exception);
                _isCompleted = true;
                continuations = _continuations;
                _continuations = null;
            }

            InvokeContinuations(continuations, scheduler);
            return true;
        }

        public override void ThrowIfFailed()
        {
            lock (this)
            {
                if (!_isCompleted)
                    throw new InvalidOperationException("The Future is not completed.");

                _exception?.Throw();
            }
        }

        private static void InvokeContinuations(object continuations, FutureScheduler scheduler)
        {
            if (continuations == null)
                return;

            if (scheduler != null)
            {
                scheduler.ScheduleCallback(InternalInvokeContinuations, continuations);
                return;
            }

            InternalInvokeContinuations(continuations);
        }

        private static readonly WaitCallback InternalInvokeContinuations = continuations =>
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

            List<object> continuationList = (List<object>) continuations;
            foreach (object continuationObject in continuationList)
            {
                Action continuation = continuationObject as Action;
                if (continuation != null)
                {
                    continuation();
                }
                else
                {
                    var scheduledContinuation = (ScheduledContinuation) continuationObject;
                    scheduledContinuation.Invoke();
                }
            }
        };

        /// <summary>
        /// Must be called inside the lock.
        /// </summary>
        private void AddContinuation(Action continuation, FutureScheduler scheduler)
        {
            object continuationObject;

            if (scheduler == null)
                continuationObject = continuation;
            else
                continuationObject = new ScheduledContinuation(continuation, scheduler);

            // If _continuations was null, just assign it with the new continuation

            object continuations = _continuations;

            if (continuations == null)
            {
                _continuations = continuationObject;
                return;
            }

            // If _continuations was already a List, add the new continuation

            List<object> continuationList = continuations as List<object>;
            if (continuationList != null)
            {
                continuationList.Add(continuationObject);
                return;
            }

            // If we are here, _continuations was assigned to a single continuation,
            // we reassign _continuations with a list containing the old and the new continuations

            continuationList = new List<object>();
            continuationList.Add(continuations);
            continuationList.Add(continuationObject);
            _continuations = continuationList;
        }

        public Future<T> GetAwaiter()
        {
            return new Future<T>(this);
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

            bool alreadyCompleted = false;
            lock (this)
            {
                if (_isCompleted)
                {
                    alreadyCompleted = true;
                }
                else
                {
                    AddContinuation(continuation, scheduler);
                }
            }

            // 'alreadyComplete' will most likely false, it will be true if
            // the promise is completed after 'IsCompleted' is checked but before
            // 'ContinueWith' is called.
            //
            // It may also be completed if 'synchronousIfCompleted' was set to false
            // in Future<T>.ContinueOn.
            if (alreadyCompleted)
            {
                if (scheduler != null)
                {
                    scheduler.ScheduleContinuation(continuation);
                }
                else
                {
                    continuation();
                }
            }
        }
    }
}