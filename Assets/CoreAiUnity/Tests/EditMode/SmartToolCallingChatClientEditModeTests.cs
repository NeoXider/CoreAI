#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode тесты SmartToolCallingChatClient: проверяют логику
    /// consecutive-error счётчика, сброса при успехе и прерывания при N ошибках подряд.
    /// </summary>
    [TestFixture]
    public sealed class SmartToolCallingChatClientEditModeTests
    {
        /// <summary>
        /// 3 ошибки подряд → агент прерывается (maxConsecutiveErrors = 3).
        /// </summary>
        [Test]
        public void ThreeConsecutiveErrors_StopsAgent()
        {
            // Модель каждый раз вызывает тулзу "my_tool", тулза всегда возвращает failure
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return MakeToolCallResponse("my_tool", "call_" + callCount);
            });

            MEAI.AIFunction failTool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":false,\"Error\":\"boom\"}"));

            SmartToolCallingChatClient client = new(fakeInner, new NullLogger(),
                UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(),
                true, new List<CoreAI.Ai.ILlmTool>(), 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { failTool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            // Модель должна быть вызвана ровно 3 раза: ошибка 1, 2, 3 → break
            Assert.AreEqual(3, callCount, "Agent must stop after 3 consecutive errors");
        }

        /// <summary>
        /// 2 ошибки, потом успех → счётчик сбрасывается.
        /// Затем ещё 3 ошибки подряд → агент прерывается.
        /// Итого: 2 ошибки + 1 успех + 3 ошибки = 6 итераций.
        /// </summary>
        [Test]
        public void SuccessResetsCounter_ThenThreeErrorsStop()
        {
            int callCount = 0;
            // Последовательность: fail, fail, success, fail, fail, fail → stop
            bool[] sequence = new[] { false, false, true, false, false, false };

            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return MakeToolCallResponse("my_tool", "call_" + callCount);
            });

            int toolInvocation = 0;
            MEAI.AIFunction tool = MakeAIFunction("my_tool", _ =>
            {
                bool success = sequence[toolInvocation];
                toolInvocation++;
                string json = success
                    ? "{\"Success\":true,\"Message\":\"ok\"}"
                    : "{\"Success\":false,\"Error\":\"boom\"}";
                return Task.FromResult<object>(json);
            });

            SmartToolCallingChatClient client = new(fakeInner, new NullLogger(),
                UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(),
                true, new List<CoreAI.Ai.ILlmTool>(), 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            // 2 ошибки (consecutiveErrors 1,2) + 1 успех (reset→0) + 3 ошибки (1,2,3→break) = 6
            Assert.AreEqual(6, callCount, "Expected 6 iterations: 2 fail + 1 success (reset) + 3 fail (stop)");
        }

        /// <summary>
        /// Успех на 3-й попытке (при maxConsecutiveErrors=3) → счётчик обнуляется.
        /// Потом ещё 2 ошибки и текстовый ответ → агент не прерван аварийно.
        /// </summary>
        [Test]
        public void SuccessOnThirdAttempt_ResetsAndContinues()
        {
            int callCount = 0;
            // fail, fail, success, fail, fail, text (модель отвечает текстом)
            bool[] sequence = new[] { false, false, true, false, false };

            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                // После 5 тулзовых вызовов модель отвечает текстом
                if (callCount > sequence.Length)
                {
                    return MakeTextResponse("Done");
                }

                return MakeToolCallResponse("my_tool", "call_" + callCount);
            });

            int toolInvocation = 0;
            MEAI.AIFunction tool = MakeAIFunction("my_tool", _ =>
            {
                bool success = sequence[toolInvocation];
                toolInvocation++;
                string json = success
                    ? "{\"Success\":true,\"Message\":\"ok\"}"
                    : "{\"Success\":false,\"Error\":\"boom\"}";
                return Task.FromResult<object>(json);
            });

            SmartToolCallingChatClient client = new(fakeInner, new NullLogger(),
                UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(),
                true, new List<CoreAI.Ai.ILlmTool>(), 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            MEAI.ChatResponse response = Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Result;

            // 5 тулзовых итераций + 1 текстовый ответ = 6 вызовов innerClient
            Assert.AreEqual(6, callCount, "Expected 6 iterations: 5 tool calls + 1 text response");
            // Последний ответ должен быть текстовым "Done", а не аварийный break
            string lastText = response.Messages?.LastOrDefault()?.Text;
            Assert.IsTrue(lastText?.Contains("Done") == true, "Agent should have finished normally with text response");
        }

        /// <summary>
        /// Все тулзы успешны, потом текстовый ответ → нормальное завершение.
        /// </summary>
        [Test]
        public void AllSuccess_CompletesNormally()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                if (callCount <= 3)
                {
                    return MakeToolCallResponse("my_tool", "call_" + callCount);
                }

                return MakeTextResponse("All done");
            });

            MEAI.AIFunction successTool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":true,\"Message\":\"ok\"}"));

            SmartToolCallingChatClient client = new(fakeInner, new NullLogger(),
                UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>(),
                true, new List<CoreAI.Ai.ILlmTool>(), 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { successTool } };
            MEAI.ChatResponse response = Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Result;

            Assert.AreEqual(4, callCount, "3 tool calls + 1 text response = 4 iterations");
            string lastText = response.Messages?.LastOrDefault()?.Text;
            Assert.IsTrue(lastText?.Contains("All done") == true, "Should complete normally");
        }

        #region Helpers

        /// <summary>
        /// Создаёт ChatResponse с tool call.
        /// </summary>
        private static MEAI.ChatResponse MakeToolCallResponse(string toolName, string callId)
        {
            MEAI.FunctionCallContent fc = new(callId, toolName, new Dictionary<string, object>());
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent> { fc });
            return new MEAI.ChatResponse(msg);
        }

        /// <summary>
        /// Создаёт ChatResponse с текстовым ответом (без tool calls).
        /// </summary>
        private static MEAI.ChatResponse MakeTextResponse(string text)
        {
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, text);
            return new MEAI.ChatResponse(msg);
        }

        /// <summary>
        /// Создаёт простую AIFunction с заданной логикой.
        /// </summary>
        private static MEAI.AIFunction MakeAIFunction(string name,
            Func<IEnumerable<KeyValuePair<string, object>>, Task<object>> handler)
        {
            Func<CancellationToken, Task<string>> func = async (CancellationToken ct) =>
            {
                object result = await handler(null);
                return result?.ToString() ?? "";
            };
            return MEAI.AIFunctionFactory.Create(func,
                new MEAI.AIFunctionFactoryOptions { Name = name, Description = "test tool" });
        }

        /// <summary>
        /// Скриптованный IChatClient — на каждый вызов GetResponseAsync
        /// вызывает callback с номером итерации.
        /// </summary>
        private sealed class ScriptedChatClient : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _scriptFn;
            private int _iteration;

            public ScriptedChatClient(Func<int, MEAI.ChatResponse> scriptFn)
            {
                _scriptFn = scriptFn;
            }

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                _iteration++;
                return Task.FromResult(_scriptFn(_iteration));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Логгер-заглушка (ничего не делает).
        /// </summary>
        private sealed class NullLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        #endregion
    }
}
#endif
