using System.Collections;
using CoreAI.Ai;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// Дымовой вызов через тот же контур, что и оркестратор: <see cref="PlayModeProductionLikeLlmFactory"/> (HTTP при настроенном env).
    /// </summary>
    public sealed class OpenAiLmStudioPlayModeTests
    {
        [UnityTest]
        public IEnumerator OpenAiChatLlmClient_Completes_WhenEnvConfigured()
        {
            if (!PlayModeProductionLikeLlmFactory.TryCreate(
                    PlayModeProductionLikeLlmBackend.OpenAiCompatibleHttp,
                    0.2f,
                    300,
                    out var handle,
                    out var ignore))
                Assert.Ignore(ignore);

            try
            {
                var client = handle.Client;
                var task = client.CompleteAsync(
                    new LlmCompletionRequest
                    {
                        AgentRoleId = BuiltInAgentRoleIds.PlayerChat,
                        SystemPrompt = "Reply with exactly the single word: PONG",
                        UserPayload = "ping"
                    });

                yield return new WaitUntil(() => task.IsCompleted);

                if (task.IsFaulted)
                    Assert.Fail(task.Exception?.GetBaseException().Message ?? "Task faulted");

                var result = task.Result;
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
