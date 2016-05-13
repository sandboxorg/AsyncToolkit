using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace AsyncToolkit
{
    public interface IFuture
    {
        bool IsCompleted { get; }
        bool IsCanceled { get; }
        bool IsSucceeded { get; }
        bool IsFailed { get; }
        Exception Exception { get; }
        void ThrowIfFailed();

        Future ToFuture();
        Future<T> ToFuture<T>();
    }

    [StructLayout(LayoutKind.Auto)]
    public struct Future<T> : IEquatable<Future<T>>, IFuture
    {
        #region Nested Types

        public struct FutureAwaiter : ICriticalNotifyCompletion
        {
            private Future<T> _future;

            public bool IsCompleted => _future.IsCompleted;

            public FutureAwaiter(Future<T> future)
            {
                _future = future;
            }

            public T GetResult() => _future.Value;

            public void OnCompleted(Action continuation)
            {
                //TODO Capture ExecutionContext
                UnsafeOnCompleted(continuation);
            }

            public void UnsafeOnCompleted(Action continuation)
            {
                var promise = _future.GetPromise();

                if (promise != null)
                {
                    promise.ContinueWith(continuation);
                    return;
                }

                // If promise is null, it means the future was created from a result,
                // invoke the continuation immediately
                continuation();
            }
        }

        [SuppressMessage("ReSharper", "ImpureMethodCallOnReadonlyValueField", Justification = "The calls do not alter the readonly field")]
        public struct ScheduledFutureAwaiter : ICriticalNotifyCompletion
        {
            private readonly Future<T> _future;
            private readonly FutureScheduler _scheduler;
            private readonly bool _synchronousIfCompleted;

            public bool IsCompleted => _synchronousIfCompleted && _future.IsCompleted;

            public ScheduledFutureAwaiter(Future<T> future, FutureScheduler scheduler, bool synchronousIfCompleted)
            {
                _future = future;
                _scheduler = scheduler;
                _synchronousIfCompleted = synchronousIfCompleted;
            }

            public T GetResult() => _future.Value;

            public void OnCompleted(Action continuation)
            {
                //TODO Capture ExecutionContext
                UnsafeOnCompleted(continuation);
            }

            public void UnsafeOnCompleted(Action continuation)
            {
                var promise = _future.GetPromise();

                if (promise != null)
                {
                    promise.ContinueWith(continuation, _scheduler);
                    return;
                }

                // If promise is null, it means the future was created from a result,
                // invoke the continuation immediately

                if (_scheduler != null)
                {
                    _scheduler.ScheduleContinuation(continuation);
                    return;
                }

                continuation();
            }
        }

        public struct ScheduledFutureAwaitable
        {
            private readonly Future<T> _future;
            private readonly FutureScheduler _scheduler;
            private readonly bool _synchronousIfCompleted;

            public ScheduledFutureAwaitable(Future<T> future, FutureScheduler scheduler, bool synchronousIfCompleted)
            {
                _future = future;
                _scheduler = scheduler;
                _synchronousIfCompleted = synchronousIfCompleted;
            }

            public ScheduledFutureAwaiter GetAwaiter()
            {
                return new ScheduledFutureAwaiter(_future, _scheduler, _synchronousIfCompleted);
            }
        }

        #endregion

        private readonly Promise<T> _promise;
        private readonly T _value;

        public bool IsCompleted => _promise == null || _promise.IsCompleted;

        public bool IsCanceled => _promise != null && _promise.IsCanceled;

        public bool IsSucceeded => _promise == null || _promise.IsSucceeded;

        public bool IsFailed => _promise != null && _promise.IsFailed;

        public T Value
        {
            get
            {
                if (_promise != null)
                    return _promise.Value;

                return _value;
            }
        }

        public Exception Exception => _promise?.Exception;

        internal Future(T value)
        {
            _promise = null;
            _value = value;
        }

        internal Future(Promise<T> promise)
        {
            if (promise == null)
                throw new ArgumentNullException(nameof(promise));

            _promise = promise;
            _value = default(T);
        }

        [Pure]
        internal Promise<T> GetPromise()
        {
            return _promise;
        }

        public FutureAwaiter GetAwaiter()
        {
            return new FutureAwaiter(this);
        }

        /// <summary>
        /// Continue the execution on the specified scheduler after an await
        /// </summary>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="synchronousIfCompleted">
        /// Allow the continuation to bypass the scheduler and be called synchronously if the promise is already completed.
        /// </param>
        public ScheduledFutureAwaitable ContinueOn(FutureScheduler scheduler, bool synchronousIfCompleted = true)
        {
            return new ScheduledFutureAwaitable(this, scheduler, synchronousIfCompleted);
        }

        public Task<T> ToTask()
        {
            if (IsCompleted)
            {
                if (IsSucceeded)
                    return Task.FromResult(Value);

                if (IsCanceled)
                    return TaskEx.FromCanceled<T>();

                if (IsFailed)
                    return TaskEx.FromException<T>(Exception);
            }

            var promise = _promise;
            TaskCompletionSource<T> tcs = new TaskCompletionSource<T>();
            promise.ContinueWith(() =>
            {
                if (promise.IsCanceled)
                    tcs.SetCanceled();
                else if (promise.Exception != null)
                    tcs.SetException(promise.Exception);
                else
                    tcs.SetResult(promise.Value);
            });

            return tcs.Task;
        }

        public void ThrowIfFailed()
        {
            _promise?.ThrowIfFailed();
        }

        Future IFuture.ToFuture()
        {
            return this;
        }

        Future<TCast> IFuture.ToFuture<TCast>()
        {
            if (typeof(T) != typeof(TCast))
                throw new InvalidOperationException($"Can't cast Future<{typeof(T).Name}> into Future<{typeof(TCast).Name}>.");

            return (Future<TCast>)(Future)this;
        }

        public static implicit operator Future(Future<T> future)
        {
            return future._promise != null ? new Future(future._promise) : new Future(future._value);
        }

        public static explicit operator Future<T>(Future future)
        {
            return future.ToFutureOf<T>();
        }

        #region Equality members

        [SuppressMessage("ReSharper", "NonReadonlyMemberInGetHashCode", Justification = "The field can't be readonly but it's not changed.")]
        public override int GetHashCode()
        {
            if (_promise != null)
                return _promise.GetHashCode();

            if (_value is ValueType)
                return _value.GetHashCode();

            return RuntimeHelpers.GetHashCode(_value);
        }

        public override bool Equals(object obj)
        {
            if (obj is Future<T>)
                return Equals((Future<T>)obj);

            if (obj is Future)
                return ((Future)obj).Equals((Future)this);

            return false;
        }

        public bool Equals(Future<T> other)
        {
            if (_promise != null || other._promise != null)
                return ReferenceEquals(_promise, other._promise);

            if (default(T) is ValueType)
                return EqualityComparer<T>.Default.Equals(_value, other._value);

            return ReferenceEquals(_value, other._value);
        }

        public static bool operator ==(Future<T> left, Future<T> right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Future<T> left, Future<T> right)
        {
            return !left.Equals(right);
        }

        #endregion
    }

    public struct Future : IEquatable<Future>, IFuture
    {
        #region Static Helpers

        public static Future<T> FromValue<T>(T value)
        {
            return new Future<T>(value);
        }

        public static Future<T> FromException<T>(Exception exception)
        {
            var promise = new Promise<T>();
            promise.SetException(exception);
            return promise.Future;
        }

        public static Future<T> FromCanceled<T>()
        {
            var promise = new Promise<T>();
            promise.SetCanceled();
            return promise.Future;
        }

        public static Future<Unit> WhenAll(params Future[] futures)
        {
            if (futures == null)
                throw new ArgumentNullException(nameof(futures));

            return InternalWhenAll(futures);
        }

        public static Future<Unit> WhenAll(IEnumerable<Future> futures)
        {
            var collection = futures as ICollection<Future>;
            if (collection != null)
                return InternalWhenAll(collection);

            if (futures == null)
                throw new ArgumentNullException(nameof(futures));

            return InternalWhenAll(futures.ToArray());
        }

        private static Future<Unit> InternalWhenAll(ICollection<Future> futures)
        {
            if (futures == null)
                throw new ArgumentNullException(nameof(futures));

            Promise<Unit> promise = new Promise<Unit>();

            int futureCount = futures.Count;
            int completedCount = 0;
            Action continuation = () =>
            {
                int count = Interlocked.Increment(ref completedCount);

                if (count == futureCount)
                {
                    List<Exception> exceptions = null;

                    foreach (var future in futures)
                    {
                        var exception = future.Exception;
                        if (exception != null)
                        {
                            if (exceptions == null)
                                exceptions = new List<Exception>();

                            exceptions.Add(exception);
                        }
                    }

                    if (exceptions == null)
                        promise.SetValue(Unit.Value);
                    else
                        promise.SetException(new AggregateException(exceptions));
                }
            };

            foreach (var future in futures)
            {
                if (future.IsCompleted)
                    continuation();
                else
                    future.GetPromise().ContinueWith(continuation);
            }

            return promise.Future;
        }

        public static Future<Future> WhenAny(params Future[] futures)
        {
            if (futures == null)
                throw new ArgumentNullException(nameof(futures));

            return InternalWhenAny(futures);
        }

        public static Future<Future> WhenAny(IEnumerable<Future> futures)
        {
            var collection = futures as ICollection<Future>;
            if (collection != null)
                return InternalWhenAny(collection);

            if (futures == null)
                throw new ArgumentNullException(nameof(futures));

            return InternalWhenAny(futures.ToArray());
        }

        private static Future<Future> InternalWhenAny(ICollection<Future> futures)
        {
            if (futures == null)
                throw new ArgumentNullException(nameof(futures));

            Promise<Future> promise = new Promise<Future>();

            Action<Future> continuation = winner =>
            {
                promise.SetValue(winner);
            };

            foreach (var future in futures)
            {
                if (promise.IsCompleted)
                    break;

                if (future.IsCompleted)
                {
                    continuation(future);
                    break;
                }

                var f = future;
                future.GetPromise().ContinueWith(() => continuation(f));
            }

            return promise.Future;
        }

        #endregion

        #region Nested Types

        [SuppressMessage("ReSharper", "ImpureMethodCallOnReadonlyValueField", Justification = "The calls do not alter the readonly field")]
        public struct FutureAwaiter : ICriticalNotifyCompletion
        {
            private readonly Future _future;

            public bool IsCompleted => _future.IsCompleted;

            public FutureAwaiter(Future future)
            {
                _future = future;
            }

            public void GetResult() => _future.ThrowIfFailed();

            public void OnCompleted(Action continuation)
            {
                //TODO Capture ExecutionContext
                UnsafeOnCompleted(continuation);
            }

            public void UnsafeOnCompleted(Action continuation)
            {
                var promise = _future.GetPromise();

                if (promise != null)
                {
                    promise.ContinueWith(continuation);
                    return;
                }

                // If promise is null, it means the future was created from a result,
                // invoke the continuation immediately
                continuation();
            }
        }

        public struct ScheduledFutureAwaiter : ICriticalNotifyCompletion
        {
            private Future _future;
            private readonly FutureScheduler _scheduler;
            private readonly bool _synchronousIfCompleted;

            public bool IsCompleted => _synchronousIfCompleted && _future.IsCompleted;

            public ScheduledFutureAwaiter(Future future, FutureScheduler scheduler, bool synchronousIfCompleted)
            {
                _future = future;
                _scheduler = scheduler;
                _synchronousIfCompleted = synchronousIfCompleted;
            }

            public void GetResult() => _future.ThrowIfFailed();

            public void OnCompleted(Action continuation)
            {
                //TODO Capture ExecutionContext
                UnsafeOnCompleted(continuation);
            }

            public void UnsafeOnCompleted(Action continuation)
            {
                var promise = _future.GetPromise();

                if (promise != null)
                {
                    promise.ContinueWith(continuation, _scheduler);
                    return;
                }

                // If promise is null, it means the future was created from a result,
                // invoke the continuation immediately

                if (_scheduler != null)
                {
                    _scheduler.ScheduleContinuation(continuation);
                    return;
                }

                continuation();
            }
        }

        public struct ScheduledFutureAwaitable
        {
            private readonly Future _future;
            private readonly FutureScheduler _scheduler;
            private readonly bool _synchronousIfCompleted;

            public ScheduledFutureAwaitable(Future future, FutureScheduler scheduler, bool synchronousIfCompleted)
            {
                _future = future;
                _scheduler = scheduler;
                _synchronousIfCompleted = synchronousIfCompleted;
            }

            public ScheduledFutureAwaiter GetAwaiter()
            {
                return new ScheduledFutureAwaiter(_future, _scheduler, _synchronousIfCompleted);
            }
        }

        #endregion

        private readonly Promise _promise;
        private readonly object _value;

        public bool IsCompleted => _promise == null || _promise.IsCompleted;

        public bool IsCanceled => _promise != null && _promise.IsCanceled;

        public Exception Exception => _promise?.Exception;

        public bool IsSucceeded => Exception == null;
        public bool IsFailed => Exception != null;

        internal Future(Promise promise)
        {
            if (promise == null)
                throw new ArgumentNullException(nameof(promise));

            _promise = promise;
            _value = null;
        }

        internal Future(object value)
        {
            _promise = null;
            _value = value;
        }

        [Pure]
        internal Promise GetPromise()
        {
            return _promise;
        }

        public FutureAwaiter GetAwaiter()
        {
            return new FutureAwaiter(this);
        }

        /// <summary>
        /// Continue the execution on the specified scheduler after an await
        /// </summary>
        /// <param name="scheduler">The scheduler</param>
        /// <param name="synchronousIfCompleted">
        /// Allow the continuation to bypass the scheduler and be called synchronously if the promise is already completed.
        /// </param>
        public ScheduledFutureAwaitable ContinueOn(FutureScheduler scheduler, bool synchronousIfCompleted = true)
        {
            return new ScheduledFutureAwaitable(this, scheduler, synchronousIfCompleted);
        }

        public void ThrowIfFailed()
        {
            _promise?.ThrowIfFailed();
        }

        Future IFuture.ToFuture()
        {
            return this;
        }

        Future<TCast> IFuture.ToFuture<TCast>()
        {
            return ToFutureOf<TCast>();
        }

        internal Future<T> ToFutureOf<T>()
        {
            return _promise != null ? new Future<T>((Promise<T>)_promise) : new Future<T>((T)_value);
        }

        #region Equality members

        public override int GetHashCode()
        {
            if (_promise != null)
                return _promise.GetHashCode();

            return RuntimeHelpers.GetHashCode(_value);
        }

        public override bool Equals(object obj)
        {
            var future = obj as IFuture;
            if (future != null)
                return Equals(future.ToFuture());

            return false;
        }

        public bool Equals(Future other)
        {
            if (_promise != null || other._promise != null)
                return ReferenceEquals(_promise, other._promise);

            if (ReferenceEquals(_value, other._value))
                return true;

            if (other._value == null)
                return false;

            if (other._value.GetType().IsValueType)
                return Equals(_value, other._value);

            return false;
        }

        public static bool operator ==(Future left, Future right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(Future left, Future right)
        {
            return !left.Equals(right);
        }

        #endregion
    }
}
