using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression coverage for audit finding E2: <c>load_scene</c> must fail (not silently report
    /// success) when the requested scene is not present/enabled in Build Settings, so the Lua/tool
    /// caller can see the failure and self-correct instead of the world silently not changing.
    /// </summary>
    public sealed class CoreAiWorldCommandExecutorLoadSceneEditModeTests
    {
        [Test]
        public void TryExecute_LoadScene_SceneNotInBuildSettings_FailsWithoutChangingActiveScene()
        {
            CoreAiWorldCommandExecutor executor = new(GameLoggerUnscopedFallback.Instance);
            string activeSceneBefore = SceneManager.GetActiveScene().name;

            string json = JsonUtility.ToJson(CoreAiWorldCommandEnvelope.LoadScene(
                "NoSuchScene_CoreAiAudit2026GapTest"));
            bool executed = executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = json,
                SourceTaskHint = "editmode_load_scene_missing"
            });

            Assert.IsFalse(executed,
                "load_scene must fail when the requested scene is missing from Build Settings.");
            Assert.AreEqual(activeSceneBefore, SceneManager.GetActiveScene().name,
                "A rejected load_scene must never change the active scene.");
        }

        [Test]
        public void TryExecute_LoadScene_EmptySceneName_Fails()
        {
            CoreAiWorldCommandExecutor executor = new(GameLoggerUnscopedFallback.Instance);

            string json = JsonUtility.ToJson(CoreAiWorldCommandEnvelope.LoadScene(""));
            bool executed = executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = json,
                SourceTaskHint = "editmode_load_scene_empty"
            });

            Assert.IsFalse(executed, "load_scene with an empty scene name must fail.");
        }
    }
}
