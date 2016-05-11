using System.Threading.Tasks;
using Xunit;

namespace AsyncToolkit.Tests
{
    public class FutureFacts
    {
        [Fact]
        public void Precompleted_ValueTypeFuture_Equals()
        {
            Assert.True(Future.FromValue(1).Equals(Future.FromValue(1)));

            Assert.False(Future.FromValue(1).Equals(Future.FromValue(2)));

            Assert.True(Equals(Future.FromValue(1), (Future)Future.FromValue(1)));

            Assert.False(Equals(Future.FromValue(1), (Future)Future.FromValue(2)));
        }

        [Fact]
        public void Precompleted_RefTypeFuture_Equals()
        {
            string a = "aaa";
            string b = "AAA".ToLower();

            Assert.True(Future.FromValue(a).Equals(Future.FromValue(a)));

            Assert.False(Future.FromValue(a).Equals(Future.FromValue(b)));

            Assert.True(Equals(Future.FromValue(a), (Future)Future.FromValue(a)));

            Assert.False(Equals(Future.FromValue(a), (Future)Future.FromValue(b)));
        }

        [Fact]
        public async Task Await_PrecompletedFuture()
        {
            var future = Future.FromValue("test");

            Assert.Equal("test", await future);
        }

        [Fact]
        public async Task Await_CompletedFuture()
        {
            var promise = new Promise<string>();
            promise.SetValue("test");

            Assert.Equal("test", await promise.Future);
        }
    }
}