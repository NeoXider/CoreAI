using System.Collections.Generic;
using System.IO;
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
    /// MVP17's minimal ParticleEmitter through the real mod runtime with headless seams: the NumberRange,
    /// NumberSequence and ColorSequence constructors, members, equality, tostring and errors; the emitter's members and
    /// their Lua types; <c>Emit</c>/<c>Clear</c> reaching a fake backend through the reconciler; the loud stubs; and the
    /// emit-burst fixture (<c>Acceptance/Mvp17EmitBurst.lua</c>). Every behaviour has a negative twin.
    /// </summary>
    [TestFixture]
    public sealed class Mvp17ParticleLuaEditModeTests
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

        private sealed class CountingBackend : IRbxParticleBackend
        {
            public readonly Dictionary<InstanceId, RbxParticleRenderState> Drawn = new();
            public readonly List<string> Calls = new();

            public void Apply(InstanceId emitterId, InstanceId hostPartId, in RbxParticleRenderState state)
            {
                Drawn[emitterId] = state;
            }

            public void Emit(InstanceId emitterId, int count)
            {
                Calls.Add("emit " + count);
            }

            public void Clear(InstanceId emitterId)
            {
                Calls.Add("clear");
            }

            public void Release(InstanceId emitterId)
            {
                Drawn.Remove(emitterId);
            }

            public bool Holds(InstanceId emitterId)
            {
                return Drawn.ContainsKey(emitterId);
            }
        }

        private sealed class World
        {
            public LuaCsRbxApiBindings Roblox;
            public LuaCsModStack Stack;
            public MemoryStore Store;
            public CountingBackend Backend;
            public RbxParticleReconciler Particles;

            /// <summary>One frame: the scheduler, then the drawing, as the host's Update and LateUpdate.</summary>
            public void Frames(int count, double seconds = FrameSeconds)
            {
                for (int index = 0; index < count; index++)
                {
                    Roblox.Scheduler.Advance(seconds);
                    Particles.Tick(new RbxVector3(0f, 0f, 0f));
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
            World world = new() { Store = new MemoryStore(), Backend = new CountingBackend() };
            world.Roblox = new LuaCsRbxApiBindings(cameraRig: new InMemoryCameraRig());
            world.Particles = new RbxParticleReconciler(world.Roblox.Registry, world.Backend,
                _ => new RbxVector3(0f, 0f, 0f), _ => new RbxVector3(2f, 1f, 2f), RbxParticleBudget.Desktop);
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
        public void MVP17_RangeAndSequenceDatatypes_ConstructCompareAndPrint_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local r = NumberRange.new(1, 3)
                store_set('range', typeof(r) .. ' ' .. r.Min .. ' ' .. r.Max .. ' [' .. tostring(NumberRange.new(2)) .. ']')
                store_set('requal', tostring(NumberRange.new(1, 3) == r))
                local n = NumberSequence.new({ NumberSequenceKeypoint.new(0, 1), NumberSequenceKeypoint.new(0.5, 2, 0.5),
                    NumberSequenceKeypoint.new(1, 0) })
                store_set('nseq', typeof(n) .. ' ' .. #n.Keypoints .. ' ' .. n.Keypoints[2].Value .. ' '
                    .. n.Keypoints[2].Envelope .. ' ' .. typeof(n.Keypoints[1]))
                store_set('ntext', tostring(NumberSequence.new(1, 0)))
                local c = ColorSequence.new(Color3.new(1, 0, 0), Color3.new(0, 0, 1))
                store_set('cseq', typeof(c) .. ' ' .. tostring(c.Keypoints[2].Value) .. ' ' .. c.Keypoints[2].Time)
                store_set('cequal', tostring(ColorSequence.new(Color3.new(1, 0, 0), Color3.new(0, 0, 1)) == c))
                store_set('ctext', tostring(ColorSequence.new(Color3.new(1, 1, 1))))");

            Assert.AreEqual("NumberRange 1 3 [2 2 ]", world.Get("m", "range"));
            Assert.AreEqual("true", world.Get("m", "requal"));
            Assert.AreEqual("NumberSequence 3 2 0.5 NumberSequenceKeypoint", world.Get("m", "nseq"));
            Assert.AreEqual("0 1 0 1 0 0 ", world.Get("m", "ntext"));
            Assert.AreEqual("ColorSequence 0, 0, 1 1", world.Get("m", "cseq"));
            Assert.AreEqual("true", world.Get("m", "cequal"));
            Assert.AreEqual("0 1 1 1 0 1 1 1 1 0 ", world.Get("m", "ctext"));
        }

        [Test]
        public void MVP17_RangeAndSequenceDatatypes_RefuseBadInput_WithTheFix_NegativeTwin()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local function try(f) local ok, err = pcall(f) return tostring(ok) .. '|' .. tostring(err) end
                store_set('range', try(function() return NumberRange.new(3, 1) end))
                store_set('one', try(function() return NumberSequence.new({ NumberSequenceKeypoint.new(0, 1) }) end))
                store_set('start', try(function() return ColorSequence.new({ ColorSequenceKeypoint.new(0.2, Color3.new()),
                    ColorSequenceKeypoint.new(1, Color3.new()) }) end))
                store_set('time', try(function() return NumberSequenceKeypoint.new(2, 1) end))
                store_set('element', try(function() return NumberSequence.new({ 1, 2 }) end))
                store_set('immutable', try(function() local r = NumberRange.new(1) r.Min = 0 end))
                store_set('ok', try(function() return NumberSequence.new(0.5) end))");

            StringAssert.Contains("minimum 3 is above the maximum 1", world.Get("m", "range"));
            StringAssert.Contains("at least two keypoints", world.Get("m", "one"));
            StringAssert.Contains("first keypoint's time must be 0", world.Get("m", "start"));
            StringAssert.Contains("between 0 and 1", world.Get("m", "time"));
            StringAssert.Contains("array of NumberSequenceKeypoint", world.Get("m", "element"));
            StringAssert.Contains("immutable", world.Get("m", "immutable"));
            StringAssert.StartsWith("true|", world.Get("m", "ok"), "twin: a single number is a flat sequence");
        }

        [Test]
        public void MVP17_ParticleEmitter_ReadsTheDumpDefaultsAndTypes_FromLua()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local e = Instance.new('ParticleEmitter')
                store_set('values', table.concat({ e.Name, tostring(e.Enabled), tostring(e.Rate), tostring(e.Lifetime),
                    tostring(e.Speed), tostring(e.Size), tostring(e.Transparency), tostring(e.Color), e.Texture,
                    tostring(e.SpreadAngle), tostring(e.Acceleration), tostring(e.EmissionDirection) }, '|'))
                store_set('types', typeof(e.Lifetime) .. typeof(e.Color) .. typeof(e.Size) .. typeof(e.SpreadAngle))");

            Assert.AreEqual("ParticleEmitter|true|20|5 10 |5 5 |0 1 0 1 1 0 |0 0 0 1 0 0 |0 1 1 1 0 1 1 1 1 0 |"
                            + "rbxasset://textures/particles/sparkles_main.dds|0, 0|0, 0, 0|Enum.NormalId.Top",
                world.Get("m", "values"));
            Assert.AreEqual("NumberRangeColorSequenceNumberSequenceVector2", world.Get("m", "types"));
        }

        [Test]
        public void MVP17_ParticleEmitter_WrongTypesAreRefused_WithTheWrappingFix_NegativeTwin()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local e = Instance.new('ParticleEmitter')
                local function try(f) local ok, err = pcall(f) return tostring(ok) .. '|' .. tostring(err) end
                store_set('color', try(function() e.Color = Color3.new(1, 0, 0) end))
                store_set('size', try(function() e.Size = 2 end))
                store_set('life', try(function() e.Lifetime = 3 end))
                store_set('dir', try(function() e.EmissionDirection = Enum.Material.Plastic end))
                store_set('stub', try(function() return e.LightEmission end))
                store_set('ok', try(function() e.Color = ColorSequence.new(Color3.new(1, 0, 0)); e.EmissionDirection = 'Bottom' end))
                store_set('after', tostring(e.Color.Keypoints[1].Value) .. ' ' .. tostring(e.EmissionDirection))");

            StringAssert.Contains("ColorSequence.new(", world.Get("m", "color"), "the fix shows the wrapping");
            StringAssert.Contains("expects a NumberSequence", world.Get("m", "size"));
            StringAssert.Contains("expects a NumberRange", world.Get("m", "life"));
            StringAssert.Contains("Enum.NormalId", world.Get("m", "dir"));
            StringAssert.Contains("NOT_IMPLEMENTED", world.Get("m", "stub"));
            Assert.AreEqual("true|nil", world.Get("m", "ok"));
            Assert.AreEqual("1, 0, 0 Enum.NormalId.Bottom", world.Get("m", "after"));
        }

        [Test]
        public void MVP17_EmitAndClear_ReachTheDrawingOnTheNextFrame_OnlyFromAPart()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local part = Instance.new('Part')
                part.Anchored = true
                part.Parent = workspace
                local e = Instance.new('ParticleEmitter')
                e.Rate = 0
                e.Parent = part
                e:Emit(12)
                e:Emit()
                local loose = Instance.new('ParticleEmitter')
                loose:Emit(99)");
            world.Frames(1);
            CollectionAssert.AreEqual(new[] { "emit 28" }, world.Backend.Calls,
                "12 plus the default 16 from the part's emitter; the loose one draws nothing");

            world.Load("clear", @"
                local part = workspace:FindFirstChildOfClass('Part')
                local e = part:FindFirstChildOfClass('ParticleEmitter')
                e:Clear()
                store_set('bad', tostring(select(2, pcall(function() e:Emit(0/0) end))))");
            world.Frames(1);
            CollectionAssert.AreEqual(new[] { "emit 28", "clear" }, world.Backend.Calls);
            StringAssert.Contains("finite particle count", world.Get("clear", "bad"));
        }

        [Test]
        public void MVP17_TweenService_TweensAnEmittersRate_ButNotASequence_NegativeTwin()
        {
            World world = BuildWorld();
            world.Load("m", @"
                local TweenService = game:GetService('TweenService')
                local e = Instance.new('ParticleEmitter')
                e.Parent = workspace
                local t = TweenService:Create(e, TweenInfo.new(0.2), { Rate = 100 })
                t.Completed:Connect(function() store_set('rate', tostring(e.Rate)) end)
                t:Play()
                store_set('size', tostring(select(2, pcall(function()
                    TweenService:Create(e, TweenInfo.new(1), { Size = NumberSequence.new(3) })
                end))))");
            world.Frames(30);

            Assert.AreEqual("100", world.Get("m", "rate"));
            StringAssert.Contains("goal for 'Size' expects a number", world.Get("m", "size"),
                "a sequence is not a tweenable goal");
        }

        [Test]
        public void MVP17_EmitBurstFixture_EmitsTwoBurstsFromItsPart()
        {
            string script = File.ReadAllText(Path.Combine(Directory.GetCurrentDirectory(), "Assets", "CoreAIMods",
                "Tests", "EditMode", "RbxApi", "Acceptance", "Mvp17EmitBurst.lua"));
            World world = BuildWorld();
            world.Load("burst", script);
            world.Frames(1);

            Assert.AreEqual("ParticleEmitter|0|0.8 1.2 |ColorSequence|NumberSequence", world.Get("burst", "emitter"));
            CollectionAssert.AreEqual(new[] { "emit 40" }, world.Backend.Calls, "the first burst on the next frame");
            Assert.AreEqual(1, world.Backend.Drawn.Count);

            world.Frames(40);
            CollectionAssert.AreEqual(new[] { "emit 40", "emit 40" }, world.Backend.Calls, "the second after 0.5 s");
            Assert.AreEqual("2", world.Get("burst", "bursts"));
            foreach (RbxParticleRenderState state in world.Backend.Drawn.Values)
            {
                Assert.AreEqual(0f, state.Rate, "the fixture's emitter emits only in bursts");
                Assert.AreEqual(360f, state.SpreadAngle.X);
            }
        }
    }
}
