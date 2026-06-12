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

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, $"coreai_world_spawn('enemy.basic', 'e1', {x}, {y}, {z})"));
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

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, $"coreai_world_move('e1', {x}, {y}, {z})"));
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
            CoreAiWorldCommandEnvelope envelope =
                JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(sink.Items[0].JsonPayload);
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

            CoreAiWorldCommandEnvelope envelope =
                JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(sink.Items[0].JsonPayload);
            Assert.AreEqual("play_sound", envelope.action);
            Assert.AreEqual("hero", envelope.targetName);
            Assert.AreEqual("laser", envelope.stringValue);
            Assert.AreEqual(1f, envelope.floatValue);
        }

        [Test]
        public void Lua_coreai_world_spawn_batch_TableEntries_PublishesSpawnCommands()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            DynValue result = env.RunChunk(script, @"
                return coreai_world_spawn_batch({
                    { prefab = 'enemy.basic', name = 'e1', x = 1, y = 2, z = 3 },
                    { prefab = 'enemy.elite', name = 'e2', x = 4, y = 5, z = 6 },
                    { prefab = 'pickup.coin', name = 'c1', x = 7, y = 8, z = 9 },
                })");

            Assert.AreEqual(3, (int)result.Number);
            Assert.AreEqual(3, sink.Items.Count);
            AssertSpawnEnvelope(sink.Items[0], "enemy.basic", "e1");
            AssertSpawnEnvelope(sink.Items[1], "enemy.elite", "e2");
            AssertSpawnEnvelope(sink.Items[2], "pickup.coin", "c1");
        }

        [Test]
        public void Lua_coreai_world_spawn_batch_MoreThanMaximum_ThrowsAndPublishesNothing()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() => env.RunChunk(script, @"
                local entries = {}
                for i = 1, 101 do
                    entries[i] = { prefab = 'cell', name = 'cell_' .. i, x = i, y = 0, z = 0 }
                end
                coreai_world_spawn_batch(entries)"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Lua_coreai_world_grid_ValidGrid_PublishesNineNamedSpawns()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            DynValue result = env.RunChunk(script, "return coreai_world_grid('p','cell',0,0,2,2,1,0)");

            Assert.AreEqual(9, (int)result.Number);
            Assert.AreEqual(9, sink.Items.Count);
            for (int ix = 0; ix < 3; ix++)
            {
                for (int iz = 0; iz < 3; iz++)
                {
                    int index = ix * 3 + iz;
                    CoreAiWorldCommandEnvelope envelope = EnvelopeAt(sink, index);
                    Assert.AreEqual("spawn", envelope.action);
                    Assert.AreEqual("p", envelope.prefabKeyOrName);
                    Assert.AreEqual($"cell_{ix}_{iz}", envelope.targetName);
                    Assert.AreEqual(ix, envelope.x);
                    Assert.AreEqual(0, envelope.y);
                    Assert.AreEqual(iz, envelope.z);
                }
            }
        }

        [Test]
        public void Lua_coreai_world_grid_StepBelowMinimum_ThrowsAndPublishesNothing()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_world_grid('p','cell',0,0,2,2,0.1,0)"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Lua_coreai_world_grid_TooLarge_ThrowsAndPublishesNothing()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_world_grid('p','cell',0,0,10,10,1,0)"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Lua_coreai_world_transaction_Commit_PublishesBufferedCommand()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, @"
                coreai_world_begin()
                coreai_world_spawn('enemy.basic', 'e1', 1, 2, 3)");
            Assert.AreEqual(0, sink.Items.Count);

            DynValue result = env.RunChunk(script, "return coreai_world_commit()");

            Assert.AreEqual(1, (int)result.Number);
            Assert.AreEqual(1, sink.Items.Count);
            AssertSpawnEnvelope(sink.Items[0], "enemy.basic", "e1");
        }

        [Test]
        public void Lua_coreai_world_transaction_Rollback_DropsBufferedCommand()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, @"
                coreai_world_begin()
                coreai_world_spawn('enemy.basic', 'e1', 1, 2, 3)");
            Assert.AreEqual(0, sink.Items.Count);

            DynValue result = env.RunChunk(script, "return coreai_world_rollback()");

            Assert.AreEqual(1, (int)result.Number);
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Lua_coreai_world_commit_WithoutBegin_Throws()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() => env.RunChunk(script, "coreai_world_commit()"));
        }

        [Test]
        public void Lua_coreai_world_begin_AfterStaleTransaction_DiscardsBufferAndStartsFresh()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            // Simulates a script that died between begin() and commit: the buffered command must
            // not survive into (or block) the next transaction on the shared bindings instance.
            env.RunChunk(script, @"
                coreai_world_begin()
                coreai_world_spawn('enemy.basic', 'stale', 1, 2, 3)");

            DynValue committed = env.RunChunk(script, @"
                coreai_world_begin()
                coreai_world_spawn('enemy.basic', 'fresh', 4, 5, 6)
                return coreai_world_commit()");

            Assert.AreEqual(1, (int)committed.Number);
            Assert.AreEqual(1, sink.Items.Count);
            AssertSpawnEnvelope(sink.Items[0], "enemy.basic", "fresh");
        }

        [Test]
        public void AbortTransaction_DropsBufferedCommands()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            CoreAiWorldLuaRuntimeBindings bindings = new(sink);
            bindings.RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, @"
                coreai_world_begin()
                coreai_world_spawn('enemy.basic', 'e1', 1, 2, 3)");

            bindings.AbortTransaction();

            Assert.Throws<ScriptRuntimeException>(() => env.RunChunk(script, "coreai_world_commit()"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Lua_coreai_world_set_props_NonFiniteOrOutOfRangeScale_ThrowsAndPublishesNothing()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_world_set_props('hero', { scale = 0/0 })"));
            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_world_set_props('hero', { scale = 1/0 })"));
            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_world_set_props('hero', { scale = 1000000 })"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Lua_coreai_world_set_props_ScaleAndColor_PublishesSetScaleThenSetColor()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_world_set_props('hero', { scale = 2, color = '#ff0000' })");

            Assert.AreEqual(2, sink.Items.Count);
            CoreAiWorldCommandEnvelope scale = EnvelopeAt(sink, 0);
            Assert.AreEqual("set_scale", scale.action);
            Assert.AreEqual("hero", scale.targetName);
            Assert.AreEqual(2f, scale.floatValue);
            CoreAiWorldCommandEnvelope color = EnvelopeAt(sink, 1);
            Assert.AreEqual("set_color", color.action);
            Assert.AreEqual("hero", color.targetName);
            Assert.AreEqual("#ff0000", color.stringValue);
        }

        [Test]
        public void Lua_coreai_world_set_props_UnknownKey_Throws()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_world_set_props('hero', { speed = 3 })"));
        }

        [Test]
        public void Lua_coreai_world_parent_ChildAndParent_PublishesParentEnvelope()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiWorldLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_world_parent('child', 'parent')");

            Assert.AreEqual(1, sink.Items.Count);
            CoreAiWorldCommandEnvelope envelope = EnvelopeAt(sink, 0);
            Assert.AreEqual("parent", envelope.action);
            Assert.AreEqual("child", envelope.targetName);
            Assert.AreEqual("parent", envelope.stringValue);
        }

        private static void AssertSpawnEnvelope(ApplyAiGameCommand command, string prefab, string name)
        {
            Assert.AreEqual(WorldCommand, command.CommandTypeId);
            CoreAiWorldCommandEnvelope envelope = JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(command.JsonPayload);
            Assert.AreEqual("spawn", envelope.action);
            Assert.AreEqual(prefab, envelope.prefabKeyOrName);
            Assert.AreEqual(name, envelope.targetName);
        }

        private static CoreAiWorldCommandEnvelope EnvelopeAt(ListSink sink, int index)
        {
            return JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(sink.Items[index].JsonPayload);
        }
    }
}
#endif