#if COREAI_LLM
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
    /// Uses portable settings; host thread marshaling belongs to the Unity adapter's own tests.
    /// </summary>
    [TestFixture]
    public sealed class SmartToolCallingChatClientEditModeTests
    {
        /// <summary>
        /// Three consecutive tool errors abort the agent when the configured limit is three.
        /// </summary>
        [Test]
        public async Task ThreeConsecutiveErrors_StopsAgent()
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
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { failTool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // 3 failed loop iterations trip the guard, then the loop makes EXACTLY ONE extra
            // tools-disabled roundtrip so the model can summarize (F6). The scripted client answers
            // that extra call with another tool call (no text), so the canned fallback is returned.
            Assert.AreEqual(4, callCount,
                "Agent must stop after 3 consecutive errors + 1 final no-tools summary roundtrip");
        }

        /// <summary>
        /// A per-request roundtrip override caps the tool-call loop at that value even when the model
        /// keeps requesting tools, independent of the global settings value.
        /// </summary>
        [Test]
        public async Task RoundtripOverride_CapsLoopAtOverrideValue()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return MakeToolCallResponse("ok_tool", "call_" + callCount);
            });

            MEAI.AIFunction okTool = MakeAIFunction("ok_tool", _ =>
                Task.FromResult<object>("{\"Success\":true}"));

            // Global settings default is 20; a per-request override of 2 must win.
            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 5, "",
                null, null, 2);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { okTool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // iteration 1 (call 1) → tool; iteration 2 (call 2) → tool; iteration 3 → over cap →
            // one final tools-disabled summary roundtrip (F6, call 3), then stop.
            Assert.AreEqual(3, callCount,
                "Override of 2 must stop the loop after 2 roundtrips + 1 final no-tools summary");
        }

        /// <summary>
        /// A roundtrip override of 0 means UNLIMITED: the loop is not cut off by the safety valve and
        /// runs until the model itself stops requesting tools.
        /// </summary>
        [Test]
        public async Task RoundtripOverrideZero_IsUnlimited()
        {
            // Model requests the tool 25 times (past the default cap of 10), then returns text.
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return callCount <= 25
                    ? MakeToolCallResponse("ok_tool", "call_" + callCount)
                    : MakeTextResponse("done");
            });

            MEAI.AIFunction okTool = MakeAIFunction("ok_tool", _ =>
                Task.FromResult<object>("{\"Success\":true}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 50, "",
                null, null, 0);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { okTool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // 25 tool roundtrips + 1 final text turn = 26 model calls; the default-20 valve never fired.
            Assert.AreEqual(26, callCount, "Override of 0 must not cap the loop (unlimited)");
        }

        /// <summary>
        /// With NO per-request override (null), the loop is capped by the GLOBAL settings value — proving the
        /// settings-driven safety valve terminates a runaway tool-calling model even without a per-agent or
        /// per-call override. Complements the override-path tests above.
        /// </summary>
        [Test]
        public async Task GlobalSettingsRoundtripCap_TerminatesLoop()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                // Never stop on its own — force the safety valve to be what ends the loop.
                return MakeToolCallResponse("ok_tool", "call_" + callCount);
            });

            MEAI.AIFunction okTool = MakeAIFunction("ok_tool", _ =>
                Task.FromResult<object>("{\"Success\":true}"));

            CoreAISettingsOptions settings = new() { MaxToolCallRoundtrips = 3 };

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                settings, true, new List<Ai.ILlmTool>(), "TestRole", 50, "",
                null, null, null); // maxRoundtripsOverride = null → inherit the global cap

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { okTool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // 3 tool roundtrips → over cap → one final tools-disabled summary roundtrip (F6) = 4 model calls.
            Assert.AreEqual(4, callCount,
                "The global settings cap (3) must stop the loop after 3 roundtrips + 1 final no-tools summary");
        }

        [Test]
        public void TryExtractToolCallsFromText_UnconvertibleArguments_DropsOnlyThatCallAndLogsIt()
        {
            // The good call must survive; the call whose arguments cannot become a dictionary is
            // dropped, and the drop must leave a log trace instead of being silently discarded.
            const string text =
                "{\"name\":\"good_tool\",\"arguments\":{\"a\":1}} " +
                "{\"name\":\"bad_tool\",\"arguments\":\"not a json object\"}";
            RecordingLog log = new();

            bool found = SmartToolCallingChatClient.TryExtractToolCallsFromText(
                text, out List<MEAI.FunctionCallContent> calls, out _, log);

            Assert.IsTrue(found);
            Assert.AreEqual(1, calls.Count, "Only the convertible call may survive");
            Assert.AreEqual("good_tool", calls[0].Name);
            Assert.IsTrue(log.Warnings.Any(w => w.Contains("bad_tool")),
                "The dropped text-extracted call must be logged, not silently discarded.");
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
                new CoreAISettingsOptions(),
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
        public async Task RequiredToolMode_AllowsProseCompletionAfterFirstToolCall()
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
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { tool },
                ToolMode = MEAI.ChatToolMode.RequireSpecific("my_tool")
            };

            MEAI.ChatResponse response = await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(2, callCount);
            Assert.IsInstanceOf<MEAI.RequiredChatToolMode>(fakeInner.ObservedOptions[0].ToolMode);
            MEAI.ChatOptions followup = fakeInner.ObservedOptions[1];
            Assert.IsTrue(followup.ToolMode == null || followup.ToolMode is MEAI.AutoChatToolMode,
                "After the required call, the model may answer or use another tool; MEAI defines null as Auto.");
            Assert.IsTrue(followup.Tools.OfType<MEAI.AIFunction>().Any(function => function.Name == tool.Name),
                "The tools remain available for optional follow-up work.");
            Assert.AreEqual("done", response.Text);
            Assert.IsInstanceOf<MEAI.RequiredChatToolMode>(options.ToolMode,
                "The caller's original request options must remain unchanged.");
        }

        /// <summary>
        /// Two errors followed by a success reset the counter; a later run of three errors
        /// then aborts the agent after six total iterations.
        /// </summary>
        [Test]
        public async Task SuccessResetsCounter_ThenThreeErrorsStop()
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
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // 2 failures (consecutiveErrors 1,2) + 1 success (reset→0) + 3 failures (1,2,3→guard)
            // + 1 final tools-disabled summary roundtrip (F6) = 7 model calls.
            Assert.AreEqual(7, callCount,
                "Expected 7 calls: 2 fail + 1 success (reset) + 3 fail (stop) + 1 final summary");
        }

        /// <summary>
        /// A success on the third attempt resets the counter so two later errors
        /// followed by a text response do not abort the agent.
        /// </summary>
        [Test]
        public async Task SuccessOnThirdAttempt_ResetsAndContinues()
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
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

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
        public async Task AllSuccess_CompletesNormally()
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
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { successTool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

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
                new CoreAISettingsOptions(),
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
        public async Task DuplicateToolCallsRejected_WhenAllowDuplicatesFalse()
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
                new CoreAISettingsOptions(),
                false,
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // Ожидаем 3 итерации: 1) успешный tool call, 2) дубликат (отклонён), 3) текст
            Assert.AreEqual(3, callCount,
                "После обнаружения дубликата должен сработать rejection, модель переходит к текстовому ответу");
        }

        /// <summary>
        /// Дефект: эхо считалось провальной итерацией, и три повтора подряд («покажи карточку ещё раз»)
        /// упирались в предел ошибок и обрывали ход сообщением об аборте. Эхо — структурированный no-op:
        /// модель получает <c>ok:true, duplicate:true</c>, счётчик сбоев не двигается, ход продолжается.
        /// </summary>
        [Test]
        public async Task RepeatedEchoes_AreNoOps_AndNeverAbortTheTurn()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                // Four identical calls in a row, then the model finishes with text.
                return callCount <= 4
                    ? MakeToolCallResponse("show_card", "call_" + callCount,
                        new Dictionary<string, object> { { "id", "card-7" } })
                    : MakeTextResponse("done");
            });

            int executions = 0;
            MEAI.AIFunction tool = MakeAIFunction("show_card", _ =>
            {
                executions++;
                return Task.FromResult<object>("{\"Success\":true}");
            });

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                new CoreAISettingsOptions(),
                false, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            MEAI.ChatResponse response = await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(5, callCount,
                "Three echoes must NOT trip the consecutive-error guard: the model keeps its turn and ends it with text");
            Assert.AreEqual(1, executions, "The card is shown once; echoes are not executed again");
            Assert.AreEqual("done", SmartToolCallingChatClient.ConcatenateAssistantTextContents(response));
            List<string> toolResultsSeenByModel = fakeInner.ObservedMessages
                .SelectMany(messages => messages)
                .SelectMany(m => m.Contents.OfType<MEAI.FunctionResultContent>())
                .Select(r => r.Result?.ToString() ?? "")
                .ToList();
            Assert.IsTrue(toolResultsSeenByModel.Any(r => r.Contains("\"duplicate\":true") && r.Contains("\"ok\":true")),
                "The model must see the structured no-op, not a failure text");
            Assert.IsTrue(client.LastExecutedToolCalls.Where(t => t.Source == "duplicate").All(t => t.Success),
                "Echo traces are successes: the user line must not read 'Tool call failed'");
        }

        /// <summary>
        /// Дефект: когда предел ошибок достигнут и сводочный ход текста не дал, наружу уходил сырой
        /// служебный JSON <c>{"error":"Agent aborted …"}</c> как реплика ассистента.
        /// </summary>
        [Test]
        public async Task MaxErrorsWithoutSummaryText_ReturnsPlainProse_NotRawJson()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                // Even the tools-disabled summary turn comes back as a tool call (no text).
                return MakeToolCallResponse("my_tool", "call_" + callCount);
            });

            MEAI.AIFunction failTool = MakeAIFunction("my_tool", _ =>
                Task.FromResult<object>("{\"Success\":false,\"Error\":\"world not loaded\"}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { failTool } };
            MEAI.ChatResponse response = await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            string text = SmartToolCallingChatClient.ConcatenateAssistantTextContents(response);
            Assert.IsFalse(text.TrimStart().StartsWith("{"), "The user must never see a raw service JSON: " + text);
            StringAssert.Contains("tool calls in a row failed", text);
            StringAssert.Contains("my_tool", text);
        }

        /// <summary>
        /// Different arguments are not duplicates even when global duplicate suppression is enabled.
        /// </summary>
        [Test]
        public async Task DifferentArgumentsNotTreatedAsDuplicate()
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
                new CoreAISettingsOptions(),
                false,
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(4, callCount,
                "Три разных аргумента + текстовый ответ = 4 итерации, блокировки не должно быть");
        }

        /// <summary>
        /// Tools with <see cref="ILlmTool.AllowDuplicates"/> are exempt from duplicate suppression.
        /// </summary>
        [Test]
        public async Task PerToolAllowDuplicates_OverridesGlobal()
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
                new CoreAISettingsOptions(),
                false,
                new List<Ai.ILlmTool> { new AllowDupTool("always_ok") }, "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(4, callCount,
                "Инструмент с AllowDuplicates=true не триггерит rejection");
        }

        // ===================== Edge Cases =====================

        /// <summary>
        /// Missing tool calls return a not-found result and increment the consecutive-error counter.
        /// </summary>
        [Test]
        public async Task ToolNotFound_CountsAsError()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return MakeToolCallResponse("missing_tool", "call_" + callCount);
            });

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                new CoreAISettingsOptions(),
                true, // отключаем дубликаты, чтобы увидеть именно not-found
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool>() };
            await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // 3 not-found failures trip the guard, then one final tools-disabled summary roundtrip (F6).
            Assert.AreEqual(4, callCount,
                "3 consecutive not-found tool calls trip the guard, then 1 final summary roundtrip");
        }

        /// <summary>
        /// Tool exceptions are caught, converted to function results, and counted as errors.
        /// </summary>
        [Test]
        public async Task ToolThrowsException_HandledAsError()
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
                new CoreAISettingsOptions(),
                true,
                new List<Ai.ILlmTool>(), "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            // 3 tool-body throws trip the guard, then one final tools-disabled summary roundtrip (F6).
            Assert.AreEqual(4, callCount,
                "3 consecutive tool exceptions trip the guard, then 1 final summary roundtrip");
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
        /// A tool that declares <c>EndsTurn</c> and SUCCEEDS closes the turn: the loop must not send its
        /// result back for another roundtrip, because the model would then react to an answer the student
        /// has not given yet. The prose said BEFORE the call is what the student keeps reading.
        /// </summary>
        [Test]
        public async Task TurnEndingToolSucceeded_StopsLoopAndKeepsProseSaidBeforeTheCall()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return iteration == 1
                    ? MakeToolCallResponseWithText("spawn_quiz", "call_1", "Проверь себя:")
                    : MakeTextResponse("Верно!");
            });

            MEAI.AIFunction quizTool = MakeAIFunction("spawn_quiz", _ =>
                Task.FromResult<object>("{\"success\":true,\"status\":\"card_shown_waiting_for_student\"}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool> { new TurnEndingTool("spawn_quiz") }, "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { quizTool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, callCount,
                "The tool result must NOT be handed back to the model: no second roundtrip.");
            string text = SmartToolCallingChatClient.ConcatenateAssistantTextContents(response);
            StringAssert.Contains("Проверь себя", text,
                "Prose said before the turn-ending call must survive as the turn's answer.");
            StringAssert.DoesNotContain("Верно", text,
                "The turn ended, so nothing from a would-be next roundtrip may appear.");
            Assert.IsTrue(client.LastExecutedToolCalls.Any(t => t.Name == "spawn_quiz" && t.Success),
                "The executed call's trace must still ride the finished turn.");
        }

        /// <summary>
        /// A FAILED turn-ending tool keeps the ordinary loop: the error result is the only thing the
        /// model can recover from, so cutting the turn there would strand the student with nothing.
        /// </summary>
        [Test]
        public async Task TurnEndingToolFailed_KeepsTheRecoveryRoundtrip()
        {
            int callCount = 0;
            ScriptedChatClient fakeInner = new(iteration =>
            {
                callCount++;
                return iteration == 1
                    ? MakeToolCallResponse("spawn_quiz", "call_1")
                    : MakeTextResponse("Карточку показать не вышло, разберём вслух.");
            });

            MEAI.AIFunction quizTool = MakeAIFunction("spawn_quiz", _ =>
                Task.FromResult<object>("{\"Success\":false,\"Error\":\"no card prefab\"}"));

            SmartToolCallingChatClient client = new(fakeInner, NullLog.Instance,
                new CoreAISettingsOptions(),
                true, new List<Ai.ILlmTool> { new TurnEndingTool("spawn_quiz") }, "TestRole", 3);

            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { quizTool } };
            MEAI.ChatResponse response =
                await client.GetResponseAsync(new List<MEAI.ChatMessage>(), options);

            Assert.AreEqual(2, callCount,
                "A failed turn-ending tool must still get its error feedback roundtrip.");
            StringAssert.Contains("разберём вслух",
                SmartToolCallingChatClient.ConcatenateAssistantTextContents(response));
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task NoneToolMode_PreservesProseAndRejectsUnsolicitedCalls(bool textCall)
        {
            int invocations = 0;
            string prose = "Example: {\"name\":\"save\",\"arguments\":{}}";
            Ai.DelegateLlmTool tool = new("save", "save", (Func<string>)(() =>
            {
                invocations++;
                return "saved";
            }));
            ScriptedChatClient provider = new(iteration => iteration == 1
                ? new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                    textCall ? new List<MEAI.AIContent> { new MEAI.TextContent(prose) }
                    : new List<MEAI.AIContent> { new MEAI.TextContent(prose),
                        new MEAI.FunctionCallContent("unsolicited", tool.Name, new Dictionary<string, object>()) }))
                : MakeTextResponse("unexpected followup"));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance, new CoreAISettingsOptions(),
                false, new List<Ai.ILlmTool> { tool }, "test", allowTextShapedToolCalls: true);
            MEAI.ChatOptions options = new() { ToolMode = MEAI.ChatToolMode.None,
                Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };

            MEAI.ChatResponse result = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);

            Assert.AreEqual(0, invocations, "None is a local execution prohibition even if the provider ignores it.");
            Assert.AreEqual(1, provider.ObservedMessages.Count);
            Assert.AreEqual(prose, result.Text);
            Assert.IsEmpty(client.LastExecutedToolCalls);
            Assert.AreSame(MEAI.ChatToolMode.None, options.ToolMode);
            Assert.AreEqual(1, options.Tools.Count, "The caller still owns its options.");
        }

        [Test]
        public async Task NoneToolMode_HonorsSameResponseCapAsAuto()
        {
            string providerText = new('x', 100);
            CoreAISettingsOptions settings = new() { MaxResponseChars = 10 };
            ScriptedChatClient autoProvider = new(_ => MakeTextResponse(providerText));
            ScriptedChatClient disabledProvider = new(_ => MakeTextResponse(providerText));
            SmartToolCallingChatClient automatic = new(autoProvider, NullLog.Instance, settings,
                false, Array.Empty<Ai.ILlmTool>(), "test");
            SmartToolCallingChatClient disabled = new(disabledProvider, NullLog.Instance, settings,
                false, Array.Empty<Ai.ILlmTool>(), "test");
            MEAI.ChatResponse expected = await automatic.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(),
                new MEAI.ChatOptions { ToolMode = MEAI.ChatToolMode.Auto });
            MEAI.ChatResponse actual = await disabled.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(),
                new MEAI.ChatOptions { ToolMode = MEAI.ChatToolMode.None });
            Assert.Less(expected.Text.Length, providerText.Length, "The configured host cap is active.");
            Assert.AreEqual(expected.Text, actual.Text, "Disabling tools must preserve the host response-size policy.");
            Assert.AreEqual(1, disabledProvider.ObservedMessages.Count);
            Assert.IsEmpty(disabled.LastExecutedToolCalls);
        }

        // The user-approval API is marked MEAI001 ("evaluation purposes only") by MEAI itself.
        // The suppression covers exactly the three approval fixtures below and is restored right
        // after them, so any other experimental API in this file still fails the build.
