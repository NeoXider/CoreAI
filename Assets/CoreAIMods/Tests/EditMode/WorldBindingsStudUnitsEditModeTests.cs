using System;
using System.Collections.Generic;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Scripting;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The Lua-facing world bindings speak studs; Unity speaks metres. Positions written through
    /// the Rbx surface must read back unchanged through the world query bindings at any scale.
    /// </summary>
    public sealed class WorldBindingsStudUnitsEditModeTests
    {
        private readonly List<GameObject> _created = new();

        [SetUp]
        public void SetUp()
        {
            // WHY: A non-1:1 scale is the only regime where a metres/studs leak is observable.
            RbxSpace.ResetForTests(0.5f);
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();
            RbxSpace.ResetForTests();
        }

        [Test]
        public void WorldPos_MatchesRbxSurfaceWrite_AtNonUnitScale()
        {
            string name = NewProbeName();
            GameObject go = new(name);
            _created.Add(go);
            // WHY: This is exactly what the Rbx binder stores for Part.Position = (4, 6, 8).
            go.transform.position = RbxSpace.ToUnity(new RbxVector3(4f, 6f, 8f));

            Dictionary<string, Delegate> api = CaptureQueryApis();
            object raw = ((Func<string, object>)api["coreai_world_pos"])(name);

            Assert.IsInstanceOf<Dictionary<string, object>>(raw);
            Dictionary<string, object> pos = (Dictionary<string, object>)raw;
            Assert.AreEqual(4d, (double)pos["x"], 1e-6);
            Assert.AreEqual(6d, (double)pos["y"], 1e-6);
            Assert.AreEqual(8d, (double)pos["z"], 1e-6);
        }

        [Test]
        public void WorldSpawn_PublishesUnityMetres_ForStudsInput()
        {
            RecordingSink sink = new();
            Dictionary<string, Delegate> api = CaptureRuntimeApis(sink);
            FakeTable props = new(new Dictionary<string, object>
            {
                { "prefab", "Cube" },
                { "name", "StudSpawnProbe" },
                { "x", 4d },
                { "y", 6d },
                { "z", 8d }
            });

            ((Func<IScriptTable, string>)api["coreai_world_spawn"])(props);

            Assert.AreEqual(1, sink.Commands.Count);
            CoreAiWorldCommandEnvelope env =
                JsonUtility.FromJson<CoreAiWorldCommandEnvelope>(sink.Commands[0].JsonPayload);
            Vector3 expected = RbxSpace.ToUnity(new RbxVector3(4f, 6f, 8f));
            Assert.AreEqual(expected.x, env.x, 1e-6f);
            Assert.AreEqual(expected.y, env.y, 1e-6f);
            Assert.AreEqual(expected.z, env.z, 1e-6f);
        }

        [Test]
        public void WorldRaycast_ReturnsStudsHitPointAndDistance()
        {
            GameObject target = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _created.Add(target);
            // WHY: 1 m cube face sits 0.5 m before its centre; at 0.5 m/stud the Rbx-space
            // hit is exactly (0, 0, 9) with distance 29 from an origin 20 studs behind it.
            target.transform.position = RbxSpace.ToUnity(new RbxVector3(0f, 0f, 10f));
            Physics.SyncTransforms();

            Dictionary<string, Delegate> api = CaptureQueryApis();
            var raycast =
                (Func<double, double, double, double, double, double, double, object>)api["coreai_world_raycast"];
            object raw = raycast(0d, 0d, -20d, 0d, 0d, 1d, 100d);

            Assert.IsInstanceOf<Dictionary<string, object>>(raw);
            Dictionary<string, object> hit = (Dictionary<string, object>)raw;
            Assert.AreEqual(0d, (double)hit["x"], 1e-3);
            Assert.AreEqual(0d, (double)hit["y"], 1e-3);
            Assert.AreEqual(9d, (double)hit["z"], 1e-3);
            Assert.AreEqual(29d, (double)hit["distance"], 1e-3);
        }

        [Test]
        public void WorldPos_RoundTrips_AtOneToOneScale()
        {
            RbxSpace.ResetForTests(1f);
            string name = NewProbeName();
            GameObject go = new(name);
            _created.Add(go);
            go.transform.position = RbxSpace.ToUnity(new RbxVector3(4f, 6f, -8f));

            Dictionary<string, Delegate> api = CaptureQueryApis();
            Dictionary<string, object> pos =
                (Dictionary<string, object>)((Func<string, object>)api["coreai_world_pos"])(name);

            Assert.AreEqual(4d, (double)pos["x"], 1e-6);
            Assert.AreEqual(6d, (double)pos["y"], 1e-6);
            Assert.AreEqual(-8d, (double)pos["z"], 1e-6);
        }

        private static string NewProbeName()
        {
            return "StudUnitsProbe_" + Guid.NewGuid().ToString("N");
        }

        private static Dictionary<string, Delegate> CaptureQueryApis()
        {
            CapturingRegistry registry = new();
            new LuaCsWorldQueryBindings().RegisterGameplayApis(registry);
            return registry.Functions;
        }

        private static Dictionary<string, Delegate> CaptureRuntimeApis(RecordingSink sink)
        {
            CapturingRegistry registry = new();
            new LuaCsWorldRuntimeBindings(sink).RegisterGameplayApis(registry);
            return registry.Functions;
        }

        /// <summary>Captures registered Lua globals for direct invocation.</summary>
        private sealed class CapturingRegistry : IScriptFunctionRegistry
        {
            public readonly Dictionary<string, Delegate> Functions = new(StringComparer.Ordinal);

            public void Register(string name, Delegate callback)
            {
                Functions[name] = callback;
            }

            public void RegisterVarArgs(string name, Func<ScriptCallContext, ScriptCallResult> callback)
            {
            }

            public bool Contains(string name)
            {
                return Functions.ContainsKey(name);
            }

            public void ApplyTo(IScriptState state)
            {
            }
        }

        /// <summary>Captures published world commands for envelope inspection.</summary>
        private sealed class RecordingSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand command)
            {
                Commands.Add(command);
            }
        }

        /// <summary>Minimal string-keyed script table view.</summary>
        private sealed class FakeTable : IScriptTable
        {
            private readonly Dictionary<string, object> _values;

            public FakeTable(Dictionary<string, object> values)
            {
                _values = values;
            }

            public object this[string key] => _values.TryGetValue(key, out object value) ? value : null;

            public bool Has(string key)
            {
                return _values.ContainsKey(key);
            }

            public IEnumerable<KeyValuePair<object, object>> Pairs
            {
                get
                {
                    foreach (KeyValuePair<string, object> pair in _values)
                    {
                        yield return new KeyValuePair<object, object>(pair.Key, pair.Value);
                    }
                }
            }
        }
    }
}
