using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Portable <see cref="ILlmAsyncMarshaler"/> defaults (no Unity thread assumptions).
    /// </summary>
    public sealed class LlmAsyncMarshalerEditModeTests
    {
        [Test]
        public async Task PassThrough_InvokeAsync_RunsSynchronouslyWithFactory()
        {
            int result = await PassThroughLlmAsyncMarshaler.Instance.InvokeAsync(() => Task.FromResult(91),
                CancellationToken.None);

            Assert.AreEqual(91, result);
        }

        [Test]
        public async Task PassThrough_InvokeAsync_AwaitsNestedAsyncWork()
        {
            int result = await PassThroughLlmAsyncMarshaler.Instance.InvokeAsync(async () =>
            {
                await Task.Delay(15, CancellationToken.None);
                return 3;
            }, CancellationToken.None);

            Assert.AreEqual(3, result);
        }
    }
}