#pragma warning disable MEAI001

        [Test]
        public async Task NoneToolMode_DoesNotExecutePreviouslyApprovedCall()
        {
            int invocations = 0;
            MEAI.AIFunction function = MakeAIFunction("save", _ =>
            {
                invocations++;
                return Task.FromResult<object>("saved");
            });
            ScriptedChatClient provider = new(iteration => iteration == 1
                ? MakeToolCallResponse("save", "approval") : MakeTextResponse("Explain only"));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance, new CoreAISettingsOptions(),
                false, Array.Empty<Ai.ILlmTool>(), "test");
            MEAI.ChatOptions options = new()
                { Tools = new List<MEAI.AITool> { new MEAI.ApprovalRequiredAIFunction(function) } };
            MEAI.ChatResponse pending = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);
            MEAI.FunctionApprovalRequestContent approval = pending.Messages.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionApprovalRequestContent>().Single();
            List<MEAI.ChatMessage> history = pending.Messages.ToList();
            history.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                new List<MEAI.AIContent> { approval.CreateResponse(true) }));
            options.ToolMode = MEAI.ChatToolMode.None;

            MEAI.ChatResponse response = await client.GetResponseAsync(history, options);

            Assert.AreEqual(0, invocations, "Approval does not override this request's None mode.");
            Assert.AreEqual(2, provider.ObservedMessages.Count, "Each request makes exactly one provider call.");
            Assert.AreEqual("Explain only", response.Text);
            Assert.IsEmpty(client.LastExecutedToolCalls);
        }

        [TestCase(true)]
        [TestCase(false)]
        public async Task NativeApprovals_ArePreservedAndOnlyApprovedCallsExecute(bool approved)
        {
            int invocations = 0;
            MEAI.AIFunction function = MakeAIFunction("save", _ =>
            {
                invocations++;
                return Task.FromResult<object>("saved");
            });
            ScriptedChatClient provider = new(iteration => iteration == 1
                ? MakeToolCallResponse("save", "approval_call") : MakeTextResponse("Resolved"));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool>(), "test");
            MEAI.ChatOptions options = new()
                { Tools = new List<MEAI.AITool> { new MEAI.ApprovalRequiredAIFunction(function) }, ToolMode = MEAI.ChatToolMode.RequireAny };

            MEAI.ChatResponse pending = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);
            MEAI.FunctionApprovalRequestContent request = pending.Messages.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionApprovalRequestContent>().Single();
            Assert.AreEqual("approval_call", request.FunctionCall.CallId);
            Assert.AreEqual(0, invocations, "Describing an approval-required function must not execute it.");
            Assert.AreEqual(1, provider.ObservedMessages.Count, "A valid approval request is not a missing-tool retry.");

            List<MEAI.ChatMessage> history = pending.Messages.ToList();
            history.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                new List<MEAI.AIContent> { request.CreateResponse(approved) }));
            MEAI.ChatResponse completed = await client.GetResponseAsync(history,
                new MEAI.ChatOptions { Tools = options.Tools });

            Assert.AreEqual(approved ? 1 : 0, invocations);
            Assert.AreEqual("Resolved", completed.Text);
            Assert.AreEqual(2, provider.ObservedMessages.Count);
            MEAI.FunctionResultContent result = provider.ObservedMessages[1].SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionResultContent>().Single();
            Assert.AreEqual(request.FunctionCall.CallId, result.CallId, "MEAI must pair the approval outcome with its original call.");
            Assert.IsFalse(completed.Messages.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionApprovalRequestContent>().Any());
        }

        [Test]
        public async Task ApprovedIdenticalCalls_InOneBatch_BothExecuteAndPreserveEndsTurn()
        {
            int invocations = 0;
            Ai.DelegateLlmTool tool = new("show", "show", (Func<string>)(() =>
            {
                invocations++;
                return "ok";
            })) { EndsTurn = true, IsMutating = true };
            ScriptedChatClient provider = new(_ => new MEAI.ChatResponse(new MEAI.ChatMessage(
                MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                {
                    new MEAI.FunctionCallContent("first", tool.Name, new Dictionary<string, object>()),
                    new MEAI.FunctionCallContent("second", tool.Name, new Dictionary<string, object>())
                })));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool> { tool }, "test");
            MEAI.ChatOptions options = new()
                { Tools = new List<MEAI.AITool> { new MEAI.ApprovalRequiredAIFunction(tool.CreateAIFunction()) } };
            MEAI.ChatResponse pending = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);
            List<MEAI.AIContent> approvals = pending.Messages.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionApprovalRequestContent>().Select(request => (MEAI.AIContent)request.CreateResponse(true)).ToList();
            Assert.AreEqual(2, approvals.Count);
            List<MEAI.ChatMessage> history = pending.Messages.ToList();
            history.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, approvals));
            MEAI.ChatResponse completed = await client.GetResponseAsync(history, options);
            Assert.AreEqual(2, invocations, "Identical calls issued in one batch are distinct intentional operations.");
            Assert.AreEqual(1, provider.ObservedMessages.Count, "An approved EndsTurn tool prevents the next model request.");
            Assert.IsTrue(client.LastTurnEndedByTool);
            Assert.AreEqual("", completed.Text);
        }

