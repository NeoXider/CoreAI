using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// A reference scene's Rbx world and mod stack, composed the way the production installer composes
    /// them around a scene host (<c>CoreAiModsInstaller</c>: <see cref="LuaCsRbxApiBindings"/> over the
    /// host's registry, game and part sink, then <see cref="LuaCsModRuntimeFactory.Create"/>). Engine-free:
    /// the caller supplies the registry and part sink, so the same code drives an in-memory world on plain
    /// .NET and the GameObject world in a Unity player.
    /// </summary>
    /// <remarks>
    /// WHY mods load as a restricted local actor and the runtime is never ticked here: issuing the host
    /// actor context is reserved to the composition root (<c>CoreServicesInstaller</c>), and this harness
    /// must work from an assembly that is not one of its friends. The mods of the reference scenes use
    /// Roblox idioms only (RunService, task), which the scheduler frame drives; the legacy
    /// <c>hooks_every</c> timers and <c>events_emit</c> queue that <c>LuaCsModRuntime.Tick</c> serves are
    /// not part of any reference scene and are measured by the micro-benchmarks instead.
    /// </remarks>
    public sealed class PerfModWorld : IDisposable
    {
        /// <summary>Parts per row of the static grid.</summary>
        public const int StaticGridRowLength = 128;

        /// <summary>Name prefix of the static grid's Parts.</summary>
        public const string StaticPartNamePrefix = "PerfStatic";

        /// <summary>Actor id the reference mods are loaded as.</summary>
        public const string ActorId = "perf-local";

        private readonly MemoryModStore _store = new();
        private bool _disposed;

        /// <summary>Composes the bindings and the mod stack over an existing world.</summary>
        /// <param name="registry">The world's instance registry (its binder decides what backs a Part).</param>
        /// <param name="game">The DataModel bootstrapped on <paramref name="registry"/>.</param>
        /// <param name="partSink">Where Part properties live (the GameObject binder in a scene).</param>
        /// <param name="counters">Receives the Lua counters; never null.</param>
        /// <param name="cameraRig">The scene camera seam, null for a headless world.</param>
        /// <param name="inputSource">The scene input seam, null for a headless world.</param>
        /// <param name="pickSource">The scene click-pick seam, null for a headless world.</param>
        public PerfModWorld(InstanceRegistry registry, RbxDataModel game, IPartPropertySink partSink,
            PerfRuntimeCounters counters, IRbxCameraRig cameraRig = null, IInputSource inputSource = null,
            IClickPickSource pickSource = null)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            Game = game ?? throw new ArgumentNullException(nameof(game));
            PartSink = partSink ?? throw new ArgumentNullException(nameof(partSink));
            Counters = counters ?? throw new ArgumentNullException(nameof(counters));
            Bindings = new LuaCsRbxApiBindings(
                registry, game,
                partSink: partSink,
                cameraRig: cameraRig,
                inputSource: inputSource,
                pickSource: pickSource,
                observability: counters,
                log: RecordWarning);
            Stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new NullGameLogger(),
                ModStore = _store,
                RbxApi = Bindings,
                Observability = counters,
                AutoPersistMods = false,
                // WHY raised to the ceiling: the default per-actor quota (2,048) also binds the host, and a
                // reference scene is a host-built world; the documented host option is the way to allow it.
                MaxRegisteredInstancesPerActor = LuaCsModRuntime.EmergencyMaxRegisteredInstances
            });
            Actor = new LocalActorIdentityProvider(
                    ActorId, "perf-session", registry.WorldId, ActorGrantSet.None, AgentMemoryScope.Empty)
                .GetActorContext(BuiltInAgentRoleIds.Programmer);
        }

        /// <summary>The world's instance registry.</summary>
        public InstanceRegistry Registry { get; }

        /// <summary>The world's DataModel.</summary>
        public RbxDataModel Game { get; }

        /// <summary>Where Part properties live.</summary>
        public IPartPropertySink PartSink { get; }

        /// <summary>The Lua counters of this world.</summary>
        public PerfRuntimeCounters Counters { get; }

        /// <summary>The Roblox API surface the mods run against.</summary>
        public LuaCsRbxApiBindings Bindings { get; }

        /// <summary>The production mod stack.</summary>
        public LuaCsModStack Stack { get; }

        /// <summary>The actor the reference mods are loaded as.</summary>
        public ActorContext Actor { get; }

        /// <summary>Diagnostics the Rbx surface reported while the scene ran (the first <see cref="MaxWarnings"/>).</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>Warnings kept; later ones are counted in <see cref="DroppedWarnings"/> so a chatty mod cannot grow the heap it is measured on.</summary>
        public const int MaxWarnings = 32;

        /// <summary>Warnings reported after <see cref="MaxWarnings"/> were kept.</summary>
        public int DroppedWarnings { get; private set; }

        /// <summary>
        /// Builds <paramref name="count"/> anchored Parts in a flat grid directly under workspace. The
        /// properties are stored before each Part is parented, so a GameObject binder materializes each Part
        /// once with its final pose instead of materializing a default Part and then moving it.
        /// </summary>
        /// <remarks>
        /// WHY no folder: the 16,384-Part scene is exactly the desktop emergency ceiling of registered
        /// instances (<see cref="LuaCsModRuntime.EmergencyMaxRegisteredInstances"/>), which a host can lower
        /// but not raise, so not one more charged instance fits in that world.
        /// </remarks>
        public void BuildStaticGrid(int count)
        {
            if (count <= 0)
            {
                return;
            }

            for (int index = 0; index < count; index++)
            {
                RbxInstance part = Registry.Create("Part");
                part.Name = StaticPartNamePrefix + index;
                PartProperties properties = PartProperties.CreateDefault();
                properties.Anchored = true;
                properties.Size = new RbxVector3(2f, 1f, 2f);
                properties.Position = new RbxVector3(
                    index % StaticGridRowLength * 3f,
                    0f,
                    index / StaticGridRowLength * 3f);
                PartSink.SetPartProperties(part.Id, in properties);
                part.Parent = Registry.WorldRoot;
            }
        }

        /// <summary>Loads the Heartbeat mover mods of <paramref name="scenario"/>, recording each outcome.</summary>
        public void LoadMovers(PerfScenario scenario, PerfScenarioResult result)
        {
            if (scenario == null || scenario.MoverCount == 0)
            {
                return;
            }

            int remaining = scenario.MoverCount;
            for (int handler = 0; handler < scenario.MoverHandlerCount; handler++)
            {
                int handlersLeft = scenario.MoverHandlerCount - handler;
                int count = (remaining + handlersLeft - 1) / handlersLeft;
                remaining -= count;
                LoadMod(PerfLuaSources.MoverModIdPrefix + handler, PerfLuaSources.BuildMoverMod(handler, count),
                    result);
            }
        }

        /// <summary>Loads every (id, source) pair, recording which loaded and which failed and why.</summary>
        public void LoadMods(IReadOnlyList<KeyValuePair<string, string>> mods, PerfScenarioResult result)
        {
            if (mods == null)
            {
                return;
            }

            for (int index = 0; index < mods.Count; index++)
            {
                LoadMod(mods[index].Key, mods[index].Value, result);
            }
        }

        /// <summary>Loads one mod; a failure is recorded on <paramref name="result"/> instead of thrown.</summary>
        public bool LoadMod(string id, string source, PerfScenarioResult result)
        {
            try
            {
                Stack.Runtime.LoadMod(Actor, id, source, LuaCapabilities.All, false);
                result?.LoadedMods.Add(id);
                return true;
            }
            catch (Exception exception)
            {
                result?.FailedMods.Add(id + ": " + FirstLine(exception.Message));
                return false;
            }
        }

        /// <summary>Runs one scheduler frame: every RunService phase, task resumes and the signal drains.</summary>
        public void AdvanceFrame(double deltaSeconds)
        {
            Bindings.Scheduler.Advance(deltaSeconds);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Bindings.Dispose();
        }

        private void RecordWarning(string message)
        {
            if (Warnings.Count < MaxWarnings)
            {
                Warnings.Add(message);
                return;
            }

            DroppedWarnings++;
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return "";
            }

            int newline = text.IndexOf('\n');
            return newline < 0 ? text : text.Substring(0, newline);
        }

        private sealed class MemoryModStore : ILuaModStore
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
                List<(string ModId, string Key)> removed = new();
                foreach ((string ModId, string Key) key in _values.Keys)
                {
                    if (string.Equals(key.ModId, modId, StringComparison.Ordinal))
                    {
                        removed.Add(key);
                    }
                }

                for (int index = 0; index < removed.Count; index++)
                {
                    _values.Remove(removed[index]);
                }
            }
        }
    }
}
