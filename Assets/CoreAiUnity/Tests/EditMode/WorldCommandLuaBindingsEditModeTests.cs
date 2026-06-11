#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System.Collections.Generic;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.EditMode
{
    public sealed class WorldCommandLuaBindingsEditModeTests
    {
        private sealed class ListSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Items = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Items.Add(command);
            }
        }

        [Test]
        public void Lua_coreai_world_spawn_PublishesWorldCommand()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

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
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_world_set_active('e1', true)");

            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(WorldCommand, sink.Items[0].CommandTypeId);
            StringAssert.Contains("\"action\":\"set_active\"", sink.Items[0].JsonPayload);
            StringAssert.Contains("e1", sink.Items[0].JsonPayload);
        }

        [TestCase("0/0", "2", "3")]
        [TestCase("2", "1/0", "3")]
        [TestCase("2", "3", "-1/0")]
        [TestCase("100000.1", "2", "3")]
        [TestCase("2", "3", "-100000.1")]
        public void Lua_coreai_world_spawn_InvalidCoordinates_ThrowsAndPublishesNothing(
            string x,
            string y,
            string z)
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(
                () => env.RunChunk(script, $"coreai_world_spawn('enemy.basic', 'e1', {x}, {y}, {z})"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [TestCase("0/0", "2", "3")]
        [TestCase("1/0", "2", "3")]
        [TestCase("2", "1/0", "3")]
        [TestCase("2", "3", "-1/0")]
        [TestCase("100000.1", "2", "3")]
        [TestCase("2", "3", "-100000.1")]
        public void Lua_coreai_world_move_InvalidCoordinates_ThrowsAndPublishesNothing(
            string x,
            string y,
            string z)
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(
                () => env.RunChunk(script, $"coreai_world_move('e1', {x}, {y}, {z})"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Lua_coreai_world_load_scene_AllowedScenes_RejectsUnlistedAllowsListed()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink, new[] { "allowed_scene" }).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() => env.RunChunk(script, "coreai_world_load_scene('forbidden')"));
            Assert.AreEqual(0, sink.Items.Count);

            env.RunChunk(script, "coreai_world_load_scene('allowed_scene')");
            Assert.AreEqual(1, sink.Items.Count);
            CoreAiWorldCommandEnvelope envelope = JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(sink.Items[0].JsonPayload);
            Assert.AreEqual(WorldCommand, sink.Items[0].CommandTypeId);
            Assert.AreEqual("load_scene", envelope.action);
            Assert.AreEqual("allowed_scene", envelope.sceneName);
        }

        [Test]
        public void Lua_coreai_world_play_sound_NaNVolume_PublishesClampedVolumeOne()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_world_play_sound('hero', 'laser', 0/0)");
            Assert.AreEqual(1, sink.Items.Count);

            CoreAiWorldCommandEnvelope envelope = JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(sink.Items[0].JsonPayload);
            Assert.AreEqual("play_sound", envelope.action);
            Assert.AreEqual("hero", envelope.targetName);
            Assert.AreEqual("laser", envelope.stringValue);
            Assert.AreEqual(1f, envelope.floatValue);
        }

    }
}
#endif
