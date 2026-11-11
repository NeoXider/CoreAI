using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// One universal-prefix default: the static fallback with no settings asset and the portable options agree on
    /// the CRITICAL RULES block. The settings asset's default is pinned by the Unity-side twin
    /// (<c>AgentRoleDefinitionAssetEditModeTests.NewSettingsAsset_StartsWithThePortableUniversalPrefix</c>).
    /// </summary>
    public sealed class UniversalPrefixDefaultEditModeTests
    {
        private ICoreAISettings _savedInstance;

        [SetUp]
        public void SetUp()
        {
            _savedInstance = CoreAISettings.Instance;
            CoreAISettings.Instance = null;
            CoreAISettings.ResetOverrides();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAISettings.ResetOverrides();
            CoreAISettings.Instance = _savedInstance;
        }

        [Test]
        public void StaticFallbackAndPortableOptions_ShareTheCriticalRulesDefault()
        {
            CoreAISettingsOptions options = new();

            Assert.AreEqual(CoreAISettings.DefaultUniversalSystemPromptPrefix, CoreAISettings.UniversalSystemPromptPrefix);
            Assert.AreEqual(CoreAISettings.DefaultUniversalSystemPromptPrefix, options.UniversalSystemPromptPrefix);
            StringAssert.StartsWith("CRITICAL RULES FOR ALL AGENTS:", CoreAISettings.DefaultUniversalSystemPromptPrefix);
        }

        [Test]
        public void ComposedPrompt_StartsWithTheDefaultPrefix()
        {
            AgentMemoryPolicy policy = new();
            AiPromptComposer composer = new(new BuiltInDefaultAgentSystemPromptProvider(), null, null, null, policy,
                new CoreAISettingsOptions());

            string prompt = composer.GetSystemPrompt(BuiltInAgentRoleIds.SmartChat);

            StringAssert.StartsWith(CoreAISettings.DefaultUniversalSystemPromptPrefix + "\n", prompt);
        }

        [Test]
        public void ClearedPrefix_SendsNoPrefix()
        {
            AiPromptComposer composer = new(new BuiltInDefaultAgentSystemPromptProvider(), null, null, null,
                new AgentMemoryPolicy(), new CoreAISettingsOptions { UniversalSystemPromptPrefix = "" });

            StringAssert.StartsWith("You are an advanced in-game assistant",
                composer.GetSystemPrompt(BuiltInAgentRoleIds.SmartChat));
        }
    }
}
