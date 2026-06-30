#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using NUnit.Framework;
using System;
using System.Reflection;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode
{
    public sealed class CoreAiFullUnityLuaRuntimeEditModeTests
    {
        [Test]
        public void FullBindings_NotRegistered_WhenCapabilityMissing()
        {
            LuaApiRegistry registry = new();
            new AggregatingGameLuaRuntimeBindings(
                    GameLoggerUnscopedFallback.Instance,
                    new CoreAiVersioningLuaRuntimeBindings(null, null),
                    null,
                    full: new CoreAiFullUnityLuaRuntimeBindings(),
                    capabilities: LuaCapabilities.Read)
                .RegisterGameplayApis(registry, LuaCapabilities.All);

            Assert.IsFalse(registry.TryGet("unity_find", out _));
        }

        [Test]
        public void FullBindings_Registered_WhenFullCapabilityGranted()
        {
            LuaApiRegistry registry = new();
            new AggregatingGameLuaRuntimeBindings(
                    GameLoggerUnscopedFallback.Instance,
                    new CoreAiVersioningLuaRuntimeBindings(null, null),
                    null,
                    full: new CoreAiFullUnityLuaRuntimeBindings(),
                    capabilities: LuaCapabilities.All | LuaCapabilities.Full)
                .RegisterGameplayApis(registry, LuaCapabilities.All | LuaCapabilities.Full);

            Assert.IsTrue(registry.TryGet("unity_find", out _));
            Assert.IsTrue(registry.TryGet("unity_set_member", out _));
        }

        [Test]
        public void FullBindings_unity_find_AndSetPosition_WorksOnSceneObject()
        {
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = "FullLuaTestCube";
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                cube.transform.position = new Vector3(1f, 2f, 3f);

                env.RunChunk(script, @"
local id = unity_find('FullLuaTestCube')
assert(id ~= 0, 'find failed')
local p = unity_get_position(id)
assert(math.abs(p.x - 1) < 0.01 and math.abs(p.y - 2) < 0.01, 'get position')
unity_set_position(id, 5, 6, 7)
");
                Vector3 pos = cube.transform.position;
                Assert.AreEqual(5f, pos.x, 0.01f);
                Assert.AreEqual(6f, pos.y, 0.01f);
                Assert.AreEqual(7f, pos.z, 0.01f);
            }
            finally
            {
                Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void FullBindings_SearchDescribeAndHierarchyApis_ReturnSceneObjectMetadata()
        {
            GameObject parent = new("FullLuaParent");
            GameObject child = new("FullLuaChild");
            child.tag = "Player";
            child.AddComponent<ForgeMemberProbe>();
            child.transform.SetParent(parent.transform, false);
            child.transform.position = new Vector3(1f, 2f, 3f);

            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);

                MoonSharp.Interpreter.DynValue result = env.RunChunk(script, @"
local by_name = unity_find_all('FullLuaChild', 10)
local by_tag = unity_find_by_tag('Player', 1)
local by_component = unity_find_by_component('CoreAI.Tests.EditMode.ForgeMemberProbe', 10)
local parent_id = unity_find('FullLuaParent')
local children = unity_get_children(parent_id)
local desc = unity_describe_object(by_name[1].id)
return by_name[1].name == 'FullLuaChild'
    and by_name[1].path == 'FullLuaParent/FullLuaChild'
    and #by_tag == 1
    and by_component[1].name == 'FullLuaChild'
    and children[1].name == 'FullLuaChild'
    and desc.parent == 'FullLuaParent'
    and desc.child_count == 0
    and desc.transform.position.x == 1
");

                Assert.IsTrue(result.Boolean);
            }
            finally
            {
                if (parent != null)
                {
                    Object.DestroyImmediate(parent);
                }
            }
        }

        [Test]
        public void FullBindings_SearchApis_AllowOmittedMaxArgument()
        {
            GameObject sun = new("FullLuaSun");
            GameObject npc = new("FullLuaNpc");
            npc.tag = "Player";
            npc.AddComponent<ForgeMemberProbe>();

            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);

                MoonSharp.Interpreter.DynValue result = env.RunChunk(script, @"
local objects = unity_list_objects()
local suns = unity_find_all('FullLuaSun')
local players = unity_find_by_tag('Player')
local probes = unity_find_by_component('CoreAI.Tests.EditMode.ForgeMemberProbe')
return #objects >= 2
    and #suns == 1
    and suns[1].name == 'FullLuaSun'
    and #players == 1
    and players[1].name == 'FullLuaNpc'
    and #probes == 1
    and probes[1].name == 'FullLuaNpc'
");

                Assert.IsTrue(result.Boolean);
            }
            finally
            {
                if (sun != null)
                {
                    Object.DestroyImmediate(sun);
                }

                if (npc != null)
                {
                    Object.DestroyImmediate(npc);
                }
            }
        }

        [Test]
        public void FullBindings_TransformMutationApis_UpdateSceneObject()
        {
            GameObject parent = new("FullLuaNewParent");
            GameObject child = new("FullLuaMovable");

            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);

                env.RunChunk(script, @"
local child = unity_find('FullLuaMovable')
local parent = unity_find('FullLuaNewParent')
assert(child ~= 0 and parent ~= 0, 'find failed')
assert(unity_set_position(child, 4, 5, 6), 'set position')
assert(unity_set_rotation_euler(child, 10, 20, 30), 'set rotation')
assert(unity_set_scale(child, 2, 3, 4), 'set scale')
assert(unity_parent(child, parent, true), 'set parent')
local t = unity_get_transform(child)
return t.position.x == 4 and t.scale.z == 4
");

                Assert.AreSame(parent.transform, child.transform.parent);
                Assert.AreEqual(4f, child.transform.position.x, 0.01f);
                Assert.AreEqual(20f, child.transform.eulerAngles.y, 0.01f);
                Assert.AreEqual(4f, child.transform.localScale.z, 0.01f);
            }
            finally
            {
                if (parent != null)
                {
                    Object.DestroyImmediate(parent);
                }

                if (child != null)
                {
                    Object.DestroyImmediate(child);
                }
            }
        }

        [Test]
        public void FullBindings_PublicOnly_ByDefault_HidesNonPublicMembers()
        {
            GameObject probe = new("ForgeProbeGo");
            probe.AddComponent<ForgeMemberProbe>();
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int id = GetObjectId(probe);

                MoonSharp.Interpreter.DynValue publicValue = env.RunChunk(script,
                    $"return unity_get_member({id}, 'CoreAI.Tests.EditMode.ForgeMemberProbe', 'publicValue')");
                Assert.AreEqual(7d, publicValue.Number, 0.001d);

                Assert.Catch(
                    () => env.RunChunk(script,
                        $"return unity_get_member({id}, 'CoreAI.Tests.EditMode.ForgeMemberProbe', 'secretValue')"),
                    "Public-only Full bindings must not expose private members.");
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void FullBindings_NonPublicOptIn_ExposesPrivateMembers()
        {
            GameObject probe = new("ForgeProbeGo");
            probe.AddComponent<ForgeMemberProbe>();
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings(null, allowNonPublicMembers: true)
                    .RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int id = GetObjectId(probe);

                MoonSharp.Interpreter.DynValue secret = env.RunChunk(script,
                    $"return unity_get_member({id}, 'CoreAI.Tests.EditMode.ForgeMemberProbe', 'secretValue')");
                Assert.AreEqual(11d, secret.Number, 0.001d);
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void FullBindings_BlacklistPolicy_BlocksDeniedMembersAndTypes()
        {
            GameObject probe = new("ForgeProbeGo");
            probe.AddComponent<ForgeMemberProbe>();
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings(
                        null,
                        allowNonPublicMembers: false,
                        new DenyForgeProbePolicy())
                    .RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int id = GetObjectId(probe);

                Assert.Catch(
                    () => env.RunChunk(script,
                        $"return unity_get_member({id}, 'CoreAI.Tests.EditMode.ForgeMemberProbe', 'publicValue')"),
                    "Policy-denied members must not be readable through Full Lua reflection.");

                MoonSharp.Interpreter.DynValue found = env.RunChunk(script,
                    "return #unity_find_by_component('CoreAI.Tests.EditMode.ForgeMemberProbe')");
                Assert.AreEqual(0d, found.Number, 0.001d);
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void FullBindings_SetMember_CoercesColorVectorQuaternionEnum()
        {
            GameObject probe = new("ForgeCoerceGo");
            ForgeMemberProbe comp = probe.AddComponent<ForgeMemberProbe>();
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int id = GetObjectId(probe);
                const string t = "'CoreAI.Tests.EditMode.ForgeMemberProbe'";

                env.RunChunk(script, $@"
unity_set_member({id}, {t}, 'tint', '#ff0000')
unity_set_member({id}, {t}, 'offset', {{ x = 1, y = 2, z = 3 }})
unity_set_member({id}, {t}, 'spin', {{ x = 0, y = 90, z = 0 }})
unity_set_member({id}, {t}, 'mode', 'Boost')
unity_set_member({id}, {t}, 'bigValue', 5000000000)
unity_set_member({id}, {t}, 'smallValue', 200)
");

                Assert.AreEqual(Color.red, comp.tint, "Color from hex string");
                Assert.AreEqual(new Vector3(1, 2, 3), comp.offset, "Vector3 from table");
                Assert.AreEqual(90f, comp.spin.eulerAngles.y, 0.1f, "Quaternion from Euler table");
                Assert.AreEqual(ProbeMode.Boost, comp.mode, "Enum from name");
                Assert.AreEqual(5000000000L, comp.bigValue, "long width");
                Assert.AreEqual((byte)200, comp.smallValue, "byte width");
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void FullBindings_SetMember_CoercesColorTableEnumNumberRectBounds()
        {
            GameObject probe = new("ForgeCoerceGo2");
            ForgeMemberProbe comp = probe.AddComponent<ForgeMemberProbe>();
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int id = GetObjectId(probe);
                const string t = "'CoreAI.Tests.EditMode.ForgeMemberProbe'";

                env.RunChunk(script, $@"
unity_set_member({id}, {t}, 'tint', {{ r = 0, g = 1, b = 0, a = 0.5 }})
unity_set_member({id}, {t}, 'tint32', {{ r = 10, g = 20, b = 30, a = 40 }})
unity_set_member({id}, {t}, 'mode', 2)
unity_set_member({id}, {t}, 'area', {{ x = 1, y = 2, width = 3, height = 4 }})
unity_set_member({id}, {t}, 'box', {{ center = {{ x = 1, y = 1, z = 1 }}, size = {{ x = 2, y = 2, z = 2 }} }})
");

                Assert.AreEqual(new Color(0, 1, 0, 0.5f), comp.tint, "Color from r/g/b/a table");
                Assert.AreEqual(new Color32(10, 20, 30, 40), comp.tint32, "Color32 from table");
                Assert.AreEqual(ProbeMode.Boost, comp.mode, "Enum from number");
                Assert.AreEqual(new Rect(1, 2, 3, 4), comp.area, "Rect from table");
                Assert.AreEqual(new Vector3(1, 1, 1), comp.box.center, "Bounds center");
                Assert.AreEqual(new Vector3(2, 2, 2), comp.box.size, "Bounds size");
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void FullBindings_SetMember_CoercesUnityObjectReferenceById()
        {
            GameObject probe = new("ForgeRefGo");
            ForgeMemberProbe comp = probe.AddComponent<ForgeMemberProbe>();
            GameObject target = new("ForgeRefTarget");
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int probeId = GetObjectId(probe);
                int targetTransformId = GetObjectId(target.transform);

                env.RunChunk(script, $@"
unity_set_member({probeId}, 'CoreAI.Tests.EditMode.ForgeMemberProbe', 'linkedTransform', {targetTransformId})
");

                Assert.AreSame(target.transform, comp.linkedTransform,
                    "A Transform reference must be assignable by its object id.");
            }
            finally
            {
                Object.DestroyImmediate(probe);
                Object.DestroyImmediate(target);
            }
        }

        [Test]
        public void FullBindings_ListMembers_ReportsSettableMembersWithTypes()
        {
            GameObject probe = new("ForgeListGo");
            probe.AddComponent<ForgeMemberProbe>();
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int id = GetObjectId(probe);

                MoonSharp.Interpreter.DynValue members = env.RunChunk(script,
                    $"return unity_list_members({id}, 'CoreAI.Tests.EditMode.ForgeMemberProbe')");

                Assert.AreEqual(MoonSharp.Interpreter.DataType.Table, members.Type,
                    "unity_list_members should return a table of settable members.");
                Assert.Greater(members.Table.Length, 0, "Probe exposes several public settable members.");
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        [Test]
        public void FullBindings_SetMember_UnknownMember_ListsValidMembers()
        {
            GameObject probe = new("ForgeHintGo");
            probe.AddComponent<ForgeMemberProbe>();
            try
            {
                SecureLuaEnvironment env = new();
                LuaApiRegistry registry = new();
                new CoreAiFullUnityLuaRuntimeBindings().RegisterGameplayApis(registry);
                MoonSharp.Interpreter.Script script = env.CreateScript(registry);
                int id = GetObjectId(probe);

                Exception ex = Assert.Catch(() => env.RunChunk(script,
                    $"unity_set_member({id}, 'CoreAI.Tests.EditMode.ForgeMemberProbe', 'noSuchMember', 1)"),
                    "Setting an unknown member must throw.");

                StringAssert.Contains("publicValue", ex.Message,
                    "The error should list valid settable members as a did-you-mean hint.");
            }
            finally
            {
                Object.DestroyImmediate(probe);
            }
        }

        private static int GetObjectId(UnityEngine.Object obj)
        {
            if (obj == null)
            {
                return 0;
            }

            return obj.GetEntityId().GetHashCode();
        }
    }

    /// <summary>Probe component with one public and one private field for Full-tier visibility tests.</summary>
    public sealed class ForgeMemberProbe : MonoBehaviour
    {
        public int publicValue = 7;
        private int secretValue = 11;

        // Typed members exercised by the coercion tests (Lua table/string/number -> C# type).
        public Color tint = Color.black;
        public Color32 tint32 = new(0, 0, 0, 255);
        public Vector3 offset = Vector3.zero;
        public Quaternion spin = Quaternion.identity;
        public Rect area = new(0, 0, 0, 0);
        public Bounds box = new(Vector3.zero, Vector3.zero);
        public ProbeMode mode = ProbeMode.Idle;
        public long bigValue;
        public byte smallValue;
        public Transform linkedTransform;

        public int RevealSecret()
        {
            return secretValue;
        }
    }

    /// <summary>Enum used by the Full-tier coercion tests (set by name and by number).</summary>
    public enum ProbeMode
    {
        Idle = 0,
        Active = 1,
        Boost = 2
    }

    internal sealed class DenyForgeProbePolicy : IFullLuaAccessBlacklistPolicy
    {
        public bool IsTypeAllowed(Type componentType)
        {
            return componentType != typeof(ForgeMemberProbe);
        }

        public bool IsMemberAllowed(MemberInfo member)
        {
            return member.Name != nameof(ForgeMemberProbe.publicValue);
        }
    }
}
#endif
