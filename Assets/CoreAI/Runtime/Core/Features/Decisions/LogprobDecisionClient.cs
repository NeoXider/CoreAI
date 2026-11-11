#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using Newtonsoft.Json;

namespace CoreAI.Decisions
{
    /// <summary>
    /// <see cref="IDecisionClient"/> over any OpenAI-compatible chat model that returns logprobs. Each question is
    /// one chat call whose answer is forced to a single-token label (<see cref="LogprobChatWire.LabelsFor"/>); the
    /// logprobs of the labels at that position go through a softmax, are renormalised over the labels, and the
    /// answer is marked <see cref="DecisionAnswer.Calibrated"/> = false. Questions run one after another so a
    /// consumer's in-flight budget stays one request.
    /// </summary>
    public sealed class LogprobDecisionClient : IDecisionClient
    {
        /// <summary>Backend name recorded on results.</summary>
        public const string BackendName = "logprobs";

        private readonly DecisionHttpEndpoint _endpoint;
        private readonly IOpenAiHttpTransport _transport;
        private readonly LogprobDecisionOptions _options;

        /// <summary>Creates a client for one chat endpoint.</summary>
        public LogprobDecisionClient(
            LlmEndpointDescriptor endpoint,
            IOpenAiHttpTransport transport,
            ILlmEndpointSecretProvider secrets = null,
            LogprobDecisionOptions options = null)
        {
            _endpoint = new DecisionHttpEndpoint(endpoint, secrets);
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
            _options = options ?? new LogprobDecisionOptions();
        }

        /// <inheritdoc />
        public async Task<DecisionResult> DecideAsync(DecisionRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<DecisionValidationError> errors = DecisionRequestValidator.Validate(request);
            if (errors.Count > 0)
            {
                return DecisionResult.Invalid(errors, BackendName);
            }

            List<IReadOnlyList<string>> labelsPerQuestion = new();
            foreach (DecisionQuestion question in request.Questions)
            {
                IReadOnlyList<string> labels = LogprobChatWire.LabelsFor(question);
                int cap = Math.Min(Math.Max(1, _options.TopLogprobs), LogprobChatWire.ChoiceLetters.Length);
                if (labels == null || labels.Count > cap)
                {
                    return DecisionResult.Failure(LlmErrorCode.InvalidRequest,
                        DecisionErrorReasons.TooManyOptionsForBackend,
                        $"Question '{question.Id}' has {question.Options.Count} options; the logprob fallback scores at most {cap} (top_logprobs). Use a System One endpoint for larger lists.",
                        BackendName);
                }

                labelsPerQuestion.Add(labels);
            }

            string url = LogprobChatWire.BuildUrl(_endpoint.BaseUrl);
            string secret = _endpoint.ResolveSecret();
            List<KeyValuePair<string, string>> headers = DecisionHttpExchange.BuildHeaders(url, secret);
            LlmUsageRecord usage = new()
            {
                RoleId = request.ConsumerId ?? "",
                TraceId = request.TraceId ?? "",
                RoutingProfileId = _endpoint.EndpointId,
                Model = _endpoint.Model
            };
            List<DecisionAnswer> answers = new();
            string answeringModel = "";
            for (int q = 0; q < request.Questions.Count; q++)
            {
                DecisionQuestion question = request.Questions[q];
                IReadOnlyList<string> labels = labelsPerQuestion[q];
                string json = LogprobChatWire.BuildRequestBody(request, question, labels, _endpoint.Model, _options)
                    .ToString(Formatting.None);
                DecisionHttpExchange.Outcome outcome = await DecisionHttpExchange.PostAsync(_transport, _endpoint,
                    url, json, headers, BackendName, secret, cancellationToken);
                if (outcome.Failure != null)
                {
                    return outcome.Failure;
                }

                LogprobLabelScan scan = LogprobChatWire.ScanLabels(outcome.Body, labels);
                usage.Add(scan.Usage);
                if (!scan.Ok)
                {
                    return DecisionResult.Failure(scan.ErrorCode, scan.Reason,
                        $"Question '{question.Id}': {scan.Message}", BackendName, usage);
                }

                double[] probabilities = DecisionProbabilityMath.SoftmaxFromLogprobs(scan.Logprobs);
                if (probabilities == null)
                {
                    return DecisionResult.Failure(LlmErrorCode.EmptyResponse, DecisionErrorReasons.NoLabelToken,
                        $"Question '{question.Id}': no label carries a finite logprob.", BackendName, usage);
                }

                answeringModel = scan.Model.Length > 0 ? scan.Model : answeringModel;
                answers.Add(BuildAnswer(question, probabilities));
            }

            usage.TotalTokens = usage.PromptTokens + usage.CompletionTokens;
            if (answeringModel.Length > 0)
            {
                usage.Model = answeringModel;
            }

            return DecisionResult.Success(answers, usage, answeringModel, BackendName);
        }

        private static DecisionAnswer BuildAnswer(DecisionQuestion question, double[] probabilities)
        {
            string[] ids = new string[question.Options.Count];
            for (int i = 0; i < ids.Length; i++)
            {
                ids[i] = question.Options[i].Id;
            }

            int chosen = DecisionProbabilityMath.ArgMax(probabilities);
            double? expected = question.Kind == DecisionQuestionKind.Score
                ? DecisionProbabilityMath.ExpectedIndex(probabilities)
                : (double?)null;
            return new DecisionAnswer(question.Id, question.Kind, ids, probabilities, chosen, probabilities[chosen],
                false, expected);
        }
    }
}
#endif
