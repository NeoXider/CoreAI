using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Lua;
using Lua.Runtime;
using Lua.Standard;

namespace LuaVmComparison
{
    /// <summary>
    /// nuskey8/Lua-CSharp VM under test. Sandbox = open only the safe libraries (base/string/table/math/
    /// coroutine/bitwise); io/os/debug/package are never opened. Because Lua-CSharp bundles
    /// load/dofile/loadfile into the BASIC library, we nil them out to fully block dynamic loading.
    /// The API is async-only, so pure-compute scripts are driven synchronously via GetAwaiter().GetResult().
    /// </summary>
    public sealed class LuaCSharpRunner : IVmRunner
    {
        private readonly LuaState _state;

        public LuaCSharpRunner()
        {
            _state = LuaState.Create();
            _state.OpenBasicLibrary();
            _state.OpenStringLibrary();
            _state.OpenTableLibrary();
            _state.OpenMathLibrary();
            _state.OpenCoroutineLibrary();
            _state.OpenBitwiseLibrary();
            // NOT opened: io, os, debug, package/require.

            // Harden: remove dynamic loaders that Lua-CSharp bundles into the basic library.
            _state.Environment["load"] = LuaValue.Nil;
            _state.Environment["loadstring"] = LuaValue.Nil;
            _state.Environment["dofile"] = LuaValue.Nil;
            _state.Environment["loadfile"] = LuaValue.Nil;

            _state.Environment["host_add"] = new LuaFunction("host_add", (ctx, ct) =>
            {
                double a = ctx.GetArgument<double>(0);
                double b = ctx.GetArgument<double>(1);
                return new ValueTask<int>(ctx.Return(a + b));
            });
        }

        public string Name => "Lua-CSharp";

        public string Eval(string code)
        {
            try
            {
                LuaClosure closure = _state.Load(code, "eval");
                LuaValue[] results = Run(closure, default);
                return results.Length == 0 ? "nil" : Normalize(results[0]);
            }
            catch (Exception e)
            {
                return "ERR:" + e.GetType().Name;
            }
        }

        /// <summary>
        /// Non-blocking counterpart to <see cref="Eval"/>. Awaits <c>ExecuteAsync</c> instead of blocking on
        /// <c>GetAwaiter().GetResult()</c>, so on single-threaded WebGL/WASM a script that calls
        /// <c>coroutine.yield</c> can have its continuation pumped by Unity's player loop across frames rather
        /// than deadlocking the only thread. Normalizes the first result exactly like <see cref="Eval"/>.
        /// </summary>
        public async System.Threading.Tasks.Task<string> EvalAsync(string code, System.Threading.CancellationToken ct = default)
        {
            try
            {
                LuaClosure closure = _state.Load(code, "eval");
                LuaValue[] results = await _state.ExecuteAsync(closure, ct);
                return results.Length == 0 ? "nil" : Normalize(results[0]);
            }
            catch (Exception e)
            {
                return "ERR:" + e.GetType().Name;
            }
        }

        public bool HasGlobal(string name)
        {
            LuaValue v = _state.Environment[name];
            return v.Type != LuaValueType.Nil;
        }

        public BenchResult Benchmark(string code, int warmup, int iters)
        {
            try
            {
                LuaClosure closure = _state.Load(code, "bench");
                for (int i = 0; i < warmup; i++) Run(closure, default);

                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++) Run(closure, default);
                sw.Stop();
                long after = GC.GetAllocatedBytesForCurrentThread();

                double micros = sw.Elapsed.TotalMilliseconds * 1000.0 / iters;
                long bytes = (after - before) / iters;
                return new BenchResult(micros, bytes);
            }
            catch (Exception e)
            {
                return BenchResult.Fail(e.GetType().Name + ": " + e.Message);
            }
        }

        public bool HaltsWithBudget(string code, int budgetMs, out string detail)
        {
            // Lua-CSharp checks the CancellationToken on every VM back-edge (Jmp / ForLoop), so a
            // wall-clock budget halts even a bare `while true do end`. We prove it empirically here.
            using var cts = new CancellationTokenSource(budgetMs);
            var sw = Stopwatch.StartNew();
            try
            {
                LuaClosure closure = _state.Load(code, "runaway");
                Run(closure, cts.Token);
                sw.Stop();
                detail = "ran to completion in " + sw.ElapsedMilliseconds + "ms (not a runaway?)";
                return false;
            }
            catch (Exception e)
            {
                sw.Stop();
                detail = "halted via CancellationToken after " + sw.ElapsedMilliseconds + "ms (" + e.GetType().Name + ")";
                return true;
            }
        }

        public void Dispose()
        {
            _state.Dispose();
        }

        private LuaValue[] Run(LuaClosure closure, CancellationToken ct)
        {
            // Pure-compute scripts complete synchronously; GetResult blocks otherwise (and the token unwinds runaways).
            return _state.ExecuteAsync(closure, ct).GetAwaiter().GetResult();
        }

        private static string Normalize(LuaValue v)
        {
            switch (v.Type)
            {
                case LuaValueType.Number:
                    return LuaNum.Format(v.Read<double>());
                case LuaValueType.String:
                    return v.Read<string>();
                case LuaValueType.Boolean:
                    return v.Read<bool>() ? "true" : "false";
                case LuaValueType.Nil:
                    return "nil";
                default:
                    return v.ToString();
            }
        }
    }
}