#pragma warning restore MEAI001

        [TestCase(false)]
        [TestCase(true)]
        public async Task LastRoundtripUsage_EmptyFollowupsPreserveLastMeasuredValue_IncludingZero(bool reportsZero)
        {
            ScriptedChatClient provider = new(iteration =>
            {
                MEAI.ChatResponse response = iteration == 1
                    ? MakeToolCallResponse("lookup", "measured") : MakeTextResponse("");
                response.Usage = iteration == 1
                    ? new MEAI.UsageDetails { InputTokenCount = 50, OutputTokenCount = 2 }
                    : reportsZero ? new MEAI.UsageDetails { InputTokenCount = 0, OutputTokenCount = 0 } : null;
                return response;
            });
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool>(), "test");
            MEAI.ChatResponse response = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(),
                new MEAI.ChatOptions { Tools = new List<MEAI.AITool>
                    { MakeAIFunction("lookup", _ => Task.FromResult<object>("ok")) } });
            Assert.Greater(provider.ObservedMessages.Count, 2, "The empty-success recovery path must be exercised.");
            Assert.IsNotNull(client.LastRoundtripUsage);
            Assert.AreEqual(reportsZero ? 0 : 50, client.LastRoundtripUsage.InputTokenCount);
            Assert.AreEqual(50, response.Usage.InputTokenCount, "Whole-turn usage is separate from context width.");
        }

        [Test]
        public async Task NativeLoop_ReturnsFinalProseOnly_AndAccumulatesUsageWithoutChangingLastRoundtrip()
        {
            ScriptedChatClient inner = new(iteration =>
            {
                MEAI.ChatResponse response = iteration == 1
                    ? MakeToolCallResponseWithText("lookup", "one", "Working")
                    : MakeTextResponse("Finished");
                response.Usage = new MEAI.UsageDetails { InputTokenCount = iteration * 10, OutputTokenCount = iteration };
                return response;
            });
            SmartToolCallingChatClient client = new(inner, NullLog.Instance,
                new CoreAISettingsOptions(), true,
                new List<Ai.ILlmTool>(), "test");
            MEAI.ChatResponse result = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(),
                new MEAI.ChatOptions { Tools = new List<MEAI.AITool> { MakeAIFunction("lookup", _ => Task.FromResult<object>("secret tool payload")) } });
            Assert.AreEqual("Finished", result.Text);
            Assert.AreEqual(30, result.Usage.InputTokenCount);
            Assert.AreEqual(3, result.Usage.OutputTokenCount);
            Assert.AreEqual(20, client.LastRoundtripUsage.InputTokenCount);
            Assert.IsFalse(client.LastTurnEndedByTool);
        }

        /// <summary>
        /// Whole-turn accumulation and the last-roundtrip snapshot both go through the shared
        /// <c>LlmUsageAccumulator</c> (native <c>UsageDetails.Add</c>), so vendor counters in
        /// <c>AdditionalCounts</c> — where prompt-cache reads and writes travel — are summed key by key
        /// across roundtrips, and the provider's own usage objects are never mutated in place.
        /// </summary>
        [Test]
        public async Task NativeLoop_SumsAdditionalCountsAcrossRoundtrips_WithoutMutatingProviderUsage()
        {
            List<MEAI.UsageDetails> reported = new();
            ScriptedChatClient inner = new(iteration =>
            {
                MEAI.ChatResponse response = iteration == 1
                    ? MakeToolCallResponseWithText("lookup", "one", "Working")
                    : MakeTextResponse("Finished");
                response.Usage = new MEAI.UsageDetails
                {
                    InputTokenCount = 10,
                    OutputTokenCount = 1,
                    AdditionalCounts = new MEAI.AdditionalPropertiesDictionary<long>
                    {
                        ["prompt_tokens_details.cached_tokens"] = iteration * 4,
                        ["cache_creation_input_tokens"] = 3
                    }
                };
                reported.Add(response.Usage);
                return response;
            });
            SmartToolCallingChatClient client = new(inner, NullLog.Instance,
                new CoreAISettingsOptions(), true,
                new List<Ai.ILlmTool>(), "test");
            MEAI.ChatResponse result = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(),
                new MEAI.ChatOptions { Tools = new List<MEAI.AITool>
                    { MakeAIFunction("lookup", _ => Task.FromResult<object>("payload")) } });

            Assert.AreEqual(2, reported.Count);
            Assert.AreEqual(12L, result.Usage.AdditionalCounts["prompt_tokens_details.cached_tokens"],
                "4 + 8 across two roundtrips.");
            Assert.AreEqual(6L, result.Usage.AdditionalCounts["cache_creation_input_tokens"],
                "3 + 3 across two roundtrips.");
            Assert.AreEqual(4L, reported[0].AdditionalCounts["prompt_tokens_details.cached_tokens"],
                "The first roundtrip's own usage object must stay as the provider reported it.");
            Assert.AreEqual(8L, client.LastRoundtripUsage.AdditionalCounts["prompt_tokens_details.cached_tokens"],
                "The last-roundtrip snapshot is a detached copy of the final roundtrip only.");
            Assert.AreNotSame(reported[1], client.LastRoundtripUsage);
        }

        [Test]
        public async Task EndsTurn_CompletesIssuedBatchOnce_WithoutAnotherModelRoundtrip()
        {
            int modelCalls = 0;
            int firstCalls = 0;
            int siblingCalls = 0;
            ScriptedChatClient inner = new(_ =>
            {
                modelCalls++;
                return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                    new List<MEAI.AIContent>
                    {
                        new MEAI.FunctionCallContent("first", "show", new Dictionary<string, object>()),
                        new MEAI.FunctionCallContent("second", "save", new Dictionary<string, object>())
                    }));
            });
            Ai.DelegateLlmTool ending = new("show", "show", (Func<string>)(() => { firstCalls++; return "ok"; }))
                { EndsTurn = true, IsMutating = true };
            Ai.DelegateLlmTool sibling = new("save", "save", (Func<string>)(() => { siblingCalls++; return "ok"; }))
                { IsMutating = true };
            SmartToolCallingChatClient client = new(inner, NullLog.Instance,
                new CoreAISettingsOptions(), true,
                new List<Ai.ILlmTool> { ending, sibling }, "test");
            await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), new MEAI.ChatOptions
                { Tools = new List<MEAI.AITool> { ending.CreateAIFunction(), sibling.CreateAIFunction() } });
            Assert.AreEqual(1, modelCalls, "EndsTurn forbids the next model request.");
            Assert.AreEqual(1, firstCalls);
            Assert.AreEqual(1, siblingCalls, "The already-issued batch still completes, once per call.");
            Assert.AreEqual(2, client.LastExecutedToolCalls.Count);
            Assert.IsTrue(client.LastTurnEndedByTool);
        }

        [Test]
        public async Task NativeLoop_ServerHandledCallBesideLocalCall_IsNotInvokedAgain()
        {
            int serverInvocations = 0;
            int localInvocations = 0;
            ScriptedChatClient inner = new(iteration => iteration == 1
                ? new MEAI.ChatResponse(new List<MEAI.ChatMessage>
                {
                    new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                    {
                        new MEAI.FunctionCallContent("server", "already_done", new Dictionary<string, object>()),
                        new MEAI.FunctionCallContent("local", "local_work", new Dictionary<string, object>())
                    }),
                    new(MEAI.ChatRole.Tool, new List<MEAI.AIContent> { new MEAI.FunctionResultContent("server", "ok") })
                })
                : MakeTextResponse("Finished"));
            SmartToolCallingChatClient client = new(inner, NullLog.Instance,
                new CoreAISettingsOptions(), true,
                new List<Ai.ILlmTool>(), "test");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool>
            {
                MakeAIFunction("already_done", _ => { serverInvocations++; return Task.FromResult<object>("SERVER_RAN"); }),
                MakeAIFunction("local_work", _ => { localInvocations++; return Task.FromResult<object>("LOCAL_RESULT"); })
            } };
            MEAI.ChatResponse result = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);
            Assert.AreEqual(0, serverInvocations, "A server-delivered result proves that call is already handled.");
            Assert.AreEqual(1, localInvocations);
            Assert.AreEqual("Finished", result.Text);
            // The service's own answer and the local tool's answer must reach the model paired with the
            // call each of them belongs to. A positional hand-off swapped them here: MEAI numbers its
            // invocations over BOTH calls while policy executed only the local one.
            MEAI.FunctionResultContent local = ResultForCall(inner.ObservedMessages[1], "local");
            MEAI.FunctionResultContent server = ResultForCall(inner.ObservedMessages[1], "server");
            StringAssert.Contains("LOCAL_RESULT", local.Result?.ToString() ?? "",
                "The local tool's own answer belongs to the local call.");
            StringAssert.Contains("ok", server.Result?.ToString() ?? "",
                "The service's own answer belongs to the call the service resolved.");
        }

        [Test]
        public async Task ToolOnlyEndsTurn_IsSuccessfulEmptyCompletion_AndSignalResetsOnNextRequest()
        {
            ScriptedChatClient inner = new(iteration => iteration == 1
                ? MakeToolCallResponse("show", "one") : MakeTextResponse("Next request"));
            SmartToolCallingChatClient client = new(inner, NullLog.Instance,
                new CoreAISettingsOptions(), true,
                new List<Ai.ILlmTool> { new TurnEndingTool("show") }, "test");
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { MakeAIFunction("show", _ => Task.FromResult<object>("ok")) } };
            MEAI.ChatResponse result = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);
            Assert.AreEqual("", result.Text);
            Assert.IsTrue(client.LastTurnEndedByTool);
            MEAI.ChatResponse next = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);
            Assert.AreEqual("Next request", next.Text);
            Assert.IsFalse(client.LastTurnEndedByTool);
        }

        // ============ Mixed batches: a call policy does NOT execute next to one it does ============
        // Every test below issues ONE model turn containing two calls, where exactly one of them is
        // handed to CoreAI policy. The other is answered elsewhere - by policy itself (an invented
        // name) or by MEAI's approval flow - and therefore never reaches the function invoker. MEAI
        // still numbers its invocations over the WHOLE message, so anything that maps a policy result
        // by position drifts as soon as such a neighbour exists.

        /// <summary>
        /// An invented name in front of a real call must not corrupt the real call's answer: the real
        /// tool runs once and the model reads THAT tool's output, not an invocation failure.
        /// </summary>
        [Test]
        public async Task MixedBatch_InventedNameBeforeRealCall_ModelReadsTheRealToolResult()
        {
            int invocations = 0;
            Ai.DelegateLlmTool real = new("real_tool", "real tool", (Func<string>)(() =>
            {
                invocations++;
                return "REAL_RESULT";
            }));
            ScriptedChatClient provider = new(iteration => iteration == 1
                ? MakeMultiToolCallResponse(("invented_tool", "call_invented"), ("real_tool", "call_real"))
                : MakeTextResponse("Finished"));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool> { real }, "test", 3);
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { real.CreateAIFunction() } };

            MEAI.ChatResponse response = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, invocations, "The bound tool runs exactly once.");
            Assert.AreEqual(2, provider.ObservedMessages.Count, "One tool roundtrip, then the model's answer.");
            string results = ToolResultTextOf(provider.ObservedMessages[1]);
            StringAssert.Contains("REAL_RESULT", results,
                "The model must be told what the tool actually returned.");
            Assert.IsFalse(results.Contains("Function failed"),
                "A tool that ran successfully must never be reported to the model as an invocation failure.");
            // Exactly one answer per call id: the invented call gets CoreAI's own answer - the one that
            // names the tools that DO exist - and not, on top of it, MEAI's terse "function not found".
            MEAI.FunctionResultContent invented = ResultForCall(provider.ObservedMessages[1], "call_invented");
            StringAssert.Contains("real_tool", invented.Result?.ToString() ?? "",
                "CoreAI's unknown-tool answer names the tools that do exist.");
            ResultForCall(provider.ObservedMessages[1], "call_real");
            Assert.AreEqual("Finished", response.Text);
        }

        /// <summary>
        /// The same mixed batch with the invented name LAST: a successful turn-ending tool still has to
        /// close the turn without one more model request.
        /// </summary>
        [Test]
        public async Task MixedBatch_InventedNameAfterTurnEndingCall_StillClosesTheTurn()
        {
            int invocations = 0;
            Ai.DelegateLlmTool show = new("show", "shows a card", (Func<string>)(() =>
            {
                invocations++;
                return "ok";
            })) { EndsTurn = true };
            ScriptedChatClient provider = new(_ =>
                MakeMultiToolCallResponse(("show", "call_show"), ("invented_tool", "call_invented")));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool> { show }, "test", 3);
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { show.CreateAIFunction() } };

            await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, invocations, "The turn-ending tool runs exactly once.");
            Assert.IsTrue(client.LastTurnEndedByTool, "A successful EndsTurn tool ends the turn.");
            Assert.AreEqual(1, provider.ObservedMessages.Count,
                "An invented name standing after the turn-ending call must not buy the model another turn.");
        }

        /// <summary>
        /// A model that keeps repeating the same mixed batch is stopped by the consecutive-error guard.
        /// The roundtrip budget (20 by default) is the wrong stopper here: it costs a full twenty
        /// provider requests, and the repeated real call is echo-suppressed rather than re-executed.
        /// </summary>
        [Test]
        public async Task MixedBatch_RepeatedInventedName_StopsOnErrorGuardNotRoundtripBudget()
        {
            int invocations = 0;
            Ai.DelegateLlmTool real = new("real_tool", "real tool", (Func<string>)(() =>
            {
                invocations++;
                return "REAL_RESULT";
            }));
            ScriptedChatClient provider = new(iteration =>
                MakeMultiToolCallResponse(("invented_tool", "call_invented_" + iteration),
                    ("real_tool", "call_real_" + iteration)));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool> { real }, "test", 3);
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { real.CreateAIFunction() } };

            MEAI.ChatResponse response = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);

            Assert.AreEqual(1, invocations,
                "The identical real call is echo-suppressed after the first turn, not executed again.");
            // Turn 1 pairs a failure with a real success (progress, counter reset); turns 2-4 pair the
            // failure with an echo no-op, so the counter climbs 1-2-3 and trips on turn 4. One final
            // tools-disabled summary roundtrip follows, which this scripted model answers with tool
            // calls again - so the canned max-errors prose is what the caller gets.
            Assert.AreEqual(5, provider.ObservedMessages.Count,
                "The error guard must end the run long before the 20-roundtrip budget.");
            StringAssert.Contains("tool calls in a row failed", response.Text,
                "The run must end through the consecutive-error guard, not the roundtrip cap.");
        }

        // The user-approval API is marked MEAI001 ("evaluation purposes only") by MEAI itself.
