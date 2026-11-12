using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Spatial;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.EditMode.RbxApi.Binding
{
    /// <summary>
    /// The Unity half of MVP17's minimal ParticleEmitter: the pooled <c>ParticleSystem</c> backend (a system on the
    /// part's GameObject, the box shape scaled by the part, studs to metres, the per-emitter cap, the emission face,
    /// bursts and Clear, reuse after release), the texture rule (a registered key, a Resources path, the soft dot for a
    /// Roblox or unresolved id) and the world host wiring with the emit-burst on a real part.
    /// </summary>
    [TestFixture]
    public sealed class Mvp17UnityParticleEditModeTests
    {
        private GameObject _hostGo;
        private RbxWorldHost _host;
        private GameObject _partGo;
        private GameObject _poolParent;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
        }

        [TearDown]
        public void TearDown()
        {
            if (_hostGo != null)
            {
                Object.DestroyImmediate(_hostGo);
            }

            if (_partGo != null)
            {
                Object.DestroyImmediate(_partGo);
            }

            if (_poolParent != null)
            {
                Object.DestroyImmediate(_poolParent);
            }

            RbxSpace.ResetForTests();
        }

        private UnityRbxParticleBackend NewBackend(out InstanceId partId)
        {
            _partGo = new GameObject("MVP17 part");
            _partGo.transform.localScale = new Vector3(2f, 1f, 2f);
            _poolParent = new GameObject("MVP17 pool parent");
            InstanceId id = new(7);
            partId = id;
            return new UnityRbxParticleBackend(candidate => candidate == id ? _partGo : null, _poolParent.transform,
                new RbxParticleBudget("test", 8, 50));
        }

        private static RbxParticleRenderState State(RbxParticleEmitter emitter)
        {
            return new RbxParticleRenderState(emitter, new RbxVector3(2f, 1f, 2f));
        }

        private static RbxParticleEmitter NewEmitterInstance(InstanceRegistry registry)
        {
            return (RbxParticleEmitter)registry.Create("ParticleEmitter");
        }

        [Test]
        public void Backend_ConfiguresASystemOnThePart_InWorldSpace_ScaledByTheShapeOnly()
        {
            InstanceRegistry registry = new();
            RbxParticleEmitter emitter = NewEmitterInstance(registry);
            emitter.SetLifetime(new RbxNumberRange(1f, 2f));
            emitter.SetSpeed(new RbxNumberRange(10f, 20f));
            emitter.SetRate(30d);
            emitter.SetSize(new RbxNumberSequence(2f, 0f));
            emitter.SetAcceleration(new RbxVector3(0f, -10f, 0f));
            using UnityRbxParticleBackend backend = NewBackend(out InstanceId partId);
            InstanceId emitterId = new(70);

            backend.Apply(emitterId, partId, State(emitter));

            ParticleSystem system = backend.GetSystem(emitterId);
            Assert.IsNotNull(system);
            Assert.AreSame(_partGo.transform, system.transform.parent, "the system follows its part");
            ParticleSystem.MainModule main = system.main;
            Assert.AreEqual(ParticleSystemSimulationSpace.World, main.simulationSpace);
            Assert.AreEqual(ParticleSystemScalingMode.Shape, main.scalingMode,
                "the part's scale sizes the box, never the particles");
            Assert.AreEqual(50, main.maxParticles, "the tier's per-emitter cap");
            Assert.AreEqual(1f, main.startLifetime.constantMin);
            Assert.AreEqual(2f, main.startLifetime.constantMax);
            Assert.AreEqual(10f * RbxSpace.MetersPerStud, main.startSpeed.constantMin, 1e-5f, "studs to metres");
            Assert.AreEqual(2f * RbxSpace.MetersPerStud, main.startSize.constant, 1e-5f);
            Assert.AreEqual(30f, system.emission.rateOverTime.constant);
            Assert.AreEqual(ParticleSystemShapeType.Box, system.shape.shapeType);
            Assert.IsTrue(system.forceOverLifetime.enabled);
            Assert.AreEqual(-10f * RbxSpace.MetersPerStud, system.forceOverLifetime.y.constant, 1e-5f);
            Assert.Less(Vector3.Angle(Vector3.up, system.transform.forward), 0.5f, "the box emits out of the Top face");

            emitter.SetEnabled(false);
            emitter.SetEmissionDirection(RbxParticleFace.Bottom);
            backend.Apply(emitterId, partId, State(emitter));
            Assert.AreEqual(0f, system.emission.rateOverTime.constant, "a disabled emitter emits nothing by itself");
            Assert.Less(Vector3.Angle(Vector3.down, system.transform.forward), 0.5f);
        }

        [Test]
        public void Backend_EmitsBurstsUpToTheCap_Clears_AndReusesAReleasedSystem()
        {
            InstanceRegistry registry = new();
            RbxParticleEmitter emitter = NewEmitterInstance(registry);
            emitter.SetRate(0d);
            using UnityRbxParticleBackend backend = NewBackend(out InstanceId partId);
            InstanceId first = new(71);
            backend.Apply(first, partId, State(emitter));
            ParticleSystem system = backend.GetSystem(first);

            backend.Emit(first, 30);
            Assert.AreEqual(30, system.particleCount);
            backend.Emit(first, 30);
            Assert.AreEqual(50, system.particleCount, "a burst past the cap emits only what fits");
            backend.Clear(first);
            Assert.AreEqual(0, system.particleCount);

            backend.Release(first);
            Assert.IsNull(backend.GetSystem(first));
            Assert.IsFalse(system.gameObject.activeInHierarchy, "a released system waits in the idle pool");
            InstanceId second = new(72);
            backend.Apply(second, partId, State(emitter));
            Assert.AreSame(system, backend.GetSystem(second), "the pool reuses it");
            Assert.AreEqual(1, backend.Count);
        }

        [Test]
        public void Backend_Textures_ARegisteredKeyFirst_ThenResources_ElseTheSoftDot()
        {
            using UnityRbxParticleBackend backend = NewBackend(out _);
            Texture2D own = new(4, 4);
            try
            {
                backend.RegisterTexture("Sparks", own);
                Assert.AreSame(own, backend.ResolveTexture("Sparks"));
                Assert.AreSame(backend.DefaultTexture, backend.ResolveTexture(RbxParticleEmitter.DefaultTexture),
                    "Roblox's default texture id draws CoreAI's soft dot");
                Assert.AreSame(backend.DefaultTexture, backend.ResolveTexture("rbxassetid://241685484"));
                Assert.AreSame(backend.DefaultTexture, backend.ResolveTexture("CoreAI/NoSuchTexture/anywhere"));
                Assert.AreSame(backend.DefaultTexture, backend.ResolveTexture(""));
                Assert.AreEqual(64, backend.DefaultTexture.width);
            }
            finally
            {
                Object.DestroyImmediate(own);
            }
        }

        [Test]
        public void Host_DrawsAnEmitterOnAWorkspacePart_AndItsBurst_ThenReleasesItWhenDestroyed()
        {
            _hostGo = new GameObject("MVP17 particle host");
            _host = _hostGo.AddComponent<RbxWorldHost>();
            _host.Initialize();
            Assert.IsNotNull(_host.ParticleReconciler);
            RbxInstance part = _host.Registry.Create("Part");
            part.Parent = _host.Registry.WorldRoot;
            _host.Binder.SetAnchored(part.Id, true);
            _host.Binder.SetSize(part.Id, new RbxVector3(2f, 1f, 2f));
            RbxParticleEmitter emitter = (RbxParticleEmitter)_host.Registry.Create("ParticleEmitter");
            emitter.SetRate(0d);
            emitter.Parent = part;
            emitter.Emit(40);

            _host.ParticleReconciler.Tick(new RbxVector3(0f, 0f, 0f));

            ParticleSystem system = _host.ParticleBackend.GetSystem(emitter.Id);
            Assert.IsNotNull(system);
            Assert.IsTrue(_host.Binder.TryGetBoundObject(part.Id, out GameObject partGo));
            Assert.AreSame(partGo.transform, system.transform.parent);
            Assert.AreEqual(40, system.particleCount, "the emit burst reached the system");

            emitter.Destroy();
            Assert.IsNull(_host.ParticleBackend.GetSystem(emitter.Id), "a destroyed emitter is released at once");
        }
    }
}
