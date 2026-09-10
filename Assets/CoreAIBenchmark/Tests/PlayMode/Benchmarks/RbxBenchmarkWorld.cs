#if COREAI_LUA
#if COREAI_LLM && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Infrastructure.Logging;
using CoreAI.Logging;
using CoreAI.Messaging;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Scripting;
using Microsoft.Extensions.AI;
using Newtonsoft.Json.Linq;
using UnityEngine;
using VContainer;

namespace CoreAI.Tests.PlayMode.Benchmarks
{
    /// <summary>
    /// The Roblox-API world a scenario builds in: the production Lua mod stack bound to a live
    /// <see cref="RbxWorldHost"/>, so <c>Instance.new("Part")</c> with <c>Material</c> and
    /// <c>Shape</c> materializes as real GameObjects the hero screenshot can frame.
    /// <para>
    /// WHY: the native <c>world_command</c> primitives have colors but no materials and only four
    /// prefab kinds. Materials and the five <c>Enum.PartType</c> shapes exist on the Rbx surface, which
    /// is also the API CoreAI actually ships, so the visual scenario grades what the product does.
    /// </para>
    /// </summary>
    public sealed class RbxBenchmarkWorld : IDisposable
    {
        /// <summary>Names the scenario grades on; parts outside this prefix are ignored.</summary>
        public const string BuildPrefix = "Castle";

        private const string MeasureLua =
            "local parts, mats, shapes, names, out = 0, {}, {}, {}, 0\n" +
            "for _, inst in ipairs(workspace:GetDescendants()) do\n" +
            "  if inst:IsA('Part') and string.sub(inst.Name, 1, PREFIXLEN) == 'PREFIX' then\n" +
            "    parts = parts + 1\n" +
            "    names[inst.Name] = true\n" +
            "    mats[(string.gsub(tostring(inst.Material), '^Enum%.%w+%.', ''))] = true\n" +
            "    shapes[(string.gsub(tostring(inst.Shape), '^Enum%.%w+%.', ''))] = true\n" +
            "    local p = inst.Position\n" +
            "    if math.abs(p.X) > 64 or math.abs(p.Z) > 64 or p.Y < -8 or p.Y > 96 then\n" +
            "      out = out + 1\n" +
            "    end\n" +
            "  end\n" +
            "end\n" +
            "local m, s, n = {}, {}, 0\n" +
            "for k in pairs(mats) do table.insert(m, k) end\n" +
            "for k in pairs(shapes) do table.insert(s, k) end\n" +
            "for _ in pairs(names) do n = n + 1 end\n" +
            "table.sort(m); table.sort(s)\n" +
            "return parts .. '|' .. n .. '|' .. out .. '|' .. table.concat(m, ',') .. '|' .. table.concat(s, ',')";

        /// <summary>What the model actually built, as read back through the same Lua surface it used.</summary>
        public readonly struct Snapshot
        {
            public Snapshot(int parts, int distinctNames, int outOfBounds,
                IReadOnlyList<string> materials, IReadOnlyList<string> shapes)
            {
                Parts = parts;
                DistinctNames = distinctNames;
                OutOfBounds = outOfBounds;
                Materials = materials;
                Shapes = shapes;
            }

            public int Parts { get; }
            public int DistinctNames { get; }
            public int OutOfBounds { get; }
            public IReadOnlyList<string> Materials { get; }
            public IReadOnlyList<string> Shapes { get; }
        }

        private readonly IObjectResolver _container;
        private readonly GameObject _hostObject;
        private readonly LuaCsModStack _stack;
        private readonly ActorContext _actor;

