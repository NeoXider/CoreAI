using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// Proof of the MaterialVariant slice through the REAL mod runtime: RbxMaterialId carries an
    /// optional variant as a correct cache key, the catalog knows MaterialVariant/MaterialService
    /// with Roblox ancestry and creatability, a Lua script creates a variant, sets its maps and
    /// BaseMaterial, assigns part.MaterialVariant and reads it back through the part sink, clearing
    /// restores the plain material, and the MaterialService fronts the variant-source port.
    /// </summary>
    [TestFixture]
    public sealed class RbxMaterialVariantLuaBindingsEditModeTests
    {
        private SynchronizationContext _savedContext;

        /// <summary>Same sync-over-async hazard as the sibling ClickDetector fixture: detach Unity's
        /// SynchronizationContext so VM continuations complete on the thread pool.</summary>
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
                List<(string ModId, string Key)> keys = new();
                foreach ((string storedModId, string key) in _values.Keys)
                {
                    if (storedModId == modId)
                    {
                        keys.Add((storedModId, key));
                    }
                }

                foreach ((string ModId, string Key) key in keys)
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class FakeGameLogger : IGameLogger
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

        private static LuaCsModStack BuildStack(LuaCsRbxApiBindings roblox, MemoryStore store)
        {
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new FakeGameLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = roblox
            });
        }

        private static RbxInstance FindFirst(LuaCsRbxApiBindings roblox, string className)
        {
            foreach (RbxInstance descendant in roblox.Game.GetDescendants())
            {
                if (descendant.ClassName == className)
                {
                    return descendant;
                }
            }

            return null;
        }

        [Test]
        public void RbxMaterialId_Variant_Equality_Hash_And_ToString()
        {
            RbxMaterialId plain = new("Rock", 256);
            RbxMaterialId plainNullVariant = new("Rock", 256, null);
            RbxMaterialId mossy = new("Rock", 256, "Mossy");
            RbxMaterialId rocky = new("Rock", 256, "Rocky");

            Assert.AreEqual(plain, plainNullVariant, "two-arg ctor equals three-arg ctor with null");
            Assert.AreEqual(plain.GetHashCode(), plainNullVariant.GetHashCode());
            Assert.IsTrue(plain == plainNullVariant);
            Assert.IsFalse(plain != plainNullVariant);

            Assert.AreNotEqual(plain, mossy, "variant distinguishes the cache key");
            Assert.AreNotEqual(mossy, rocky, "different variants are not equal");
            Assert.AreNotEqual(mossy.GetHashCode(), rocky.GetHashCode(), "variants do not collide");
            Assert.AreNotEqual(plain.GetHashCode(), mossy.GetHashCode());
            Assert.IsTrue(mossy != rocky);
            Assert.IsFalse(mossy == rocky);
            Assert.IsTrue(mossy.Equals((object)new RbxMaterialId("Rock", 256, "Mossy")));
            Assert.IsFalse(mossy.Equals((object)new RbxMaterialId("Rock", 256, "Rocky")));
            Assert.IsFalse(mossy.Equals("not a material"));

            Assert.AreEqual("Enum.Material.Rock", plain.ToString(), "plain renders unchanged");
            Assert.AreNotEqual(plain.ToString(), mossy.ToString(), "variant renders unambiguously");
            Assert.IsTrue(mossy.ToString().Contains("Mossy"), "variant name survives ToString");
        }

        [Test]
        public void ClassCatalog_Knows_MaterialVariant_And_MaterialService()
        {
            ClassCatalog catalog = ClassCatalog.CreateMvp1();

            Assert.IsTrue(catalog.TryGet("MaterialVariant", out ClassDescriptor variant));
            Assert.AreEqual("Instance", variant.BaseClassName);
            Assert.IsTrue(variant.IsCreatable, "scripts create variants via Instance.new");
            Assert.IsFalse(variant.IsService);
            Assert.IsFalse(variant.IsAbstract);
            Assert.IsNotNull(variant.Factory, "catalog row carries the behavior factory");

            Assert.IsTrue(catalog.TryGet("MaterialService", out ClassDescriptor service));
            Assert.AreEqual("Instance", service.BaseClassName);
            Assert.IsFalse(service.IsCreatable, "services resolve via GetService, like ReplicatedStorage");
            Assert.IsTrue(service.IsService);
            Assert.IsFalse(service.IsAbstract);

            Assert.IsTrue(catalog.IsA("MaterialVariant", "Instance"));
            Assert.IsTrue(catalog.IsA("MaterialService", "Instance"));
        }

        [Test]
        public void Lua_MaterialVariant_Create_SetProps_AssignPart_RoundTrips()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local svc = game:GetService('MaterialService')
                store_set('svc', svc.ClassName)
                local v = Instance.new('MaterialVariant')
                v.Name = 'MossyRock'
                v.BaseMaterial = Enum.Material.Rock
                v.ColorMap = 'rbxassetid://1'
                v.NormalMap = 'rbxassetid://2'
                v.RoughnessMap = 'rbxassetid://3'
                v.MetalnessMap = 'rbxassetid://4'
                v.StudsPerTile = 2
                v.Parent = svc
                store_set('base_ok', tostring(v.BaseMaterial == Enum.Material.Rock))
                store_set('color', v.ColorMap)
                store_set('spt_ok', tostring(v.StudsPerTile == 2))
                local part = Instance.new('Part')
                part.Parent = workspace
                store_set('default_variant', part.MaterialVariant)
                part.MaterialVariant = 'MossyRock'
                store_set('after', part.MaterialVariant)");

            Assert.AreEqual("MaterialService", store.Get("m", "svc"), "service resolves via GetService");
            Assert.AreEqual("true", store.Get("m", "base_ok"), "BaseMaterial round-trips as Enum item");
            Assert.AreEqual("rbxassetid://1", store.Get("m", "color"), "ColorMap round-trips");
            Assert.AreEqual("true", store.Get("m", "spt_ok"), "StudsPerTile round-trips");
            Assert.AreEqual("", store.Get("m", "default_variant"), "unset variant reads as empty");
            Assert.AreEqual("MossyRock", store.Get("m", "after"), "part.MaterialVariant reads back");

            RbxInstance part = FindFirst(roblox, "Part");
            Assert.IsNotNull(part, "the Part materialized in the world");
            PartProperties stored = roblox.PartSink.GetPartPropertiesOrDefault(part.Id);
            Assert.AreEqual("MossyRock", stored.MaterialVariant, "sink stores the variant name");
            Assert.AreEqual("Plastic", stored.Material.Name, "Part.Material itself is untouched");

            RbxMaterialVariant variant = FindFirst(roblox, "MaterialVariant") as RbxMaterialVariant;
            Assert.IsNotNull(variant, "the variant materialized in the world");
            Assert.AreEqual("MossyRock", variant.Name);
            Assert.AreEqual("Rock", variant.BaseMaterial.Name, "variant BaseMaterial persisted");
            Assert.AreEqual("rbxassetid://2", variant.NormalMap);
            Assert.AreEqual(2f, variant.StudsPerTile);
        }

        [Test]
        public void Lua_Part_MaterialVariant_Clear_Restores_Plain_Material()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                part.MaterialVariant = 'MossyRock'
                store_set('set', part.MaterialVariant)
                part.MaterialVariant = ''
                store_set('cleared_empty', part.MaterialVariant)
                part.MaterialVariant = 'MossyRock'
                part.MaterialVariant = nil
                store_set('cleared_nil', part.MaterialVariant)");

            Assert.AreEqual("MossyRock", store.Get("m", "set"));
            Assert.AreEqual("", store.Get("m", "cleared_empty"), "empty string restores plain material");
            Assert.AreEqual("", store.Get("m", "cleared_nil"), "nil restores plain material");

            RbxInstance part = FindFirst(roblox, "Part");
            Assert.IsNotNull(part);
            Assert.IsNull(roblox.PartSink.GetPartPropertiesOrDefault(part.Id).MaterialVariant,
                "sink stores null after clearing");
        }

        [Test]
        public void MaterialService_VariantSource_Resolves_Registered_And_Misses_Unknown()
        {
            LuaCsRbxApiBindings roblox = new();
            MemoryStore store = new();
            LuaCsModStack stack = BuildStack(roblox, store);

            stack.Runtime.LoadMod("m", @"
                local v = Instance.new('MaterialVariant')
                v.Name = 'MossyRock'
                v.BaseMaterial = Enum.Material.Slate
                v.ColorMap = 'rbxassetid://9'
                v.StudsPerTile = 4
                v.Parent = game:GetService('MaterialService')");

            IRbxMaterialVariantSource source =
                roblox.Game.GetService("MaterialService") as IRbxMaterialVariantSource;
            Assert.IsNotNull(source, "MaterialService fronts the variant-source port");

            Assert.IsTrue(source.TryGetVariant("MossyRock", out RbxMaterialVariantData data),
                "registered variant resolves by name");
            Assert.AreEqual("Slate", data.BaseMaterial.Name, "snapshot carries the BaseMaterial");
            Assert.AreEqual("rbxassetid://9", data.ColorMap, "snapshot carries the maps");
            Assert.AreEqual(string.Empty, data.NormalMap, "unset maps read as empty");
            Assert.AreEqual(4f, data.StudsPerTile, "snapshot carries StudsPerTile");

            Assert.IsFalse(source.TryGetVariant("NoSuchVariant", out _), "unknown name reports false");
            Assert.IsFalse(source.TryGetVariant(null, out _), "null name reports false");
            Assert.IsFalse(source.TryGetVariant(string.Empty, out _), "empty name reports false");
        }
    }
}
