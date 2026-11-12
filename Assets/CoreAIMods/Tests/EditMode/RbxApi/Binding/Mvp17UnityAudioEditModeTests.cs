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
    /// The Unity half of MVP17's local sound playback: the pooled <c>AudioSource</c> backend (2D for a global voice,
    /// 3D at its part's position for a positional one, Unity's own roll-off neutralised, sources reused), the world
    /// host wiring (its <see cref="RbxWorldHost.Sounds"/> library on the world's SoundService, the reconciler, the
    /// release of a destroyed sound) and the <c>Resources</c> fallback. The clips are procedural sines; no audio file
    /// is shipped. Whether the sources are audible is not asserted: a batch run may have no audio device.
    /// </summary>
    [TestFixture]
    public sealed class Mvp17UnityAudioEditModeTests
    {
        private GameObject _hostGo;
        private RbxWorldHost _host;
        private AudioClip _clip;
        private GameObject _parentGo;

        [SetUp]
        public void SetUp()
        {
            RbxSpace.ResetForTests();
            _clip = Sine("mvp17-sine", 0.5f);
        }

        [TearDown]
        public void TearDown()
        {
            if (_hostGo != null)
            {
                Object.DestroyImmediate(_hostGo);
            }

            if (_parentGo != null)
            {
                Object.DestroyImmediate(_parentGo);
            }

            Object.DestroyImmediate(_clip);
            RbxSpace.ResetForTests();
        }

        private static AudioClip Sine(string name, float seconds)
        {
            const int frequency = 22050;
            int samples = Mathf.CeilToInt(frequency * seconds);
            AudioClip clip = AudioClip.Create(name, samples, 1, frequency, false);
            float[] data = new float[samples];
            for (int index = 0; index < samples; index++)
            {
                data[index] = 0.2f * Mathf.Sin(2f * Mathf.PI * 440f * index / frequency);
            }

            clip.SetData(data, 0);
            return clip;
        }

        private RbxWorldHost NewHost()
        {
            _hostGo = new GameObject("MVP17 RbxWorldHost");
            _host = _hostGo.AddComponent<RbxWorldHost>();
            _host.Initialize();
            return _host;
        }

        private static RbxSoundRenderState State(AudioClip clip, bool positional, RbxVector3 position, float volume = 0.5f,
            float speed = 1f, bool looped = false, int seek = 0)
        {
            return new RbxSoundRenderState(clip, clip.length, 0d, seek, volume, speed, looped, positional, position);
        }

        [Test]
        public void Backend_PlaysAGlobalVoiceFlat_AndAPositionalVoiceIn3DAtItsPosition()
        {
            _parentGo = new GameObject("MVP17 audio parent");
            using UnityRbxAudioBackend backend = new(_parentGo.transform);
            RbxSoundVoiceKey global = RbxSoundVoiceKey.ForSound(new InstanceId(11));
            RbxSoundVoiceKey positional = RbxSoundVoiceKey.ForSound(new InstanceId(12));

            backend.Apply(global, State(_clip, false, new RbxVector3(0f, 0f, 0f), 0.4f, 1.5f, true));
            backend.Apply(positional, State(_clip, true, new RbxVector3(10f, 2f, 5f)));

            AudioSource flat = backend.GetSource(global);
            AudioSource heard = backend.GetSource(positional);
            Assert.IsNotNull(flat);
            Assert.AreSame(_clip, flat.clip);
            Assert.AreEqual(0f, flat.spatialBlend, "a global sound plays at the same volume everywhere");
            Assert.AreEqual(0.4f, flat.volume, 1e-6f);
            Assert.AreEqual(1.5f, flat.pitch, 1e-6f);
            Assert.IsTrue(flat.loop);
            Assert.AreEqual(1f, heard.spatialBlend, "a sound in a part is heard from the part");
            Assert.AreEqual(RbxSpace.ToUnity(new RbxVector3(10f, 2f, 5f)), heard.transform.localPosition);
            Assert.AreEqual(0f, heard.dopplerLevel);
            Assert.Greater(heard.minDistance, 10000f, "the distance curve is the engine-free roll-off, not Unity's");
            Assert.AreEqual(UnityRbxAudioBackend.HolderName, heard.transform.parent.name);
            Assert.AreSame(_parentGo.transform, heard.transform.parent.parent);
        }

        [Test]
        public void Backend_ReusesReleasedSources_ClipsVolumeAndPitch_AndReleasesOnANullClip()
        {
            _parentGo = new GameObject("MVP17 audio parent");
            using UnityRbxAudioBackend backend = new(_parentGo.transform);
            RbxSoundVoiceKey first = RbxSoundVoiceKey.ForSound(new InstanceId(21));
            RbxSoundVoiceKey copy = RbxSoundVoiceKey.ForLocalCopy(1);
            backend.Apply(first, State(_clip, false, new RbxVector3(0f, 0f, 0f), 6f, 12f));
            AudioSource source = backend.GetSource(first);
            Assert.AreEqual(1f, source.volume, "a Roblox volume above 1 plays at the clip's full level");
            Assert.AreEqual(UnityRbxAudioBackend.MaxPitch, source.pitch, "Unity's pitch ends at 3");

            backend.Release(first);
            Assert.IsNull(backend.GetSource(first));
            Assert.IsNull(source.clip, "a released source lets go of its clip");
            backend.Apply(copy, State(_clip, false, new RbxVector3(0f, 0f, 0f)));
            Assert.AreSame(source, backend.GetSource(copy), "the pool reuses the released source");
            Assert.AreEqual(1, backend.Count);

            backend.Apply(copy, new RbxSoundRenderState(null, 0d, 0d, 0, 1f, 1f, false, false,
                new RbxVector3(0f, 0f, 0f)));
            Assert.IsFalse(backend.Holds(copy), "twin: a state with no clip releases the voice");
        }

        [Test]
        public void Host_HandsItsSoundsToTheWorld_PlaysASoundAtItsPart_AndReleasesItWhenDestroyed()
        {
            NewHost();
            _host.RegisterSound("sine", _clip);
            RbxSoundService service = (RbxSoundService)_host.Game.GetService("SoundService");
            Assert.AreSame(_host.Sounds, service.Library, "the world plays the host's sounds");
            CollectionAssert.AreEqual(new[] { "sine" }, service.GetSoundIds());

            RbxInstance part = _host.Registry.Create("Part");
            part.Parent = _host.Registry.WorldRoot;
            _host.Binder.SetAnchored(part.Id, true);
            _host.Binder.SetPosition(part.Id, new RbxVector3(4f, 3f, 2f));
            RbxSound sound = (RbxSound)_host.Registry.Create("Sound");
            sound.SetSoundId("sine");
            sound.Parent = part;
            using RbxSoundPlayback playback = new(_host.Registry, _host.Game, null);
            playback.Step(0d);
            Assert.IsTrue(sound.IsLoaded);
            Assert.AreEqual(_clip.length, sound.TimeLength, 1e-6d);
            sound.Play();

            _host.SoundReconciler.Tick(new RbxVector3(4f, 3f, 2f));

            AudioSource source = _host.AudioBackend.GetSource(RbxSoundVoiceKey.ForSound(sound.Id));
            Assert.IsNotNull(source);
            Assert.AreSame(_clip, source.clip);
            Assert.AreEqual(1f, source.spatialBlend);
            Assert.AreEqual(RbxSpace.ToUnity(new RbxVector3(4f, 3f, 2f)), source.transform.localPosition);
            Assert.AreEqual(0.5f, source.volume, 1e-6f, "the listener stands at the part: no attenuation");

            sound.Destroy();
            Assert.IsNull(_host.AudioBackend.GetSource(RbxSoundVoiceKey.ForSound(sound.Id)),
                "a destroyed sound is released at once");
            Assert.AreEqual(0, _host.AudioBackend.Count);
        }

        [Test]
        public void ResourcesFallback_LoadsNothingForAMissingPathOrARobloxId()
        {
            Assert.IsFalse(UnityRbxAudioBackend.TryLoadFromResources("CoreAI/NoSuchSound/anywhere", out _));
            Assert.IsFalse(UnityRbxAudioBackend.TryLoadFromResources("rbxassetid://1843463175", out _),
                "an id with a scheme is never a Resources path");
            Assert.IsFalse(UnityRbxAudioBackend.TryLoadFromResources("  ", out _));
        }

        [Test]
        public void Host_WithSoundsSwitchedOff_KeepsTheLibrary_ButBuildsNoBackend()
        {
            _hostGo = new GameObject("MVP17 silent host");
            _hostGo.SetActive(false);
            _host = _hostGo.AddComponent<RbxWorldHost>();
            System.Reflection.FieldInfo field = typeof(RbxWorldHost).GetField("_playSounds",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            Assert.IsNotNull(field, "the host keeps its play-sounds switch");
            field.SetValue(_host, false);
            _host.Initialize();

            Assert.IsNull(_host.SoundReconciler);
            Assert.IsNull(_host.AudioBackend);
            Assert.AreSame(_host.Sounds, ((RbxSoundService)_host.Game.GetService("SoundService")).Library,
                "sounds still load and keep their timelines with the output switched off");
        }
    }
}
