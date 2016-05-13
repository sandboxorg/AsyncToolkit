using System.Threading.Tasks;

namespace AsyncToolkit.Tests
{
    public class TestBase
    {
        protected async Task<T> AwaitFuture<T>(Future<T> future)
        {
            return await future;
        }

        public Future<T> CreateCompleted<T>(T value)
        {
            Promise<T> promise = new Promise<T>();
            promise.SetValue(value);
            return promise.Future;
        }
    }
}