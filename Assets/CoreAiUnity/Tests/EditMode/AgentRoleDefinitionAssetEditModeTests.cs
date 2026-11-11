using System.Linq;
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Llm;
using CoreAI.Unity;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The Inspector-authored role definition maps onto the portable <see cref="RoleDefinition"/> field for
    /// field, overrides a built-in role through the installer, and the settings asset starts with the same
    /// universal prefix as the portable settings.
    /// </summary>
    public sealed class AgentRoleDefinitionAssetEditModeTests
    {
        private AgentRoleDefinitionAsset _asset;

        [SetUp]
        public void SetUp()
        {
            _asset = ScriptableObject.CreateInstance<AgentRoleDefinitionAsset>();
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_asset);
        }

        [Test]
        public void EveryShippedDefinition_SurvivesTheAssetRoundTrip()
        {
            foreach (RoleDefinition shipped in BuiltInRoleDefinitions.CreateAll())
            {
                _asset.CopyFrom(shipped);
                RoleDefinition back = _asset.ToRoleDefinition();

                AgentMemoryPolicy fromShipped = new();
                AgentMemoryPolicy fromAsset = new();
                fromShipped.ApplyRoleDefinition(shipped);
                fromAsset.ApplyRoleDefinition(back);
                Assert.AreEqual(Describe(fromShipped, shipped.Id), Describe(fromAsset, shipped.Id), shipped.Id);
                CollectionAssert.AreEqual(shipped.ToolProfiles, back.ToolProfiles, shipped.Id);
            }
        }

        [Test]
        public void OptionalFields_MapToInheritWhenOff_AndToTheValueWhenOn()
        {
            _asset.CopyFrom(new RoleDefinition("Scout"));
            RoleDefinition inherit = _asset.ToRoleDefinition();
            Assert.IsNull(inherit.MaxToolCallRoundtrips);
            Assert.IsNull(inherit.MaxOutputTokens);
            Assert.IsNull(inherit.Temperature);
            Assert.IsNull(inherit.AllowDuplicateToolCalls);
            Assert.IsNull(inherit.EnableStreaming);
            Assert.IsNull(inherit.CompactionTriggerRatio);

            _asset.CopyFrom(new RoleDefinition("Scout")
            {
                MaxToolCallRoundtrips = 0,
                MaxOutputTokens = 256,
                Temperature = 0.7f,
                AllowDuplicateToolCalls = true,
                EnableStreaming = false,
                CompactionTriggerRatio = 0.5f,
                RoutingProfileId = "fast",
                PromptRoleId = BuiltInAgentRoleIds.AiNpc
            });
            RoleDefinition set = _asset.ToRoleDefinition();
            Assert.AreEqual(0, set.MaxToolCallRoundtrips, "0 stays unlimited, not inherit");
            Assert.AreEqual(256, set.MaxOutputTokens);
            Assert.AreEqual(0.7f, set.Temperature);
            Assert.AreEqual(true, set.AllowDuplicateToolCalls);
            Assert.AreEqual(false, set.EnableStreaming);
            Assert.AreEqual(0.5f, set.CompactionTriggerRatio);
            Assert.AreEqual("fast", set.RoutingProfileId);
            Assert.AreEqual(BuiltInAgentRoleIds.AiNpc, set.PromptRoleId);
        }

        [Test]
        public void Installer_AppliesAnAssetOverABuiltInRole_AndSkipsOneWithoutId()
        {
            AgentRoleDefinitionAsset unnamed = ScriptableObject.CreateInstance<AgentRoleDefinitionAsset>();
            try
            {
                RoleDefinition merchant = BuiltInRoleDefinitions.Create(BuiltInAgentRoleIds.Merchant);
                merchant.UseMemoryTool = false;
                _asset.CopyFrom(merchant);
                AgentMemoryPolicy policy = new();
                Logging.ILog saved = Logging.Log.Instance;
                Logging.Log.Instance = Logging.NullLog.Instance;
                int applied;
                try
                {
                    applied = AgentRoleDefinitionsInstaller.ApplyDefinitions(policy, new[] { _asset, null, unnamed });
                }
                finally
                {
                    Logging.Log.Instance = saved;
                }

                Assert.AreEqual(1, applied);
                Assert.IsFalse(policy.IsMemoryEnabled(BuiltInAgentRoleIds.Merchant));
                Assert.IsTrue(policy.IsMemoryEnabled(BuiltInAgentRoleIds.SmartChat), "other roles keep the shipped data");
            }
            finally
            {
                Object.DestroyImmediate(unnamed);
            }
        }

        [Test]
        public void NewSettingsAsset_StartsWithThePortableUniversalPrefix()
        {
            CoreAISettingsAsset settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            try
            {
                Assert.AreEqual(CoreAISettings.DefaultUniversalSystemPromptPrefix, settings.UniversalSystemPromptPrefix);
                Assert.AreEqual(new CoreAISettingsOptions().UniversalSystemPromptPrefix,
                    settings.UniversalSystemPromptPrefix);
            }
            finally
            {
                Object.DestroyImmediate(settings);
            }
        }

        private static string Describe(AgentMemoryPolicy policy, string roleId)
        {
            AgentMemoryPolicy.RoleMemoryConfig config = policy.GetRoleConfig(roleId);
            return string.Join("|",
                config.UseMemoryTool, config.DefaultAction, config.AllowDuplicateToolCalls, config.WithChatHistory,
                config.PersistChatHistory, config.ContextTokens, config.MaxChatHistoryMessages, config.MaxOutputTokens,
                config.MaxToolCallRoundtrips, config.ToolResultMemory, config.Temperature,
                config.UseLlmContextCompaction, config.CompactionTriggerRatio,
                policy.IsUniversalPrefixOverridden(roleId),
                string.Join(",", policy.GetToolProfilesForRole(roleId).ToArray()));
        }
    }
}
