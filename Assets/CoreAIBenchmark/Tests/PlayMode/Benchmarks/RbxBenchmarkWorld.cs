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

        public RbxBenchmarkWorld(ICoreAISettings settings, Transform parent)
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

            Tool = new LuaLlmTool(_stack.ToolExecutor, settings, Log.Instance,
                new LuaGenerationRateLimiter());
        }

        /// <summary>The production <c>execute_lua</c> tool over this world.</summary>
        public LuaLlmTool Tool { get; }

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
    }
}
#endif
#endif
