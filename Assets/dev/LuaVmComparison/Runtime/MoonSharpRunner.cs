using System;
using System.Diagnostics;
using MoonSharp.Interpreter;

namespace LuaVmComparison
{
    /// <summary>
    /// MoonSharp VM under test. Sandbox = explicit safe CoreModules (no OS_System/OS_Time/IO/Debug/LoadMethods),
    /// so os/io/debug/load/dofile/loadfile are all absent — mirrors CoreAI's hardened preset.
    /// </summary>
    public sealed class MoonSharpRunner : IVmRunner
    {
        // Base + metatables + string + table + math + bit32 + iterators + coroutine + error handling + consts.
        // Deliberately excludes OS_System, OS_Time, IO, Debug, LoadMethods, Dynamic.
        private const CoreModules SafeModules =
            CoreModules.Basic
            | CoreModules.Metatables
            | CoreModules.String
            | CoreModules.Table
            | CoreModules.Math
            | CoreModules.Bit32
            | CoreModules.TableIterators
            | CoreModules.Coroutine
            | CoreModules.ErrorHandling
            | CoreModules.GlobalConsts;

        private readonly Script _script;

        public MoonSharpRunner()
        {
            _script = new Script(SafeModules);
            _script.Globals["host_add"] = (Func<double, double, double>)((a, b) => a + b);
        }

        public string Name => "MoonSharp";

        public string Eval(string code)
        {
            try
            {
                DynValue v = _script.DoString(code);
                return Normalize(v);
            }
            catch (Exception e)
            {
                return "ERR:" + e.GetType().Name;
            }
        }

        public bool HasGlobal(string name) => !_script.Globals.Get(name).IsNil();

        public BenchResult Benchmark(string code, int warmup, int iters)
        {
            try
            {
                DynValue chunk = _script.LoadString(code);
                for (int i = 0; i < warmup; i++) _script.Call(chunk);

                long before = GC.GetAllocatedBytesForCurrentThread();
                var sw = Stopwatch.StartNew();
                for (int i = 0; i < iters; i++) _script.Call(chunk);
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
            // MoonSharp has no built-in instruction/time budget; a runaway loop is halted by attaching an
            // IDebugger that aborts after N instructions. CoreAI ships exactly that (InstructionLimitDebugger).
            // We do NOT execute the runaway loop here (it would hang the Editor without that debugger wired in).
            detail = "requires IDebugger (CoreAI: InstructionLimitDebugger); not run in this harness";
            return false;
        }

        public void Dispose()
        {
            // MoonSharp Script has no unmanaged resources to release.
        }

        private static string Normalize(DynValue v)
        {
            switch (v.Type)
            {
                case DataType.Number:
                    return LuaNum.Format(v.Number);
                case DataType.String:
                    return v.String;
                case DataType.Boolean:
                    return v.Boolean ? "true" : "false";
                case DataType.Nil:
                case DataType.Void:
                    return "nil";
                default:
                    return v.ToPrintString();
            }
        }
    }
}