        /// <param name="settings">CoreAI settings the tool logs against.</param>
        /// <param name="parent">Visual root the world is parented under so the hero shot frames it.</param>
        /// <param name="liveResultNote">
        /// Optional live note (the scenario clock) stamped into every <c>execute_lua</c> result as
        /// <c>TimeLeft</c>; null leaves results untouched.
        /// </param>
        public RbxBenchmarkWorld(ICoreAISettings settings, Transform parent, Func<string> liveResultNote = null)
        {
            ContainerBuilder builder = new();
            builder.Register<DefaultGameLogSettings>(Lifetime.Singleton).As<IGameLogSettings>();
            builder.RegisterCore();
            _container = builder.Build();
            _actor = _container.Resolve<IActorIdentityProvider>()
                .GetActorContext(BuiltInAgentRoleIds.Programmer);

            _hostObject = new GameObject("RbxBenchmarkWorld");
            if (parent != null)
            {
                // WHY: the hero screenshot frames everything under the visual executor's root, so the
                // Rbx world has to live there or the capture photographs an empty scene.
                _hostObject.transform.SetParent(parent, false);
            }

            RbxWorldHost host = _hostObject.AddComponent<RbxWorldHost>();
            host.Initialize();

            _stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = GameLoggerUnscopedFallback.Instance,
                CommandSink = new NullSink(),
                Log = Log.Instance,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All & ~LuaCapabilities.Full,
                RbxApi = new LuaCsRbxApiBindings(
                    registry: host.Registry,
                    game: host.Game,
                    partSink: host.Binder,
                    cameraRig: host.CameraRig,
                    pickSource: host.PickSource)
            });

