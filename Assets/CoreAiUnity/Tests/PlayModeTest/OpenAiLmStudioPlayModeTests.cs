using System.Collections;
using System.Threading.Tasks;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Дымовой вызов через тот же контур, что и оркестратор: <see cref="PlayModeProductionLikeLlmFactory"/> (HTTP при настроенном env).
    /// </summary>
    public sealed class OpenAiLmStudioPlayModeTests
    {
        [UnityTest]
        [Timeout(900000)]
        public IEnumerator OpenAiChatLlmClient_Completes_WhenEnvConfigured()
        {
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                    0.2f,
                    300,
                    out PlayModeProductionLikeLlmHandle handle,
                    out string ignore))
            {
                Assert.Ignore(ignore);
            }

            try
            {
                ILlmClient client = handle.Client;
                Task<LlmCompletionResult> task = client.CompleteAsync(
                    new LlmCompletionRequest
                    {
                        AgentRoleId = BuiltInAgentRoleIds.PlayerChat,
                        SystemPrompt = "Reply with exactly the single word: PONG",
                        UserPayload = "ping"
                    });

                yield return PlayModeTestAwait.WaitTask(task, 300f, "OpenAI completion");

                LlmCompletionResult result = task.Result;
                Assert.IsTrue(result.Ok, result.Error ?? "(no error text)");
                StringAssert.Contains("PONG", result.Content.ToUpperInvariant());
            }
            finally
            {
                handle.Dispose();
            }
        }
    }
}