#pragma warning disable MEAI001

        /// <summary>
        /// An approval-required call is withheld from CoreAI policy the same way an invented name is,
        /// so the mixed shape needs its own coverage. MEAI 9.10.2 settles it atomically: one guarded
        /// call turns the WHOLE batch into approval requests, so nothing runs behind the user's back -
        /// and once the user says yes, each call must reach the model with its OWN result.
        /// </summary>
        [Test]
        public async Task MixedBatch_ApprovalRequiredBesideOrdinaryCall_AsksForBothThenDeliversBothResults()
        {
            int ordinaryInvocations = 0;
            int guardedInvocations = 0;
            Ai.DelegateLlmTool guarded = new("guarded_tool", "needs approval", (Func<string>)(() =>
            {
                guardedInvocations++;
                return "GUARDED_RESULT";
            }));
            Ai.DelegateLlmTool ordinary = new("ordinary_tool", "ordinary tool", (Func<string>)(() =>
            {
                ordinaryInvocations++;
                return "ORDINARY_RESULT";
            }));
            ScriptedChatClient provider = new(iteration => iteration == 1
                ? MakeMultiToolCallResponse(("guarded_tool", "call_guarded"), ("ordinary_tool", "call_ordinary"))
                : MakeTextResponse("Finished"));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool> { guarded, ordinary }, "test", 3);
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool>
                {
                    new MEAI.ApprovalRequiredAIFunction(guarded.CreateAIFunction()),
                    ordinary.CreateAIFunction()
                }
            };

            MEAI.ChatResponse pending = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);

            Assert.AreEqual(0, guardedInvocations, "An approval-required call must not execute unapproved.");
            Assert.AreEqual(0, ordinaryInvocations,
                "Its neighbour must not run either: the user is answering about the whole batch.");
            List<MEAI.FunctionApprovalRequestContent> requests = pending.Messages
                .SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionApprovalRequestContent>().ToList();
            CollectionAssert.AreEquivalent(new[] { "call_guarded", "call_ordinary" },
                requests.Select(request => request.FunctionCall.CallId).ToList(),
                "Every call of the batch must reach the caller as its own approval request.");
            Assert.AreEqual(1, provider.ObservedMessages.Count, "A pending approval is not a retry.");

            List<MEAI.ChatMessage> history = pending.Messages.ToList();
            history.Add(new MEAI.ChatMessage(MEAI.ChatRole.User,
                requests.Select(request => (MEAI.AIContent)request.CreateResponse(true)).ToList()));
            MEAI.ChatResponse completed = await client.GetResponseAsync(history, options);

            Assert.AreEqual(1, guardedInvocations, "The approved guarded tool runs exactly once.");
            Assert.AreEqual(1, ordinaryInvocations, "The approved ordinary tool runs exactly once.");
            Assert.AreEqual("Finished", completed.Text);
            string results = ToolResultTextOf(provider.ObservedMessages[1]);
            StringAssert.Contains("GUARDED_RESULT", results, "Each approved call must carry its own answer.");
            StringAssert.Contains("ORDINARY_RESULT", results, "Each approved call must carry its own answer.");
            Assert.IsFalse(results.Contains("Function failed"),
                "A tool that ran successfully must never be reported to the model as an invocation failure.");
        }

        /// <summary>
        /// Approval-required call standing AFTER a turn-ending one: once approved, the successful
        /// turn-ending tool still closes the turn instead of buying the model another request.
        /// </summary>
        [Test]
        public async Task MixedBatch_ApprovalRequiredAfterTurnEndingCall_ClosesTheTurnOnceApproved()
        {
            int invocations = 0;
            Ai.DelegateLlmTool guarded = new("guarded_tool", "needs approval", (Func<string>)(() => "guarded"));
            Ai.DelegateLlmTool show = new("show", "shows a card", (Func<string>)(() =>
            {
                invocations++;
                return "ok";
            })) { EndsTurn = true };
            ScriptedChatClient provider = new(_ =>
                MakeMultiToolCallResponse(("show", "call_show"), ("guarded_tool", "call_guarded")));
            SmartToolCallingChatClient client = new(provider, NullLog.Instance,
                new CoreAISettingsOptions(), false,
                new List<Ai.ILlmTool> { show, guarded }, "test", 3);
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool>
                {
                    show.CreateAIFunction(),
                    new MEAI.ApprovalRequiredAIFunction(guarded.CreateAIFunction())
                }
            };

            MEAI.ChatResponse pending = await client.GetResponseAsync(Array.Empty<MEAI.ChatMessage>(), options);
            Assert.AreEqual(0, invocations, "Nothing runs while the batch waits for a decision.");
            List<MEAI.AIContent> approvals = pending.Messages.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionApprovalRequestContent>()
                .Select(request => (MEAI.AIContent)request.CreateResponse(true)).ToList();
            Assert.AreEqual(2, approvals.Count);

            List<MEAI.ChatMessage> history = pending.Messages.ToList();
            history.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, approvals));
            await client.GetResponseAsync(history, options);

            Assert.AreEqual(1, invocations, "The turn-ending tool runs exactly once.");
            Assert.IsTrue(client.LastTurnEndedByTool, "A successful EndsTurn tool ends the turn.");
            Assert.AreEqual(1, provider.ObservedMessages.Count,
                "An approved batch whose turn-ending tool succeeded must not ask the model again.");
        }

