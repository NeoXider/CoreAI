using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class WaitLlmToolEditModeTests
    {
        // Tiny cap keeps these tests fast: the longest real delay is 0.05s.
        private const double TinyCapSeconds = 0.05;

        private static WaitLlmTool.WaitResult Deserialize(string json)
        {
            return JsonConvert.DeserializeObject<WaitLlmTool.WaitResult>(json);
        }

        private sealed class RecordingMarshaler : ILlmAsyncMarshaler
        {
            public int LastDelayMs = -1;
            public int DelayCalls;

            public Task<T> InvokeAsync<T>(Func<Task<T>> factory, CancellationToken cancellationToken)
            {
                return factory();
            }

            public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
            {
                LastDelayMs = milliseconds;
                DelayCalls++;
                return Task.CompletedTask;
            }
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_ClampsToMaxSeconds()
        {
            WaitLlmTool tool = new(TinyCapSeconds);

            string json = await tool.ExecuteAsync(10);
            WaitLlmTool.WaitResult result = Deserialize(json);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(TinyCapSeconds, result.WaitedSeconds, 1e-6);
            Assert.AreEqual(10d, result.RequestedSeconds, 1e-6);
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_WaitsRequestedAmountBelowCap()
        {
            WaitLlmTool tool = new(TinyCapSeconds);

            string json = await tool.ExecuteAsync(0.02);
            WaitLlmTool.WaitResult result = Deserialize(json);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(0.02, result.WaitedSeconds, 1e-6);
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_ZeroSeconds_FailsWithError()
        {
            WaitLlmTool tool = new(TinyCapSeconds);

            string json = await tool.ExecuteAsync(0);
            WaitLlmTool.WaitResult result = Deserialize(json);

            Assert.IsFalse(result.Success);
            Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_NaNSeconds_FailsWithError()
        {
            WaitLlmTool tool = new(TinyCapSeconds);

            string json = await tool.ExecuteAsync(double.NaN);
            WaitLlmTool.WaitResult result = Deserialize(json);

            Assert.IsFalse(result.Success);
            Assert.IsFalse(string.IsNullOrEmpty(result.Error));
        }

        [Test]
        public void WaitLlmTool_Metadata_AllowsDuplicatesAndIsNamedWait()
        {
            WaitLlmTool tool = new(TinyCapSeconds);

            Assert.IsTrue(tool.AllowDuplicates);
            Assert.AreEqual("wait", tool.Name);
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_RoutesDelayThroughInjectedMarshaler()
        {
            RecordingMarshaler marshaler = new();
            WaitLlmTool tool = new(60d, marshaler);

            string json = await tool.ExecuteAsync(2.5);
            WaitLlmTool.WaitResult result = Deserialize(json);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, marshaler.DelayCalls);
            Assert.AreEqual(2500, marshaler.LastDelayMs);
            Assert.AreEqual(2.5, result.WaitedSeconds, 1e-6);
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_ClampsMarshalerDelayToCap()
        {
            RecordingMarshaler marshaler = new();
            WaitLlmTool tool = new(TinyCapSeconds, marshaler);

            string json = await tool.ExecuteAsync(10);
            WaitLlmTool.WaitResult result = Deserialize(json);

            Assert.IsTrue(result.Success);
            Assert.AreEqual(1, marshaler.DelayCalls);
            Assert.AreEqual(50, marshaler.LastDelayMs);
            Assert.AreEqual(TinyCapSeconds, result.WaitedSeconds, 1e-6);
        }

        [Test]
        public async Task WaitLlmTool_ExecuteAsync_InvalidInput_DoesNotTouchMarshaler()
        {
            RecordingMarshaler marshaler = new();
            WaitLlmTool tool = new(60d, marshaler);

            string json = await tool.ExecuteAsync(0);
            WaitLlmTool.WaitResult result = Deserialize(json);

            Assert.IsFalse(result.Success);
            Assert.AreEqual(0, marshaler.DelayCalls);
            Assert.AreEqual(-1, marshaler.LastDelayMs);
        }
    }
}
