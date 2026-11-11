using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Logging;
using Newtonsoft.Json;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Offline LLM client: returns a configured custom line or structured stubs per role when no live model is used.
    /// </summary>
    public sealed class OfflineLlmClient : ILlmClient
    {
        private readonly CoreAISettingsAsset _settings;
        private IReadOnlyList<ILlmTool> _tools = Array.Empty<ILlmTool>();

        /// <summary>What the Programmer answers with no model: a line a player can act on.</summary>
        public const string OfflineProgrammerReply =
            "No AI model is set up, so the AI cannot build or script anything yet. " +
            "Choose a model in the Hub (Settings), then ask again.";

        public OfflineLlmClient(CoreAISettingsAsset settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        /// <inheritdoc />
        public bool SupportsNativeToolCalling => false;

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _tools = tools ?? Array.Empty<ILlmTool>();
        }

        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            Log.Instance.Info("[OfflineLlmClient] Returning offline deterministic response (no live LLM).", LogTag.Llm);

            string response;
            string roleId = request.AgentRoleId ?? "";

            if (_settings.ShouldUseOfflineCustomResponse(roleId))
            {
                response = _settings.OfflineCustomResponse;
            }
            else if (LlmConversationalRolePolicy.IsConversationalUserFacingRole(roleId))
            {
                response = _settings.OfflineCustomResponse;
            }
            else
            {
                response = GetStructuredOfflineStub(request);
            }

            return Task.FromResult(new LlmCompletionResult
            {
                Ok = true,
                Content = response
            });
        }

        private static string GetStructuredOfflineStub(LlmCompletionRequest request)
        {
            string role = request.AgentRoleId?.ToLowerInvariant() ?? "";

            if (role.Contains("programmer"))
            {
                // WHY: a sentence and not a code stub - the Programmer answers the in-world chat, so this is what a
                // player reads when no model is set up.
                return OfflineProgrammerReply;
            }

            if (role.Contains("mechanic") || role.Contains("coremechanic"))
            {
                return "{\"result\": \"ok\", \"value\": 0, \"note\": \"offline\"}";
            }

            if (role.Contains("creator"))
            {
                return "{\"created\": false, \"note\": \"offline\"}";
            }

            if (role.Contains("analyzer"))
            {
                return "{\"recommendations\": [], \"status\": \"offline\"}";
            }

            if (role.Contains("merchant"))
            {
                return "{\"items\": [], \"note\": \"offline\"}";
            }

            string roleToken = JsonConvert.SerializeObject(string.IsNullOrEmpty(role) ? "unknown" : role);
            return $"{{\"status\": \"offline\", \"role\":{roleToken}}}";
        }
    }
}