            // WHY no rate limit: LuaLlmTool's default limiter (20 execute_lua calls per 60 s) guards the
            // envelope pipeline against runaway generation loops. The free build tells the model to keep
            // calling until the scene is complete under a 1000-roundtrip cap and a 10-minute clock; with
            // the limiter, a model building in small sections has its 21st call of a minute rejected as
            // a FAILED tool call (mandatory clean_tools plus the per-failure penalty) for doing exactly
            // what the prompt asked. The roundtrip cap remains the runaway valve.
            LuaLlmTool inner = new(_stack.ToolExecutor, settings, Log.Instance,
                new LuaGenerationRateLimiter(maxPerWindow: 0));
            Tool = new TimedExecuteLuaTool(inner, liveResultNote);
        }

        /// <summary>
        /// The production <c>execute_lua</c> tool over this world, with the scenario clock stamped into
        /// every result as <c>TimeLeft</c> when one was supplied.
        /// </summary>
        public IAIFunctionLlmTool Tool { get; }

        /// <summary>
        /// Reads the built scene back through the same Lua surface the model used.
        /// <para>
        /// WHY: grading is synchronous. A plain measurement chunk completes inside the executor without
        /// suspending (no mutation gate is wired here), so the task is already finished; a chunk that
        /// somehow does suspend yields an empty snapshot rather than blocking the test thread.
        /// </para>
        /// </summary>
        public Snapshot Measure()
        {
            string lua = MeasureLua
                .Replace("PREFIXLEN", BuildPrefix.Length.ToString())
                .Replace("PREFIX", BuildPrefix);
            Task<LuaTool.LuaResult> task =
                _stack.ToolExecutor.ExecuteAsync(lua, _actor, CancellationToken.None);
            if (!task.IsCompleted)
            {
                return new Snapshot(0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
            }

            LuaTool.LuaResult result = task.Result;
            if (!result.Success || string.IsNullOrWhiteSpace(result.Output))
            {
                return new Snapshot(0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
            }

            string[] fields = result.Output.Split('|');
            if (fields.Length != 5
                || !int.TryParse(fields[0], out int parts)
                || !int.TryParse(fields[1], out int names)
                || !int.TryParse(fields[2], out int outOfBounds))
            {
                return new Snapshot(0, 0, 0, Array.Empty<string>(), Array.Empty<string>());
            }

            return new Snapshot(parts, names, outOfBounds, Split(fields[3]), Split(fields[4]));
        }

        private static string[] Split(string csv)
        {
            return string.IsNullOrWhiteSpace(csv)
                ? Array.Empty<string>()
                : csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public void Dispose()
        {
            if (_hostObject != null)
            {
                UnityEngine.Object.DestroyImmediate(_hostObject);
            }

            _container?.Dispose();
        }

        private sealed class NullSink : IAiGameCommandSink
        {
            public void Publish(ApplyAiGameCommand command)
            {
            }
        }

        /// <summary>
        /// <c>execute_lua</c> with the scenario clock stamped into each result.
        /// <para>
        /// WHY: the free-build goal promises the model a countdown after every call so it can pace a
        /// 10-minute build. The <c>world_command</c> tool appends that note itself; <c>LuaLlmTool</c>
        /// has no such seam, so on the Roblox-API build the promise was simply false and the model
        /// built blind until the deadline cut it off. The note rides inside the JSON result
        /// (<c>{"Success":true,"TimeLeft":"~412s left …"}</c>) rather than after it, so the tool
        /// policy's success detection still parses the result as JSON.
        /// </para>
        /// </summary>
        internal sealed class TimedExecuteLuaTool : IAIFunctionLlmTool
        {
            private const string TimeLeftProperty = "TimeLeft";

            private readonly LuaLlmTool _inner;
            private readonly Func<string> _liveResultNote;

            public TimedExecuteLuaTool(LuaLlmTool inner, Func<string> liveResultNote)
            {
                _inner = inner ?? throw new ArgumentNullException(nameof(inner));
                _liveResultNote = liveResultNote;
            }

            public string Name => _inner.Name;

            public string Description => _inner.Description;

            public string ParametersSchema => _inner.ParametersSchema;

            public bool AllowDuplicates => _inner.AllowDuplicates;

            public int? ToolTimeoutMsOverride => ((ILlmTool)_inner).ToolTimeoutMsOverride;

            public bool IsMutating => ((ILlmTool)_inner).IsMutating;

            public AIFunction CreateAIFunction()
            {
                return WithTimeLeft(_inner.CreateAIFunction(), _liveResultNote);
            }

            /// <summary>
            /// Wraps <paramref name="function"/> so every result carries the live note as
            /// <c>TimeLeft</c>; a null note returns the function unchanged.
            /// </summary>
            internal static AIFunction WithTimeLeft(AIFunction function, Func<string> liveResultNote)
            {
                if (function == null)
                {
                    throw new ArgumentNullException(nameof(function));
                }

                return liveResultNote == null
                    ? function
                    : new NoteStampingFunction(function, liveResultNote);
            }

            /// <summary>
            /// Adds <c>TimeLeft</c> to a JSON-object result; leaves anything else untouched.
            /// <para>
            /// WHY: <c>AIFunctionFactory.Create</c> over a <c>Task&lt;string&gt;</c> delegate — the
            /// shape <c>LuaTool.CreateAIFunction</c> builds — hands the delegate's string back as a
            /// <see cref="System.Text.Json.JsonElement"/> of kind String, not as a <c>string</c>
            /// (verified against the bundled Microsoft.Extensions.AI.Abstractions 10.9.0). A
            /// string-only check let every result through unstamped while the prompt promised the
            /// note was there. The stamped result goes back as a <c>string</c>: the tool policy reads
            /// both shapes through <c>ToString()</c>, so its success detection sees the same JSON text
            /// as before plus one top-level property it does not look at.
            /// </para>
            /// </summary>
            internal static object Stamp(object result, Func<string> liveResultNote)
            {
                string note;
                try
                {
                    note = liveResultNote?.Invoke();
                }
                catch
                {
                    return result;
                }

                if (string.IsNullOrWhiteSpace(note) || !TryReadText(result, out string json))
                {
                    return result;
                }

                try
                {
                    JObject payload = JObject.Parse(json);
                    payload[TimeLeftProperty] = note.Trim();
                    return payload.ToString(Newtonsoft.Json.Formatting.None);
                }
                catch (Newtonsoft.Json.JsonException)
                {
                    return result;
                }
            }

            private static bool TryReadText(object result, out string text)
            {
                switch (result)
                {
                    case string s:
                        text = s;
                        return true;
                    case System.Text.Json.JsonElement element
                        when element.ValueKind == System.Text.Json.JsonValueKind.String:
                        text = element.GetString();
                        return true;
                    default:
                        text = null;
                        return false;
                }
            }

            private sealed class NoteStampingFunction : DelegatingAIFunction
            {
                private readonly Func<string> _liveResultNote;

                public NoteStampingFunction(AIFunction innerFunction, Func<string> liveResultNote)
                    : base(innerFunction)
                {
                    _liveResultNote = liveResultNote;
                }

                protected override async ValueTask<object> InvokeCoreAsync(
                    AIFunctionArguments arguments,
                    CancellationToken cancellationToken)
                {
                    object result = await InnerFunction.InvokeAsync(arguments, cancellationToken);
                    return Stamp(result, _liveResultNote);
                }
            }
        }
    }
}
#endif
#endif
