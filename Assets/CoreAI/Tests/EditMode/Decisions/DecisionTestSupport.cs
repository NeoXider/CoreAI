using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Decisions;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
#endif

namespace CoreAI.Core.Tests.EditMode.Decisions
{
    /// <summary>
    /// Response and error bodies written from the documented shapes (TypeSafe API reference, llama.cpp server
    /// README, Ollama and OpenRouter docs, OpenAI chat logprobs). None of them was recorded from a live server.
    /// </summary>
    internal static class DecisionFixtures
    {
        public const string TypeSafeResponse =
            "{\"model\":\"jev-1.14-20261001\",\"answers\":{" +
            "\"hostile\":{\"type\":\"noul\",\"noul\":0.82}," +
            "\"action\":{\"type\":\"choice\",\"choice\":\"flee\",\"confidence\":0.71," +
            "\"probabilities\":{\"attack\":0.12,\"flee\":0.79,\"trade\":0.09}}," +
            "\"danger\":{\"type\":\"score\",\"score\":2.4,\"confidence\":0.66," +
            "\"legend\":{\"0\":\"safe\",\"1\":\"wary\",\"2\":\"risky\",\"3\":\"deadly\"}," +
            "\"probabilities\":{\"0\":0.05,\"1\":0.15,\"2\":0.15,\"3\":0.65}}}," +
            "\"usage\":{\"input_tokens\":174,\"output_tokens\":3}}";

        public const string OllamaResponse =
            "{\"model\":\"nimble\",\"answers\":{\"label\":{\"type\":\"choice\",\"choice\":\"bug\"," +
            "\"probabilities\":{\"billing\":0.0125,\"bug\":0.9781,\"account\":0.0093},\"confidence\":0.8906}}," +
            "\"usage\":{\"input_tokens\":174,\"output_tokens\":1}}";

        public const string LlamaCppResponse =
            "{\"model\":\"openjev\",\"answers\":{" +
            "\"route\":{\"type\":\"choice\",\"choice\":\"billing\",\"probabilities\":{\"billing\":0.91,\"shipping\":0.06,\"technical\":0.03},\"confidence\":0.88}," +
            "\"refund\":{\"type\":\"noul\",\"noul\":0.6328}," +
            "\"urgency\":{\"type\":\"score\",\"score\":1.7,\"legend\":{\"0\":\"can wait\",\"1\":\"this week\",\"2\":\"today\",\"3\":\"right now\"}," +
            "\"probabilities\":{\"0\":0.1,\"1\":0.25,\"2\":0.48,\"3\":0.17},\"confidence\":0.41}}," +
            "\"usage\":{\"input_tokens\":212,\"output_tokens\":0}}";

        public const string OpenRouterRefusalResponse =
            "{\"id\":\"gen-1\",\"model\":\"typesafe/jev-1.13-20260917\",\"provider\":\"TypeSafe\",\"answers\":{" +
            "\"team\":{\"choice\":\"payments\",\"confidence\":0.75,\"probabilities\":{\"account\":0,\"frontend\":0.16,\"payments\":0.84},\"type\":\"choice\"}," +
            "\"unsafe\":{\"type\":\"refusal\"}}," +
            "\"usage\":{\"input_tokens\":120,\"output_tokens\":0,\"cost\":0.000005,\"input_tokens_details\":{\"cached_tokens\":40}}}";

        public const string TypeSafeValidation422 =
            "{\"detail\":[{\"loc\":[\"body\",\"questions\",\"action\",\"criteria\"],\"msg\":\"Field required\",\"type\":\"missing\"}]}";

        public const string TypeSafeTooManyOptions400 = "{\"detail\":\"choice criteria has too many options (at most 255)\"}";

        public const string OpenRouterCredits402 = "{\"error\":{\"code\":402,\"message\":\"Insufficient credits\"},\"user_id\":\"u1\"}";

        public const string LlamaCppNotDecision501 =
            "{\"error\":{\"code\":501,\"message\":\"the loaded model is not a decision model\",\"type\":\"not_supported_error\"}}";

