using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using Newtonsoft.Json;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode-тесты для MEAI-обёртки <see cref="LuaTool"/>:
    /// проверяют ExecuteAsync на успех / пустой код / исключение / логирование.
    /// Используется поддельный <see cref="LuaTool.ILuaExecutor"/> — без реального sandbox.
    /// </summary>
    [TestFixture]
    public sealed class LuaToolEditModeTests
    {
        // ===================== ExecuteAsync =====================

        [Test]
        public async Task ExecuteAsync_Success_ReturnsSerializedResult()
        {
            FakeExecutor executor = new(new LuaTool.LuaResult
            {
                Success = true,
                Output = "hello from lua"
            });
            LuaTool tool = new(executor, new FakeSettings(), new NullLog());

            string json = await tool.ExecuteAsync("print('hi')");
            LuaTool.LuaResult parsed = JsonConvert.DeserializeObject<LuaTool.LuaResult>(json);

            Assert.IsTrue(parsed.Success);
            Assert.AreEqual("hello from lua", parsed.Output);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual("print('hi')", executor.LastCode);
        }

        [Test]
        public async Task ExecuteAsync_EmptyCode_ReturnsErrorWithoutCallingExecutor()
        {
            FakeExecutor executor = new(new LuaTool.LuaResult { Success = true });
            LuaTool tool = new(executor, new FakeSettings(), new NullLog());

            string json = await tool.ExecuteAsync("");
            LuaTool.LuaResult parsed = JsonConvert.DeserializeObject<LuaTool.LuaResult>(json);

            Assert.IsFalse(parsed.Success);
            StringAssert.Contains("required", parsed.Error);
            Assert.AreEqual(0, executor.CallCount,
                "Пустой code не должен попадать в executor");
        }

        [Test]
        public async Task ExecuteAsync_NullCode_ReturnsErrorWithoutCallingExecutor()
        {
            FakeExecutor executor = new(new LuaTool.LuaResult { Success = true });
            LuaTool tool = new(executor, new FakeSettings(), new NullLog());

            string json = await tool.ExecuteAsync(null);
            LuaTool.LuaResult parsed = JsonConvert.DeserializeObject<LuaTool.LuaResult>(json);

            Assert.IsFalse(parsed.Success);
            Assert.AreEqual(0, executor.CallCount);
        }

        [Test]
        public async Task ExecuteAsync_ExecutorThrows_ReturnsFailureResult()
        {
            FakeExecutor executor = FakeExecutor.Throwing(new InvalidOperationException("sandbox kaboom"));
            LuaTool tool = new(executor, new FakeSettings(), new NullLog());

            string json = await tool.ExecuteAsync("bad()");
            LuaTool.LuaResult parsed = JsonConvert.DeserializeObject<LuaTool.LuaResult>(json);

            Assert.IsFalse(parsed.Success);
            Assert.IsNotNull(parsed.Error);
            StringAssert.Contains("sandbox kaboom", parsed.Error,
                "Сообщение исключения должно быть перенаправлено в LuaResult.Error");
        }

        [Test]
        public async Task ExecuteAsync_PropagatesCancellationToken()
        {
            TaskCompletionSource<bool> started = new();
            FakeExecutor executor = new((code, ct) =>
            {
                started.TrySetResult(true);
                return Task.Delay(Timeout.Infinite, ct)
                    .ContinueWith<LuaTool.LuaResult>(_ => new LuaTool.LuaResult { Success = true }, ct);
            });
            LuaTool tool = new(executor, new FakeSettings(), new NullLog());

            using CancellationTokenSource cts = new();
            Task<string> task = tool.ExecuteAsync("long_running()", cts.Token);
            await started.Task;
            cts.Cancel();

            string json;
            try
            {
                json = await task;
            }
            catch (OperationCanceledException)
            {
                Assert.Pass("ExecuteAsync может выбрасывать OperationCanceledException — это допустимое поведение");
                return;
            }

            // Либо результат с ошибкой cancellation (если поймал catch)
            LuaTool.LuaResult parsed = JsonConvert.DeserializeObject<LuaTool.LuaResult>(json);
            Assert.IsFalse(parsed.Success);
        }

        // ===================== CreateAIFunction =====================

        [Test]
        public void CreateAIFunction_ReturnsNonNull_WithCorrectName()
        {
            FakeExecutor executor = new(new LuaTool.LuaResult { Success = true });
            LuaTool tool = new(executor, new FakeSettings(), new NullLog());

            var aiFunc = tool.CreateAIFunction();

            Assert.IsNotNull(aiFunc);
            Assert.AreEqual("execute_lua", aiFunc.Name);
        }

        // ===================== Constructor argument validation =====================

        [Test]
        public void Constructor_NullExecutor_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => new LuaTool(null, new FakeSettings(), new NullLog()));
        }

        [Test]
        public void Constructor_NullSettings_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new LuaTool(new FakeExecutor(new LuaTool.LuaResult()), null, new NullLog()));
        }

        [Test]
        public void Constructor_NullLogger_Throws()
        {
            Assert.Throws<ArgumentNullException>(() =>
                new LuaTool(new FakeExecutor(new LuaTool.LuaResult()), new FakeSettings(), null));
        }

        // ===================== LuaLlmTool (тонкая обёртка) =====================

        [Test]
        public void LuaLlmTool_Metadata_IsConsistent()
        {
            FakeExecutor executor = new(new LuaTool.LuaResult { Success = true });
            LuaLlmTool wrapper = new(executor, new FakeSettings(), new NullLog());

            Assert.AreEqual("execute_lua", wrapper.Name);
            Assert.IsTrue(wrapper.AllowDuplicates,
                "execute_lua должен разрешать дубликаты (несколько последовательных Lua блоков — нормальный use case)");
            Assert.IsFalse(string.IsNullOrEmpty(wrapper.Description));
            Assert.IsFalse(string.IsNullOrEmpty(wrapper.ParametersSchema));
            StringAssert.Contains("\"code\"", wrapper.ParametersSchema,
                "JSON schema должна описывать параметр code");
        }

        // ===================== Helpers =====================

        private sealed class FakeExecutor : LuaTool.ILuaExecutor
        {
            public int CallCount;
            public string LastCode;

            private readonly LuaTool.LuaResult _result;
            private readonly Exception _throwException;
            private readonly Func<string, CancellationToken, Task<LuaTool.LuaResult>> _handler;

            public FakeExecutor(LuaTool.LuaResult result)
            {
                _result = result;
            }

            public FakeExecutor(Func<string, CancellationToken, Task<LuaTool.LuaResult>> handler)
            {
                _handler = handler;
            }

            private FakeExecutor(Exception ex)
            {
                _throwException = ex;
            }

            public static FakeExecutor Throwing(Exception ex) => new(ex);

            public Task<LuaTool.LuaResult> ExecuteAsync(string code, CancellationToken cancellationToken)
            {
                CallCount++;
                LastCode = code;

                if (_throwException != null)
                {
                    throw _throwException;
                }

                if (_handler != null)
                {
                    return _handler(code, cancellationToken);
                }

                return Task.FromResult(_result);
            }
        }

        private sealed class NullLog : ILog
        {
            public void Debug(string message, string tag = null) { }
            public void Info(string message, string tag = null) { }
            public void Warn(string message, string tag = null) { }
            public void Error(string message, string tag = null) { }
        }

        private sealed class FakeSettings : ICoreAISettings
        {
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.1f;
            public int ContextWindowTokens => 8192;
            public int MaxLuaRepairRetries => 3;
            public int MaxToolCallRetries => 3;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableHttpDebugLogging => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 15f;
            public int MaxLlmRequestRetries => 2;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool EnableStreaming => true;
        }
    }
}
