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

        /// <summary>The first sentence of the Programmer's answer with no model.</summary>
        public const string OfflineProgrammerReplyStart =
            "No AI model is set up, so the AI cannot build or script anything yet.";

        /// <summary>
        /// The second sentence when the scene names no place of its own: it fits a game with the Hub (whose page
        /// is called AI Settings) and one with its own settings screen.
        /// </summary>
        public const string DefaultModelSetupHint = "Choose a model in the AI settings, then ask again.";

        /// <summary>What the Programmer answers with no model when no host set a hint: a line a player can act on.</summary>
        public const string OfflineProgrammerReply = OfflineProgrammerReplyStart + " " + DefaultModelSetupHint;

        /// <summary>
        /// The Programmer's answer with no model: <see cref="OfflineProgrammerReplyStart"/> and
        /// <paramref name="modelSetupHint"/> (a scene's own "press F2 ..." sentence), or
        /// <see cref="OfflineProgrammerReply"/> when the hint is empty.
        /// </summary>
        public static string ProgrammerReplyFor(string modelSetupHint)
        {
            string hint = (modelSetupHint ?? "").Trim();
            return hint.Length == 0 ? OfflineProgrammerReply : OfflineProgrammerReplyStart + " " + hint;
        }

        /// <summary>The error an NPC turn fails with when no model is set up.</summary>
        public const string NpcNoModelError = "No AI model is set up, so an NPC has nothing generated to say.";

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

            // WHY a failure for an NPC: an NPC says whatever text it is given, and the offline line is a system
            // message, not something a character says. A failed turn makes the NPC speak its own in-character
            // fallback line instead. A game that names the NPC role for its custom offline line still gets it.
            if (string.Equals(roleId, BuiltInAgentRoleIds.AiNpc, StringComparison.OrdinalIgnoreCase)
                && !NamesRoleForCustomResponse(roleId))
            {
                return Task.FromResult(new LlmCompletionResult
                {
                    Ok = false,
                    ErrorCode = LlmErrorCode.NotConfigured,
                    Error = NpcNoModelError
                });
            }

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
                response = GetStructuredOfflineStub(request, _settings.ModelSetupHint);
            }

            return Task.FromResult(new LlmCompletionResult
            {
                Ok = true,
                Content = response
            });
        }

        /// <summary>True when the custom offline line is on and its role list names <paramref name="roleId"/>
        /// itself, not through the <c>*</c> wildcard.</summary>
        private bool NamesRoleForCustomResponse(string roleId)
        {
            if (!_settings.OfflineUseCustomResponse)
            {
                return false;
            }

            foreach (string role in _settings.OfflineCustomResponseRoles.Split(','))
            {
                if (role.Trim().Equals(roleId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetStructuredOfflineStub(LlmCompletionRequest request, string modelSetupHint)
        {
            string role = request.AgentRoleId?.ToLowerInvariant() ?? "";

            if (role.Contains("programmer"))
            {
                // WHY: a sentence and not a code stub - the Programmer answers the in-world chat, so this is what a
                // player reads when no model is set up; the scene's hint says where to set one up in this scene.
                return ProgrammerReplyFor(modelSetupHint);
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