        /// <summary>
        /// JSON-schema forced answer <c>{"answer":"B"}</c>. Label mass: B 0.60 + b 0.02, A 0.25, C 0.05; the
        /// non-label "The" holds 0.08. Renormalised over labels: B 0.62/0.92, A 0.25/0.92, C 0.05/0.92.
        /// </summary>
        public const string ChatLogprobsResponse =
            "{\"id\":\"chatcmpl-1\",\"model\":\"gpt-4o-mini-2024-07-18\",\"choices\":[{\"index\":0," +
            "\"message\":{\"role\":\"assistant\",\"content\":\"{\\\"answer\\\":\\\"B\\\"}\"}," +
            "\"logprobs\":{\"content\":[" +
            "{\"token\":\"{\\\"\",\"logprob\":-0.0001,\"top_logprobs\":[{\"token\":\"{\\\"\",\"logprob\":-0.0001}]}," +
            "{\"token\":\"answer\",\"logprob\":0.0,\"top_logprobs\":[{\"token\":\"answer\",\"logprob\":0.0}]}," +
            "{\"token\":\"\\\":\\\"\",\"logprob\":-0.0002,\"top_logprobs\":[{\"token\":\"\\\":\\\"\",\"logprob\":-0.0002}]}," +
            "{\"token\":\"B\",\"logprob\":-0.51082562,\"top_logprobs\":[" +
            "{\"token\":\"B\",\"logprob\":-0.51082562},{\"token\":\"A\",\"logprob\":-1.38629436}," +
            "{\"token\":\"The\",\"logprob\":-2.52572864},{\"token\":\"C\",\"logprob\":-2.99573227}," +
            "{\"token\":\"b\",\"logprob\":-3.91202301}]}," +
            "{\"token\":\"\\\"}\",\"logprob\":0.0,\"top_logprobs\":[]}]},\"finish_reason\":\"stop\"}]," +
            "\"usage\":{\"prompt_tokens\":96,\"completion_tokens\":5,\"total_tokens\":101}}";

        /// <summary>A yes/no answer whose label varies in case and spacing: Yes 0.5, " yes" 0.2, no 0.1, "NO" 0.1.</summary>
        public const string ChatLogprobsYesNoResponse =
            "{\"model\":\"llama\",\"choices\":[{\"message\":{\"content\":\"Yes\"},\"logprobs\":{\"content\":[" +
            "{\"token\":\"Yes\",\"logprob\":-0.69314718,\"top_logprobs\":[" +
            "{\"token\":\"Yes\",\"logprob\":-0.69314718},{\"token\":\" yes\",\"logprob\":-1.60943791}," +
            "{\"token\":\"no\",\"logprob\":-2.30258509},{\"token\":\"NO\",\"logprob\":-2.30258509}]}]}}]," +
            "\"usage\":{\"prompt_tokens\":50,\"completion_tokens\":1}}";

        /// <summary>A score answer over levels 0-2: "2" 0.5, "1" 0.3, "0" 0.2.</summary>
        public const string ChatLogprobsScoreResponse =
            "{\"model\":\"llama\",\"choices\":[{\"message\":{\"content\":\"2\"},\"logprobs\":{\"content\":[" +
            "{\"token\":\"2\",\"logprob\":-0.69314718,\"top_logprobs\":[" +
            "{\"token\":\"2\",\"logprob\":-0.69314718},{\"token\":\"1\",\"logprob\":-1.20397280}," +
            "{\"token\":\"0\",\"logprob\":-1.60943791}]}]}}]," +
            "\"usage\":{\"prompt_tokens\":40,\"completion_tokens\":1}}";

        public const string ChatWithoutLogprobsResponse =
            "{\"model\":\"lmstudio-model\",\"choices\":[{\"message\":{\"content\":\"{\\\"answer\\\":\\\"A\\\"}\"}}]," +
            "\"usage\":{\"prompt_tokens\":10,\"completion_tokens\":4}}";

        public const string ChatWithoutLabelResponse =
            "{\"model\":\"m\",\"choices\":[{\"message\":{\"content\":\"maybe\"},\"logprobs\":{\"content\":[" +
            "{\"token\":\"maybe\",\"logprob\":-0.1,\"top_logprobs\":[{\"token\":\"maybe\",\"logprob\":-0.1}]}]}}]}";

        public const string LlamaCppModels =
            "{\"object\":\"list\",\"data\":[{\"id\":\"d1-2B-Q4_K_M.gguf\",\"object\":\"model\"," +
            "\"architecture\":{\"input_modalities\":[\"text\",\"image\"],\"output_modalities\":[\"decisions\"]}}]}";

