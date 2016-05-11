using System;
using System.Threading.Tasks;

namespace AsyncToolkit
{
    public static class TaskEx
    {
        public static Task<TResult> FromException<TResult>(Exception exception)
        {
            TaskCompletionSource<TResult> completion = new TaskCompletionSource<TResult>();
            completion.SetException(exception);
            return completion.Task;
        }

        public static Task<TResult> FromCanceled<TResult>()
        {
            TaskCompletionSource<TResult> completion = new TaskCompletionSource<TResult>();
            completion.SetCanceled();
            return completion.Task;
        }
    }
}