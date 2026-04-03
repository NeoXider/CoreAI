using System.Collections.Generic;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using NUnit.Framework;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.EditMode
{
    public sealed class WorldCommandLuaBindingsEditModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command) => Items.Add(command);
        }

        [Test]
        public void Lua_coreai_world_spawn_PublishesWorldCommand()
        {
            var sink = new ListSink();
            var reg = new LuaApiRegistry();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            var env = new SecureLuaEnvironment();
            var script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_world_spawn('enemy.basic','e1', 1,2,3)");

            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(WorldCommand, sink.Items[0].CommandTypeId);
            StringAssert.Contains("\"action\":\"spawn\"", sink.Items[0].JsonPayload);
            StringAssert.Contains("enemy.basic", sink.Items[0].JsonPayload);
            StringAssert.Contains("e1", sink.Items[0].JsonPayload);
        }

        [Test]
        public void Lua_coreai_world_set_active_PublishesWorldCommand()
        {
            var sink = new ListSink();
            var reg = new LuaApiRegistry();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            var env = new SecureLuaEnvironment();
            var script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_world_set_active('e1', true)");

            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(WorldCommand, sink.Items[0].CommandTypeId);
            StringAssert.Contains("\"action\":\"set_active\"", sink.Items[0].JsonPayload);
            StringAssert.Contains("e1", sink.Items[0].JsonPayload);
        }
    }
}