        public const string LlamaCppTextModels =
            "{\"object\":\"list\",\"data\":[{\"id\":\"qwen3-4b.gguf\",\"object\":\"model\"," +
            "\"architecture\":{\"input_modalities\":[\"text\"],\"output_modalities\":[\"text\"]}}]}";

        public const string OpenRouterModels =
            "{\"data\":[" +
            "{\"id\":\"typesafe/jev-1.14\",\"architecture\":{\"input_modalities\":[\"text\",\"image\"],\"output_modalities\":[\"decisions\"]},\"supported_parameters\":[]}," +
            "{\"id\":\"openai/gpt-4o-mini\",\"architecture\":{\"output_modalities\":[\"text\"]},\"supported_parameters\":[\"logprobs\",\"top_logprobs\",\"response_format\"]}," +
            "{\"id\":\"some/chat\",\"architecture\":{\"output_modalities\":[\"text\"]},\"supported_parameters\":[\"temperature\"]}]}";

        public static DecisionRequest NpcRequest()
        {
            return new DecisionRequest("A goblin with 3 hp faces the player.",
                DecisionQuestion.YesNo("hostile", "Is the player hostile?", "the player attacks", "the player trades"),
                DecisionQuestion.Choice("action", "What should the goblin do?", new[]
                {
                    new DecisionOption("attack", "Hit the player."),
                    new DecisionOption("flee"),
                    new DecisionOption("trade", "Offer a deal.")
                }),
                DecisionQuestion.Score("danger", "How dangerous is the player?",
                    new[] { "safe", "wary", "risky", "deadly" }));
        }

        public static LlmEndpointDescriptor Endpoint(string id, LlmDecisionProtocol protocol,
            string baseUrl = "http://127.0.0.1:8080/v1", string model = "d1-2B", string secretReference = "")
        {
            return new LlmEndpointDescriptor
            {
                EndpointId = id,
                DisplayName = id,
                Kind = LlmEndpointKind.HttpOpenAi,
                BaseUrl = baseUrl,
                Model = model,
                SecretReference = secretReference,
                DecisionProtocol = protocol
            };
        }

        /// <summary>A successful result answering the first question of <paramref name="request"/> with one option.</summary>
        public static DecisionResult Answer(DecisionRequest request, string optionId, double confidence,
            bool calibrated = true)
        {
            DecisionQuestion question = request.Questions[0];
            int count = question.Options.Count;
            int chosen = question.IndexOf(optionId);
            double[] probabilities = new double[count];
            double rest = count > 1 ? (1d - confidence) / (count - 1) : 0d;
            string[] ids = new string[count];
            for (int i = 0; i < count; i++)
            {
                ids[i] = question.Options[i].Id;
                probabilities[i] = i == chosen ? (count > 1 ? confidence : 1d) : rest;
            }

            DecisionAnswer answer = new(question.Id, question.Kind, ids, probabilities, chosen, confidence, calibrated);
            return DecisionResult.Success(new[] { answer }, new LlmUsageRecord(), "scripted", "scripted");
        }
    }

    internal static class DecisionAsserts
    {
        /// <summary>Awaits <paramref name="action"/> and returns the exception of type <typeparamref name="T"/>, never blocking.</summary>
        public static async Task<T> AwaitThrows<T>(Func<Task> action) where T : Exception
        {
            try
            {
                await action();
            }
            catch (T expected)
            {
                return expected;
            }

            NUnit.Framework.Assert.Fail("Expected " + typeof(T).Name + " was not thrown.");
            return null;
        }
    }

    /// <summary>Deterministic clock: time moves only on <see cref="Advance"/>, which completes due delays inline.</summary>
    internal sealed class ManualDecisionClock : IDecisionClock
    {
        private readonly List<(double Due, TaskCompletionSource<bool> Source)> _delays = new();

        public double NowSeconds { get; private set; }

        public int PendingDelays
        {
            get
            {
                _delays.RemoveAll(d => d.Source.Task.IsCompleted);
                return _delays.Count;
            }
        }

        public Task DelayAsync(int milliseconds, CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled(cancellationToken);
            }

