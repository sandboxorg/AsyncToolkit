using System;
using System.Collections.Generic;

namespace AsyncToolkit
{
    /// <summary>
    /// Asynchronous multi-producer/single-consumer queue.
    /// <remarks>
    /// This classed is optimized for usage in StreamAssociationHandle, review carefully before reuse.
    /// </remarks>
    /// </summary>
    /// <typeparam name="T"></typeparam>
    internal sealed class FutureQueue<T> : IDisposable
    {
        private readonly Queue<T> _queue;
        private Promise<T> _pendingDequeue;

        private bool _addingCompleted;
        private bool _isDisposed;

        public FutureQueue()
        {
            _queue = new Queue<T>();
        }

        /// <summary>
        /// Add an item to the queue. Can be called concurrently.
        /// </summary>
        /// <returns>True if the item was enqueued, otherwise False.</returns>
        public bool Enqueue(T item)
        {
            Promise<T> completedWaiter = null;

            lock (_queue)
            {
                if (_addingCompleted)
                    return false;

                if (_pendingDequeue != null)
                {
                    completedWaiter = _pendingDequeue;
                    _pendingDequeue = null;
                }
                else
                {
                    _queue.Enqueue(item);
                }
            }

            completedWaiter?.SetValue(item);

            return true;
        }

        /// <summary>
        /// Dequeue an item from the queue. The queue is single consumer, Dequeue must not be called again until the previous dequeue operation have completed.
        /// </summary>
        /// <returns>A future of the dequeued item.</returns>
        public Future<T> Dequeue()
        {
            lock (_queue)
            {
                if (_pendingDequeue != null)
                    throw new InvalidOperationException("Dequeue operation is already in progress. This queue is single consumer.");

                if (_isDisposed)
                    return Future.FromCanceled<T>();

                if (_queue.Count > 0)
                {
                    T value = _queue.Dequeue();
                    return Future.FromValue(value);
                }

                if (_addingCompleted)
                    return Future.FromCanceled<T>();

                Promise<T> promise = new Promise<T>();
                _pendingDequeue = promise;
                return promise.Future;
            }
        }

        public void CompleteAdding()
        {
            Promise<T> pendingDequeue;

            lock (_queue)
            {
                _addingCompleted = true;
                pendingDequeue = _pendingDequeue;
            }

            pendingDequeue?.SetCanceled();
        }

        public void Dispose()
        {
            Promise<T> pendingDequeue;
            lock (_queue)
            {
                if (_isDisposed)
                    return;

                _isDisposed = true;
                _addingCompleted = true;

                pendingDequeue = _pendingDequeue;
            }

            pendingDequeue?.SetCanceled();
        }
    }
}