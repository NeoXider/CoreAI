using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Rendering;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.RbxApi.Unity
{
    /// <summary>Real Unity material ownership, isolation and ray endpoint regressions.</summary>
    [TestFixture]
    public sealed class RbxMaterialLifetimeRegressionTests
    {
        private readonly Dictionary<string, Texture2D> _textures = new();
        private RbxTextureMaterialProvider _provider;
        private VariantSource _source;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            _source = new VariantSource();
            _provider = new RbxTextureMaterialProvider(LoadTexture) { VariantSource = _source };
        }

        [TearDown]
        public void TearDown()
        {
            _provider?.Dispose();
            RbxTextureMaterialProvider.ResetSharedCacheForTests();
            RbxProceduralMaterialProvider.ResetSharedCacheForTests();
            foreach (Texture2D texture in _textures.Values)
            {
                Object.DestroyImmediate(texture);
            }

            _textures.Clear();
            RbxSpace.ResetForTests();
        }

        [Test]
        public void RBX004_IndependentProvidersNeverMutateAnotherWorldsVariantHandle()
        {
            _source.Set("SharedName", "world-a", 2f);
            VariantSource otherSource = new();
            otherSource.Set("SharedName", "world-b", 8f);
            using RbxTextureMaterialProvider other = new(LoadTexture) { VariantSource = otherSource };
            RbxMaterialId id = new("Brick", 848, "SharedName");

            Assert.IsTrue(_provider.TryGetMaterial(in id, out Material first));
            Assert.IsTrue(other.TryGetMaterial(in id, out Material second));
            Assert.AreNotSame(first, second);
            Assert.AreSame(_textures["world-a"], first.GetTexture("_BaseMap"));
            Assert.AreSame(_textures["world-b"], second.GetTexture("_BaseMap"));
            float otherScale = second.GetFloat("_TextureScale");
            _source.Set("SharedName", "world-a-edited", 1f);
            Assert.IsTrue(_provider.TryGetMaterial(in id, out Material edited));
            Assert.AreSame(first, edited);
            Assert.AreSame(_textures["world-b"], second.GetTexture("_BaseMap"));
            Assert.AreEqual(otherScale, second.GetFloat("_TextureScale"));
            _provider.Dispose();
            Assert.IsTrue(first == null);
            Assert.IsTrue(second != null);
        }

        [Test]
        public void RBX003_IdleLookupsAreBoundedAndAcquiredHandlesSurviveEviction()
        {
            _source.Set("Pinned", "pinned", 1f);
            RbxMaterialId pinnedId = new("Brick", 848, "Pinned");
            Assert.IsTrue(_provider.TryAcquireMaterial(in pinnedId, out Material pinned));
            Material oldest = null;
            for (int index = 0; index < 128; index++)
            {
                string name = "Idle" + index;
                _source.Set(name, "idle", 1f);
                RbxMaterialId id = new("Brick", 848, name);
                Assert.IsTrue(_provider.TryGetMaterial(in id, out Material material));
                if (index == 0)
                {
                    oldest = material;
                }

                Assert.LessOrEqual(_provider.VariantMaterialCount,
                    RbxTextureMaterialProvider.MaximumIdleVariantMaterials + 1);
                Assert.IsTrue(pinned != null);
            }

            Assert.IsTrue(oldest == null);
            _provider.ReleaseMaterial(pinned);
            Assert.IsTrue(pinned == null);
            _provider.Dispose();
            _provider.Dispose();
            Assert.AreEqual(0, _provider.VariantMaterialCount);
            Assert.Throws<ObjectDisposedException>(() => _provider.TryGetMaterial(in pinnedId, out _));
        }

        [Test]
        public void RBX003_PartReplacementAndDestroyReleaseOnlyUnusedMaterials()
        {
            _source.Set("Shared", "shared", 1f);
            using BoundWorld world = new(_provider, _source);
            RbxInstance first = world.Part("Shared");
            RbxInstance second = world.Part("Shared");
            Material shared = world.Renderer(first).sharedMaterial;
            Assert.AreSame(shared, world.Renderer(second).sharedMaterial);
            first.Destroy();
            Assert.IsTrue(shared != null);
            Assert.AreEqual(1, _provider.VariantMaterialCount);
            for (int index = 0; index < 128; index++)
            {
                string name = "Renamed" + index;
                _source.Set(name, "renamed", 2f);
                world.Binder.SetMaterialVariant(second.Id, name);
                Assert.AreEqual(1, _provider.VariantMaterialCount);
            }

            Assert.IsTrue(shared == null);
            Material last = world.Renderer(second).sharedMaterial;
            second.Destroy();
            Assert.IsTrue(last == null);
            Assert.AreEqual(0, _provider.VariantMaterialCount);
        }

        [Test]
        public void RBX003_WorldTeardownReleasesAllLeasesAndKeepsInjectedProviderUsable()
        {
            _source.Set("World", "world", 1f);
            using BoundWorld world = new(_provider, _source);
            RbxInstance part = world.Part("World");
            Renderer renderer = world.Renderer(part);
            Material material = renderer.sharedMaterial;
            world.Binder.BeginHostTeardown();
            world.Binder.BeginHostTeardown();
            Assert.IsTrue(material == null);
            Assert.IsNull(renderer.sharedMaterial);
            Assert.AreEqual(0, _provider.VariantMaterialCount);
            RbxMaterialId id = new("Brick", 848, "World");
            Assert.IsTrue(_provider.TryGetMaterial(in id, out Material replacement));
            Assert.IsTrue(replacement != null);
        }

        [Test]
        public void RBX004_ChangingVariantSourcePreservesExistingLeasesUntilRepaintOrRelease()
        {
            _source.Set("Shared", "old-source", 1f);
            RbxMaterialId id = new("Brick", 848, "Shared");
            Assert.IsTrue(_provider.TryAcquireMaterial(in id, out Material previous));
            VariantSource next = new();
            next.Set("Shared", "new-source", 4f);
            _provider.VariantSource = next;
            Assert.IsTrue(_provider.TryAcquireMaterial(in id, out Material replacement));
            Assert.AreNotSame(previous, replacement);
            Assert.AreSame(_textures["old-source"], previous.GetTexture("_BaseMap"));
            _provider.ReleaseMaterial(replacement);
            Assert.IsTrue(previous != null);
            _provider.ReleaseMaterial(previous);
            Assert.AreEqual(0, _provider.VariantMaterialCount);
        }

        [Test]
        public void RBX003_ThrowingReleaseDoesNotPreventOtherLeaseCleanupOrRepeatIt()
        {
            FaultingLeaseProvider provider = new(_provider.FallbackMaterial);
            using BoundWorld world = new(provider, null);
            world.Part(null);
            world.Part(null);
            int acquired = provider.Acquired;
            Assert.AreEqual(2, provider.Active);
            provider.OnRelease = world.Binder.BeginHostTeardown;
            world.Binder.BeginHostTeardown();
            Assert.AreEqual(0, provider.Active);
            Assert.AreEqual(acquired, provider.Released);
            world.Binder.BeginHostTeardown();
            world.Game.Destroy();
            Assert.AreEqual(acquired, provider.Released);
        }

        [Test]
        public void RBX003_ActiveAppearanceReplacementKeepsExactlyOneLease()
        {
            using CallbackLeaseProvider provider = new(_provider.FallbackMaterial);
            using (BoundWorld world = new(provider, null))
            {
                RbxInstance part = world.Part(null);
                Renderer renderer = world.Renderer(part);
                Material previous = renderer.sharedMaterial;
                int acquiredBefore = provider.Acquired;
                Assert.AreEqual(1, provider.Active);

                world.Binder.SetColor(part.Id, new RbxColor3(0.25f, 0.5f, 0.75f));

                Assert.AreEqual(acquiredBefore + 1, provider.Acquired);
                Assert.AreEqual(provider.Acquired - 1, provider.Released);
                Assert.AreEqual(1, provider.Active);
                Assert.AreEqual(0, provider.UnexpectedReleases);
                Assert.IsTrue(previous == null, "The replaced native material must be released.");
                Assert.AreSame(provider.LastAcquired, renderer.sharedMaterial);
                Assert.IsTrue(renderer.sharedMaterial != null);
            }

            AssertAllLeasesReleased(provider);
        }

        [Test]
        public void RBX003_TeardownDuringAcquireReleasesReturningLeaseImmediately()
        {
            using CallbackLeaseProvider provider = new(_provider.FallbackMaterial);
            using (BoundWorld world = new(provider, null))
            {
                RbxInstance part = world.Part(null);
                Renderer renderer = world.Renderer(part);
                int acquiredBefore = provider.Acquired;
                Assert.AreEqual(1, provider.Active);
                provider.OnAcquire = world.Binder.BeginHostTeardown;

                Assert.DoesNotThrow(() =>
                    world.Binder.SetColor(part.Id, new RbxColor3(0.25f, 0.5f, 0.75f)));

                Assert.AreEqual(acquiredBefore + 1, provider.Acquired);
                AssertAllLeasesReleased(provider);
                Assert.IsNull(renderer.sharedMaterial,
                    "A returning acquisition must not publish a material after host teardown.");

                world.Binder.SetMaterialVariant(part.Id, "RetiredWorld");
                Assert.AreEqual(acquiredBefore + 1, provider.Acquired,
                    "A retired binder must not acquire another material on a later appearance write.");
                world.Binder.BeginHostTeardown();
                world.Game.Destroy();
                AssertAllLeasesReleased(provider);
            }

            AssertAllLeasesReleased(provider);
        }

        [Test]
        public void RBX003_DestroyDuringAcquireReleasesRemovedBindingsReturningLease()
        {
            using CallbackLeaseProvider provider = new(_provider.FallbackMaterial);
            using (BoundWorld world = new(provider, null))
            {
                RbxInstance part = world.Part(null);
                int acquiredBefore = provider.Acquired;
                Assert.AreEqual(1, provider.Active);
                provider.OnAcquire = part.Destroy;

                Assert.DoesNotThrow(() =>
                    world.Binder.SetColor(part.Id, new RbxColor3(0.25f, 0.5f, 0.75f)));

                Assert.AreEqual(acquiredBefore + 1, provider.Acquired);
                Assert.IsTrue(part.IsDestroyed);
                Assert.IsFalse(world.Binder.TryGetBoundObject(part.Id, out _));
                AssertAllLeasesReleased(provider);

                part.Destroy();
                world.Binder.BeginHostTeardown();
                world.Game.Destroy();
                AssertAllLeasesReleased(provider);
            }

            AssertAllLeasesReleased(provider);
        }

        [Test]
        public void RBX003_ThrowingTextureLoaderDoesNotLeakAnUnpublishedVariant()
        {
            using RbxTextureMaterialProvider provider = new(path =>
                path == "broken-map" ? throw new InvalidOperationException("Expected map failure.")
                    : LoadTexture(path));
            provider.VariantSource = _source;
            RbxMaterialId plain = new("Brick", 848);
            Assert.IsTrue(provider.TryGetMaterial(in plain, out _));
            _source.Set("Faulted", "broken-map", 1f);
            RbxMaterialId variant = new("Brick", 848, "Faulted");
            for (int index = 0; index < 16; index++)
            {
                Assert.Throws<InvalidOperationException>(() => provider.TryAcquireMaterial(in variant, out _));
                Assert.AreEqual(0, provider.VariantMaterialCount);
            }

            foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
            {
                Assert.AreNotEqual("CoreAiRbxTextureMaterial_Brick_Faulted", material.name,
                    "failed construction must destroy its native material before publishing a record");
            }

            _source.Set("Faulted", "recovered-map", 1f);
            Assert.IsTrue(provider.TryAcquireMaterial(in variant, out Material recovered));
            Assert.IsTrue(recovered != null);
            provider.ReleaseMaterial(recovered);
            Assert.AreEqual(0, provider.VariantMaterialCount);
        }

        [TestCase(0.28f, 10f, 0f, 15f, 0f, false)]
        [TestCase(1f, 10f, 0f, 15f, 0f, false)]
        [TestCase(0.28f, 0.5f, 0f, 0.375f, 0f, true)]
        [TestCase(1f, 0.5f, 0f, 0.375f, 0f, true)]
        [TestCase(0.28f, 0.3f, 0.4f, 0.21f, 0.28f, true)]
        [TestCase(1f, 0.3f, 0.4f, 0.21f, 0.28f, true)]
        [TestCase(0.28f, 6f, 8f, 9f, 12f, false)]
        [TestCase(1f, 6f, 8f, 9f, 12f, false)]
        public void RBX001_RealPhysicsUsesDirectionMagnitudeAsRange(float scale, float dx, float dy,
            float targetX, float targetY, bool expectedHit)
        {
            RbxSpace.Configure(scale);
            using BoundWorld world = new(_provider, null);
            RbxInstance target = world.Part(null);
            world.Binder.SetSize(target.Id, new RbxVector3(0.05f, 0.05f, 0.05f));
            world.Binder.SetPosition(target.Id, new RbxVector3(targetX, 500f + targetY, 0f));
            Physics.SyncTransforms();
            using UnityRbxPhysicsPort port = new(world.Binder);
            RbxVector3 direction = new(dx, dy, 0f);

            bool found = port.TryRaycast(new RbxVector3(0f, 500f, 0f), direction,
                false, null, out RbxPhysicsRaycastHit hit);

            Assert.AreEqual(expectedHit, found);
            if (found)
            {
                Assert.AreEqual(target.Id, hit.Instance);
                Assert.LessOrEqual(hit.Distance, direction.Magnitude + 0.001f);
            }
        }

        private static void AssertAllLeasesReleased(CallbackLeaseProvider provider)
        {
            Assert.AreEqual(0, provider.Active, "No acquired native handle may remain retained.");
            Assert.AreEqual(provider.Acquired, provider.Released,
                "Every acquired handle must have exactly one matching release.");
            Assert.AreEqual(0, provider.UnexpectedReleases,
                "Cleanup must not release the same handle twice or release an unknown handle.");
            Assert.AreEqual(0, provider.DisposeCalls,
                "The binder must release its leases without disposing the injected provider.");
        }

        private Texture2D LoadTexture(string path)
        {
            if (!_textures.TryGetValue(path, out Texture2D texture))
            {
                texture = new Texture2D(4, 4) { name = path };
                _textures.Add(path, texture);
            }

            return texture;
        }

        private sealed class VariantSource : IRbxMaterialVariantSource
        {
            private readonly Dictionary<string, RbxMaterialVariantData> _variants = new();

            public void Set(string name, string color, float tileWidth)
            {
                _variants[name] = new RbxMaterialVariantData(new RbxMaterialId("Brick", 848),
                    color, "", "", "", tileWidth);
            }

            public bool TryGetVariant(string name, out RbxMaterialVariantData data)
            {
                return _variants.TryGetValue(name, out data);
            }
        }

        private sealed class CallbackLeaseProvider : IRbxMaterialLeaseProvider<Material>, IDisposable
        {
            private readonly HashSet<Material> _active = new();

            public CallbackLeaseProvider(Material fallback)
            {
                FallbackMaterial = fallback;
            }

            public Material FallbackMaterial { get; }
            public Material LastAcquired { get; private set; }
            public int Acquired { get; private set; }
            public int Released { get; private set; }
            public int Active => _active.Count;
            public int UnexpectedReleases { get; private set; }
            public int DisposeCalls { get; private set; }
            public Action OnAcquire { get; set; }

            public bool TryGetMaterial(in RbxMaterialId id, out Material material)
            {
                material = FallbackMaterial;
                return true;
            }

            public bool TryAcquireMaterial(in RbxMaterialId id, out Material material)
            {
                material = new Material(FallbackMaterial) { name = "RbxReentrantLease" + Acquired };
                LastAcquired = material;
                _active.Add(material);
                Acquired++;
                Action callback = OnAcquire;
                OnAcquire = null;
                callback?.Invoke();
                return true;
            }

            public void ReleaseMaterial(Material material)
            {
                if (!_active.Remove(material))
                {
                    UnexpectedReleases++;
                    return;
                }

                Released++;
                Object.DestroyImmediate(material);
            }

            public void Dispose()
            {
                DisposeCalls++;
                foreach (Material material in _active)
                {
                    Object.DestroyImmediate(material);
                }

                _active.Clear();
            }
        }

        private sealed class FaultingLeaseProvider : IRbxMaterialLeaseProvider<Material>
        {
            public FaultingLeaseProvider(Material fallback) { FallbackMaterial = fallback; }
            public Material FallbackMaterial { get; }
            public int Acquired { get; private set; }
            public int Released { get; private set; }
            public int Active => Acquired - Released;
            public Action OnRelease { get; set; }

            public bool TryGetMaterial(in RbxMaterialId id, out Material material)
            {
                material = FallbackMaterial;
                return true;
            }

            public bool TryAcquireMaterial(in RbxMaterialId id, out Material material)
            {
                Acquired++;
                return TryGetMaterial(in id, out material);
            }

            public void ReleaseMaterial(Material material)
            {
                Released++;
                OnRelease?.Invoke();
                throw new InvalidOperationException("Expected material release failure.");
            }
        }

        private sealed class BoundWorld : IDisposable
        {
            private readonly GameObject _root;
            public BoundWorld(IRbxMaterialProvider<Material> provider, IRbxMaterialVariantSource source)
            {
                _root = new GameObject("RbxContractWorld");
                Binder = new InstanceGameObjectBinder(_root.transform, materialProvider: provider);
                Registry = new InstanceRegistry(binder: Binder);
                Game = DataModelBootstrap.CreateGame(Registry);
                Binder.MaterialVariantSource = source;
            }

            public InstanceGameObjectBinder Binder { get; }
            public InstanceRegistry Registry { get; }
            public RbxDataModel Game { get; }

            public RbxInstance Part(string variant)
            {
                RbxInstance part = Registry.Create("Part");
                PartProperties properties = PartProperties.CreateDefault();
                properties.Anchored = true;
                properties.Material = new RbxMaterialId("Brick", 848);
                properties.MaterialVariant = variant;
                Binder.SetPartProperties(part.Id, in properties);
                part.Parent = Game.GetService("Workspace");
                return part;
            }

            public Renderer Renderer(RbxInstance part)
            {
                Assert.IsTrue(Binder.TryGetBoundObject(part.Id, out GameObject bound));
                return bound.GetComponent<Renderer>();
            }

            public void Dispose()
            {
                Binder.BeginHostTeardown();
                Game.Destroy();
                Object.DestroyImmediate(_root);
            }
        }
    }
}
