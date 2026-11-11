using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
#endif
using NUnit.Framework;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// Runs the prompt-regression fixture set (<see cref="PromptRegressionFixtures"/>). The scripted half pins, per
    /// built-in role and input, the request a shipped composition sends (universal prefix, role prompt, offered
    /// tools, chat history) and that the recorded tool choice is one the request actually offers. The explicit half
    /// sends the same requests to a real OpenAI-compatible model and checks its first tool choice.
    /// </summary>
    public sealed class PromptRegressionEditModeTests
    {
        private const string EndpointVariable = "COREAI_PROMPT_EVAL_BASE_URL";
        private const string ModelVariable = "COREAI_PROMPT_EVAL_MODEL";
        private const string ApiKeyVariable = "COREAI_PROMPT_EVAL_API_KEY";

        private static IEnumerable<PromptFixtureCase> Fixtures()
        {
            return PromptRegressionFixtures.All;
        }

        [Test]
        public void EveryBuiltInRole_HasAtLeastTwoFixtures_AndToolRolesHaveAToolCase()
        {
            foreach (string roleId in BuiltInAgentRoleIds.AllBuiltInRoles)
            {
                List<PromptFixtureCase> cases = PromptRegressionFixtures.All.Where(c => c.RoleId == roleId).ToList();
                Assert.GreaterOrEqual(cases.Count, 2, roleId);
                bool offersRoleTools = cases.Any(c => c.OfferedTools.Any(tool => tool != "memory"));
                if (offersRoleTools)
                {
                    Assert.IsTrue(cases.Any(c => c.AcceptableFirstTools.Any(tool => tool != "memory")),
                        roleId + " offers its own tools, so one fixture must expect one of them");
                }
            }

            CollectionAssert.AllItemsAreUnique(PromptRegressionFixtures.All.Select(c => c.Name));
        }

        [TestCaseSource(nameof(Fixtures))]
        public async Task RequestShape_MatchesTheFixture(PromptFixtureCase fixture)
        {
            (LlmCompletionRequest first, LlmCompletionRequest second) =
                await PromptRegressionFixtures.ComposeRequestsAsync(fixture, CancellationToken.None);

            CollectionAssert.AreEquivalent(fixture.OfferedTools, AgentRoleTestHarness.ToolNames(first),
                "offered tools");
            StringAssert.StartsWith(CoreAISettings.DefaultUniversalSystemPromptPrefix, first.SystemPrompt,
                "the package universal prefix opens every built-in role's prompt");
            StringAssert.Contains(fixture.SystemPromptMarker, first.SystemPrompt, "role prompt");
            StringAssert.Contains(fixture.Input, first.UserPayload);
            Assert.AreEqual(fixture.ExpectHistory, AgentRoleTestHarness.HistoryTurnCount(second) > 0,
                "chat history on the second turn");
        }

        [TestCaseSource(nameof(Fixtures))]
        public async Task RecordedToolChoice_IsOfferedAndReadBackByTheRunner(PromptFixtureCase fixture)
        {
            (LlmCompletionRequest first, _) =
                await PromptRegressionFixtures.ComposeRequestsAsync(fixture, CancellationToken.None);
            RecordedModel model = new(fixture.RecordedFirstTool);

            string chosen = await PromptRegressionFixtures.FirstToolCallAsync(model, first, CancellationToken.None);

            Assert.AreEqual(fixture.RecordedFirstTool, chosen);
            CollectionAssert.AreEquivalent(fixture.OfferedTools, model.OfferedFunctionNames,
                "every offered tool reaches the provider as a function");
            if (chosen != null)
            {
                CollectionAssert.Contains(model.OfferedFunctionNames, chosen,
                    "a fixture must never expect a tool the composition does not wire");
            }
        }

        [Test]
        public async Task Runner_DetectsAWrongToolChoice()
        {
            PromptFixtureCase merchant = PromptRegressionFixtures.All.First(c => c.Name == "Merchant_Browses");
            (LlmCompletionRequest first, _) =
                await PromptRegressionFixtures.ComposeRequestsAsync(merchant, CancellationToken.None);

            string chosen = await PromptRegressionFixtures.FirstToolCallAsync(new RecordedModel("memory"), first,
                CancellationToken.None);

            CollectionAssert.DoesNotContain(merchant.AcceptableFirstTools, chosen,
                "negative twin: a model that skips get_inventory is reported, not accepted");
        }

#if COREAI_LLM
        /// <summary>
        /// Sends every fixture to a real model and fails with the list of wrong first tool choices. Configure an
        /// OpenAI-compatible endpoint with COREAI_PROMPT_EVAL_BASE_URL, COREAI_PROMPT_EVAL_MODEL and (optionally)
        /// COREAI_PROMPT_EVAL_API_KEY; the per-case results are written to the test output.
        /// </summary>
        [Test]
        [Explicit("Sends real requests to the model named by COREAI_PROMPT_EVAL_* environment variables.")]
        [Category("PromptEval")]
        public async Task RealModel_PicksTheExpectedFirstTool()
        {
            string endpoint = Environment.GetEnvironmentVariable(EndpointVariable);
            string modelId = Environment.GetEnvironmentVariable(ModelVariable);
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(modelId))
            {
                Assert.Ignore($"Set {EndpointVariable} and {ModelVariable} to run the prompt evaluation.");
            }

            OpenAiHttpOptions http = new()
            {
                ApiBaseUrl = endpoint,
                ApiKey = Environment.GetEnvironmentVariable(ApiKeyVariable) ?? "",
                Model = modelId,
                MaxTokens = 1024,
                RequestTimeoutSeconds = 120,
                LogLlmInput = false,
                LogLlmOutput = false
            };
            using MeaiOpenAiChatClient model = new(http);
            StringBuilder report = new();
            List<string> wrong = new();
            foreach (PromptFixtureCase fixture in PromptRegressionFixtures.All)
            {
                (LlmCompletionRequest first, _) =
                    await PromptRegressionFixtures.ComposeRequestsAsync(fixture, CancellationToken.None);
                string chosen = await PromptRegressionFixtures.FirstToolCallAsync(model, first, CancellationToken.None);
                bool ok = fixture.AcceptableFirstTools.Length == 0
                    ? chosen == null || chosen == "memory"
                    : fixture.AcceptableFirstTools.Contains(chosen);
                report.AppendLine($"| {fixture.Name} | {Describe(fixture.AcceptableFirstTools)} | {chosen ?? "(prose)"} | {(ok ? "pass" : "FAIL")} |");
                if (!ok)
                {
                    wrong.Add($"{fixture.Name}: expected {Describe(fixture.AcceptableFirstTools)}, got {chosen ?? "(prose)"}");
                }
            }

            TestContext.WriteLine($"Model {modelId} at {endpoint}");
            TestContext.WriteLine("| Fixture | Expected first tool | Chosen | Result |");
            TestContext.WriteLine("|---|---|---|---|");
            TestContext.Write(report.ToString());
            Assert.IsEmpty(wrong, string.Join("\n", wrong));
        }
