using System.Collections.Generic;
using System.IO;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.LuaBindings
{
    /// <summary>
    /// MVP17's Sound, SoundGroup and SoundService through the real mod runtime with headless seams: the members
    /// and their Lua types, the playback rules a script sees (Loaded, Played, Ended, the Wait idioms), the read-only
    /// and type errors, the loud stubs, <c>PlayLocalSound</c>, and the two CoreAI decisions (DEV-133
    /// <c>GetSoundIds</c>, DEV-134 the id rule and its one-line note). Every behaviour has a negative twin.
    /// </summary>
    [TestFixture]
    public sealed class Mvp17SoundLuaEditModeTests
    {
        private const double FrameSeconds = 1d / 60d;

        private SynchronizationContext _savedContext;

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
            }
        }

        private sealed class SilentLogger : IGameLogger
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

        private sealed class World
        {
            public LuaCsRbxApiBindings Roblox;
            public LuaCsModStack Stack;
            public MemoryStore Store;
            public RbxSoundLibrary Library;
            public List<string> Log;

            public void Frames(int count, double seconds = FrameSeconds)
            {
                for (int index = 0; index < count; index++)
                {
                    Roblox.Scheduler.Advance(seconds);
                }
            }

            public void Load(string modId, string code)
            {
                Stack.Runtime.LoadMod(modId, code);
            }

            public string Get(string modId, string key)
            {
                return Store.Get(modId, key);
            }
        }

        private static World BuildWorld()
        {
            World world = new()
            {
                Store = new MemoryStore(),
                Log = new List<string>(),
                Library = new RbxSoundLibrary()
            };
            world.Library.Register("beep", 0.5d, "beep-clip");
            world.Library.Register("theme", 3d, "theme-clip");
            world.Roblox = new LuaCsRbxApiBindings(log: world.Log.Add, cameraRig: new InMemoryCameraRig());
            world.Roblox.Sounds.Service.AttachLibrary(world.Library);
            world.Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = world.Store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = world.Roblox
            });
            return world;
        }

        [Test]
        public void MVP17_Sound_ReadsTheDumpDefaultsAndTypes_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local s = Instance.new('Sound')
                local SoundService = game:GetService('SoundService')
                store_set('service', tostring(SoundService == game.SoundService) .. '|' .. SoundService.ClassName)
                store_set('values', table.concat({ s.Name, '[' .. s.SoundId .. ']', tostring(s.Volume),
                    tostring(s.Looped), tostring(s.PlaybackSpeed), tostring(s.TimePosition), tostring(s.TimeLength),
                    tostring(s.Playing), tostring(s.IsPlaying), tostring(s.IsPaused), tostring(s.IsLoaded),
                    tostring(s.RollOffMinDistance), tostring(s.RollOffMaxDistance), tostring(s.RollOffMode),
                    tostring(s.PlayOnRemove), tostring(s.SoundGroup) }, ','))
                store_set('events', typeof(s.Played) .. typeof(s.Ended) .. typeof(s.Loaded) .. typeof(s.DidLoop))
                store_set('group', tostring(Instance.new('SoundGroup').Volume))
                store_set('isa', tostring(s:IsA('Instance')) .. tostring(s:IsA('BasePart')))");

            Assert.AreEqual("true|SoundService", world.Get("m", "service"));
            Assert.AreEqual(
                "Sound,[],0.5,false,1,0,0,false,false,true,false,10,10000,Enum.RollOffMode.Inverse,false,nil",
                world.Get("m", "values"));
            Assert.AreEqual("RBXScriptSignalRBXScriptSignalRBXScriptSignalRBXScriptSignal", world.Get("m", "events"));
            Assert.AreEqual("0.5", world.Get("m", "group"));
            Assert.AreEqual("truefalse", world.Get("m", "isa"));
        }

        [Test]
        public void MVP17_Sound_TheCommonIdioms_LoadedWaitPlayEndedWait_RunInOrder_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local s = Instance.new('Sound')
                s.SoundId = 'beep'
                s.Parent = workspace
                local log = {}
                s.Played:Connect(function(id) table.insert(log, 'Played:' .. id) end)
                s.Ended:Connect(function(id) table.insert(log, 'Ended:' .. id) end)
                if not s.IsLoaded then s.Loaded:Wait() end
                table.insert(log, 'loaded ' .. tostring(s.TimeLength))
                s:Play()
                s.Ended:Wait()
                table.insert(log, 'after ' .. tostring(s.Playing) .. ' ' .. tostring(s.TimePosition))
                store_set('log', table.concat(log, '|'))");

            world.Frames(2);
            Assert.AreEqual("", world.Get("m", "log"), "half a second of clip has not played yet");
            world.Frames(40);

            Assert.AreEqual("loaded 0.5|Played:beep|Ended:beep|after false 0", world.Get("m", "log"));
        }

        [Test]
        public void MVP17_Sound_StopPauseResumeAndLoop_FireTheirEventsWithTheId_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local s = Instance.new('Sound')
                s.SoundId = 'beep'
                s.Looped = true
                s.Parent = workspace
                local log = {}
                for _, name in ipairs({ 'Played', 'Paused', 'Resumed', 'Stopped', 'Ended', 'DidLoop' }) do
                    s[name]:Connect(function(id, n) table.insert(log, name .. ':' .. id .. (n and (',' .. n) or ''))
                        store_set('log', table.concat(log, '|')) end)
                end
                s.Loaded:Wait()
                s:Play()
                task.wait(0.6)
                s:Pause()
                s:Resume()
                s:Stop()
                store_set('after', tostring(s.Playing) .. ' ' .. tostring(s.TimePosition))");

            world.Frames(60);

            Assert.AreEqual("Played:beep|DidLoop:beep,1|Paused:beep|Resumed:beep|Stopped:beep", world.Get("m", "log"),
                "a looped sound never fires Ended");
            Assert.AreEqual("false 0", world.Get("m", "after"));
        }

        [Test]
        public void MVP17_Sound_ReadOnlyMembersAndWrongTypes_RaiseLoudErrors_NegativeTwin()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local s = Instance.new('Sound')
                local function try(f) local ok, err = pcall(f) return tostring(ok) .. '|' .. tostring(err) end
                store_set('length', try(function() s.TimeLength = 3 end))
                store_set('loaded', try(function() s.IsLoaded = true end))
                store_set('volume', try(function() s.Volume = 'loud' end))
                store_set('mode', try(function() s.RollOffMode = Enum.PartType.Ball end))
                store_set('group', try(function() s.SoundGroup = Instance.new('Folder') end))
                store_set('nan', try(function() s.PlaybackSpeed = 0/0 end))
                store_set('ok', try(function() s.Volume = 3; s.RollOffMode = 'Linear' end))
                store_set('after', tostring(s.Volume) .. ' ' .. tostring(s.RollOffMode))");

            StringAssert.Contains("Property is read only", world.Get("m", "length"));
            StringAssert.Contains("Property is read only", world.Get("m", "loaded"));
            StringAssert.Contains("Sound.Volume expects a number", world.Get("m", "volume"));
            StringAssert.Contains("Enum.RollOffMode", world.Get("m", "mode"));
            StringAssert.Contains("SoundGroup expects a SoundGroup", world.Get("m", "group"));
            StringAssert.Contains("finite number", world.Get("m", "nan"));
            Assert.AreEqual("true|nil", world.Get("m", "ok"), "twin: a number and an item name are accepted");
            Assert.AreEqual("3 Enum.RollOffMode.Linear", world.Get("m", "after"));
        }

        [Test]
        public void MVP17_Sound_UnimplementedMembersAreLoudStubs_AndDeprecatedOnesNameTheirReplacement()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local s = Instance.new('Sound')
                local SoundService = game:GetService('SoundService')
                local function try(f) local ok, err = pcall(f) return tostring(ok) .. '|' .. tostring(err) end
                store_set('region', try(function() return s.PlaybackRegion end))
                store_set('pitch', try(function() s.Pitch = 2 end))
                store_set('lower', try(function() s:play() end))
                store_set('listener', try(function() return SoundService.ListenerType end))
                store_set('bogus', try(function() return s.Loudness end))");

            StringAssert.Contains("NOT_IMPLEMENTED", world.Get("m", "region"));
            StringAssert.Contains("use PlaybackSpeed", world.Get("m", "pitch"));
            StringAssert.Contains("use Play", world.Get("m", "lower"));
            StringAssert.Contains("the listener is the camera", world.Get("m", "listener"));
            StringAssert.Contains("is not a valid member of Sound", world.Get("m", "bogus"),
                "twin: a name Roblox does not have is still the plain unknown-member error");
        }

        [Test]
        public void DEV133_GetSoundIds_ListsTheHostsKeysInOrder_FromLua()
        {
            World world = BuildWorld();
            world.Library.Register("Alarm", 1d);
            world.Load("m", @"
                local ids = game:GetService('SoundService'):GetSoundIds()
                store_set('ids', typeof(ids) .. ' ' .. table.concat(ids, ','))");

            Assert.AreEqual("table Alarm,beep,theme", world.Get("m", "ids"));

            world.Roblox.Sounds.Service.AttachLibrary(null);
            world.Load("empty", @"
                store_set('n', tostring(#game:GetService('SoundService'):GetSoundIds()))");
            Assert.AreEqual("0", world.Get("empty", "n"), "twin: a host that ships no sounds lists none");
        }

        [Test]
        public void DEV134_AnIdThatLoadsNothing_IsLoggedOnceWithTheFix_AndAKnownIdNever()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local a = Instance.new('Sound')
                a.SoundId = 'rbxassetid://9120386436'
                a.Parent = workspace
                local b = Instance.new('Sound')
                b.SoundId = 'rbxassetid://9120386436'
                b.Parent = workspace
                b:Play()
                local c = Instance.new('Sound')
                c.SoundId = 'beep'
                c.Parent = workspace");
            world.Frames(3);

            List<string> notes = world.Log.FindAll(line => line.Contains("Sound.SoundId"));
            Assert.AreEqual(1, notes.Count, "one line per id: " + string.Join(" / ", notes));
            StringAssert.Contains("Roblox asset id", notes[0]);
            StringAssert.Contains("SoundService:GetSoundIds()", notes[0]);
            StringAssert.Contains("RbxWorldHost.Sounds.Register", notes[0]);
            StringAssert.Contains("Resources folder", notes[0]);
        }

        [Test]
        public void MVP17_PlayingASoundOutsideTheGame_IsNotedOncePerMod_NegativeTwin()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local loose = Instance.new('Sound')
                loose.SoundId = 'beep'
                loose:Play()
                loose:Play()
                local placed = Instance.new('Sound')
                placed.SoundId = 'beep'
                placed.Parent = workspace
                placed:Play()");

            Assert.AreEqual(1, world.Log.FindAll(line => line.Contains("not inside the game plays nothing")).Count);
        }

        [Test]
        public void MVP17_PlayLocalSound_PlaysACopy_AndRefusesANonSound_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local SoundService = game:GetService('SoundService')
                local s = Instance.new('Sound')
                s.SoundId = 'theme'
                s.Parent = SoundService
                s.Loaded:Wait()
                SoundService:PlayLocalSound(s)
                store_set('original', tostring(s.Playing))
                store_set('bad', tostring(select(2, pcall(function() SoundService:PlayLocalSound(workspace) end))))");
            world.Frames(3);

            Assert.AreEqual("false", world.Get("m", "original"), "the original does not play");
            Assert.AreEqual(1, world.Roblox.Sounds.Service.LocalVoices.Count);
            StringAssert.Contains("PlayLocalSound expects a Sound at argument 1", world.Get("m", "bad"));
        }

        [Test]
        public void MVP17_SoundOnTouchFixture_PlaysOncePerTouchWhileNotPlaying_AndEnds()
        {
            string script = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets", "CoreAIMods",
                "Tests", "EditMode", "RbxApi", "Acceptance", "Mvp17SoundOnTouch.lua"));
            World world = BuildWorld();
            world.Library.Register("chime", 0.4d, "chime-clip");
            world.Load("touch", script);
            world.Frames(1);
            Assert.AreEqual("false|beep,chime,theme", world.Get("touch", "ready"),
                "the id loads on the frame after it is set, as Roblox loads asynchronously");

            RbxBasePart pad = (RbxBasePart)world.Roblox.Registry.WorldRoot.FindFirstChild("ChimePad");
            RbxInstance ball = world.Roblox.Registry.Create("Part");
            ball.Parent = world.Roblox.Registry.WorldRoot;
            pad.FireTouched(ball);
            world.Frames(2);
            pad.FireTouched(ball);
            world.Frames(2);
            Assert.AreEqual("chime x1", world.Get("touch", "played"), "a touch while it plays does not restart it");

            world.Frames(30);
            Assert.AreEqual("chime", world.Get("touch", "ended"));
            pad.FireTouched(ball);
            world.Frames(2);
            Assert.AreEqual("chime x2", world.Get("touch", "played"), "twin: after the end a touch plays it again");
        }

        [Test]
        public void MVP17_TweenService_FadesASoundsVolume_AndRefusesABoolean_NegativeTwin()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local TweenService = game:GetService('TweenService')
                local s = Instance.new('Sound')
                s.Volume = 1
                s.Parent = workspace
                local fade = TweenService:Create(s, TweenInfo.new(0.25), { Volume = 0, PlaybackSpeed = 2 })
                fade.Completed:Connect(function() store_set('faded', tostring(s.Volume) .. ' ' .. tostring(s.PlaybackSpeed)) end)
                fade:Play()
                store_set('looped', tostring(select(2, pcall(function()
                    TweenService:Create(s, TweenInfo.new(1), { Looped = true })
                end))))");
            world.Frames(30);

            Assert.AreEqual("0 2", world.Get("m", "faded"), "the fade-out idiom: tween Volume to 0");
            StringAssert.Contains("NOT_IMPLEMENTED", world.Get("m", "looped"),
                "twin: a boolean is not interpolated, as for every class");
        }

        [Test]
        public void MVP17_GetPropertyChangedSignal_FiresForLoadingAndForAScriptsPlayheadMove_NotForTheClock()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local s = Instance.new('Sound')
                local counts = { TimeLength = 0, IsLoaded = 0, TimePosition = 0 }
                for name in pairs(counts) do
                    s:GetPropertyChangedSignal(name):Connect(function()
                        counts[name] = counts[name] + 1
                        store_set(name, tostring(counts[name]))
                    end)
                end
                s.SoundId = 'theme'
                s.Parent = workspace
                s.Loaded:Wait()
                s:Play()
                task.wait(0.5)
                s.TimePosition = 2");
            world.Frames(60);

            Assert.AreEqual("1", world.Get("m", "TimeLength"));
            Assert.AreEqual("1", world.Get("m", "IsLoaded"));
            Assert.AreEqual("1", world.Get("m", "TimePosition"),
                "the playing clock fires nothing; the script's own write fires once");
        }
    }
}
