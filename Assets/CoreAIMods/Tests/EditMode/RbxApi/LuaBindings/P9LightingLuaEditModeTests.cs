using System;
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
    /// P9's day-cycle slice and PointLight through the real mod runtime with headless seams: the
    /// <c>Lighting</c> members, signals and loud stubs, the sink a Lua write reaches, <c>PointLight</c>,
    /// tweens of Lighting, Light and <c>Camera.FieldOfView</c>, the "Ray" name in argument errors, and
    /// the retagged stub phases. Every behaviour has a negative twin.
    /// </summary>
    [TestFixture]
    public sealed class P9LightingLuaEditModeTests
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

        private sealed class RecordingSink : IRbxLightingSink
        {
            public readonly List<RbxLightingState> States = new();

            public void Apply(in RbxLightingState state)
            {
                States.Add(state);
            }
        }

        /// <summary>A host rig from before the lens members: it keeps the interface defaults.</summary>
        private sealed class LenslessRig : IRbxCameraRig
        {
            private RbxCFrame _cframe = RbxCFrame.Identity;

            public RbxCFrame GetCFrame()
            {
                return _cframe;
            }

            public void SetCFrame(in RbxCFrame cframe)
            {
                _cframe = cframe;
            }

            public bool Follow(InstanceId id)
            {
                return true;
            }

            public void StopFollowing()
            {
            }
        }

        private sealed class World
        {
            public InMemoryCameraRig Rig;
            public LuaCsRbxApiBindings Roblox;
            public LuaCsModStack Stack;
            public MemoryStore Store;

            public void Frames(int count)
            {
                for (int index = 0; index < count; index++)
                {
                    Roblox.Scheduler.Advance(FrameSeconds);
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

        private static World BuildWorld(IRbxCameraRig rig = null)
        {
            World world = new()
            {
                Rig = rig == null ? new InMemoryCameraRig() : rig as InMemoryCameraRig,
                Store = new MemoryStore()
            };
            world.Roblox = new LuaCsRbxApiBindings(cameraRig: rig ?? world.Rig);
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

        // ---- Lighting ------------------------------------------------------------------------------

        [Test]
        public void P9_Lighting_ReadsTheDumpDefaults_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local L = game:GetService('Lighting')
                store_set('clock', tostring(L.ClockTime))
                store_set('time', L.TimeOfDay)
                store_set('brightness', tostring(L.Brightness))
                store_set('latitude', string.format('%.4f', L.GeographicLatitude))
                store_set('ambient', tostring(L.Ambient))
                store_set('outdoor', tostring(L.OutdoorAmbient))
                store_set('minutes', tostring(L:GetMinutesAfterMidnight()))
                store_set('sun', typeof(L:GetSunDirection()))
                store_set('changed', typeof(L.LightingChanged))");

            Assert.AreEqual("14", world.Get("m", "clock"));
            Assert.AreEqual("14:00:00", world.Get("m", "time"));
            Assert.AreEqual("1", world.Get("m", "brightness"));
            Assert.AreEqual("41.7333", world.Get("m", "latitude"));
            Assert.AreEqual("0.5, 0.5, 0.5", world.Get("m", "ambient"));
            Assert.AreEqual("0.5, 0.5, 0.5", world.Get("m", "outdoor"));
            Assert.AreEqual("840", world.Get("m", "minutes"));
            Assert.AreEqual("Vector3", world.Get("m", "sun"));
            Assert.AreEqual("RBXScriptSignal", world.Get("m", "changed"));
        }

        [Test]
        public void P9_Lighting_ClockTimeTimeOfDayAndMinutesStayInSync_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local L = game:GetService('Lighting')
                L.TimeOfDay = '06:30:00'
                store_set('a', tostring(L.ClockTime))
                L.ClockTime = 21.5
                store_set('b', L.TimeOfDay)
                L:SetMinutesAfterMidnight(13 * 60 + 15)
                store_set('c', L.TimeOfDay .. '|' .. tostring(L.ClockTime))
                L.ClockTime = 26
                store_set('d', L.TimeOfDay)
                L.ClockTime = 0
                store_set('night', tostring(L:GetSunDirection().Y < 0))
                L.ClockTime = 12
                store_set('day', tostring(L:GetSunDirection().Y > 0.5))");

            Assert.AreEqual("6.5", world.Get("m", "a"));
            Assert.AreEqual("21:30:00", world.Get("m", "b"));
            Assert.AreEqual("13:15:00|13.25", world.Get("m", "c"));
            Assert.AreEqual("02:00:00", world.Get("m", "d"), "a ClockTime past midnight wraps");
            Assert.AreEqual("true", world.Get("m", "night"));
            Assert.AreEqual("true", world.Get("m", "day"));
        }

        [Test]
        public void P9_Lighting_ARealChangeFiresChangedPropertySignalsAndLightingChanged_AnEqualWriteNothing()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local L = game:GetService('Lighting')
                local changed, clock, time, lighting, sky = {}, 0, 0, 0, 'unset'
                L.Changed:Connect(function(name) table.insert(changed, name); store_set('changed', table.concat(changed, ',')) end)
                L:GetPropertyChangedSignal('ClockTime'):Connect(function() clock = clock + 1; store_set('clock', tostring(clock)) end)
                L:GetPropertyChangedSignal('TimeOfDay'):Connect(function() time = time + 1; store_set('time', tostring(time)) end)
                L.LightingChanged:Connect(function(skyChanged) lighting = lighting + 1; sky = tostring(skyChanged)
                    store_set('lighting', tostring(lighting) .. '|' .. sky) end)
                L.ClockTime = 18
                L.ClockTime = 18
                L.Brightness = 1
                L.Brightness = 2");
            world.Frames(2);

            Assert.AreEqual("ClockTime,TimeOfDay,Brightness", world.Get("m", "changed"));
            Assert.AreEqual("1", world.Get("m", "clock"), "the equal second write fired nothing");
            Assert.AreEqual("1", world.Get("m", "time"));
            Assert.AreEqual("2|false", world.Get("m", "lighting"),
                "one LightingChanged per real change, skyChanged false");
        }

        [Test]
        public void P9_Lighting_ALuaWriteReachesTheAttachedSink_AndAnUndrivenWorldNever()
        {
            World world = BuildWorld();
            RecordingSink sink = new();
            world.Roblox.Lighting.AttachSink(sink);
            world.Load("read", @"
                local L = game:GetService('Lighting')
                store_set('x', tostring(L.ClockTime))");
            Assert.AreEqual(0, sink.States.Count, "reading drives nothing");

            world.Load("day", @"
                local L = game:GetService('Lighting')
                L.ClockTime = 0
                L.OutdoorAmbient = Color3.fromRGB(70, 70, 90)");

            Assert.AreEqual(2, sink.States.Count);
            Assert.AreEqual(0f, sink.States[1].ClockTime);
            Assert.Less(sink.States[1].SunDirection.Y, 0f);
            Assert.AreEqual(70f / 255f, sink.States[1].OutdoorAmbient.R, 1e-6f);
        }

        [Test]
        public void P9_Lighting_RefusesWrongTypesAndMalformedTimes_NegativeTwin()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local L = game:GetService('Lighting')
                local function try(f) local ok, err = pcall(f) return tostring(ok) .. '|' .. tostring(err) end
                store_set('type', try(function() L.ClockTime = {} end))
                store_set('time', try(function() L.TimeOfDay = 'noon' end))
                store_set('nan', try(function() L.Brightness = 0/0 end))
                store_set('color', try(function() L.Ambient = 5 end))
                store_set('minutes', try(function() L:SetMinutesAfterMidnight('late') end))
                store_set('after', tostring(L.ClockTime))");

            StringAssert.StartsWith("false|", world.Get("m", "type"));
            StringAssert.Contains("Lighting.ClockTime expects a number", world.Get("m", "type"));
            StringAssert.Contains("BAD_ARGUMENT", world.Get("m", "time"));
            StringAssert.Contains("Lighting.TimeOfDay expects a 24-hour time", world.Get("m", "time"));
            StringAssert.Contains("needs a finite number", world.Get("m", "nan"));
            StringAssert.Contains("Lighting.Ambient expects a Color3", world.Get("m", "color"));
            StringAssert.Contains("Lighting:SetMinutesAfterMidnight expects a number at argument 1",
                world.Get("m", "minutes"));
            Assert.AreEqual("14", world.Get("m", "after"), "refused writes leave the clock alone");
        }

        [Test]
        public void P9_Lighting_TheRestOfP9IsALoudStub_AndSecurityMembersAreUnsupported()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local L = game:GetService('Lighting')
                local function try(f) local ok, err = pcall(f) return tostring(ok) .. '|' .. tostring(err) end
                store_set('fog', try(function() return L.FogEnd end))
                store_set('fogWrite', try(function() L.FogEnd = 50 end))
                store_set('moon', try(function() return L:GetMoonPhase() end))
                store_set('tech', try(function() return L.Technology end))
                store_set('sky', try(function() return Instance.new('Sky') end))
                store_set('spot', try(function() return Instance.new('SpotLight') end))");

            StringAssert.Contains("NOT_IMPLEMENTED: Lighting.FogEnd is planned for P9.", world.Get("m", "fog"));
            StringAssert.Contains("is planned for P9", world.Get("m", "fogWrite"));
            StringAssert.Contains("Lighting:GetMoonPhase is planned for P9", world.Get("m", "moon"));
            StringAssert.Contains("deliberately unsupported", world.Get("m", "tech"));
            StringAssert.Contains("Instance.new(\"Sky\") is planned for P9", world.Get("m", "sky"));
            StringAssert.Contains("Instance.new(\"SpotLight\") is planned for P9", world.Get("m", "spot"));
        }

        [Test]
        public void P9_Lighting_TweensClockTime()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local L = game:GetService('Lighting')
                L.ClockTime = 6
                local tween = game:GetService('TweenService'):Create(L, TweenInfo.new(0.5, Enum.EasingStyle.Linear), {ClockTime = 18, OutdoorAmbient = Color3.new(0, 0, 0)})
                tween:Play()");
            world.Frames(45);

            Assert.AreEqual(18f, world.Roblox.Lighting.ClockTime, 1e-3f);
            Assert.AreEqual("18:00:00", world.Roblox.Lighting.TimeOfDay);
            Assert.AreEqual(0f, world.Roblox.Lighting.OutdoorAmbient.R, 1e-6f);
        }

        [Test]
        public void P9_Lighting_EnvironmentScales_StartAtOne_ClampToZeroOne_AndTween()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local L = game:GetService('Lighting')
                store_set('defaults', tostring(L.EnvironmentDiffuseScale) .. '|' .. tostring(L.EnvironmentSpecularScale))
                L.EnvironmentDiffuseScale = 2
                L.EnvironmentSpecularScale = -1
                store_set('clamped', tostring(L.EnvironmentDiffuseScale) .. '|' .. tostring(L.EnvironmentSpecularScale))
                local changed = {}
                L.Changed:Connect(function(name) table.insert(changed, name); store_set('changed', table.concat(changed, ',')) end)
                game:GetService('TweenService'):Create(L, TweenInfo.new(0.5, Enum.EasingStyle.Linear),
                    {EnvironmentDiffuseScale = 0, EnvironmentSpecularScale = 1}):Play()");
            world.Frames(45);

            Assert.AreEqual("1|1", world.Get("m", "defaults"), "a new world's Lighting starts both at 1");
            Assert.AreEqual("1|0", world.Get("m", "clamped"), "writes clamp into 0..1");
            Assert.AreEqual(0f, world.Roblox.Lighting.EnvironmentDiffuseScale, 1e-4f);
            Assert.AreEqual(1f, world.Roblox.Lighting.EnvironmentSpecularScale, 1e-4f);
            StringAssert.Contains("EnvironmentDiffuseScale", world.Get("m", "changed"));
            StringAssert.Contains("EnvironmentSpecularScale", world.Get("m", "changed"));
        }

        // ---- PointLight --------------------------------------------------------------------------

        [Test]
        public void P9_PointLight_IsARealInstance_WithLightMembers()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local light = Instance.new('PointLight')
                store_set('defaults', table.concat({ tostring(light.Brightness), tostring(light.Color),
                    tostring(light.Enabled), tostring(light.Shadows), tostring(light.Range) }, '|'))
                light.Brightness = 3
                light.Color = Color3.fromRGB(255, 160, 60)
                light.Range = 100
                light.Shadows = true
                light.Parent = part
                store_set('set', table.concat({ tostring(light.Brightness), tostring(light.Range),
                    tostring(light.Shadows), light.Parent.ClassName }, '|'))
                store_set('isa', tostring(light:IsA('Light')) .. '|' .. tostring(light:IsA('PointLight')))
                local copy = light:Clone()
                store_set('clone', tostring(copy.Range) .. '|' .. tostring(copy.Brightness))
                local ok, err = pcall(function() light.Enabled = 'yes' end)
                store_set('bad', tostring(ok) .. '|' .. tostring(err))
                local abstractOk = pcall(function() return Instance.new('Light') end)
                store_set('abstract', tostring(abstractOk))");

            Assert.AreEqual("1|1, 1, 1|true|false|8", world.Get("m", "defaults"));
            Assert.AreEqual("3|60|true|Part", world.Get("m", "set"), "Range holds to 60 studs");
            Assert.AreEqual("true|true", world.Get("m", "isa"));
            Assert.AreEqual("60|3", world.Get("m", "clone"));
            StringAssert.StartsWith("false|", world.Get("m", "bad"));
            StringAssert.Contains("PointLight.Enabled expects a boolean", world.Get("m", "bad"));
            Assert.AreEqual("false", world.Get("m", "abstract"), "Light itself is not creatable");
        }

        [Test]
        public void P9_PointLight_FiresPropertySignals_AndTweensBrightness()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local part = Instance.new('Part')
                part.Parent = workspace
                local light = Instance.new('PointLight')
                light.Parent = part
                local hits = 0
                light:GetPropertyChangedSignal('Range'):Connect(function() hits = hits + 1; store_set('hits', tostring(hits)) end)
                light.Range = 12
                light.Range = 12
                game:GetService('TweenService'):Create(light, TweenInfo.new(0.25, Enum.EasingStyle.Linear), {Brightness = 5}):Play()");
            world.Frames(30);

            Assert.AreEqual("1", world.Get("m", "hits"), "an equal write fires nothing");
            RbxPointLight light = null;
            foreach (RbxInstance instance in world.Roblox.Registry.GetLiveInstances())
            {
                light ??= instance as RbxPointLight;
            }

            Assert.IsNotNull(light);
            Assert.AreEqual(5f, light.Brightness, 1e-4f);
        }

        // ---- item 4 fixes ------------------------------------------------------------------------

        [Test]
        public void P9_CameraFieldOfView_Tweens_ThroughTheRigLens()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local cam = workspace.CurrentCamera
                local changes = 0
                cam:GetPropertyChangedSignal('FieldOfView'):Connect(function() changes = changes + 1; store_set('changes', tostring(changes)) end)
                game:GetService('TweenService'):Create(cam, TweenInfo.new(0.25, Enum.EasingStyle.Linear), {FieldOfView = 30}):Play()");
            world.Frames(30);

            Assert.AreEqual(30d, world.Rig.FieldOfView, 1e-6);
            Assert.Greater(int.Parse(world.Get("m", "changes")), 1, "every tweened step that moved the lens notifies");
        }

        [Test]
        public void P9_CameraFieldOfView_TweenOnALenslessRig_IsALoudStub_NegativeTwin()
        {
            World world = BuildWorld(new LenslessRig());
            world.Load("m", @"
                local ok, err = pcall(function()
                    return game:GetService('TweenService'):Create(workspace.CurrentCamera, TweenInfo.new(1), {FieldOfView = 30})
                end)
                store_set('r', tostring(ok) .. '|' .. tostring(err))");

            StringAssert.StartsWith("false|", world.Get("m", "r"));
            StringAssert.Contains("NOT_IMPLEMENTED", world.Get("m", "r"));
            StringAssert.Contains("Camera.FieldOfView", world.Get("m", "r"));
        }

        [Test]
        public void P9_ArgumentErrors_NameARayAsRay()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local ok, err = pcall(function() return Vector3.new(1, 2, 3):Dot(Ray.new()) end)
                store_set('r', tostring(err))");

            StringAssert.Contains("got Ray at argument 1", world.Get("m", "r"));
            StringAssert.DoesNotContain("RbxRay", world.Get("m", "r"));
        }

        [Test]
        public void P9_RetaggedStubPhases_NameTheCurrentPlanIds()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local function try(f) local ok, err = pcall(f) return tostring(err) end
                store_set('sound', try(function() return Instance.new('Sound') end))
                store_set('gui', try(function() return Instance.new('UIGradient') end))
                store_set('mesh', try(function() return Instance.new('MeshPart') end))
                store_set('store', try(function() return game:GetService('DataStoreService'):GetDataStore('x') end))
                store_set('actions', try(function() return game:GetService('ContextActionService'):BindAction() end))
                store_set('close', try(function() game:BindToClose(function() end) end))");

            StringAssert.Contains("is planned for MVP17", world.Get("m", "sound"));
            StringAssert.Contains("is planned for MVP15", world.Get("m", "gui"));
            StringAssert.Contains("is planned for P10", world.Get("m", "mesh"));
            StringAssert.Contains("is planned for MVP16", world.Get("m", "store"));
            StringAssert.Contains("is planned for MVP7", world.Get("m", "actions"));
            StringAssert.Contains("is planned for MVP16", world.Get("m", "close"));
            foreach (string key in new[] { "sound", "gui", "store", "actions", "close" })
            {
                StringAssert.DoesNotContain("MVP14", world.Get("m", key));
                StringAssert.DoesNotContain("MVP9.", world.Get("m", key));
            }
        }
    }
}