#pragma warning restore MEAI001

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

        /// <summary>
        /// Tool that hands control to a human and therefore ends the model's turn on success.
        /// </summary>
        private sealed class TurnEndingTool : Ai.ILlmTool
        {
            public TurnEndingTool(string name)
            {
                Name = name;
            }

            public string Name { get; }
            public string Description => "shows a card and waits for the student";
            public string ParametersSchema => "{}";
            public bool AllowDuplicates => true;
            public bool EndsTurn => true;
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
        /// Creates a chat response where the model said something visible AND called a tool in the same
        /// turn — the shape a teacher agent produces right before a card is shown.
        /// </summary>
        private static MEAI.ChatResponse MakeToolCallResponseWithText(string toolName, string callId,
            string text)
        {
            MEAI.ChatMessage msg = new(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
            {
                new MEAI.FunctionCallContent(callId, toolName, new Dictionary<string, object>()),
                new MEAI.TextContent(text)
            });
            return new MEAI.ChatResponse(msg);
        }

        /// <summary>
        /// Creates one assistant turn carrying several tool calls in the given order.
        /// </summary>
        private static MEAI.ChatResponse MakeMultiToolCallResponse(params (string ToolName, string CallId)[] calls)
        {
            List<MEAI.AIContent> contents = calls
                .Select(call => (MEAI.AIContent)new MEAI.FunctionCallContent(call.CallId, call.ToolName,
                    new Dictionary<string, object>()))
                .ToList();
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, contents));
        }

        /// <summary>
        /// The single tool result answering <paramref name="callId"/> in one provider request.
        /// Fails the test when the call is unanswered or answered twice - both break provider pairing.
        /// </summary>
        private static MEAI.FunctionResultContent ResultForCall(IEnumerable<MEAI.ChatMessage> request,
            string callId)
        {
            List<MEAI.FunctionResultContent> matches = request.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionResultContent>()
                .Where(result => result.CallId == callId).ToList();
            Assert.AreEqual(1, matches.Count, $"Call '{callId}' must be answered exactly once.");
            return matches[0];
        }

        /// <summary>
        /// Every tool result the model would read in one provider request, joined for assertion.
        /// </summary>
        private static string ToolResultTextOf(IEnumerable<MEAI.ChatMessage> request)
        {
            return string.Join("\n", request.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionResultContent>()
                .Select(result => result.Result?.ToString() ?? ""));
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
        /// Exercises the shared history policy through its public typed boundary,
        /// mutating <paramref name="messages"/> in place exactly as the production loop would.
        /// </summary>
        private static void InvokeTrim(List<MEAI.ChatMessage> messages, int maxToolMessages)
        {
            ToolCallHistoryTrimmer.Trim(messages, maxToolMessages);
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
        /// Logger that records warning lines so tests can assert on dropped-call diagnostics.
        /// </summary>
        private sealed class RecordingLog : ILog
        {
            public List<string> Warnings { get; } = new();

            public void Debug(string message, string tag = null)
            {
            }

            public void Info(string message, string tag = null)
            {
            }

            public void Warn(string message, string tag = null)
            {
                Warnings.Add(message);
            }

            public void Error(string message, string tag = null)
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
