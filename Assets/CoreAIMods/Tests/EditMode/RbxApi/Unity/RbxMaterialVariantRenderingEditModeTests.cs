using System.Collections.Generic;
using System.Text.RegularExpressions;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using CoreAI.Tests.EditMode.RbxApi.Acceptance;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>Rendering coverage for MaterialVariant overrides: variant shared materials,
    /// plain fallback, in-place live edits, tiling, and the Lua-to-renderer path.</summary>
    [TestFixture]
    public sealed class RbxMaterialVariantRenderingEditModeTests
    {
        private const int BrickValue = 848;
        private const int GrassValue = 1280;
        private const float BaseBrickTileWidthStuds = 10f;

        private static readonly int BaseMapPropertyId = Shader.PropertyToID("_BaseMap");
        private static readonly int BumpMapPropertyId = Shader.PropertyToID("_BumpMap");
        private static readonly int TextureScalePropertyId = Shader.PropertyToID("_TextureScale");

        private Dictionary<string, Texture2D> _textures;
        private HashSet<string> _missingPaths;
        private FakeVariantSource _source;
        private RbxTextureMaterialProvider _provider;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = true;
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            _textures = new Dictionary<string, Texture2D>();
            _missingPaths = new HashSet<string>();
            _source = new FakeVariantSource();
            _provider = new RbxTextureMaterialProvider(LoadTexture);
            _provider.VariantSource = _source;
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Texture2D texture in _textures.Values)
            {
                Object.DestroyImmediate(texture);
            }

            _textures = null;
            RbxTextureMaterialProvider.IgnoreProjectOverrideForTests = false;
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            RbxSpace.ResetForTests();
        }

        [Test]
        public void PartWithNoVariant_ResolvesSameSharedInstance()
        {
            RbxMaterialId brick = new("Brick", BrickValue);

            Assert.IsTrue(_provider.TryGetMaterial(in brick, out Material first));
            Assert.IsTrue(_provider.TryGetMaterial(in brick, out Material second));
            Assert.AreSame(first, second, "plain lookups must keep returning the shared instance");
            Assert.AreEqual("CoreAiRbxTextureMaterial_Brick", first.name);
        }

        [Test]
        public void VariantOverridingOnlyColorMap_KeepsBaseNormal()
        {
            RegisterBrickVariant("MyBrick", "MyPack/brick_color", string.Empty,
                string.Empty, string.Empty, 0f);
            RbxMaterialId brick = new("Brick", BrickValue);
            RbxMaterialId variant = new("Brick", BrickValue, "MyBrick");

            Assert.IsTrue(_provider.TryGetMaterial(in brick, out Material plain));
            Assert.IsTrue(_provider.TryGetMaterial(in variant, out Material resolved));
            Assert.AreNotSame(plain, resolved, "a variant must not alias the plain material");
            Assert.AreEqual("CoreAiRbxTextureMaterial_Brick_MyBrick", resolved.name);
            Assert.AreSame(_textures["MyPack/brick_color"], resolved.GetTexture(BaseMapPropertyId),
                "ColorMap override must replace the albedo slot");
            Assert.AreSame(plain.GetTexture(BumpMapPropertyId),
                resolved.GetTexture(BumpMapPropertyId),
                "an unnamed NormalMap must keep the base entry normal");
        }

        [Test]
        public void UnknownVariantName_FallsBackToPlainMaterial()
        {
            RbxMaterialId brick = new("Brick", BrickValue);
            RbxMaterialId unknown = new("Brick", BrickValue, "NoSuchVariant");

            Assert.IsTrue(_provider.TryGetMaterial(in brick, out Material plain));
            Assert.IsTrue(_provider.TryGetMaterial(in unknown, out Material resolved));
            Assert.AreSame(plain, resolved, "unknown variant must degrade to the plain material");
            Assert.AreNotSame(_provider.FallbackMaterial, resolved,
                "a bad variant name must never surface the diagnostic fallback");
        }

        [Test]
        public void MissingVariantSource_FallsBackToPlainMaterial()
        {
            RbxTextureMaterialProvider naked = new(LoadTexture);
            RbxMaterialId brick = new("Brick", BrickValue);
            RbxMaterialId variant = new("Brick", BrickValue, "MyBrick");

            Assert.IsTrue(naked.TryGetMaterial(in brick, out Material plain));
            Assert.IsTrue(naked.TryGetMaterial(in variant, out Material resolved));
            Assert.AreSame(plain, resolved, "no source must render the plain material");
        }

        [Test]
        public void TwoVariants_GetDifferentMaterials_SameVariantReturnsSameInstance()
        {
            RegisterBrickVariant("VariantA", "MyPack/a_color", string.Empty,
                string.Empty, string.Empty, 0f);
            RegisterBrickVariant("VariantB", "MyPack/b_color", string.Empty,
                string.Empty, string.Empty, 0f);
            RbxMaterialId variantA = new("Brick", BrickValue, "VariantA");
            RbxMaterialId variantB = new("Brick", BrickValue, "VariantB");

            Assert.IsTrue(_provider.TryGetMaterial(in variantA, out Material firstA));
            Assert.IsTrue(_provider.TryGetMaterial(in variantA, out Material secondA));
            Assert.IsTrue(_provider.TryGetMaterial(in variantB, out Material firstB));
            Assert.AreSame(firstA, secondA, "repeat lookups must return the cached instance");
            Assert.AreNotSame(firstA, firstB, "different variants need different materials");
        }

        [Test]
        public void EditingVariantColorMap_MutatesSameMaterialInPlace()
        {
            RegisterBrickVariant("LiveBrick", "MyPack/live_a", string.Empty,
                string.Empty, string.Empty, 0f);
            RbxMaterialId variant = new("Brick", BrickValue, "LiveBrick");

            Assert.IsTrue(_provider.TryGetMaterial(in variant, out Material first));
            Assert.AreSame(_textures["MyPack/live_a"], first.GetTexture(BaseMapPropertyId));

            RegisterBrickVariant("LiveBrick", "MyPack/live_b", string.Empty,
                string.Empty, string.Empty, 0f);

            Assert.IsTrue(_provider.TryGetMaterial(in variant, out Material second));
            Assert.AreSame(first, second, "live edits must reuse the material instance");
            Assert.AreSame(_textures["MyPack/live_b"], second.GetTexture(BaseMapPropertyId),
                "the albedo slot must follow the edited variant");
        }

        [Test]
        public void EditingVariantBaseMaterial_RefreshesTheInheritedSlots()
        {
            // Only the albedo is overridden, so the normal slot carries whatever the BASE material
            // supplies — which is exactly the slot a repointed BaseMaterial has to move.
            _source.Variants["Repointed"] = new RbxMaterialVariantData(
                new RbxMaterialId("Brick", BrickValue), "MyPack/repointed", string.Empty,
                string.Empty, string.Empty, 0f);
            RbxMaterialId brick = new("Brick", BrickValue);
            RbxMaterialId grass = new("Grass", GrassValue);
            RbxMaterialId variant = new("Brick", BrickValue, "Repointed");

            Assert.IsTrue(_provider.TryGetMaterial(in brick, out Material plainBrick));
            Assert.IsTrue(_provider.TryGetMaterial(in grass, out Material plainGrass));
            Assert.IsTrue(_provider.TryGetMaterial(in variant, out Material first));
            Assert.AreSame(plainBrick.GetTexture(BumpMapPropertyId),
                first.GetTexture(BumpMapPropertyId),
                "an unoverridden slot must start on the base material's own texture");

            _source.Variants["Repointed"] = new RbxMaterialVariantData(
                new RbxMaterialId("Grass", GrassValue), "MyPack/repointed", string.Empty,
                string.Empty, string.Empty, 0f);

            Assert.IsTrue(_provider.TryGetMaterial(in variant, out Material second));
            Assert.AreSame(first, second, "repointing must reuse the material instance");
            Assert.AreSame(plainGrass.GetTexture(BumpMapPropertyId),
                second.GetTexture(BumpMapPropertyId),
                "the unoverridden normal slot must follow the variant's new BaseMaterial, not stay " +
                "on the entry the cached record was first built from");
        }

        [Test]
        public void StudsPerTile_ChangesTextureScaleRelativeToBase()
        {
            RegisterBrickVariant("TiledBrick", string.Empty, string.Empty,
                string.Empty, string.Empty, 5f);
            RbxMaterialId brick = new("Brick", BrickValue);
            RbxMaterialId variant = new("Brick", BrickValue, "TiledBrick");

            Assert.IsTrue(_provider.TryGetMaterial(in brick, out Material plain));
            Assert.IsTrue(_provider.TryGetMaterial(in variant, out Material resolved));
            float baseScale = plain.GetFloat(TextureScalePropertyId);
            float variantScale = resolved.GetFloat(TextureScalePropertyId);
            Assert.AreEqual(baseScale * (BaseBrickTileWidthStuds / 5f), variantScale, 1e-5f,
                "halving the tile width must double the texture scale");
        }

        [Test]
        public void FailedVariantMap_LogsOnceAndKeepsBaseTexture()
        {
            _missingPaths.Add("MyPack/missing_color");
            RegisterBrickVariant("BadBrick", "MyPack/missing_color", string.Empty,
                string.Empty, string.Empty, 0f);
            RbxMaterialId brick = new("Brick", BrickValue);
            RbxMaterialId variant = new("Brick", BrickValue, "BadBrick");
            LogAssert.Expect(LogType.Error, new Regex(
                "MaterialVariant 'BadBrick'.*MyPack/missing_color",
                RegexOptions.IgnoreCase));

            Assert.IsTrue(_provider.TryGetMaterial(in brick, out Material plain));
            Assert.IsTrue(_provider.TryGetMaterial(in variant, out Material resolved));

            Assert.AreNotSame(plain, resolved);
            Assert.AreSame(plain.GetTexture(BaseMapPropertyId),
                resolved.GetTexture(BaseMapPropertyId),
                "a map that fails to load must keep the base texture");
            LogAssert.NoUnexpectedReceived();
        }

        [Test]
        public void LuaPartMaterialVariant_RendersVariantMaterialOnBackingRenderer()
        {
            RbxTextureMaterialProvider provider = new(LoadTexture);
            using (Mvp1AcceptanceWorld world = new(materialProvider: provider))
            {
                world.Binder.MaterialVariantSource =
                    world.Game.GetService("MaterialService") as IRbxMaterialVariantSource;
                world.Stack.Runtime.LoadMod("variant-render", @"
                    local svc = game:GetService('MaterialService')
                    local v = Instance.new('MaterialVariant')
                    v.Name = 'MyBrick'
                    v.BaseMaterial = Enum.Material.Brick
                    v.ColorMap = 'MyPack/brick_color'
                    v.Parent = svc
                    local part = Instance.new('Part', workspace)
                    part.Name = 'VariantPart'
                    part.Material = Enum.Material.Brick
                    part.MaterialVariant = 'MyBrick'
                    local plain = Instance.new('Part', workspace)
                    plain.Name = 'PlainPart'
                    plain.Material = Enum.Material.Brick");

                RbxInstance variantPart = world.Workspace.FindFirstChild("VariantPart");
                RbxInstance plainPart = world.Workspace.FindFirstChild("PlainPart");
                Assert.IsNotNull(variantPart);
                Assert.IsNotNull(plainPart);
                Material variantMaterial = world.BoundObject(variantPart)
                    .GetComponent<Renderer>().sharedMaterial;
                Material plainMaterial = world.BoundObject(plainPart)
                    .GetComponent<Renderer>().sharedMaterial;

                Assert.AreNotSame(plainMaterial, variantMaterial);
                Assert.AreEqual("CoreAiRbxTextureMaterial_Brick_MyBrick", variantMaterial.name);
                Assert.AreSame(_textures["MyPack/brick_color"],
                    variantMaterial.GetTexture(BaseMapPropertyId));
                Assert.AreSame(plainMaterial.GetTexture(BumpMapPropertyId),
                    variantMaterial.GetTexture(BumpMapPropertyId));
            }
        }

        [Test]
        public void VariantSourceArrivingAfterTheParts_RepaintsThemInsteadOfLeavingThemPlain()
        {
            // WHY: this is the restore path. A loaded world is staged binder-first — every part
            // materializes before the host can point the binder at the new MaterialService — so a
            // binder that only consults the source at materialization time renders every variant
            // in a loaded world plain, and never asks again.
            RbxTextureMaterialProvider provider = new(LoadTexture);
            using (Mvp1AcceptanceWorld world = new(materialProvider: provider))
            {
                world.Stack.Runtime.LoadMod("variant-late-source", @"
                    local svc = game:GetService('MaterialService')
                    local v = Instance.new('MaterialVariant')
                    v.Name = 'LateBrick'
                    v.BaseMaterial = Enum.Material.Brick
                    v.ColorMap = 'MyPack/late_color'
                    v.Parent = svc
                    local part = Instance.new('Part', workspace)
                    part.Name = 'LatePart'
                    part.Material = Enum.Material.Brick
                    part.MaterialVariant = 'LateBrick'");

                RbxInstance latePart = world.Workspace.FindFirstChild("LatePart");
                Assert.IsNotNull(latePart);
                Renderer renderer = world.BoundObject(latePart).GetComponent<Renderer>();
                Assert.AreEqual("CoreAiRbxTextureMaterial_Brick", renderer.sharedMaterial.name,
                    "with no source wired the part must sit on the plain material");

                world.Binder.MaterialVariantSource =
                    world.Game.GetService("MaterialService") as IRbxMaterialVariantSource;

                Assert.AreEqual("CoreAiRbxTextureMaterial_Brick_LateBrick",
                    renderer.sharedMaterial.name,
                    "wiring the source must repaint parts that already named a variant");
                Assert.AreSame(_textures["MyPack/late_color"],
                    renderer.sharedMaterial.GetTexture(BaseMapPropertyId));
            }
        }

        [Test]
        public void EditingALiveVariantFromLua_RepaintsThePartsAlreadyWearingIt()
        {
            // WHY: the provider only re-reads a variant when something asks it to, and editing the
            // variant's own properties touches no part. Without a push from the write path a script
            // that changed a live variant's ColorMap left every part wearing it on the old texture,
            // forever, with nothing in the log.
            RbxTextureMaterialProvider provider = new(LoadTexture);
            using (Mvp1AcceptanceWorld world = new(materialProvider: provider))
            {
                world.Binder.MaterialVariantSource =
                    world.Game.GetService("MaterialService") as IRbxMaterialVariantSource;
                world.Stack.Runtime.LoadMod("variant-live-edit", @"
                    local v = Instance.new('MaterialVariant')
                    v.Name = 'LiveEdit'
                    v.BaseMaterial = Enum.Material.Brick
                    v.ColorMap = 'MyPack/edit_before'
                    v.Parent = game:GetService('MaterialService')
                    local part = Instance.new('Part', workspace)
                    part.Name = 'EditPart'
                    part.Material = Enum.Material.Brick
                    part.MaterialVariant = 'LiveEdit'");

                RbxInstance part = world.Workspace.FindFirstChild("EditPart");
                Assert.IsNotNull(part);
                Renderer renderer = world.BoundObject(part).GetComponent<Renderer>();
                Assert.AreSame(_textures["MyPack/edit_before"],
                    renderer.sharedMaterial.GetTexture(BaseMapPropertyId));

                world.Stack.Runtime.LoadMod("variant-live-edit-2", @"
                    local v = game:GetService('MaterialService'):FindFirstChild('LiveEdit')
                    v.ColorMap = 'MyPack/edit_after'");

                Assert.AreSame(_textures["MyPack/edit_after"],
                    renderer.sharedMaterial.GetTexture(BaseMapPropertyId),
                    "editing the variant must repaint the part already wearing it");
            }
        }

        [Test]
        public void WorldHostInitialize_PointsBinderAtLiveMaterialService()
        {
            GameObject hostObject = new("VariantHostProbe");
            try
            {
                RbxWorldHost host = hostObject.AddComponent<RbxWorldHost>();
                host.Initialize();

                Assert.IsNotNull(host.Binder.MaterialVariantSource,
                    "a real host must wire the variant source without extra setup");
                Assert.AreSame(
                    host.Game.GetService("MaterialService") as IRbxMaterialVariantSource,
                    host.Binder.MaterialVariantSource);
            }
            finally
            {
                Object.DestroyImmediate(hostObject);
            }
        }

        private void RegisterBrickVariant(string name, string colorMap, string normalMap,
            string roughnessMap, string metalnessMap, float studsPerTile)
        {
            _source.Variants[name] = new RbxMaterialVariantData(
                new RbxMaterialId("Brick", BrickValue), colorMap, normalMap, roughnessMap,
                metalnessMap, studsPerTile);
        }

        private Texture2D LoadTexture(string resourcePath)
        {
            if (resourcePath == null || _missingPaths.Contains(resourcePath))
            {
                return null;
            }

            if (!_textures.TryGetValue(resourcePath, out Texture2D texture))
            {
                texture = new Texture2D(4, 2);
                _textures[resourcePath] = texture;
            }

            return texture;
        }

        /// <summary>Mutable engine-free variant registry backing the rendering tests.</summary>
        private sealed class FakeVariantSource : IRbxMaterialVariantSource
        {
            public readonly Dictionary<string, RbxMaterialVariantData> Variants =
                new Dictionary<string, RbxMaterialVariantData>();

            public bool TryGetVariant(string name, out RbxMaterialVariantData data)
            {
                if (!string.IsNullOrEmpty(name) && Variants.TryGetValue(name, out data))
                {
                    return true;
                }

                data = default;
                return false;
            }
        }
    }
}
