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
    /// Guards for the tool contract promises the docs made and the code did not keep. Every test here is written
    /// from the defect's point of view: a tool author builds behaviour on the promise and writes no defence of
    /// their own, which makes a divergence here more dangerous than an ordinary bug.
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

        // ==================== Defect 5: schema on the native channel ====================

        /// <summary>
        /// Two docs promised it: on the native channel the <c>ParametersSchema</c> text is not sent. The production
        /// path (<c>AppendStableRoleToolContract</c>) printed the schema of every tool regardless of the channel,
        /// and the model received two diverging definitions of the same tool.
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
        /// The prefix has to be byte-stable for the sake of the prompt cache: skipping definitions depends only on
        /// the role and the channel, so two calls with the same inputs produce exactly the same text.
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

        // ==================== Defect 8: classification without stack frames ====================

        /// <summary>
        /// A tool body (not a delegate, an ordinary function with no exception boundary) changes the world and
        /// throws an exception "shaped like a conversion". It used to be classified as an argument binding failure
        /// ("the tool was never invoked", retry-safe) - under IL2CPP any body failure would look exactly like that.
        /// Now anything that escapes the call is "invoked", and the trace stops decorators repeating the mutation.
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
        /// MEAI rejects an unconvertible argument before the body runs. The trace conservatively counts the boundary
        /// as invoked: our own reflection binding check does not duplicate MEAI's mechanism.
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
        /// The <see cref="DelegateLlmTool"/> boundary no longer reads the stack: any body exception, synchronous or
        /// after the first await, becomes an <c>Error: ...</c> result.
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
        /// The same verdict for a delegate tool as for a raw function: an unconvertible argument is rejected by the
        /// structural preflight, so the trace is <c>arg-conversion</c> and counts as never-invoked.
        /// <para>
        /// WHY this test previously expected <c>native</c>/invoked and that expectation was wrong: it was written
        /// during the interim state where the argument-vs-body distinction had been deleted OUTRIGHT. That deletion
        /// was right about the old mechanism - the verdict was guessed by hunting for the delegate's method in the
        /// exception stack, and IL2CPP/WebGL strips those frames, so an exception FROM THE BODY was read as "never
        /// invoked" and retry decorators replayed a turn that had already changed the world. But the distinction did
        /// not stay deleted: it came back as <c>TryBindArgumentsStructurally</c>, which runs MEAI's own coercion
        /// standalone BEFORE the invocation boundary and therefore PROVES the body was never entered instead of
        /// inferring it from a stack. Keeping the interim expectation would have pinned the strictly worse outcome -
        /// a turn that executed nothing being reported as "a tool ran", which blocks retry and fallback.
        /// </para>
        /// <para>
        /// What this case adds over the raw-function one next to it: a delegate tool is invoked through
        /// <c>DelegateExceptionBoundaryAIFunction</c>, a <c>DelegatingAIFunction</c> wrapper. The preflight can only
        /// prove anything while <c>UnderlyingMethod</c> and <c>JsonSerializerOptions</c> reach it through that
        /// wrapper; if a future wrapper stopped forwarding them the preflight would silently fall back to the
        /// conservative verdict, and this test is what notices.
        /// </para>
        /// </summary>
        [Test]
        public async Task DelegateLlmTool_ArgumentCoercionFailure_IsRejectedBeforeInvocation()
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

            Assert.AreEqual(0, sideEffects, "Sanity: the argument really is rejected before the body");
            Assert.IsFalse(result.Succeeded);
            Assert.AreEqual(1, policy.ExecutedTraces.Count);
            Assert.AreEqual("arg-conversion", policy.ExecutedTraces[0].Source,
                "The preflight must see through the delegate's exception-boundary wrapper to UnderlyingMethod");
            Assert.IsFalse(LoggingLlmClientDecorator.TraceIndicatesInvocation(policy.ExecutedTraces[0]),
                "Nothing ran, so a later 429 must stay retryable and failover-eligible");
        }

        // ==================== Defect 6: the result "verbatim" ====================

        /// <summary>
        /// Truncation at <c>MaxToolResultChars</c> must be visible to the model as an explicit marker carrying the original length.
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
        /// An empty result is not replaced by an invented "Success" with an execution message: the envelope says
        /// <c>empty:true</c> outright, so the model can tell "nothing to say" apart from an answer.
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
        /// Everything else reaches the model verbatim, including a refusal from the tool, so that it can fix it.
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