            TaskCompletionSource<bool> source = new();
            cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            _delays.Add((NowSeconds + milliseconds / 1000d, source));
            return source.Task;
        }

        public void Advance(double seconds)
        {
            NowSeconds += seconds;
            List<(double Due, TaskCompletionSource<bool> Source)> due = _delays.FindAll(d => d.Due <= NowSeconds + 1e-9);
            _delays.RemoveAll(d => d.Due <= NowSeconds + 1e-9);
            due.Sort((a, b) => a.Due.CompareTo(b.Due));
            foreach ((double _, TaskCompletionSource<bool> source) in due)
            {
                source.TrySetResult(true);
            }
        }
    }

    /// <summary>A decision client whose answers the test scripts: immediate, pending, or throwing.</summary>
    internal sealed class ScriptedDecisionClient : IDecisionClient
    {
        private readonly List<TaskCompletionSource<DecisionResult>> _pending = new();

        public Func<DecisionRequest, DecisionResult> Answer { get; set; }

        public bool HoldAnswers { get; set; }

        public bool HonourCancellation { get; set; } = true;

        public Exception Throw { get; set; }

        public List<DecisionRequest> Requests { get; } = new();

        public List<CancellationToken> Tokens { get; } = new();

        public int Calls => Requests.Count;

        public int PendingCount => _pending.FindAll(p => !p.Task.IsCompleted).Count;

        public Task<DecisionResult> DecideAsync(DecisionRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            Tokens.Add(cancellationToken);
            if (Throw != null)
            {
                throw Throw;
            }

            if (!HoldAnswers)
            {
                return Task.FromResult(Answer != null ? Answer(request) : DecisionFixtures.Answer(request,
                    request.Questions[0].Options[0].Id, 0.9d));
            }

            TaskCompletionSource<DecisionResult> source = new();
            if (HonourCancellation)
            {
                cancellationToken.Register(() => source.TrySetCanceled(cancellationToken));
            }

            _pending.Add(source);
            return source.Task;
        }

        /// <summary>Completes the oldest pending call.</summary>
        public void CompleteNext(DecisionResult result)
        {
            TaskCompletionSource<DecisionResult> next = _pending.Find(p => !p.Task.IsCompleted);
            next?.TrySetResult(result);
        }
    }

    internal sealed class FixedSecretProvider : ILlmEndpointSecretProvider
    {
        private readonly string _reference;
        private readonly string _secret;

        public FixedSecretProvider(string reference, string secret)
        {
            _reference = reference;
            _secret = secret;
        }

        public bool TryResolve(string secretReference, out string secret)
        {
            secret = string.Equals(secretReference, _reference, StringComparison.Ordinal) ? _secret : "";
            return secret.Length > 0;
        }
    }

#if COREAI_LLM
    /// <summary>Records every POST and answers from a script; the decision clients' only path to the network.</summary>
    internal sealed class RecordingTransport : IOpenAiHttpTransport
    {
        private readonly Queue<OpenAiHttpPostResult> _responses = new();

        public List<OpenAiHttpPostRequest> Requests { get; } = new();

        public Exception Throw { get; set; }

        public Action OnPost { get; set; }

        public string DebugLabel => "recording";

        public bool SupportsSseStreaming => false;

        public RecordingTransport Enqueue(int status, string body)
        {
            _responses.Enqueue(new OpenAiHttpPostResult { StatusCode = status, BodyText = body });
            return this;
        }

        public Task<OpenAiHttpPostResult> PostNonStreamingAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            OnPost?.Invoke();
            if (Throw != null)
            {
                throw Throw;
            }

            OpenAiHttpPostResult result = _responses.Count > 0
                ? _responses.Dequeue()
                : new OpenAiHttpPostResult { StatusCode = 500, BodyText = "{\"error\":{\"message\":\"no scripted response\"}}" };
            return Task.FromResult(result);
        }

        public Task<OpenAiHttpSseOpenResult> OpenSseResponseStreamAsync(OpenAiHttpPostRequest request,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException("Decisions never stream.");
        }

        public string Header(int requestIndex, string name)
        {
            foreach (KeyValuePair<string, string> header in Requests[requestIndex].Headers)
            {
                if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
                {
                    return header.Value;
                }
            }

            return null;
        }
    }
#endif
}
