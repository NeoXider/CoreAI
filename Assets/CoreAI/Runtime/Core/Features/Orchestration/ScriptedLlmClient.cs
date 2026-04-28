using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.Ai
{
    /// <summary>
    /// Deterministic LLM client for EditMode/PlayMode scenarios that must not depend on a real provider.
    /// </summary>
    public sealed class ScriptedLlmClient : ILlmClient
    {
        private readonly List<ScriptedLlmRule> _rules = new();
        private string _fallback = "";

        /// <summary>Adds a rule that matches when system or user text contains the marker.</summary>
        public ScriptedLlmClient WhenContextContains(string marker, string reply)
        {
            _rules.Add(new ScriptedLlmRule(marker, reply));
            return this;
        }

        /// <summary>Sets a fallback reply used when no rule matches.</summary>
        public ScriptedLlmClient OtherwiseReply(string reply)
        {
            _fallback = reply ?? "";
            return this;
        }

        /// <inheritdoc />
        public Task<LlmCompletionResult> CompleteAsync(
            LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string reply = ResolveReply(request);
            return Task.FromResult(new LlmCompletionResult
            {
                Ok = true,
                Content = reply,
                Model = "scripted"
            });
        }

        /// <inheritdoc />
        public async IAsyncEnumerable<LlmStreamChunk> CompleteStreamingAsync(
            LlmCompletionRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            string reply = ResolveReply(request);
            if (!string.IsNullOrEmpty(reply))
            {
                yield return new LlmStreamChunk { Text = reply, Model = "scripted" };
                await Task.Yield();
            }

            yield return new LlmStreamChunk { IsDone = true, Model = "scripted" };
        }

        private string ResolveReply(LlmCompletionRequest request)
        {
            string haystack = (request?.SystemPrompt ?? "") + "\n" + (request?.UserPayload ?? "");
            foreach (ScriptedLlmRule rule in _rules)
            {
                if (rule.Matches(haystack))
                {
                    return rule.Reply;
                }
            }

            return _fallback;
        }

        private readonly struct ScriptedLlmRule
        {
            private readonly string _marker;

            public ScriptedLlmRule(string marker, string reply)
            {
                _marker = marker ?? "";
                Reply = reply ?? "";
            }

            public string Reply { get; }

            public bool Matches(string value)
            {
                return string.IsNullOrEmpty(_marker) ||
                       (value ?? "").Contains(_marker, System.StringComparison.Ordinal);
            }
        }
    }
}
