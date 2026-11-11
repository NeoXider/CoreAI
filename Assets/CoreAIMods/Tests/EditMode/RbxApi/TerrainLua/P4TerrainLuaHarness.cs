using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Terrain;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;

namespace CoreAI.Tests.EditMode.RbxApi.TerrainLua
{
    /// <summary>
    /// One script state over the production Rbx registration with the P4 Terrain hooks added the way
    /// composition adds them: the Terrain class row, <c>Region3</c> as a global, and — standing in for
    /// the two lines in the Instance member read and the method table — a metatable on the Workspace
    /// and Terrain proxies that consults <see cref="LuaCsTerrainBindings.TryReadWorkspaceTerrain"/> and
    /// <see cref="LuaCsTerrainBindings.RegisterMethods"/> first and the shipped Instance read after.
    /// </summary>
    /// <remarks>
    /// WHY a chained metatable and not the shipped method table: the method table is built inside the
    /// Instance bindings, which this package does not edit; once composition adds the two lines, the
    /// production meta answers the same members from the same functions. WHY the chain is a host
    /// function that calls the shipped one through the VM: a Lua closure as a userdata __index loses
    /// the method's self in a colon call on this VM, which no production meta does.
    /// </remarks>
    internal sealed class P4TerrainLuaHarness
    {
        private readonly LuaCsScriptEngine _engine = new();
        private readonly IScriptState _state;
        private readonly Dictionary<string, LuaValue> _terrainMethods = new(StringComparer.Ordinal);

        public P4TerrainLuaHarness(LuaCapabilities capabilities = LuaCapabilities.All,
            TerrainLimits limits = null, string modId = "p4-terrain")
        {
            Registry = new InstanceRegistry(Terrain.P4TerrainTestWorld.CatalogWithTerrain());
            Bindings = new LuaCsRbxApiBindings(registry: Registry);
            Workspace = Bindings.Game.FindFirstChildOfClass("Workspace");
            TerrainInstance = RbxTerrainBootstrap.EnsureTerrain(Registry, Workspace, limits ?? TerrainLimits.Desktop);

            LuaCsApiRegistry registry = (LuaCsApiRegistry)_engine.CreateFunctionRegistry();
            Bindings.Register(registry, capabilities, modId);
            registry.RegisterValue("Region3", LuaCsTerrainBindings.BuildRegion3Global);
            _state = _engine.CreateState();
            registry.ApplyTo(_state);

            LuaState lua = LuaCsScriptState.Unwrap(_state);
            LuaCsRbxLua.TryGetInstance(lua.Environment["workspace"], out LuaCsRbxInstanceProxy workspaceProxy);
            Context = workspaceProxy.Context;
            LuaCsTerrainBindings.RegisterMethods(Context, (name, value, _) => _terrainMethods.Add(name, value));
            ChainMetatables(lua, workspaceProxy);
        }

        public InstanceRegistry Registry { get; }

        public LuaCsRbxApiBindings Bindings { get; }

        public RbxInstance Workspace { get; }

        public RbxTerrain TerrainInstance { get; }

        internal LuaCsRbxModContext Context { get; }

        /// <summary>Names the harness bound, for the surface check.</summary>
        public IReadOnlyCollection<string> MethodNames => _terrainMethods.Keys;

        /// <summary>Runs a chunk and returns its results as C# values (bool, double, string or null).</summary>
        public object[] Run(string source)
        {
            object[] results = _engine.RunChunk(_state, source);
            for (int index = 0; index < results.Length; index++)
            {
                if (results[index] is LuaValue value)
                {
                    results[index] = value.Type switch
                    {
                        LuaValueType.Boolean => value.Read<bool>(),
                        LuaValueType.Number => value.Read<double>(),
                        LuaValueType.String => value.Read<string>(),
                        LuaValueType.Nil => null,
                        _ => value.ToString()
                    };
                }
            }

            return results;
        }

        /// <summary>Exposes a C# callback to scripts as a global function.</summary>
        public void DefineFunction(string name, Action callback)
        {
            LuaState lua = LuaCsScriptState.Unwrap(_state);
            lua.Environment[name] = new LuaFunction(name, (ctx, _) =>
            {
                callback();
                return new ValueTask<int>(ctx.Return());
            });
        }

        private void ChainMetatables(LuaState lua, LuaCsRbxInstanceProxy workspaceProxy)
        {
            LuaTable shipped = workspaceProxy.Metatable;
            LuaValue shippedIndex = shipped[Metamethods.Index];
            LuaTable chained = new();
            LuaValue key = LuaValue.Nil;
            while (shipped.TryGetNext(key, out KeyValuePair<LuaValue, LuaValue> pair))
            {
                chained[pair.Key] = pair.Value;
                key = pair.Key;
            }

            chained[Metamethods.Index] = new LuaFunction("p4.index", async (ctx, ct) =>
            {
                LuaValue self = ctx.HasArgument(0) ? ctx.GetArgument(0) : LuaValue.Nil;
                LuaValue member = ctx.HasArgument(1) ? ctx.GetArgument(1) : LuaValue.Nil;
                if (member.Type == LuaValueType.String
                    && LuaCsRbxLua.TryGetInstance(self, out LuaCsRbxInstanceProxy proxy))
                {
                    string name = member.Read<string>();
                    if (LuaCsTerrainBindings.TryReadWorkspaceTerrain(proxy.Context, proxy.Instance, name,
                            out LuaValue terrain))
                    {
                        return ctx.Return(terrain);
                    }

                    if (proxy.Instance is RbxTerrain && _terrainMethods.TryGetValue(name, out LuaValue method))
                    {
                        return ctx.Return(method);
                    }
                }

                LuaValue[] results = await LuaCsSecureEnvironment.CallCountedAsync(ctx.State, shippedIndex,
                    new[] { self, member }, ct, "P4 harness __index");
                return ctx.Return(results.Length > 0 ? results[0] : LuaValue.Nil);
            });
            workspaceProxy.Metatable = chained;
            LuaCsRbxLua.TryGetInstance(Context.WrapInstance(TerrainInstance), out LuaCsRbxInstanceProxy terrainProxy);
            terrainProxy.Metatable = chained;
        }
    }
}
