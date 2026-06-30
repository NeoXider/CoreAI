#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.EditMode
{
    public sealed class ComponentCommandLuaBindingsEditModeTests
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
        public void Lua_coreai_component_add_PublishesComponentCommand()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiComponentLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_component_add('Player', 'Rigidbody')");

            Assert.AreEqual(1, sink.Items.Count);
            Assert.AreEqual(ComponentCommand, sink.Items[0].CommandTypeId);
            Assert.AreEqual("component_command", sink.Items[0].SourceTaskHint);
            Assert.AreEqual("lua:component_command", sink.Items[0].SourceTag);
            CoreAiComponentCommandEnvelope envelope = EnvelopeAt(sink, 0);
            Assert.AreEqual("add", envelope.action);
            Assert.AreEqual("Player", envelope.targetName);
            Assert.AreEqual("Rigidbody", envelope.componentType);
        }

        [Test]
        public void Lua_coreai_component_remove_PublishesComponentCommand()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiComponentLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, "coreai_component_remove('Player', 'Rigidbody')");

            CoreAiComponentCommandEnvelope envelope = EnvelopeAt(sink, 0);
            Assert.AreEqual(ComponentCommand, sink.Items[0].CommandTypeId);
            Assert.AreEqual("remove", envelope.action);
            Assert.AreEqual("Player", envelope.targetName);
            Assert.AreEqual("Rigidbody", envelope.componentType);
        }

        [Test]
        public void Lua_coreai_component_setters_PublishExpectedPayloads()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiComponentLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            env.RunChunk(script, @"
                coreai_component_set_number('Player', 'Rigidbody', 'mass', 4.5)
                coreai_component_set_bool('Player', 'Collider', 'isTrigger', true)
                coreai_component_set_text('Player', 'TextMeshProUGUI', 'text', 'Ready')
                coreai_component_set_vector('Player', 'Transform', 'localScale', 1, 2, 3)");

            Assert.AreEqual(4, sink.Items.Count);

            CoreAiComponentCommandEnvelope number = EnvelopeAt(sink, 0);
            Assert.AreEqual("set", number.action);
            Assert.AreEqual("Rigidbody", number.componentType);
            Assert.AreEqual("mass", number.propertyName);
            Assert.AreEqual(4.5f, number.floatValue);

            CoreAiComponentCommandEnvelope boolean = EnvelopeAt(sink, 1);
            Assert.AreEqual("set", boolean.action);
            Assert.AreEqual("Collider", boolean.componentType);
            Assert.AreEqual("isTrigger", boolean.propertyName);
            Assert.AreEqual(1, boolean.boolValue);

            CoreAiComponentCommandEnvelope text = EnvelopeAt(sink, 2);
            Assert.AreEqual("set", text.action);
            Assert.AreEqual("TextMeshProUGUI", text.componentType);
            Assert.AreEqual("text", text.propertyName);
            Assert.AreEqual("Ready", text.stringValue);

            CoreAiComponentCommandEnvelope vector = EnvelopeAt(sink, 3);
            Assert.AreEqual("set", vector.action);
            Assert.AreEqual("Transform", vector.componentType);
            Assert.AreEqual("localScale", vector.propertyName);
            Assert.AreEqual(1f, vector.x);
            Assert.AreEqual(2f, vector.y);
            Assert.AreEqual(3f, vector.z);
        }

        [Test]
        public void Lua_coreai_component_set_number_NonFinite_ThrowsAndPublishesNothing()
        {
            ListSink sink = new();
            LuaApiRegistry reg = new();
            new CoreAiComponentLuaRuntimeBindings(sink).RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);

            Assert.Throws<ScriptRuntimeException>(() =>
                env.RunChunk(script, "coreai_component_set_number('Player', 'Rigidbody', 'mass', 0/0)"));
            Assert.AreEqual(0, sink.Items.Count);
        }

        [Test]
        public void Aggregator_RegistersComponentCommands_WithWorldEditCapability()
        {
            ListSink sink = new();
            LuaApiRegistry registry = new();
            AggregatingGameLuaRuntimeBindings bindings = new(
                GameLoggerUnscopedFallback.Instance,
                new CoreAiVersioningLuaRuntimeBindings(null, null),
                null,
                components: new CoreAiComponentLuaRuntimeBindings(sink),
                capabilities: LuaCapabilities.All);

            bindings.RegisterGameplayApis(registry, LuaCapabilities.WorldEdit);

            Assert.IsTrue(registry.TryGet("coreai_component_add", out _));
        }

        private static CoreAiComponentCommandEnvelope EnvelopeAt(ListSink sink, int index)
        {
            return JsonUtility.FromJson<CoreAiComponentCommandEnvelope>(sink.Items[index].JsonPayload);
        }
    }
}
#endif