#endif

        private static string Describe(string[] tools)
        {
            return tools.Length == 0 ? "(prose)" : string.Join(" or ", tools);
        }

        /// <summary>A recorded transcript: answers with one tool call, or with prose when no tool is recorded.</summary>
        private sealed class RecordedModel : MEAI.IChatClient
        {
            private readonly string _toolName;

            public RecordedModel(string toolName)
            {
                _toolName = toolName;
            }

            public List<string> OfferedFunctionNames { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                OfferedFunctionNames.Clear();
                foreach (MEAI.AITool tool in options?.Tools ?? new List<MEAI.AITool>())
                {
                    OfferedFunctionNames.Add(tool.Name);
                }

                MEAI.ChatMessage answer = _toolName == null
                    ? new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "A prose answer.")
                    : new MEAI.ChatMessage(MEAI.ChatRole.Assistant, new List<MEAI.AIContent>
                    {
                        new MEAI.FunctionCallContent("call-1", _toolName, new Dictionary<string, object>())
                    });
                return Task.FromResult(new MEAI.ChatResponse(answer));
            }

            public async IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                MEAI.ChatResponse response = await GetResponseAsync(messages, options, cancellationToken);
                foreach (MEAI.ChatResponseUpdate update in response.ToChatResponseUpdates())
                {
                    yield return update;
                }
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
    }
}
