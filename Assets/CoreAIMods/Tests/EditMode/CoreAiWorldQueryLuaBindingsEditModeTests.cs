using System.Collections.Generic;
using CoreAI.Infrastructure.World;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class CoreAiWorldQueryLuaBindingsEditModeTests
    {
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
            {
                if (_created[i] != null)
                {
                    Object.DestroyImmediate(_created[i]);
                }
            }

            _created.Clear();
        }

        [Test]
        public void CoreAiWorldQueryLuaBindings_Exists_ReturnsTrueForExistingFalseForMissing()
        {
            CreateObject("QueryTestObj");
            Script script = CreateScript();

            DynValue result = new SecureLuaEnvironment().RunChunk(
                script,
                "return coreai_world_exists('QueryTestObj') and not coreai_world_exists('MissingQueryTestObj')");

            Assert.IsTrue(result.Boolean);
        }

        [Test]
        public void CoreAiWorldQueryLuaBindings_Pos_ReturnsPositionTableForExistingObject()
        {
            GameObject target = CreateObject("QueryTestObj");
            target.transform.position = new Vector3(1.25f, 2.5f, -3.75f);
            Script script = CreateScript();

            DynValue result = new SecureLuaEnvironment().RunChunk(script, @"
                local pos = coreai_world_pos('QueryTestObj')
                return { pos.x, pos.y, pos.z }");

            Assert.AreEqual(1.25d, result.Table.Get(1).Number);
            Assert.AreEqual(2.5d, result.Table.Get(2).Number);
            Assert.AreEqual(-3.75d, result.Table.Get(3).Number);
        }

        [Test]
        public void CoreAiWorldQueryLuaBindings_Pos_MissingObjectReturnsNil()
        {
            Script script = CreateScript();

            DynValue result = new SecureLuaEnvironment().RunChunk(
                script,
                "return coreai_world_pos('MissingQueryTestObj') == nil");

            Assert.IsTrue(result.Boolean);
        }

        [Test]
        public void CoreAiWorldQueryLuaBindings_Find_CaseInsensitivePatternFindsObject()
        {
            CreateObject("QueryTestObj");
            Script script = CreateScript();

            DynValue result = new SecureLuaEnvironment().RunChunk(script, @"
                local found = coreai_world_find('querytestobj')
                for i = 1, #found do
                    if found[i] == 'QueryTestObj' then
                        return true
                    end
                end
                return false");

            Assert.IsTrue(result.Boolean);
        }

        [Test]
        public void CoreAiWorldQueryLuaBindings_Raycast_NaNArgumentThrows()
        {
            Script script = CreateScript();

            Assert.Throws<ScriptRuntimeException>(() =>
                new SecureLuaEnvironment().RunChunk(
                    script,
                    "coreai_world_raycast(0/0, 0, 0, 0, 1, 0, 10)"));
        }

        private GameObject CreateObject(string name)
        {
            GameObject go = new(name);
            _created.Add(go);
            return go;
        }

        private static Script CreateScript()
        {
            LuaApiRegistry reg = new();
            new CoreAiWorldQueryLuaBindings().RegisterGameplayApis(reg);
            return new SecureLuaEnvironment().CreateScript(reg);
        }
    }
}
