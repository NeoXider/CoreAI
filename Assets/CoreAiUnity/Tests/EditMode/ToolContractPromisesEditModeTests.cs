using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Logging;
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
#endif

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Стражи обещаний контракта инструментов, которые доки давали, а код не выполнял. Каждый тест
    /// написан от лица дефекта: автор инструмента строит поведение на обещании и не пишет своей защиты,
    /// поэтому расхождение здесь опаснее обычного бага.
    /// </summary>
    public sealed class ToolContractPromisesEditModeTests
    {
        private sealed class StubSettings : ICoreAISettings
        {
            public int MaxLuaRepairRetries => 3;
            public bool EnableMeaiDebugLogging => false;
            public float LlmRequestTimeoutSeconds => 30f;
            public int MaxLlmRequestRetries => 3;
            public bool EnableHttpDebugLogging => false;
            public bool LogTokenUsage => false;
            public bool LogLlmLatency => false;
            public bool LogLlmConnectionErrors => false;
            public int ContextWindowTokens => 4096;
            public string UniversalSystemPromptPrefix => "";
            public float Temperature => 0.7f;
            public int MaxToolCallRetries => 3;
            public bool LogToolCalls => false;
            public bool LogToolCallArguments => false;
            public bool LogToolCallResults => false;
            public bool LogMeaiToolCallingSteps => false;
            public bool AllowDuplicateToolCalls => false;
            public bool EnableStreaming => true;
            public int MaxParallelToolCalls => 1;
            public int MaxToolResultChars { get; set; } = 8000;
            public ILlmAsyncMarshaler ToolInvocationMarshaler => PassThroughLlmAsyncMarshaler.Instance;
        }

        private sealed class StubTool : ILlmTool
        {
            public StubTool(string name, string schema = "{}")
            {
                Name = name;
                ParametersSchema = schema;
            }

            public string Name { get; }
            public string Description => "stub tool description";
            public string ParametersSchema { get; }
            public bool AllowDuplicates => false;
        }

        private const string CountSchema =
            "{\"type\":\"object\",\"properties\":{\"count\":{\"type\":\"integer\",\"description\":\"How many\"}},\"required\":[\"count\"]}";

        // ==================== Дефект 5: схема на нативном канале ====================

        /// <summary>
        /// Два дока обещали: на нативном канале текст <c>ParametersSchema</c> не отправляется. Боевой
        /// путь (<c>AppendStableRoleToolContract</c>) печатал схему каждого инструмента независимо от
        /// канала, и модель получала два расходящихся определения одного инструмента.
        /// </summary>
        [Test]
        public void StableRoleContract_NativeChannel_CarriesNoTextualDefinitions()
        {
            ILlmTool[] tools = { new StubTool("buy_item", CountSchema) };

            string native = AiToolContractPromptFormatter.AppendStableRoleToolContract(
                "sys", tools, new StubSettings(), supportsNativeToolCalling: true);
            string textShaped = AiToolContractPromptFormatter.AppendStableRoleToolContract(
                "sys", tools, new StubSettings(), supportsNativeToolCalling: false);

            StringAssert.Contains("## Tool Contract", native, "The calling rules stay in the prefix");
            StringAssert.DoesNotContain("Role tool definitions:", native);
            StringAssert.DoesNotContain("schema:", native);
            StringAssert.DoesNotContain("How many", native,
                "A hand-written schema must not reach a native endpoint: the delegate-generated one does");

            StringAssert.Contains("Role tool definitions:", textShaped);
            StringAssert.Contains("schema:", textShaped);
            StringAssert.Contains("How many", textShaped, "Text-shaped endpoints have no other channel for it");
        }

        /// <summary>
        /// Префикс обязан быть байт-стабильным ради кэша промпта: пропуск определений зависит только от
        /// роли и канала, поэтому два вызова с теми же входами дают один и тот же текст.
        /// </summary>
        [Test]
        public void StableRoleContract_NativeChannel_IsByteStable()
        {
            ILlmTool[] tools = { new StubTool("z_tool", CountSchema), new StubTool("a_tool") };

            string first = AiToolContractPromptFormatter.AppendStableRoleToolContract(
                "sys", tools, new StubSettings(), supportsNativeToolCalling: true);
            string second = AiToolContractPromptFormatter.AppendStableRoleToolContract(
                "sys", tools.Reverse().ToArray(), new StubSettings(), supportsNativeToolCalling: true);

            Assert.AreEqual(first, second);
        }

#if COREAI_LLM
        private static ToolExecutionPolicy MakePolicy(StubSettings settings, params ILlmTool[] tools)
        {
            return new ToolExecutionPolicy(NullLog.Instance, settings, tools, false, "Tester");
        }

        private static MEAI.ChatOptions OptionsFor(params MEAI.AIFunction[] functions)
        {
            return new MEAI.ChatOptions { Tools = functions.Cast<MEAI.AITool>().ToList() };
        }

        private static MEAI.AIFunction Function(string name, Delegate body)
        {
            return MEAI.AIFunctionFactory.Create(body,
                new MEAI.AIFunctionFactoryOptions { Name = name, Description = name });
        }

        // ==================== Дефект 8: классификация без стек-фреймов ====================

        /// <summary>
        /// Тело инструмента (не делегат — обычная функция без границы исключений) меняет мир и бросает
        /// исключение «формы преобразования». Раньше оно классифицировалось как сбой привязки аргументов
        /// («инструмент не вызывался», retry-safe) — под IL2CPP так выглядел бы любой сбой тела. Теперь
        /// всё, что вылетело из вызова, — «вызов», и трасса не даёт декораторам повторить мутацию.
        /// </summary>
        [Test]
        public async Task ExecuteSingle_BodyThrowsConversionShapedException_IsRecordedAsInvoked()
        {
            int sideEffects = 0;
            MEAI.AIFunction grant = Function("grant_item", (Func<int, string>)(count =>
            {
                sideEffects += count;
                throw new FormatException("Input string was not in a correct format.");
            }));
            ToolExecutionPolicy policy = MakePolicy(new StubSettings(), new StubTool("grant_item", CountSchema));

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "grant_item", new Dictionary<string, object> { ["count"] = 2 }),
                OptionsFor(grant), CancellationToken.None);

            Assert.AreEqual(2, sideEffects, "Sanity: the body ran and mutated state");
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("native", policy.ExecutedTraces[0].Source,
                "An exception escaping the invocation is an INVOKED call; a never-invoked verdict here would " +
                "let the retry/fallback decorators execute the mutation a second time");
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(policy.ExecutedTraces[0]));
            StringAssert.Contains("matching this schema", result.Result.Result.ToString(),
                "The schema hint is still appended by exception shape — it is text for the model, not a verdict");
        }

        /// <summary>
        /// MEAI отвергает неконвертируемый аргумент до тела. Трасса консервативно считает границу
        /// вызванной: собственная reflection-проверка привязки не дублирует механизм MEAI.
        /// </summary>
        [Test]
        public async Task ExecuteSingle_UnconvertibleArgument_IsRejectedBeforeInvocation()
        {
            int sideEffects = 0;
            MEAI.AIFunction grant = Function("grant_item", (Func<int, string>)(count =>
            {
                sideEffects += count;
                return "ok";
            }));
            ToolExecutionPolicy policy = MakePolicy(new StubSettings(), new StubTool("grant_item", CountSchema));

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "grant_item",
                    new Dictionary<string, object> { ["count"] = "many" }),
                OptionsFor(grant), CancellationToken.None);

            Assert.AreEqual(0, sideEffects, "The body must not run when the arguments cannot bind");
            Assert.IsFalse(result.Succeeded);
            // WHY this now expects the structural preflight and not MEAI's own rejection: the arguments
            // are checked against the method's parameter types BEFORE the invocation boundary, so this
            // failure genuinely proves the body was never entered — which a failure thrown across MEAI's
            // boundary never could. The weaker "native"/possibly-invoked shape is still what a failure
            // INSIDE MEAI produces; it is pinned by the tests around this one.
            Assert.AreEqual("arg-conversion", policy.ExecutedTraces[0].Source);
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(policy.ExecutedTraces[0]),
                "A structural rejection happens before invocation, so a retry cannot repeat a mutation.");
            string text = result.Result.Result.ToString();
            StringAssert.Contains("count", text);
            StringAssert.Contains("matching this schema", text);
        }

        /// <summary>
        /// Граница <see cref="DelegateLlmTool"/> больше не читает стек: любое исключение тела —
        /// синхронное или после первого await — становится результатом <c>Error: …</c>.
        /// </summary>
        [Test]
        public async Task DelegateLlmTool_BodyExceptions_BecomeErrorResults_WithoutStackInspection()
        {
            DelegateLlmTool syncTool = new("sync_tool", "throws synchronously",
                (Func<string>)(() => throw new InvalidCastException("sync boom")));
            DelegateLlmTool asyncTool = new("async_tool", "throws after an await",
                (Func<Task<string>>)(async () =>
                {
                    await Task.Yield();
                    throw new FormatException("async boom");
                }));

            object syncResult = await syncTool.CreateAIFunction()
                .InvokeAsync(new MEAI.AIFunctionArguments(), CancellationToken.None);
            object asyncResult = await asyncTool.CreateAIFunction()
                .InvokeAsync(new MEAI.AIFunctionArguments(), CancellationToken.None);

            Assert.AreEqual("Error: sync boom", syncResult?.ToString());
            Assert.AreEqual("Error: async boom", asyncResult?.ToString());
        }

        /// <summary>
        /// Тот же вердикт для инструмента-делегата: MEAI отвергает неконвертируемый аргумент ДО тела, но
        /// трасса всё равно «вызывался». Раньше здесь стоял источник <c>arg-conversion</c>, полученный
        /// поиском метода делегата в стеке; под IL2CPP/WebGL фреймы срываются, и по тому же признаку
        /// исключение ИЗ ТЕЛА объявлялось «инструмент не вызывался» — декораторы ретрая повторяли ход,
        /// уже изменивший мир. Различение снято целиком, поэтому у безопасной и небезопасной ситуации
        /// теперь ОДИН консервативный вердикт, и стоит он лишь одной несделанной повторной попытки.
        /// </summary>
        [Test]
        public async Task DelegateLlmTool_ArgumentCoercionFailure_IsTracedAsInvoked()
        {
            int sideEffects = 0;
            DelegateLlmTool tool = new("grant_items", "Grant items.", (Func<int, string>)(count =>
            {
                sideEffects += count;
                return "ok";
            }));
            ToolExecutionPolicy policy = MakePolicy(new StubSettings(), tool);
            MEAI.ChatOptions options = new()
            {
                Tools = new List<MEAI.AITool> { tool.CreateAIFunction() }
            };

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "grant_items",
                    new Dictionary<string, object> { ["count"] = "not-an-integer" }),
                options, CancellationToken.None);

            Assert.AreEqual(0, sideEffects, "Sanity: MEAI really does reject the argument before the body");
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("native", policy.ExecutedTraces[0].Source,
                "No stack-shape classification survives: the invocation boundary is the verdict");
            Assert.IsTrue(LoggingLlmClientDecorator.TraceIndicatesInvocation(policy.ExecutedTraces[0]),
                "Retry/fallback must treat it as invoked — the cheap verdict is the safe one");
        }

        // ==================== Дефект 6: результат «дословно» ====================

        /// <summary>
        /// Обрезка по <c>MaxToolResultChars</c> обязана быть видна модели явной пометкой с исходной длиной.
        /// </summary>
        [Test]
        public async Task ExecuteSingle_OversizedResult_IsTruncatedWithVisibleMarker()
        {
            string payload = new string('x', 500);
            MEAI.AIFunction big = Function("big", (Func<string>)(() => payload));
            ToolExecutionPolicy policy = MakePolicy(new StubSettings { MaxToolResultChars = 100 });

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "big", new Dictionary<string, object>()),
                OptionsFor(big), CancellationToken.None);

            string text = result.Result.Result.ToString();
            Assert.IsTrue(result.Succeeded);
            StringAssert.StartsWith(new string('x', 100), text);
            StringAssert.Contains(ToolExecutionPolicy.TruncatedResultMarker, text);
            StringAssert.Contains("500 chars total -> 100 shown", text,
                "The model must be told how much it is NOT seeing");
        }

        /// <summary>
        /// Пустой результат не подменяется выдуманным «Success» с сообщением о выполнении: конверт
        /// явно говорит <c>empty:true</c>, чтобы модель отличала «нечего сказать» от ответа.
        /// </summary>
        [Test]
        public async Task ExecuteSingle_EmptyResult_IsAnHonestEmptyEnvelope()
        {
            MEAI.AIFunction silent = Function("silent", (Func<string>)(() => "   "));
            ToolExecutionPolicy policy = MakePolicy(new StubSettings());

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "silent", new Dictionary<string, object>()),
                OptionsFor(silent), CancellationToken.None);

            Newtonsoft.Json.Linq.JObject json =
                Newtonsoft.Json.Linq.JObject.Parse(result.Result.Result.ToString());
            Assert.IsTrue(result.Succeeded, "An empty result is not a failure signal");
            Assert.IsTrue(json.Value<bool>("empty"));
            Assert.IsTrue(json.Value<bool>("ok"));
            Assert.IsNull(json["Success"], "No invented payload key that a tool never returned");
        }

        /// <summary>
        /// Всё остальное уходит модели дословно — включая отказ инструмента, чтобы она могла его исправить.
        /// </summary>
        [Test]
        public async Task ExecuteSingle_NormalResult_ReachesTheModelVerbatim()
        {
            const string refusal = "{\"ok\":false,\"error\":\"not_enough_materials\",\"missing\":[{\"item_id\":\"iron\"}]}";
            MEAI.AIFunction craft = Function("craft", (Func<string>)(() => refusal));
            ToolExecutionPolicy policy = MakePolicy(new StubSettings());

            ToolExecutionPolicy.ToolCallResult result = await policy.ExecuteSingleAsync(
                new MEAI.FunctionCallContent("c1", "craft", new Dictionary<string, object>()),
                OptionsFor(craft), CancellationToken.None);

            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(refusal, result.Result.Result.ToString());
        }
#endif
    }
}
