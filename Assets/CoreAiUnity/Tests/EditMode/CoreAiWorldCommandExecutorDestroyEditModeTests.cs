using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression coverage for audit finding 13: <c>destroy</c> must fail (not silently report
    /// success) when the target object does not exist, so the model sees the failure on a typo'd
    /// name and can self-correct — matching every other verb's unresolved-target contract.
    /// </summary>
    public sealed class CoreAiWorldCommandExecutorDestroyEditModeTests
    {
        [Test]
        public void TryExecute_Destroy_MissingTarget_ReturnsFalse()
        {
            CoreAiWorldCommandExecutor executor = new(GameLoggerUnscopedFallback.Instance);

            string json = JsonUtility.ToJson(CoreAiWorldCommandEnvelope.Destroy(
                "NoSuchObject_CoreAiAudit2026DestroyTest"));
            bool executed = executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = json,
                SourceTaskHint = "editmode_destroy_missing"
            });

            Assert.IsFalse(executed,
                "destroy must fail when the target object cannot be resolved in the scene.");
        }
    }
}
