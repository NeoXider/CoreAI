#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Infrastructure.Llm;
using CoreAI.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for <see cref="SmartToolCallingChatClient"/> consecutive-error
    /// counting, reset-on-success behavior, duplicate handling, and missing-tool failures.
    /// </summary>
    [TestFixture]
    public sealed class SmartToolCallingChatClientEditModeTests
    {
        /// <summary>
        /// Three consecutive tool errors abort the agent when the configured limit is three.
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

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { failTool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            // Модель должна быть вызвана ровно 3 раза: ошибка 1, 2, 3 → break
            Assert.AreEqual(3, callCount, "Agent must stop after 3 consecutive errors");
        }

        [Test]
        public void ConcatenateAssistantTextContents_JoinsMultipleTextParts()
        {
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
            {
                new MEAI.TextContent("line1"),
                new MEAI.TextContent("line2")
            });
            MEAI.ChatResponse response = new(msg);
            Assert.AreEqual("line1\nline2", SmartToolCallingChatClient.ConcatenateAssistantTextContents(response));
        }

        [Test]
        public async Task RequiredToolMode_RetriesWhenModelReturnsText()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                if (iteration == 1)
                {
                    return MakeTextResponse("I handled it without a tool.");
                }

                if (iteration == 2)
                {
                    return MakeToolCallResponse("my_tool", "call_required");
                }

                return MakeTextResponse("done");
            });

            MEAI.AIFunction tool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":true,\"Message\":\"ok\"}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { tool },
                ToolMode = MEAI.ChatToolMode.RequireSpecific("my_tool")
            };

            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(3, callCount, "Text-only required-tool responses should get one correction turn.");
            Assert.IsTrue(fakeInner.ObservedMessages[1].Any(m =>
                    m.Role == MEAI.ChatRole.User &&
                    (m.Text?.Contains("Tool-call contract violation") ?? false)),
                "Second request should include a correction that forces the required tool call.");
            Assert.IsTrue(client.LastExecutedToolCalls.Any(t => t.Name == "my_tool" && t.Success),
                "Required tool must execute after correction.");
            Assert.That(response.Messages?.LastOrDefault()?.Text, Does.Contain("done"));
        }

        [Test]
        public async Task RequiredToolMode_ResetsToAutoAfterFirstToolCall()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return iteration == 1
                    ? MakeToolCallResponse("my_tool", "call_required")
                    : MakeTextResponse("done");
            });

            MEAI.AIFunction tool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":true,\"Message\":\"ok\"}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { tool },
                ToolMode = MEAI.ChatToolMode.RequireSpecific("my_tool")
            };

            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(2, callCount);
            Assert.IsInstanceOf<MEAI.RequiredChatToolMode>(fakeInner.ObservedOptions[0].ToolMode);
            Assert.IsInstanceOf<MEAI.AutoChatToolMode>(fakeInner.ObservedOptions[1].ToolMode);
        }

        /// <summary>
        /// Two errors followed by a success reset the counter; a later run of three errors
        /// then aborts the agent after six total iterations.
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

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            // 2 ошибки (consecutiveErrors 1,2) + 1 успех (reset→0) + 3 ошибки (1,2,3→break) = 6
            Assert.AreEqual(6, callCount, "Expected 6 iterations: 2 fail + 1 success (reset) + 3 fail (stop)");
        }

        /// <summary>
        /// A success on the third attempt resets the counter so two later errors
        /// followed by a text response do not abort the agent.
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

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            MEAI.ChatResponse response =
                Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Result;

            // 5 тулзовых итераций + 1 текстовый ответ = 6 вызовов innerClient
            Assert.AreEqual(6, callCount, "Expected 6 iterations: 5 tool calls + 1 text response");
            // Последний ответ должен быть текстовым "Done", а не аварийный break
            string lastText = response.Messages?.LastOrDefault()?.Text;
            Assert.IsTrue(lastText?.Contains("Done") == true, "Agent should have finished normally with text response");
        }

        /// <summary>
        /// Successful tools followed by a text response complete normally.
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

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { successTool } };
            MEAI.ChatResponse response =
                Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Result;

            Assert.AreEqual(4, callCount, "3 tool calls + 1 text response = 4 iterations");
            string lastText = response.Messages?.LastOrDefault()?.Text;
            Assert.IsTrue(lastText?.Contains("All done") == true, "Should complete normally");
        }

        [Test]
        public async Task ToolResult_IsReturnedToModel_WithOriginalCallId()
        {
            ScriptedChatClient fakeInner = new(iteration =>
            {
                if (iteration == 1)
                {
                    return MakeToolCallResponse("my_tool", "call_123");
                }

                return MakeTextResponse("done");
            });

            MEAI.AIFunction tool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":true,\"Message\":\"ok\"}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(2, fakeInner.ObservedMessages.Count);
            List<MEAI.ChatMessage> secondIterationMessages = fakeInner.ObservedMessages[1];
            Assert.AreEqual(2, secondIterationMessages.Count);

            MEAI.FunctionCallContent call =
                secondIterationMessages[0].Contents.OfType<MEAI.FunctionCallContent>().Single();
            MEAI.FunctionResultContent result =
                secondIterationMessages[1].Contents.OfType<MEAI.FunctionResultContent>().Single();

            Assert.AreEqual("call_123", call.CallId);
            Assert.AreEqual("call_123", result.CallId);
            StringAssert.Contains("\"Success\":true", result.Result?.ToString());
        }

        // ===================== Duplicate Detection =====================

        /// <summary>
        /// With duplicate suppression enabled, two identical consecutive tool calls reject
        /// the second call and return a duplicate-call explanation to the model.
        /// </summary>
        [Test]
        public void DuplicateToolCallsRejected_WhenAllowDuplicatesFalse()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                if (callCount <= 2)
                {
                    // Одинаковый tool call с одинаковыми args
                    return MakeToolCallResponse("my_tool", "call_" + callCount,
                        new Dictionary<string, object> { { "x", 42 } });
                }

                return MakeTextResponse("done");
            });

            MEAI.AIFunction tool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":true}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                false,
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            // Ожидаем 3 итерации: 1) успешный tool call, 2) дубликат (отклонён), 3) текст
            Assert.AreEqual(3, callCount,
                "После обнаружения дубликата должен сработать rejection, модель переходит к текстовому ответу");
        }

        /// <summary>
        /// Different arguments are not duplicates even when global duplicate suppression is enabled.
        /// </summary>
        [Test]
        public void DifferentArgumentsNotTreatedAsDuplicate()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                if (callCount <= 3)
                {
                    return MakeToolCallResponse("my_tool", "call_" + callCount,
                        new Dictionary<string, object> { { "x", callCount } });
                }

                return MakeTextResponse("done");
            });

            MEAI.AIFunction tool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":true}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                false,
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            Assert.AreEqual(4, callCount,
                "Три разных аргумента + текстовый ответ = 4 итерации, блокировки не должно быть");
        }

        /// <summary>
        /// Tools with <see cref="ILlmTool.AllowDuplicates"/> are exempt from duplicate suppression.
        /// </summary>
        [Test]
        public void PerToolAllowDuplicates_OverridesGlobal()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                if (callCount <= 3)
                {
                    return MakeToolCallResponse("always_ok", "call_" + callCount,
                        new Dictionary<string, object> { { "x", 42 } });
                }

                return MakeTextResponse("done");
            });

            MEAI.AIFunction tool = MakeAIFunction("always_ok", _ =>
                Task.FromResult<object>("{\"Success\":true}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                false,
                new List<Ai.ILlmTool> { new AllowDupTool("always_ok") }, "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            Assert.AreEqual(4, callCount,
                "Инструмент с AllowDuplicates=true не триггерит rejection");
        }

        // ===================== Edge Cases =====================

        /// <summary>
        /// Missing tool calls return a not-found result and increment the consecutive-error counter.
        /// </summary>
        [Test]
        public void ToolNotFound_CountsAsError()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return MakeToolCallResponse("missing_tool", "call_" + callCount);
            });

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, // отключаем дубликаты, чтобы увидеть именно not-found
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool>() };
            Task.Run(() => client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Wait();

            Assert.AreEqual(3, callCount,
                "3 попытки подряд вызвать несуществующий тул → прерывание");
        }

        /// <summary>
        /// Tool exceptions are caught, converted to function results, and counted as errors.
        /// </summary>
        [Test]
        public void ToolThrowsException_HandledAsError()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return MakeToolCallResponse("broken_tool", "call_" + callCount);
            });

            MEAI.AIFunction tool = MakeAIFunction("broken_tool",
                _ => throw new InvalidOperationException("boom from tool"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true,
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            MEAI.ChatResponse response = Task.Run(() =>
                client.GetResponseAsync(new List<MEAI.ChatMessage>(), options)).Result;

            Assert.AreEqual(3, callCount, "3 падения подряд → прерывание агента");
            Assert.IsNotNull(response);
        }

        // ===================== Tool Call History Trim =====================

        /// <summary>
        /// An over-cap unit list removes the OLDEST whole unit (Assistant tool_calls + its Tool
        /// result) and the surviving list never begins with an orphaned Tool message.
        /// </summary>
        [Test]
        public void TrimToolCallHistory_OverCap_RemovesOldestWholeUnit_NoOrphanLead()
        {
            // [System, User, A(tool_calls #1), Tool #1, A(tool_calls #2), Tool #2]
            // 4 tool-related messages, cap 2 → the oldest unit (#1) is dropped as a whole.
            List<MEAI.ChatMessage> messages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.System, "you are helpful"),
                new MEAI.ChatMessage(MEAI.ChatRole.User, "do work"),
                MakeAssistantToolCall("call_1", "tool_1"),
                MakeToolResult("call_1", "ok_1"),
                MakeAssistantToolCall("call_2", "tool_2"),
                MakeToolResult("call_2", "ok_2")
            };

            InvokeTrim(messages, 2);

            // System + User preserved; only the newest unit (#2) survives.
            Assert.AreEqual(MEAI.ChatRole.System, messages[0].Role, "System message must be preserved");
            Assert.AreEqual(MEAI.ChatRole.User, messages[1].Role, "Original user message must be preserved");
            Assert.IsFalse(messages.Any(m => CallNameOf(m) == "tool_1"),
                "Oldest unit (tool_1) must be removed as a whole");
            Assert.IsTrue(messages.Any(m => CallNameOf(m) == "tool_2"),
                "Newest unit (tool_2) must survive");

            // The first Tool message must never appear before its assistant tool_calls turn.
            int firstTool = messages.FindIndex(m => m.Role == MEAI.ChatRole.Tool);
            int firstAssistantCall = messages.FindIndex(m =>
                m.Role == MEAI.ChatRole.Assistant && HasFunctionCall(m));
            Assert.IsTrue(firstTool > firstAssistantCall,
                "Surviving list must not start with an orphan Tool message");
            AssertNoOrphanToolMessage(messages);
        }

        /// <summary>
        /// A unit whose Assistant <c>tool_calls</c> turn is answered by MULTIPLE contiguous Tool
        /// result messages is trimmed as one block, never split mid-unit.
        /// </summary>
        [Test]
        public void TrimToolCallHistory_MultiResultUnit_TrimsAsOneBlock()
        {
            // Unit #1 has two contiguous Tool results (e.g. a parallel tool_calls turn the provider
            // answered with separate 'tool' messages). 5 tool-related messages, cap 2 → unit #1
            // (its assistant turn + BOTH tool results) is removed together.
            List<MEAI.ChatMessage> messages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.User, "do work"),
                MakeAssistantToolCall("call_1a", "tool_1"),
                MakeToolResult("call_1a", "ok_1a"),
                MakeToolResult("call_1b", "ok_1b"),
                MakeAssistantToolCall("call_2", "tool_2"),
                MakeToolResult("call_2", "ok_2")
            };

            InvokeTrim(messages, 2);

            Assert.IsFalse(messages.Any(m => CallNameOf(m) == "tool_1"),
                "Multi-result unit must be removed as one block (assistant turn gone)");
            Assert.IsFalse(messages.Any(m => m.Role == MEAI.ChatRole.Tool &&
                    ResultCallIdOf(m).StartsWith("call_1")),
                "Both contiguous Tool results of the trimmed unit must be removed together");
            Assert.IsTrue(messages.Any(m => CallNameOf(m) == "tool_2"),
                "Newest unit must survive intact");
            AssertNoOrphanToolMessage(messages);
        }

        /// <summary>
        /// An input already within the cap is returned unchanged (same instances, same order).
        /// </summary>
        [Test]
        public void TrimToolCallHistory_UnderCap_ReturnsUnchanged()
        {
            List<MEAI.ChatMessage> messages = new()
            {
                new MEAI.ChatMessage(MEAI.ChatRole.System, "sys"),
                new MEAI.ChatMessage(MEAI.ChatRole.User, "do work"),
                MakeAssistantToolCall("call_1", "tool_1"),
                MakeToolResult("call_1", "ok_1")
            };
            List<MEAI.ChatMessage> snapshot = messages.ToList();

            // 2 tool-related messages, cap 5 → nothing to trim.
            InvokeTrim(messages, 5);

            Assert.AreEqual(snapshot.Count, messages.Count, "Under-cap input must keep its length");
            for (int i = 0; i < snapshot.Count; i++)
            {
                Assert.AreSame(snapshot[i], messages[i],
                    "Under-cap input must be returned unchanged (same instances, same order)");
            }
        }

        /// <summary>
        /// Across several over-cap shapes, no surviving Tool message is left without a preceding
        /// Assistant tool_calls turn (the orphaned-'tool' provider-400 invariant).
        /// </summary>
        [Test]
        public void TrimToolCallHistory_Invariant_NoOrphanToolMessageSurvives()
        {
            // Three units, every cap from 1..5 forces a different amount of trimming.
            for (int cap = 1; cap <= 5; cap++)
            {
                List<MEAI.ChatMessage> messages = new()
                {
                    new MEAI.ChatMessage(MEAI.ChatRole.System, "sys"),
                    new MEAI.ChatMessage(MEAI.ChatRole.User, "do work"),
                    MakeAssistantToolCall("call_1", "tool_1"),
                    MakeToolResult("call_1", "ok_1"),
                    MakeAssistantToolCall("call_2", "tool_2"),
                    MakeToolResult("call_2", "ok_2a"),
                    MakeToolResult("call_2", "ok_2b"),
                    MakeAssistantToolCall("call_3", "tool_3"),
                    MakeToolResult("call_3", "ok_3")
                };

                InvokeTrim(messages, cap);

                AssertNoOrphanToolMessage(messages);
            }
        }

        /// <summary>
        /// Simple <see cref="ILlmTool"/> implementation with duplicate calls explicitly allowed.
        /// </summary>
        private sealed class AllowDupTool : Ai.ILlmTool
        {
            public AllowDupTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;
        }

        #region Helpers

        /// <summary>
        /// Creates a chat response containing a tool call.
        /// </summary>
        private static MEAI.ChatResponse MakeToolCallResponse(string toolName, string callId)
        {
            MEAI.FunctionCallContent fc = new(callId, toolName, new Dictionary<string, object>());
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent> { fc });
            return new MEAI.ChatResponse(msg);
        }

        private static MEAI.ChatResponse MakeToolCallResponse(string toolName, string callId,
            IDictionary<string, object> arguments)
        {
            MEAI.FunctionCallContent fc = new(callId, toolName, arguments);
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent> { fc });
            return new MEAI.ChatResponse(msg);
        }

        /// <summary>
        /// Creates a text-only chat response.
        /// </summary>
        private static MEAI.ChatResponse MakeTextResponse(string text)
        {
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, text);
            return new MEAI.ChatResponse(msg);
        }

        /// <summary>
        /// Creates a simple <c>AIFunction</c> using the supplied implementation.
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
        /// Builds an Assistant turn carrying a single <c>tool_calls</c> entry.
        /// </summary>
        private static MEAI.ChatMessage MakeAssistantToolCall(string callId, string toolName)
        {
            return new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent(callId, toolName) });
        }

        /// <summary>
        /// Builds a Tool result turn answering the call with <paramref name="callId"/>.
        /// </summary>
        private static MEAI.ChatMessage MakeToolResult(string callId, string result)
        {
            return new MEAI.ChatMessage(MEAI.ChatRole.Tool,
                new List<MEAI.AIContent> { new MEAI.FunctionResultContent(callId, result) });
        }

        /// <summary>
        /// Returns the tool name of an Assistant tool_calls turn, or <c>null</c> for other messages.
        /// </summary>
        private static string CallNameOf(MEAI.ChatMessage message)
        {
            return message.Contents
                .OfType<MEAI.FunctionCallContent>()
                .Select(c => c.Name)
                .FirstOrDefault();
        }

        /// <summary>
        /// Returns the call id of a Tool result turn, or empty for other messages.
        /// </summary>
        private static string ResultCallIdOf(MEAI.ChatMessage message)
        {
            return message.Contents
                .OfType<MEAI.FunctionResultContent>()
                .Select(c => c.CallId)
                .FirstOrDefault() ?? "";
        }

        /// <summary>
        /// True when the message carries at least one <c>tool_calls</c> entry.
        /// </summary>
        private static bool HasFunctionCall(MEAI.ChatMessage message)
        {
            return message.Contents.OfType<MEAI.FunctionCallContent>().Any();
        }

        /// <summary>
        /// Asserts every surviving Tool message is preceded (somewhere earlier) by an Assistant
        /// tool_calls turn, and that no Tool message immediately follows a non-tool message without
        /// such a preceding tool_calls turn. Mirrors the provider rule the trim protects.
        /// </summary>
        private static void AssertNoOrphanToolMessage(List<MEAI.ChatMessage> messages)
        {
            bool sawAssistantToolCall = false;
            for (int i = 0; i < messages.Count; i++)
            {
                MEAI.ChatMessage m = messages[i];
                if (m.Role == MEAI.ChatRole.Assistant && HasFunctionCall(m))
                {
                    sawAssistantToolCall = true;
                }
                else if (m.Role == MEAI.ChatRole.Tool)
                {
                    Assert.IsTrue(sawAssistantToolCall,
                        $"Tool message at index {i} has no preceding assistant tool_calls turn (orphan)");
                }
                else
                {
                    // A plain (non-tool) message ends the current tool-call block.
                    sawAssistantToolCall = false;
                }
            }
        }

        /// <summary>
        /// Invokes the private <c>TrimToolCallHistory(List, int)</c> on a client wired to a low cap,
        /// mutating <paramref name="messages"/> in place exactly as the production loop would.
        /// </summary>
        private static void InvokeTrim(List<MEAI.ChatMessage> messages, int maxToolMessages)
        {
            SmartToolCallingChatClient client = new(new ScriptedChatClient(_ => MakeTextResponse("noop")),
                NullLog.Instance, UnityEngine.ScriptableObject.CreateInstance<CoreAISettingsAsset>(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            System.Reflection.MethodInfo trim = typeof(SmartToolCallingChatClient).GetMethod(
                "TrimToolCallHistory",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(trim, "TrimToolCallHistory(List, int) must exist for trim coverage");
            trim.Invoke(client, new object[] { messages, maxToolMessages });
        }

        /// <summary>
        /// Scripted <c>IChatClient</c> that invokes a callback for each response iteration.
        /// </summary>
        private sealed class ScriptedChatClient : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _scriptFn;
            private int _iteration;

            public ScriptedChatClient(Func<int, MEAI.ChatResponse> scriptFn)
            {
                _scriptFn = scriptFn;
            }

            public List<List<MEAI.ChatMessage>> ObservedMessages { get; } = new();
            public List<MEAI.ChatOptions> ObservedOptions { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(
                IEnumerable<MEAI.ChatMessage> chatMessages,
                MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                _iteration++;
                ObservedMessages.Add(chatMessages.ToList());
                ObservedOptions.Add(options);
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
        /// Logger stub used when test output is irrelevant.
        /// </summary>
        private sealed class NullLogger : ILog
        {
            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
            }

            public void Error(string message, string tag = null)
            {
            }
        }

        #endregion
    }
}
#endif
