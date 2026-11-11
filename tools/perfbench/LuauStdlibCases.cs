using System.Collections.Generic;
using System.Threading;
using CoreAI.Sandbox.LuaCs;
using Lua;
using Lua.Runtime;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// The Luau standard library of MVP13 against the Lua it replaces: each pair runs the same work once as the
    /// sandbox's library call and once as the Lua loop a script would otherwise write, both inside one guarded
    /// execution with the production hook, so the per-operation times compare directly.
    /// </summary>
    internal static class LuauStdlibCases
    {
        private const string Setup = @"
            T64 = {} for i = 1, 64 do T64[i] = i end
            CSV16 = 'alpha,beta,gamma,delta,epsilon,zeta,eta,theta,iota,kappa,lambda,mu,nu,xi,omicron,pi'
            function luaFind(t, x) for i = 1, #t do if t[i] == x then return i end end return nil end
            function luaSplit(s, sep)
              local out, i = {}, 1
              while true do
                local j = string.find(s, sep, i, true)
                if not j then out[#out + 1] = string.sub(s, i) return out end
                out[#out + 1] = string.sub(s, i, j - 1)
                i = j + #sep
              end
            end
            function luaCreate(n, v) local t = {} for i = 1, n do t[i] = v end return t end
            function luaClone(t) local c = {} for k, v in next, t do c[k] = v end return c end";

        public static void Add(List<MicroCase> cases)
        {
            AddPair(cases, "table.find, 64 elements, needle last",
                "local t = T64 local r for i = 1, n do r = table.find(t, 64) end return r",
                "local t = T64 local r for i = 1, n do r = luaFind(t, 64) end return r");
            AddPair(cases, "string.split, 16 fields",
                "local s = CSV16 local r for i = 1, n do r = string.split(s, ',') end return #r",
                "local s = CSV16 local r for i = 1, n do r = luaSplit(s, ',') end return #r");
            AddPair(cases, "table.create(256, 0)",
                "local r for i = 1, n do r = table.create(256, 0) end return #r",
                "local r for i = 1, n do r = luaCreate(256, 0) end return #r");
            AddPair(cases, "table.clone, 64 entries",
                "local t = T64 local r for i = 1, n do r = table.clone(t) end return #r",
                "local t = T64 local r for i = 1, n do r = luaClone(t) end return #r");
            AddSingle(cases, "string.format('%.2f'), exact C rounding",
                "local r for i = 1, n do r = string.format('%.2f', i * 0.37) end return #r");
            AddSingle(cases, "string.format('%s'), the capped native path alone",
                "local r for i = 1, n do r = string.format('%s', 'x') end return #r");
            AddSingle(cases, "string.format('%d of %s')",
                "local r for i = 1, n do r = string.format('%d of %s', i, 'x') end return #r");
        }

        private static void AddPair(List<MicroCase> cases, string name, string library, string lua)
        {
            AddSingle(cases, name + " [library]", library);
            AddSingle(cases, name + " [Lua loop]", lua);
        }

        private static void AddSingle(List<MicroCase> cases, string name, string body)
        {
            LuaState state = null;
            LuaFunction function = null;
            LuaCsExecutionGuard guard = null;
            cases.Add(new MicroCase("luau stdlib", name, ops =>
            {
                LuaValue[] result = guard.Execute(state, function, CancellationToken.None, new LuaValue(ops));
                return (long)result[0].Read<double>();
            }, "one operation per loop turn inside one guarded execution with the production hook",
                setup: () =>
                {
                    LuaCsSecureEnvironment environment = new();
                    state = environment.Create();
                    environment.RunChunk(state, Setup);
                    LuaClosure chunk = state.Load("return function(n) " + body + " end", "perfbench");
                    LuaValue[] loaded = state.ExecuteAsync(chunk, CancellationToken.None).AsTask().GetAwaiter()
                        .GetResult();
                    function = loaded[0].Read<LuaFunction>();
                    guard = new LuaCsExecutionGuard(timeoutMs: 600_000, maxSteps: long.MaxValue / 4);
                }, fixedOps: 20_000));
        }
    }
}
