using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Instances
{
    /// <summary>
    /// MVP17's Sound, SoundGroup and SoundService, engine-free: the <c>Full-API-Dump.json</c> defaults, the
    /// Sound.yaml playback rules (Play from the last script-set position, Stop to 0, Pause, Resume, Ended, DidLoop,
    /// Loaded), the library's id rule (DEV-133, DEV-134), the scheduler-driven clock, the roll-off formulas of
    /// RollOffMode.yaml, local copies, PlayOnRemove, and the reconciler with a fake backend and its budget. Each
    /// behaviour has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class Mvp17SoundEngineFreeEditModeTests
    {
        private const double Frame = 1d / 60d;

        private InstanceRegistry _registry;
        private RbxDataModel _game;
        private RbxSoundService _service;
        private ModScheduler _scheduler;
        private RbxSoundPlayback _playback;
        private RbxSoundLibrary _library;
        private List<string> _log;

        [SetUp]
        public void SetUp()
        {
            _registry = new InstanceRegistry();
            _game = DataModelBootstrap.CreateGame(_registry);
            _service = (RbxSoundService)_game.GetService("SoundService");
            _scheduler = new ModScheduler(new NoThreadFactory(), new RbxAccumulatingTimeSource());
            _log = new List<string>();
            _playback = new RbxSoundPlayback(_registry, _game, _scheduler, _log.Add);
            _library = new RbxSoundLibrary();
            _library.Register("beep", 1d, "beep-clip");
            _library.Register("music", 4d, "music-clip");
            _service.AttachLibrary(_library);
        }

        [TearDown]
        public void TearDown()
        {
            _playback.Dispose();
        }

        private sealed class NoThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("these tests connect plain C# handlers only");
            }
        }

        private sealed class FakeBackend : IRbxAudioBackend
        {
            public readonly Dictionary<RbxSoundVoiceKey, RbxSoundRenderState> Playing = new();
            public int ApplyCalls;
            public int ReleaseCalls;

            public void Apply(RbxSoundVoiceKey voice, in RbxSoundRenderState state)
            {
                ApplyCalls++;
                Playing[voice] = state;
            }

            public void Release(RbxSoundVoiceKey voice)
            {
                ReleaseCalls++;
                Playing.Remove(voice);
            }

            public bool Holds(RbxSoundVoiceKey voice)
            {
                return Playing.ContainsKey(voice);
            }
        }

        private RbxSound NewSound(string id, RbxInstance parent)
        {
            RbxSound sound = (RbxSound)_registry.Create("Sound");
            sound.SetSoundId(id);
            sound.Parent = parent;
            return sound;
        }

        private RbxInstance NewPart()
        {
            RbxInstance part = _registry.Create("Part");
            part.Name = "Host";
            part.Parent = _registry.WorldRoot;
            return part;
        }

        private void Watch(RbxScriptSignal signal, string label, List<string> events)
        {
            signal.BindScheduler(_scheduler);
            signal.Connect((Action<object[]>)(arguments =>
                events.Add(arguments.Length == 0 ? label : label + ":" + string.Join(",", arguments))));
        }

        private void WatchAll(RbxSound sound, List<string> events)
        {
            Watch(sound.Played, "Played", events);
            Watch(sound.Paused, "Paused", events);
            Watch(sound.Resumed, "Resumed", events);
            Watch(sound.Stopped, "Stopped", events);
            Watch(sound.Ended, "Ended", events);
            Watch(sound.Loaded, "Loaded", events);
            Watch(sound.DidLoop, "DidLoop", events);
        }

        private void Frames(int count, double seconds = Frame)
        {
            for (int index = 0; index < count; index++)
            {
                _scheduler.Advance(seconds);
            }
        }

        // ---- defaults and setters ----------------------------------------------------------------

        [Test]
        public void MVP17_Sound_DefaultsEqualTheFullApiDump()
        {
            RbxSound sound = (RbxSound)_registry.Create("Sound");
            RbxSoundGroup group = (RbxSoundGroup)_registry.Create("SoundGroup");

            Assert.AreEqual("Sound", sound.Name);
            Assert.AreEqual("", sound.SoundId);
            Assert.AreEqual(0.5f, sound.Volume);
            Assert.IsFalse(sound.Looped);
            Assert.AreEqual(1f, sound.PlaybackSpeed);
            Assert.AreEqual(0d, sound.TimePosition);
            Assert.AreEqual(0d, sound.TimeLength);
            Assert.IsFalse(sound.Playing);
            Assert.IsFalse(sound.IsPlaying);
            Assert.IsTrue(sound.IsPaused, "the dump's IsPaused default is true");
            Assert.IsFalse(sound.IsLoaded);
            Assert.AreEqual(10f, sound.RollOffMinDistance);
            Assert.AreEqual(10000f, sound.RollOffMaxDistance);
            Assert.AreEqual(RbxRollOffMode.Inverse, sound.RollOffMode);
            Assert.IsFalse(sound.PlayOnRemove);
            Assert.IsNull(sound.SoundGroup);
            Assert.AreEqual(0.5f, group.Volume);
            Assert.AreEqual("SoundGroup", group.Name);
            Assert.AreSame(_game.FindFirstChildOfClass("SoundService"), _service,
                "SoundService is a child of game, as in Roblox");
            Assert.IsTrue(_registry.Catalog.TryGet("Sound", out ClassDescriptor descriptor) && descriptor.IsCreatable);
            Assert.IsFalse(_registry.Catalog.TryGetKnownUnimplementedClass("Sound", out _),
                "the MVP17 loud stub is gone now that Sound is a real class");
        }

        [Test]
        public void MVP17_Sound_SettersClampToTheMirrorsRanges_AndRefuseWhatCannotPlay_NegativeTwin()
        {
            RbxSound sound = (RbxSound)_registry.Create("Sound");
            sound.SetVolume(12d);
            Assert.AreEqual(10f, sound.Volume, "Sound.yaml: Volume can be set between 0 and 10");
            sound.SetVolume(-1d);
            Assert.AreEqual(0f, sound.Volume);
            sound.SetPlaybackSpeed(-2d);
            Assert.AreEqual(0f, sound.PlaybackSpeed);
            sound.SetRollOffMinDistance(-5d);
            Assert.AreEqual(0f, sound.RollOffMinDistance);

            RbxError nan = Assert.Throws<RbxError>(() => sound.SetVolume(double.NaN));
            Assert.AreEqual(RbxErrorCode.BadArgument, nan.Code);
            Assert.Throws<RbxError>(() => sound.SetTimePosition(double.PositiveInfinity));
            Assert.Throws<RbxError>(() => sound.SetRollOffMode((RbxRollOffMode)7));
            RbxError group = Assert.Throws<RbxError>(() => sound.SetSoundGroup(_registry.Create("Part")));
            StringAssert.Contains("SoundGroup", group.Message);
            Assert.AreEqual(0f, sound.Volume, "a refused write changes nothing");
        }

        [Test]
        public void MVP17_Sound_ARealChangeFiresChangedOnce_AnEqualWriteNothing()
        {
            RbxSound sound = (RbxSound)_registry.Create("Sound");
            List<string> changed = new();
            sound.Changed.BindScheduler(_scheduler);
            sound.Changed.Connect((Action<object[]>)(arguments => changed.Add((string)arguments[0])));

            sound.SetVolume(0.8d);
            sound.SetVolume(0.8d);
            sound.SetLooped(true);
            sound.SetLooped(true);
            sound.SetRollOffMode(RbxRollOffMode.Linear);
            sound.SetRollOffMode(RbxRollOffMode.Linear);
            Frames(1);

            CollectionAssert.AreEqual(new[] { "Volume", "Looped", "RollOffMode" }, changed);
        }

        // ---- loading -----------------------------------------------------------------------------

        [Test]
        public void MVP17_Playback_ARegisteredIdLoadsOnTheNextFrame_FiringLoadedWithTheId()
        {
            RbxSound sound = NewSound("beep", _registry.WorldRoot);
            List<string> events = new();
            WatchAll(sound, events);
            Assert.IsFalse(sound.IsLoaded, "loading is asynchronous: nothing is loaded inside the write");

            Frames(2);

            Assert.IsTrue(sound.IsLoaded);
            Assert.AreEqual(1d, sound.TimeLength);
            Assert.AreEqual("beep-clip", sound.LoadedAsset.Clip);
            CollectionAssert.AreEqual(new[] { "Loaded:beep" }, events);
            CollectionAssert.IsEmpty(_log, "a known id is never noted");
        }

        [Test]
        public void DEV134_AnUnknownOrRobloxAssetId_NeverLoads_AndIsNotedOncePerIdWithBothWaysToProvideASound()
        {
            RbxSound unknown = NewSound("does/not/exist", _registry.WorldRoot);
            RbxSound roblox = NewSound("rbxassetid://1843463175", _registry.WorldRoot);
            RbxSound again = NewSound("does/not/exist", _registry.WorldRoot);
            List<string> events = new();
            WatchAll(unknown, events);
            unknown.Play();

            Frames(5);

            Assert.IsFalse(unknown.IsLoaded);
            Assert.IsFalse(roblox.IsLoaded);
            Assert.IsFalse(again.IsLoaded);
            Assert.AreEqual(0d, unknown.TimeLength);
            Assert.AreEqual(0d, unknown.TimePosition, "an unloaded sound's clock does not move");
            Assert.IsTrue(unknown.Playing, "Play still sets Playing, as on a failed Roblox load");
            CollectionAssert.AreEqual(new[] { "Played:does/not/exist" }, events, "Loaded never fires");
            Assert.AreEqual(2, _log.Count, "one line per id, however many sounds use it");
            StringAssert.Contains("names no sound this host provides", _log[0]);
            StringAssert.Contains("RbxWorldHost.Sounds.Register", _log[0]);
            StringAssert.Contains("Resources folder", _log[0]);
            StringAssert.Contains("SoundService:GetSoundIds()", _log[0]);
            StringAssert.Contains("Roblox asset id", _log[1]);
        }

        [Test]
        public void DEV134_ARegistrationLater_LoadsASoundThatFailedEarlier()
        {
            RbxSound sound = NewSound("late", _registry.WorldRoot);
            Frames(2);
            Assert.IsFalse(sound.IsLoaded);

            _library.Register("late", 2d, "late-clip");
            Frames(1);

            Assert.IsTrue(sound.IsLoaded, "a new registration bumps the library version, so the sound tries again");
            Assert.AreEqual(2d, sound.TimeLength);
        }

        [Test]
        public void DEV134_TheLibrary_ResolvesKeysFirst_ThenItsFallback_ButNeverSendsARobloxIdToTheFallback()
        {
            List<string> asked = new();
            RbxSoundLibrary library = new((string id, out RbxSoundAsset asset) =>
            {
                asked.Add(id);
                asset = id == "Sounds/door" ? new RbxSoundAsset(id, 0.5d, "door") : default;
                return asset.IsValid;
            });
            library.Register("rbxassetid://12222216", 3d, "imported");

            Assert.IsTrue(library.TryResolve("Sounds/door", out RbxSoundAsset door));
            Assert.AreEqual(0.5d, door.LengthSeconds);
            Assert.IsTrue(library.TryResolve("Sounds/door", out _));
            Assert.IsTrue(library.TryResolve("rbxassetid://12222216", out RbxSoundAsset imported),
                "a host may serve an imported place's asset ids by registering them");
            Assert.AreEqual("imported", imported.Clip);
            Assert.IsFalse(library.TryResolve("rbxassetid://5", out _));
            Assert.IsFalse(library.TryResolve("Sounds/missing", out _));
            Assert.IsFalse(library.TryResolve("Sounds/missing", out _));
            CollectionAssert.AreEqual(new[] { "Sounds/door", "Sounds/missing" }, asked,
                "answers are cached, and a Roblox id never reaches the fallback");
            Assert.Throws<ArgumentOutOfRangeException>(() => library.Register("bad", 0d));
            Assert.Throws<ArgumentException>(() => library.Register("  ", 1d));
        }

        [Test]
        public void DEV133_GetSoundIds_ListsTheRegisteredKeysInOrdinalOrder_AndNothingWithoutALibrary()
        {
            _library.Register("Click", 0.1d);
            CollectionAssert.AreEqual(new[] { "Click", "beep", "music" }, _service.GetSoundIds());
            _library.Remove("beep");
            CollectionAssert.AreEqual(new[] { "Click", "music" }, _service.GetSoundIds());

            _service.AttachLibrary(null);
            CollectionAssert.IsEmpty(_service.GetSoundIds());
        }

        // ---- the clock ---------------------------------------------------------------------------

        [Test]
        public void MVP17_Playback_APlayingSoundAdvancesByScaledTimeAndSpeed_ThenEndsAtZeroWithEnded()
        {
            RbxSound sound = NewSound("beep", _registry.WorldRoot);
            Frames(1);
            List<string> events = new();
            WatchAll(sound, events);
            sound.SetPlaybackSpeed(2d);
            sound.Play();

            Frames(1, 0.25d);
            Assert.AreEqual(0.5d, sound.TimePosition, 1e-9, "a quarter second at speed 2 is half a second of clip");
            Assert.IsTrue(sound.IsPlaying);

            Frames(1, 0.25d);
            Frames(1, 0d);

            Assert.IsFalse(sound.Playing, "a sound that is not looped stops at its end");
            Assert.IsTrue(sound.IsPaused);
            Assert.AreEqual(0d, sound.TimePosition);
            CollectionAssert.AreEqual(new[] { "Played:beep", "Ended:beep" }, events);
        }

        [Test]
        public void MVP17_Playback_ALoopedSoundWraps_FiresDidLoopWithTheCount_AndNeverEnds()
        {
            RbxSound sound = NewSound("beep", _registry.WorldRoot);
            sound.SetLooped(true);
            Frames(1);
            List<string> events = new();
            WatchAll(sound, events);
            sound.Play();

            Frames(1, 1.25d);
            Frames(1, 1d);
            Frames(1, 0d);

            Assert.IsTrue(sound.Playing);
            Assert.AreEqual(0.25d, sound.TimePosition, 1e-9);
            CollectionAssert.AreEqual(new[] { "Played:beep", "DidLoop:beep,1", "DidLoop:beep,2" }, events);

            sound.Stop();
            sound.Play();
            Frames(1, 1.5d);
            Frames(1, 0d);
            Assert.AreEqual("DidLoop:beep,1", events[events.Count - 1], "Stop resets the loop count (Sound.yaml)");
        }

        [Test]
        public void MVP17_Sound_PlayStartsFromTheLastPositionAScriptSet_StopReturnsToZero()
        {
            RbxSound sound = NewSound("music", _registry.WorldRoot);
            Frames(1);
            sound.SetTimePosition(1.5d);
            sound.Play();
            Assert.AreEqual(1.5d, sound.TimePosition, "Sound.yaml: Play starts at the last value set by a script");
            Frames(1, 1d);
            Assert.AreEqual(2.5d, sound.TimePosition, 1e-9);

            sound.Play();
            Assert.AreEqual(1.5d, sound.TimePosition, "playing a playing sound restarts it from that value");

            sound.Stop();
            Assert.AreEqual(0d, sound.TimePosition, "Stop sets TimePosition to 0");
            sound.Play();
            Assert.AreEqual(0d, sound.TimePosition, "after Stop, Play starts at 0");
            sound.SetTimePosition(99d);
            Assert.AreEqual(4d, sound.TimePosition, "a loaded sound holds the playhead to its length");
        }

        [Test]
        public void MVP17_Sound_PauseKeepsThePlayhead_ResumeContinues_AndEachFiresOnlyWhenItChangesSomething()
        {
            RbxSound sound = NewSound("music", _registry.WorldRoot);
            Frames(1);
            List<string> events = new();
            WatchAll(sound, events);
            sound.Pause();
            sound.Play();
            Frames(1, 1d);
            sound.Pause();
            sound.Pause();
            Frames(1, 1d);
            Assert.AreEqual(1d, sound.TimePosition, 1e-9, "a paused sound does not advance");
            sound.Resume();
            sound.Resume();
            Frames(1, 0.5d);
            Frames(1, 0d);

            Assert.AreEqual(1.5d, sound.TimePosition, 1e-9);
            CollectionAssert.AreEqual(new[] { "Played:music", "Paused:music", "Resumed:music" }, events,
                "Pause on a stopped or paused sound and Resume on a playing one fire nothing");
        }

        [Test]
        public void MVP17_Sound_ThePlayingProperty_ContinuesAndHoldsWithoutPlayedOrPaused()
        {
            RbxSound sound = NewSound("music", _registry.WorldRoot);
            Frames(1);
            List<string> events = new();
            WatchAll(sound, events);
            sound.SetTimePosition(1d);
            sound.SetPlaying(true);
            Frames(1, 1d);
            sound.SetPlaying(false);
            Frames(1, 1d);
            Frames(1, 0d);

            Assert.AreEqual(2d, sound.TimePosition, 1e-9,
                "Playing = true continues from TimePosition and false keeps it (Sound.yaml)");
            CollectionAssert.IsEmpty(events, "only the methods fire Played and Paused");
        }

        [Test]
        public void MVP17_Playback_ASoundOutsideTheGame_NeitherLoadsNorAdvances_NegativeTwin()
        {
            RbxSound loose = NewSound("beep", null);
            RbxSound placed = NewSound("beep", _registry.WorldRoot);
            loose.Play();
            Frames(3, 0.1d);

            Assert.IsFalse(loose.IsLoaded, "an unparented sound is not in the experience, so it does not load");
            Assert.IsTrue(loose.Playing);
            Assert.AreEqual(0d, loose.TimePosition);
            Assert.IsTrue(placed.IsLoaded, "twin: a sound in the workspace loads");

            loose.Parent = _service;
            Frames(2, 0.1d);
            Assert.IsTrue(loose.IsLoaded, "under SoundService it is in the game and loads");
            Assert.Greater(loose.TimePosition, 0d);
        }

        [Test]
        public void MVP17_Sound_ChangingTheId_Unloads_AndTheNextFrameLoadsTheNewClip()
        {
            RbxSound sound = NewSound("beep", _registry.WorldRoot);
            Frames(1);
            Assert.AreEqual(1d, sound.TimeLength);

            sound.SetSoundId("music");
            Assert.IsFalse(sound.IsLoaded);
            Assert.AreEqual(0d, sound.TimeLength, "TimeLength is 0 while the sound is not loaded (Sound.yaml)");
            Frames(1);
            Assert.AreEqual(4d, sound.TimeLength);
        }

        [Test]
        public void MVP17_Sound_Clone_CopiesTheProperties_AndTheCopyLoadsOnItsOwn()
        {
            RbxSoundGroup group = (RbxSoundGroup)_registry.Create("SoundGroup");
            group.Parent = _service;
            RbxSound sound = NewSound("beep", _registry.WorldRoot);
            sound.SetVolume(0.3d);
            sound.SetLooped(true);
            sound.SetSoundGroup(group);
            Frames(1);

            RbxSound copy = (RbxSound)sound.Clone();
            Assert.AreEqual("beep", copy.SoundId);
            Assert.AreEqual(0.3f, copy.Volume);
            Assert.IsTrue(copy.Looped);
            Assert.AreSame(group, copy.SoundGroup, "a group outside the cloned tree is kept");
            Assert.IsFalse(copy.IsLoaded, "the copy loads its own clip");
            copy.Parent = _registry.WorldRoot;
            Frames(1);
            Assert.IsTrue(copy.IsLoaded);
        }

        // ---- roll-off ----------------------------------------------------------------------------

        [Test]
        public void MVP17_RollOff_EveryModeFollowsRollOffModeYaml()
        {
            Assert.AreEqual(1f, RbxSoundRollOff.Gain(RbxRollOffMode.Inverse, 10f, 100f, 5f), "full volume inside min");
            Assert.AreEqual(0.5f, RbxSoundRollOff.Gain(RbxRollOffMode.Inverse, 10f, 100f, 20f), 1e-6f, "min / distance");
            Assert.AreEqual(0.5f, RbxSoundRollOff.Gain(RbxRollOffMode.Linear, 10f, 110f, 60f), 1e-6f);
            Assert.AreEqual(0.25f, RbxSoundRollOff.Gain(RbxRollOffMode.LinearSquare, 10f, 110f, 60f), 1e-6f);
            Assert.AreEqual(Math.Min(10f / 60f, 0.25f),
                RbxSoundRollOff.Gain(RbxRollOffMode.InverseTapered, 10f, 110f, 60f), 1e-6f, "the lesser of the two");
            Assert.AreEqual(0.5f, RbxSoundRollOff.Gain(RbxRollOffMode.InverseTapered, 10f, 110f, 20f), 1e-6f,
                "near min the inverse curve is the lesser");
            foreach (RbxRollOffMode mode in new[]
                     {
                         RbxRollOffMode.Inverse, RbxRollOffMode.Linear, RbxRollOffMode.LinearSquare,
                         RbxRollOffMode.InverseTapered
                     })
            {
                Assert.AreEqual(0f, RbxSoundRollOff.Gain(mode, 10f, 100f, 100f), mode + ": silent at max");
                Assert.AreEqual(0f, RbxSoundRollOff.Gain(mode, 10f, 100f, 500f), mode + ": silent past max");
            }
        }

        // ---- the reconciler ----------------------------------------------------------------------

        private RbxSoundReconciler Reconciler(FakeBackend backend, Dictionary<InstanceId, RbxVector3> positions,
            int budget = 32, List<string> notices = null)
        {
            return new RbxSoundReconciler(_registry, _game, backend,
                id => positions.TryGetValue(id, out RbxVector3 position) ? position : new RbxVector3(0f, 0f, 0f),
                new RbxSoundBudget("test", budget), notices == null ? null : (Action<string>)notices.Add);
        }

        [Test]
        public void MVP17_Reconciler_AGlobalSoundPlaysFlat_APartsSoundIsHeardFromThePart_WithTheGroupsVolume()
        {
            FakeBackend backend = new();
            Dictionary<InstanceId, RbxVector3> positions = new();
            using RbxSoundReconciler reconciler = Reconciler(backend, positions);
            RbxSoundGroup group = (RbxSoundGroup)_registry.Create("SoundGroup");
            group.SetVolume(0.5d);
            group.Parent = _service;
            RbxSound global = NewSound("music", _registry.WorldRoot);
            global.SetSoundGroup(group);
            RbxInstance part = NewPart();
            positions[part.Id] = new RbxVector3(20f, 0f, 0f);
            RbxSound positional = NewSound("music", part);
            Frames(1);
            global.Play();
            positional.Play();

            reconciler.Tick(new RbxVector3(0f, 0f, 0f));

            RbxSoundRenderState flat = backend.Playing[RbxSoundVoiceKey.ForSound(global.Id)];
            RbxSoundRenderState heard = backend.Playing[RbxSoundVoiceKey.ForSound(positional.Id)];
            Assert.IsFalse(flat.Positional);
            Assert.AreEqual(0.25f, flat.Volume, 1e-6f, "0.5 times the group's 0.5 (SoundGroup.yaml)");
            Assert.AreEqual("music-clip", flat.Clip);
            Assert.IsTrue(heard.Positional);
            Assert.AreEqual(20f, heard.PositionStuds.X);
            Assert.AreEqual(0.25f, heard.Volume, 1e-6f, "0.5 times Inverse 10 / 20");

            positions[part.Id] = new RbxVector3(20000f, 0f, 0f);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.IsFalse(backend.Holds(RbxSoundVoiceKey.ForSound(positional.Id)),
                "past RollOffMaxDistance the voice is released");
            Assert.IsTrue(backend.Holds(RbxSoundVoiceKey.ForSound(global.Id)), "twin: the global sound plays on");
        }

        [Test]
        public void MVP17_Reconciler_PausedStoppedAndDestroyedSoundsAreReleased_DestroyFiresNoStopped()
        {
            FakeBackend backend = new();
            using RbxSoundReconciler reconciler = Reconciler(backend, new Dictionary<InstanceId, RbxVector3>());
            RbxSound paused = NewSound("music", _registry.WorldRoot);
            RbxSound destroyed = NewSound("music", _registry.WorldRoot);
            Frames(1);
            List<string> events = new();
            WatchAll(destroyed, events);
            paused.Play();
            destroyed.Play();
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(2, backend.Playing.Count);

            paused.Pause();
            destroyed.Destroy();
            Frames(1);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));

            Assert.AreEqual(0, backend.Playing.Count);
            CollectionAssert.AreEqual(new[] { "Played:music" }, events,
                "Sound.yaml: destroying a playing sound does not fire Stopped");

            paused.Resume();
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.IsTrue(backend.Holds(RbxSoundVoiceKey.ForSound(paused.Id)), "twin: a resumed sound plays again");
        }

        [Test]
        public void MVP17_Reconciler_OverBudget_TheLoudestAreHeard_AndTheBudgetIsReportedOnce()
        {
            FakeBackend backend = new();
            List<string> notices = new();
            using RbxSoundReconciler reconciler = Reconciler(backend, new Dictionary<InstanceId, RbxVector3>(), 2,
                notices);
            List<RbxSound> sounds = new();
            for (int index = 0; index < 4; index++)
            {
                RbxSound sound = NewSound("music", _registry.WorldRoot);
                sound.SetVolume(0.1d * (index + 1));
                sounds.Add(sound);
            }

            Frames(1);
            foreach (RbxSound sound in sounds)
            {
                sound.Play();
            }

            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));

            Assert.AreEqual(2, reconciler.PlayingVoices);
            Assert.AreEqual(2, reconciler.CulledByBudget);
            Assert.IsTrue(backend.Holds(RbxSoundVoiceKey.ForSound(sounds[3].Id)));
            Assert.IsTrue(backend.Holds(RbxSoundVoiceKey.ForSound(sounds[2].Id)));
            Assert.IsFalse(backend.Holds(RbxSoundVoiceKey.ForSound(sounds[0].Id)));
            Assert.AreEqual(1, notices.Count, "reported once while the budget is exceeded");
            Assert.IsTrue(sounds[0].Playing, "a silenced sound's timeline keeps running");
        }

        // ---- local copies and PlayOnRemove ---------------------------------------------------------

        [Test]
        public void MVP17_PlayLocalSound_PlaysACopyOnce_EvenFromALoopedSound_AndTheOriginalStaysStill()
        {
            FakeBackend backend = new();
            using RbxSoundReconciler reconciler = Reconciler(backend, new Dictionary<InstanceId, RbxVector3>());
            RbxSound sound = NewSound("beep", _service);
            sound.SetLooped(true);
            Frames(1);
            List<string> events = new();
            WatchAll(sound, events);

            RbxSoundLocalVoice copy = _service.PlayLocalSound(sound);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.IsNotNull(copy);
            Assert.IsTrue(backend.Holds(RbxSoundVoiceKey.ForLocalCopy(copy.Id)));
            Assert.IsFalse(backend.Playing[RbxSoundVoiceKey.ForLocalCopy(copy.Id)].Looped,
                "SoundService.yaml: Looped does not carry over to the copy");

            Frames(1, 1.5d);
            reconciler.Tick(new RbxVector3(0f, 0f, 0f));
            Assert.AreEqual(0, _service.LocalVoices.Count, "the copy ended after one play");
            Assert.AreEqual(0, backend.Playing.Count);
            Assert.IsFalse(sound.Playing);
            CollectionAssert.IsEmpty(events, "the original fires nothing");

            RbxSound unknown = NewSound("nope", _service);
            Assert.IsNull(_service.PlayLocalSound(unknown), "twin: a sound with no clip plays no copy");
        }

        [Test]
        public void MVP17_PlayOnRemove_DestroyingOrUnparentingALoadedSoundStartsACopy_NegativeTwin()
        {
            RbxSound destroyed = NewSound("beep", _registry.WorldRoot);
            destroyed.SetPlayOnRemove(true);
            RbxSound unparented = NewSound("beep", _registry.WorldRoot);
            unparented.SetPlayOnRemove(true);
            RbxSound plain = NewSound("beep", _registry.WorldRoot);
            Frames(1);

            destroyed.Destroy();
            unparented.Parent = null;
            plain.Destroy();
            Frames(1, 0d);

            Assert.AreEqual(2, _service.LocalVoices.Count,
                "Sound.yaml: Destroy and Parent = nil both play a PlayOnRemove sound; the plain one plays nothing");
        }

        [Test]
        public void MVP17_Playback_ARestoredWorldsSoundsAreTracked_AndDisposeStopsTheClock()
        {
            RbxSound sound = NewSound("beep", _registry.WorldRoot);
            using RbxSoundPlayback second = new(_registry, _game, null);
            Assert.AreEqual(1, second.TrackedSounds, "sounds that existed before the clock are tracked");

            _playback.Dispose();
            sound.Play();
            Frames(3, 0.1d);
            Assert.IsFalse(sound.IsLoaded, "a disposed clock neither loads nor advances");
            second.Step(0.1d);
            Assert.IsTrue(sound.IsLoaded, "a host may step a clock that has no scheduler itself");
        }
    }
}
