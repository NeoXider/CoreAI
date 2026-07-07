using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LuaVmComparison
{
    /// <summary>
    /// VM-agnostic comparison corpus (correctness + sandbox + performance) run against every <see cref="IVmRunner"/>.
    /// <see cref="RunAll"/> returns a Markdown report; it constructs and disposes the runners itself.
    /// </summary>
    public static class LuaVmBench
    {
        private sealed class Case
        {
            public string Name;
            public string Code;
            public string Expected; // correctness only
            public int Warmup;      // perf only
            public int Iters;       // perf only
        }

        private static readonly Case[] Correctness =
        {
            new Case { Name = "arithmetic",  Code = "return (2+3)*4 - 10/2", Expected = "15" },
            new Case { Name = "string_ops",  Code = "return ('a'..'b'):upper() .. tostring(#'hello')", Expected = "AB5" },
            new Case { Name = "table_ipairs", Code = "local t={1,2,3}; local s=0; for _,v in ipairs(t) do s=s+v end; return s", Expected = "6" },
            new Case { Name = "closures",    Code = "local function mk() local n=0; return function() n=n+1; return n end end local f=mk(); f(); return f()", Expected = "2" },
            new Case { Name = "recursion_fib15", Code = "local function fib(n) if n<2 then return n end return fib(n-1)+fib(n-2) end return fib(15)", Expected = "610" },
            new Case { Name = "metatables",  Code = "local t=setmetatable({}, {__index=function() return 42 end}); return t.anything", Expected = "42" },
            new Case { Name = "coroutines",  Code =
                "local co = coroutine.create(function(a) local b = coroutine.yield(a+1); return b*2 end)\n" +
                "local _, x = coroutine.resume(co, 10)\n" +
                "local _, y = coroutine.resume(co, 5)\n" +
                "return x + y", Expected = "21" },
            new Case { Name = "pcall_error", Code =
                "local ok,err = pcall(function() error('boom') end)\n" +
                "return tostring(ok)..':'..(type(err)=='string' and 'str' or type(err))", Expected = "false:str" },
        };

        // Dangerous globals that a hardened sandbox must not expose to untrusted mod code.
        private static readonly string[] DangerousGlobals =
            { "os", "io", "debug", "package", "require", "load", "loadstring", "dofile", "loadfile" };

        private static readonly Case[] Performance =
        {
            new Case { Name = "tight_loop_1e6",  Code = "local s=0; for i=1,1000000 do s=s+i end; return s", Warmup = 1, Iters = 5 },
            new Case { Name = "fib30",           Code = "local function fib(n) if n<2 then return n end return fib(n-1)+fib(n-2) end return fib(30)", Warmup = 1, Iters = 3 },
            new Case { Name = "string_build_5k", Code = "local s=''; for i=1,5000 do s=s..'x' end; return #s", Warmup = 2, Iters = 10 },
            new Case { Name = "table_churn_50k", Code = "local t={}; for i=1,50000 do t[i]=i*2 end; local s=0; for i=1,50000 do s=s+t[i] end; return s", Warmup = 2, Iters = 10 },
            new Case { Name = "host_call_1e5",   Code = "local s=0; for i=1,100000 do s=s+host_add(i,1) end; return s", Warmup = 1, Iters = 5 },
        };

        public static string RunAll()
        {
            var runners = new IVmRunner[] { new MoonSharpRunner(), new LuaCSharpRunner() };
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("# Lua VM comparison run — MoonSharp vs Lua-CSharp");
                sb.AppendLine();
                sb.AppendLine("Runners: " + string.Join(", ", Array.ConvertAll(runners, r => r.Name)));
                sb.AppendLine();

                AppendSandbox(sb, runners);
                AppendCorrectness(sb, runners);
                AppendPerformance(sb, runners);
                AppendRunaway(sb, runners);

                return sb.ToString();
            }
            finally
            {
                foreach (var r in runners) r.Dispose();
            }
        }

        /// <summary>
        /// Single-threaded-safe smoke: runs ONLY the correctness corpus (no perf loops, no thread/timer-based
        /// runaway halt), so it is safe on WebGL/WASM where threads do not exist. Its real purpose is to force
        /// Lua-CSharp's <c>Lua.dll</c> to be exercised in an IL2CPP/AOT player, proving the VM survives AOT.
        /// </summary>
        public static string RunCorrectnessSmoke() => RunCorrectnessSmoke(null);

        /// <summary>
        /// Same smoke, but emits a line to <paramref name="log"/> at every step (runner construction and each
        /// case) BEFORE it blocks on that step. On a WebGL/WASM host this pinpoints exactly which VM/step hangs
        /// (e.g. Lua-CSharp's async API driven synchronously) instead of leaving a silent frozen frame.
        /// </summary>
        public static string RunCorrectnessSmoke(Action<string> log)
        {
            void Emit(string s) { log?.Invoke(s); }

            var sb = new StringBuilder();
            sb.AppendLine("Lua VM WebGL/AOT smoke — correctness only (no threads/perf/runaway)");
            Emit("LUAVM_STEP: begin");

            IVmRunner moon = null, lc = null;
            try
            {
                Emit("LUAVM_STEP: ctor MoonSharp");
                moon = new MoonSharpRunner();
                Emit("LUAVM_STEP: ctor Lua-CSharp");
                lc = new LuaCSharpRunner();
                Emit("LUAVM_STEP: both runners constructed");

                var runners = new[] { moon, lc };
                bool allOk = true;
                foreach (var c in Correctness)
                {
                    foreach (var r in runners)
                    {
                        Emit($"LUAVM_STEP: eval {r.Name} {c.Name} ...");
                        string got = r.Eval(c.Code);
                        bool ok = got == c.Expected;
                        allOk &= ok;
                        string line = $"{(ok ? "OK  " : "FAIL")} {r.Name} {c.Name}: '{got}' (expected '{c.Expected}')";
                        sb.AppendLine(line);
                        Emit("LUAVM_STEP: " + line);
                    }
                }

                sb.AppendLine(allOk ? "RESULT: all correctness cases pass on both VMs under AOT." : "RESULT: a case FAILED — see above.");
                return sb.ToString();
            }
            finally
            {
                moon?.Dispose();
                lc?.Dispose();
            }
        }

        private static void AppendSandbox(StringBuilder sb, IVmRunner[] runners)
        {
            sb.AppendLine("## Sandbox — dangerous globals (want: absent on both)");
            sb.AppendLine();
            sb.Append("| global |");
            foreach (var r in runners) sb.Append(" " + r.Name + " |");
            sb.AppendLine();
            sb.Append("|---|");
            foreach (var _ in runners) sb.Append("---|");
            sb.AppendLine();
            foreach (var g in DangerousGlobals)
            {
                sb.Append("| `" + g + "` |");
                foreach (var r in runners)
                {
                    bool present = r.HasGlobal(g);
                    sb.Append(present ? " PRESENT ⚠ |" : " absent |");
                }
                sb.AppendLine();
            }
            sb.AppendLine();
        }

        private static void AppendCorrectness(StringBuilder sb, IVmRunner[] runners)
        {
            sb.AppendLine("## Correctness");
            sb.AppendLine();
            sb.Append("| case | expected |");
            foreach (var r in runners) sb.Append(" " + r.Name + " |");
            sb.AppendLine(" match |");
            sb.Append("|---|---|");
            foreach (var _ in runners) sb.Append("---|");
            sb.AppendLine("---|");

            foreach (var c in Correctness)
            {
                sb.Append("| " + c.Name + " | `" + c.Expected + "` |");
                bool allMatch = true;
                foreach (var r in runners)
                {
                    string got = r.Eval(c.Code);
                    if (got != c.Expected) allMatch = false;
                    sb.Append(" `" + got + "` |");
                }
                sb.AppendLine(allMatch ? " ✅ |" : " ❌ |");
            }
            sb.AppendLine();
        }

        private static void AppendPerformance(StringBuilder sb, IVmRunner[] runners)
        {
            sb.AppendLine("## Performance (mean per iteration; lower is better)");
            sb.AppendLine();
            sb.Append("| case |");
            foreach (var r in runners) sb.Append(" " + r.Name + " µs | " + r.Name + " GC B |");
            sb.AppendLine(" speedup (MS/LC) |");
            sb.Append("|---|");
            foreach (var _ in runners) sb.Append("---|---|");
            sb.AppendLine("---|");

            foreach (var c in Performance)
            {
                sb.Append("| " + c.Name + " |");
                var micros = new List<double>();
                foreach (var r in runners)
                {
                    BenchResult br = r.Benchmark(c.Code, c.Warmup, c.Iters);
                    if (br.Ok)
                    {
                        micros.Add(br.MicrosPerIter);
                        sb.Append(" " + br.MicrosPerIter.ToString("F1", CultureInfo.InvariantCulture) + " |");
                        sb.Append(" " + br.BytesPerIter.ToString("N0", CultureInfo.InvariantCulture) + " |");
                    }
                    else
                    {
                        micros.Add(double.NaN);
                        sb.Append(" ERR |");
                        sb.Append(" " + br.Error + " |");
                    }
                }
                if (micros.Count == 2 && micros[1] > 0 && !double.IsNaN(micros[0]) && !double.IsNaN(micros[1]))
                    sb.AppendLine(" " + (micros[0] / micros[1]).ToString("F2", CultureInfo.InvariantCulture) + "× |");
                else
                    sb.AppendLine(" - |");
            }
            sb.AppendLine();
            sb.AppendLine("_speedup >1 means Lua-CSharp is faster than MoonSharp on that case._");
            sb.AppendLine();
        }

        private static void AppendRunaway(StringBuilder sb, IVmRunner[] runners)
        {
            sb.AppendLine("## Runaway halt — `while true do end` with a 500ms host budget");
            sb.AppendLine();
            const string runaway = "while true do end";
            foreach (var r in runners)
            {
                bool halted = r.HaltsWithBudget(runaway, 500, out string detail);
                sb.AppendLine("- **" + r.Name + "**: " + (halted ? "HALTED ✅" : "not halted") + " — " + detail);
            }
            sb.AppendLine();
        }
    }
}
