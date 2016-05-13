using System;
using System.Threading.Tasks;
using Xunit;

namespace AsyncToolkit.Tests
{
    public class FutureFacts : TestBase
    {
        [Fact]
        public void Future_Equals()
        {
            TestEquals(Future.FromValue(1), Future.FromValue(2));
            TestEquals(Future.FromValue("a"), Future.FromValue("A".ToLower()));

            TestEquals(Future.FromValue(1), CreateCompleted(1));

            TestEquals(CreateCompleted(1), CreateCompleted(1));

            TestEquals(new Promise<int>().Future, new Promise<int>().Future);
        }

        private void TestEquals<T>(Future<T> future1, Future<T> future2)
        {
            var future1Copy = future1;
            var future2Copy = future2;

            Assert.True(Equals(future1, (Future<T>)(Future)future1Copy));
            Assert.True(future1 == future1Copy);

            Assert.True(!Equals(future1, future2Copy));
            Assert.True(future1 != future2);

            Assert.True(Equals((Future)future1, future1));
            Assert.True(Equals(future1, (Future)future1));
            Assert.True(Equals((Future)future1, (Future)future1Copy));

            Assert.True(!Equals((Future)future1, future2));
            Assert.True(!Equals(future1, (Future)future2));
            Assert.True(!Equals((Future)future1, (Future)future2));
        }

        [Fact]
        public async Task Await_PrecompletedFuture()
        {
            var future = Future.FromValue("test");

            Assert.True(future.IsCompleted);
            Assert.False(future.IsCanceled);
            Assert.False(future.IsFailed);
            Assert.True(future.IsSucceeded);

            Assert.Equal("test", await future);
        }

        [Fact]
        public async Task Await_CompletedFuture()
        {
            var promise = new Promise<string>();

            Assert.False(promise.Future.IsCompleted);

            promise.SetValue("test");

            Assert.True(promise.Future.IsCompleted);
            Assert.True(promise.Future.IsSucceeded);
            Assert.Null(promise.Future.Exception);

            Assert.Equal("test", await promise.Future);
        }

        [Fact]
        public async Task Await_CompletedCanceledFuture()
        {
            var future = Future.FromCanceled<string>();

            Assert.True(future.IsCompleted);
            Assert.True(future.IsCanceled);
            Assert.NotNull(future.Exception);

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await future);
        }

        [Fact]
        public async Task Await_CompletedFailedFuture()
        {
            var future = Future.FromException<string>(new ApplicationException());

            Assert.True(future.IsCompleted);
            Assert.True(future.IsFailed);
            Assert.NotNull(future.Exception);

            await Assert.ThrowsAsync<ApplicationException>(async () => await future);
        }

        [Fact]
        public async Task Await_UncompletedFuture()
        {
            var promise = new Promise<string>();

            var task = AwaitFuture(promise.Future);

            promise.SetValue("test");

            Assert.Equal("test", await task);
        }

        [Fact]
        public async Task Await_UncompletedCanceledFuture()
        {
            var promise = new Promise<string>();

            var task = AwaitFuture(promise.Future);

            promise.SetCanceled();

            await Assert.ThrowsAsync<OperationCanceledException>(async () => await task);
        }

        [Fact]
        public async Task Await_UncompletedFailedFuture()
        {
            var promise = new Promise<string>();

            var task = AwaitFuture(promise.Future);

            promise.SetException(new ApplicationException());

            await Assert.ThrowsAsync<ApplicationException>(async () => await task);
        }
    }
}