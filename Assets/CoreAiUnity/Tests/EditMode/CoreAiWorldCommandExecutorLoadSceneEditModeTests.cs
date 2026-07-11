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

        /// <summary>
        /// Security contract: when a NON-EMPTY scene whitelist is configured, a scene that is not on it is
        /// rejected by the whitelist check — before (and independent of) the Build Settings presence check.
        /// This pins that the whitelist actually restricts <c>load_scene</c> on the native tool path.
        /// </summary>
        [Test]
        public void TryExecute_LoadScene_NonEmptyWhitelist_RejectsSceneNotOnList()
        {
            CoreAiWorldCommandExecutor executor = new(
                GameLoggerUnscopedFallback.Instance,
                null,
                new[] { "OnlyThisSceneIsAllowed" });
            string activeSceneBefore = SceneManager.GetActiveScene().name;

            string json = JsonUtility.ToJson(CoreAiWorldCommandEnvelope.LoadScene("SomeOtherScene"));
            bool executed = executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = json,
                SourceTaskHint = "editmode_load_scene_whitelist_reject"
            });

            Assert.IsFalse(executed,
                "A scene not on a configured non-empty whitelist must be rejected.");
            Assert.AreEqual(activeSceneBefore, SceneManager.GetActiveScene().name,
                "A whitelist-rejected load_scene must never change the active scene.");
        }

        /// <summary>
        /// Documents the DELIBERATE legacy-permissive contract: an EMPTY/absent whitelist does NOT restrict
        /// scenes — any scene present in Build Settings stays loadable. A missing scene here fails on the
        /// Build Settings gate, not the whitelist, proving the whitelist was not applied. This pins the
        /// intended behavior so it is not mistaken for a security hole (tooltip and runtime agree: empty =
        /// any Build Settings scene). To restrict scenes, configure a non-empty whitelist.
        /// </summary>
        [Test]
        public void TryExecute_LoadScene_EmptyWhitelist_IsPermissive_MissingSceneStillFailsOnBuildSettings()
        {
            CoreAiWorldCommandExecutor executor = new(
                GameLoggerUnscopedFallback.Instance,
                null,
                System.Array.Empty<string>());
            string activeSceneBefore = SceneManager.GetActiveScene().name;

            string json = JsonUtility.ToJson(CoreAiWorldCommandEnvelope.LoadScene(
                "NoSuchScene_CoreAiEmptyWhitelistTest"));
            bool executed = executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = json,
                SourceTaskHint = "editmode_load_scene_empty_whitelist"
            });

            // Still false, but for the Build-Settings reason — the empty whitelist added no restriction.
            Assert.IsFalse(executed,
                "A missing scene fails on the Build Settings gate even with an empty (permissive) whitelist.");
            Assert.AreEqual(activeSceneBefore, SceneManager.GetActiveScene().name);
        }
    }
}
