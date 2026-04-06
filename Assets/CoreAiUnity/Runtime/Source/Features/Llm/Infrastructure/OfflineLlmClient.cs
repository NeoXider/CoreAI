using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Офлайн клиент — возвращает кастомный ответ или заглушку по ролям.
    /// Используется когда LLM недоступен или выбран Offline режим.
    /// </summary>
    public sealed class OfflineLlmClient : ILlmClient
    {
        private readonly CoreAISettingsAsset _settings;
        private IReadOnlyList<ILlmTool> _tools = Array.Empty<ILlmTool>();

        public OfflineLlmClient(CoreAISettingsAsset settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public void SetTools(IReadOnlyList<ILlmTool> tools)
        {
            _tools = tools ?? Array.Empty<ILlmTool>();
        }

        public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request,
            CancellationToken cancellationToken = default)
        {
            string response;
            string roleId = request.AgentRoleId ?? "";

            // Если включён кастомный ответ для этой роли — используем его
            if (_settings.ShouldUseOfflineCustomResponse(roleId))
            {
                response = _settings.OfflineCustomResponse;
            }
            else
            {
                // Fallback: заглушка по ролям (как StubLlmClient)
                response = GetStubResponse(request);
            }

            return Task.FromResult(new LlmCompletionResult
            {
                Ok = true,
                Content = response
            });
        }

        private static string GetStubResponse(LlmCompletionRequest request)
        {
            string role = request.AgentRoleId?.ToLowerInvariant() ?? "";
            string userPayload = request.UserPayload ?? "";

            // Programmer — fenced Lua
            if (role.Contains("programmer"))
            {
                return "```lua\n-- Offline: Lua not available\nfunction noop() end\n```";
            }

            // CoreMechanicAI — numeric JSON
            if (role.Contains("mechanic") || role.Contains("coremechanic"))
            {
                return "{\"result\": \"ok\", \"value\": 0, \"note\": \"offline\"}";
            }

            // Creator — JSON object
            if (role.Contains("creator"))
            {
                return "{\"created\": false, \"note\": \"offline\"}";
            }

            // Analyzer — metrics JSON
            if (role.Contains("analyzer"))
            {
                return "{\"recommendations\": [], \"status\": \"offline\"}";
            }

            // AINpc / PlayerChat — echo
            if (role.Contains("npc") || role.Contains("playerchat") || role.Contains("chat"))
            {
                return $"[Offline] {userPayload}";
            }

            // Merchant — inventory response
            if (role.Contains("merchant"))
            {
                return "{\"items\": [], \"note\": \"offline\"}";
            }

            // Default — JSON
            return $"{{\"status\": \"offline\", \"role\": \"{role}\", \"echo\": \"{userPayload}\"}}";
        }
    }
}
