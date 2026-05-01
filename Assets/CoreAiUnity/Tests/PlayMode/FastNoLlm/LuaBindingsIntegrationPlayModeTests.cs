using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using NUnit.Framework;
using UnityEngine.TestTools;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.PlayMode
{
    public sealed class LuaBindingsIntegrationPlayModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        private sealed class NoOpGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        [UnityTest]
        public System.Collections.IEnumerator Envelope_WithAggregatingBindings_AppliesDataOverlay_AndPublishesEvent()
        {
            yield return null;

            ListSink sink = new();
            MemoryDataOverlayVersionStore dataStore = new();
            MemoryLuaScriptVersionStore luaStore = new();
            CoreAiVersioningLuaRuntimeBindings versioning = new(luaStore, dataStore, sink,
                new DefaultDataOverlayPayloadValidator());
            CoreAiWorldLuaRuntimeBindings world = new(sink);
            AggregatingGameLuaRuntimeBindings bindings = new(new NoOpGameLogger(), versioning, world);
            LuaAiEnvelopeProcessor proc = new(
                new SecureLuaEnvironment(),
                bindings,
                sink,
                () => null,
                new NullLuaExecutionObserver(),
                luaStore);

            proc.Process(new ApplyAiGameCommand
            {
                CommandTypeId = Envelope,
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                JsonPayload = "```lua\ncoreai_data_apply('arena.cfg', '{\\\"xp\\\":1}')\n```"
            });

            Assert.IsTrue(dataStore.TryGetCurrentPayload("arena.cfg", out string current));
            Assert.AreEqual("{\"xp\":1}", current);
            Assert.IsTrue(sink.Items.Exists(i => i.CommandTypeId == DataOverlayApplied));
            Assert.IsTrue(sink.Items.Exists(i => i.CommandTypeId == LuaExecutionSucceeded));
        }
    }
}