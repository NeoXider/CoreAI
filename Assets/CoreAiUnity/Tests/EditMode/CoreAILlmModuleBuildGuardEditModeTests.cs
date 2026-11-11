using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Editor;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="CoreAILlmModuleBuildGuard"/> refuses a player build whose shipped settings select a mode that
    /// needs <c>COREAI_LLM</c> while the target has no such define, and lets every other build through: the
    /// define present, Offline, or Auto without a model.
    /// </summary>
    [TestFixture]
    public sealed class CoreAILlmModuleBuildGuardEditModeTests
    {
        private readonly List<Object> _created = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _created)
            {
                if (created != null)
                {
                    Object.DestroyImmediate(created);
                }
            }

            _created.Clear();
        }

        private CoreAISettingsAsset Settings(Action<CoreAISettingsAsset> configure)
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _created.Add(settings);
            configure(settings);
            return settings;
        }

        [Test]
        public void HttpMode_WithoutTheDefine_FailsTheBuild_NamingModeTargetAndFix()
        {
            string failure = CoreAILlmModuleBuildGuard.Evaluate(
                Settings(s => s.ConfigureClientOwnedApi("https://openrouter.ai/api/v1", "", "some/model")),
                "COREAI_LUA;DOTWEEN", "WebGL");

            Assert.IsNotNull(failure);
            StringAssert.Contains("ClientOwnedApi", failure);
            StringAssert.Contains("'WebGL'", failure);
            StringAssert.Contains("COREAI_LLM", failure);
            StringAssert.Contains("Offline", failure);
        }

        [Test]
        public void HttpMode_WithTheDefine_Builds()
        {
            Assert.IsNull(CoreAILlmModuleBuildGuard.Evaluate(
                Settings(s => s.ConfigureClientOwnedApi("https://openrouter.ai/api/v1", "", "some/model")),
                "COREAI_LUA; COREAI_LLM ;DOTWEEN", "WebGL"));
        }

        [Test]
        public void OfflineAndAutoWithoutAModel_BuildWithoutTheDefine()
        {
            Assert.IsNull(CoreAILlmModuleBuildGuard.Evaluate(Settings(s => s.ConfigureOffline()), "", "Standalone"));
            Assert.IsNull(CoreAILlmModuleBuildGuard.Evaluate(
                Settings(s => s.SetModelResolution(LlmExecutionMode.Auto, LlmBackendType.Auto, "", "")),
                null, "Standalone"));
        }

        [Test]
        public void LocalModel_WithoutTheDefine_FailsTheBuild()
        {
            Assert.IsNotNull(CoreAILlmModuleBuildGuard.Evaluate(Settings(s => s.ConfigureLlmUnity()), "",
                "Standalone"));
        }

        [Test]
        public void MissingSettings_NeverFailsTheBuild()
        {
            Assert.IsNull(CoreAILlmModuleBuildGuard.Evaluate(null, "", "Standalone"));
        }

        [TestCase("COREAI_LLM", true)]
        [TestCase("A;COREAI_LLM", true)]
        [TestCase(" COREAI_LLM ;B", true)]
        [TestCase("COREAI_LLMUNITY", false)]
        [TestCase("NOT_COREAI_LLM", false)]
        [TestCase("", false)]
        public void HasDefine_MatchesWholeSymbolsOnly(string defines, bool expected)
        {
            Assert.AreEqual(expected, CoreAILlmModuleBuildGuard.HasDefine(defines, "COREAI_LLM"));
        }
    }
}
