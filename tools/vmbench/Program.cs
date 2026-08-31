using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Lua;
using Lua.Runtime;
using Lua.Standard;

internal static class Program
{
    private const string LuaDllPath = @"D:\Git\CoreAI\Assets\CoreAIMods\Plugins\Lua.dll";
    private const int ProductionBatch = 4;
    private const double TargetTrialSeconds = 0.65d;
    private const int TrialCount = 5;

    private const string NumericLoopTemplate = @"
local function work(N)
  local acc = 0.0
  for i = 1, N do
    acc = acc + i * 3 - (i % 7) + (i * i) % 101
  end
  return acc
end
return work(__N__)";

    private const string YieldLoop = @"
return function()
  while true do
    coroutine.yield()
  end
end";

    private static int Main()
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        Console.WriteLine($"LuaDll={LuaDllPath}");
        Console.WriteLine($"LuaDllVersion={System.Reflection.AssemblyName.GetAssemblyName(LuaDllPath).Version}");
        Console.WriteLine($"Framework={RuntimeInformation.FrameworkDescription}");
        Console.WriteLine($"OS={RuntimeInformation.OSDescription}");
        Console.WriteLine($"Architecture={RuntimeInformation.ProcessArchitecture}");
        Console.WriteLine($"ProcessorCount={Environment.ProcessorCount}");
        Console.WriteLine($"StopwatchFrequency={Stopwatch.Frequency}");
        MeasurePrimitiveCosts();

        InstructionFormula formula = CalibrateInstructionFormula();
        Console.WriteLine($"InstructionFormula={formula.InstructionsPerIteration}*N+{formula.FixedInstructions}");

        int[] batches = new int[] { 0, 1, 2, 4, 8, 16, 32, 64, 128, 256 };
        List<ThroughputResult> throughput = new List<ThroughputResult>();
        foreach (int batch in batches)
        {
            ThroughputResult result = MeasureThroughput(batch, formula);
            throughput.Add(result);
            Console.WriteLine(
                $"THROUGHPUT batch={(batch == 0 ? "raw" : batch.ToString())} N={result.Iterations} " +
                $"instructions={result.Instructions} seconds={result.MedianSeconds:F6} " +
                $"iterations_per_s={result.IterationsPerSecond:F0} instructions_per_s={result.InstructionsPerSecond:F0} " +
                $"min_seconds={result.MinSeconds:F6} max_seconds={result.MaxSeconds:F6} " +
                $"hook_calls={result.MedianHookCalls} checksum={result.Checksum:R}");
        }

        ThroughputResult raw = throughput.Single(item => item.Batch == 0);
        ThroughputResult guarded4 = throughput.Single(item => item.Batch == ProductionBatch);
        ThroughputResult handleThroughput = MeasureHandleThroughput(formula);
        Console.WriteLine(
            $"HANDLE_THROUGHPUT batch=1 N={handleThroughput.Iterations} " +
            $"instructions={handleThroughput.Instructions} seconds={handleThroughput.MedianSeconds:F6} " +
            $"iterations_per_s={handleThroughput.IterationsPerSecond:F0} " +
            $"instructions_per_s={handleThroughput.InstructionsPerSecond:F0} " +
            $"min_seconds={handleThroughput.MinSeconds:F6} max_seconds={handleThroughput.MaxSeconds:F6} " +
            $"hook_calls={handleThroughput.MedianHookCalls} checksum={handleThroughput.Checksum:R}");
        double yieldInstructions = MeasureYieldInstructionCount();
        Console.WriteLine($"YieldInstructionsPerResume={yieldInstructions:F6}");

        List<ResumeResult> resumes = new List<ResumeResult>
        {
            MeasureResumeOverhead(ResumeMode.Raw),
            MeasureResumeOverhead(ResumeMode.ReusedBatch4),
            MeasureResumeOverhead(ResumeMode.NewBatch4),
            MeasureResumeOverhead(ResumeMode.HandleBatch1)
        };

        foreach (ResumeResult resume in resumes)
        {
            Console.WriteLine(
                $"RESUME mode={resume.Mode} resumes={resume.ResumeCount} seconds={resume.Seconds:F6} " +
                $"ns_per_resume={resume.NanosecondsPerResume:F1} hook_calls={resume.HookCalls}");
        }

        ResumeResult rawResume = resumes.Single(item => item.Mode == ResumeMode.Raw);
        ResumeResult reused4Resume = resumes.Single(item => item.Mode == ResumeMode.ReusedBatch4);
        ResumeResult new4Resume = resumes.Single(item => item.Mode == ResumeMode.NewBatch4);
        ResumeResult handle1Resume = resumes.Single(item => item.Mode == ResumeMode.HandleBatch1);

        PrintDerived("batch4_reused", guarded4, reused4Resume, yieldInstructions, raw.InstructionsPerSecond);
        PrintDerived("batch4_new_hook", guarded4, new4Resume, yieldInstructions, raw.InstructionsPerSecond);
        PrintDerived("current_handle_batch1", handleThroughput, handle1Resume, yieldInstructions,
            handleThroughput.InstructionsPerSecond);
        PrintDerived("raw_control", raw, rawResume, yieldInstructions, raw.InstructionsPerSecond);
        return 0;
    }

    private static void MeasurePrimitiveCosts()
    {
        MeasurePrimitiveCost("Stopwatch.GetTimestamp", false);
        MeasurePrimitiveCost("GC.GetTotalMemory(false)", true);
    }

    private static void MeasurePrimitiveCost(string name, bool heapRead)
    {
        int probeCount = heapRead ? 1000 : 100_000;
        double probeSeconds = MeasurePrimitiveCostOnce(probeCount, heapRead);
        int count = (int)Math.Clamp(Math.Round(probeCount * TargetTrialSeconds / probeSeconds), 10_000d, 5_000_000d);
        List<double> samples = new List<double>();
        for (int trial = 0; trial < TrialCount; trial++)
        {
            samples.Add(MeasurePrimitiveCostOnce(count, heapRead));
        }

        double medianSeconds = samples.OrderBy(item => item).ElementAt(TrialCount / 2);
        Console.WriteLine($"PRIMITIVE name={name} calls={count} ns_per_call={medianSeconds * 1_000_000_000d / count:F1}");
    }

    private static double MeasurePrimitiveCostOnce(int count, bool heapRead)
    {
        long sink = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < count; i++)
        {
            sink ^= heapRead ? GC.GetTotalMemory(false) : Stopwatch.GetTimestamp();
        }

        stopwatch.Stop();
        GC.KeepAlive(sink);
        return stopwatch.Elapsed.TotalSeconds;
    }

    private static LuaState CreateState(bool coroutine)
    {
        LuaState state = LuaState.Create();
        state.OpenBasicLibrary();
        state.OpenMathLibrary();
        if (coroutine)
        {
            state.OpenCoroutineLibrary();
        }

        return state;
    }

    private static LuaClosure LoadNumericLoop(LuaState state, int iterations)
    {
        string source = NumericLoopTemplate.Replace("__N__", iterations.ToString(CultureInfo.InvariantCulture));
        return state.Load(source, "guarded_vm_benchmark");
    }

    private static LuaValue[] Execute(LuaState state, LuaClosure closure)
    {
        return state.ExecuteAsync(closure, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static InstructionFormula CalibrateInstructionFormula()
    {
        long at1000 = CountInstructions(1000);
        long at1001 = CountInstructions(1001);
        long perIteration = at1001 - at1000;
        long fixedInstructions = at1000 - perIteration * 1000L;
        long at2000 = CountInstructions(2000);
        long expected = perIteration * 2000L + fixedInstructions;
        if (at2000 != expected)
        {
            throw new InvalidOperationException($"Instruction formula validation failed: expected {expected}, got {at2000}.");
        }

        return new InstructionFormula(perIteration, fixedInstructions);
    }

    private static long CountInstructions(int iterations)
    {
        LuaState state = CreateState(false);
        LuaClosure closure = LoadNumericLoop(state, iterations);
        CountingHook hook = new CountingHook();
        state.SetHook(hook.Function, string.Empty, 1);
        Execute(state, closure);
        state.SetHook(null, string.Empty, 0);
        return hook.Calls;
    }

    private static ThroughputResult MeasureThroughput(int batch, InstructionFormula formula)
    {
        int initialIterations = batch == 0 ? 500_000 : 20_000;
        double probeSeconds = MeasureThroughputOnce(batch, initialIterations).Seconds;
        int iterations = ScaleIterations(initialIterations, probeSeconds, batch == 0 ? 5_000_000 : 2_000_000);

        MeasureThroughputOnce(batch, Math.Max(1000, iterations / 10));
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        List<RunSample> samples = new List<RunSample>();
        for (int trial = 0; trial < TrialCount; trial++)
        {
            samples.Add(MeasureThroughputOnce(batch, iterations));
        }

        RunSample[] ordered = samples.OrderBy(item => item.Seconds).ToArray();
        RunSample median = ordered[ordered.Length / 2];
        long instructions = formula.ForIterations(iterations);
        return new ThroughputResult(
            batch,
            iterations,
            instructions,
            median.Seconds,
            ordered[0].Seconds,
            ordered[ordered.Length - 1].Seconds,
            median.HookCalls,
            median.Checksum);
    }

    private static int ScaleIterations(int initialIterations, double probeSeconds, int maximum)
    {
        if (probeSeconds <= 0d)
        {
            return initialIterations;
        }

        double scaled = initialIterations * TargetTrialSeconds / probeSeconds;
        return (int)Math.Clamp(Math.Round(scaled), 10_000d, maximum);
    }

    private static ThroughputResult MeasureHandleThroughput(InstructionFormula formula)
    {
        int initialIterations = 20_000;
        double probeSeconds = MeasureHandleThroughputOnce(initialIterations).Seconds;
        int iterations = ScaleIterations(initialIterations, probeSeconds, 2_000_000);
        MeasureHandleThroughputOnce(Math.Max(1000, iterations / 10));

        List<RunSample> samples = new List<RunSample>();
        for (int trial = 0; trial < TrialCount; trial++)
        {
            samples.Add(MeasureHandleThroughputOnce(iterations));
        }

        RunSample[] ordered = samples.OrderBy(item => item.Seconds).ToArray();
        RunSample median = ordered[ordered.Length / 2];
        long instructions = formula.ForIterations(iterations);
        return new ThroughputResult(
            1,
            iterations,
            instructions,
            median.Seconds,
            ordered[0].Seconds,
            ordered[ordered.Length - 1].Seconds,
            median.HookCalls,
            median.Checksum);
    }

    private static RunSample MeasureHandleThroughputOnce(int iterations)
    {
        LuaState state = CreateState(false);
        LuaClosure closure = LoadNumericLoop(state, iterations);
        HandleHook hook = new HandleHook();
        state.SetHook(hook.Function, string.Empty, 1);
        Stopwatch stopwatch = Stopwatch.StartNew();
        LuaValue[] values = Execute(state, closure);
        stopwatch.Stop();
        state.SetHook(null, string.Empty, 0);
        return new RunSample(stopwatch.Elapsed.TotalSeconds, hook.Calls, values[0].Read<double>());
    }

    private static RunSample MeasureThroughputOnce(int batch, int iterations)
    {
        LuaState state = CreateState(false);
        LuaClosure closure = LoadNumericLoop(state, iterations);
        ProductionHook hook = batch > 0 ? new ProductionHook(batch) : null;
        if (hook != null)
        {
            hook.Reset();
            state.SetHook(hook.Function, string.Empty, batch);
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        LuaValue[] values = Execute(state, closure);
        stopwatch.Stop();

        if (hook != null)
        {
            state.SetHook(null, string.Empty, 0);
        }

        double checksum = values[0].Read<double>();
        return new RunSample(stopwatch.Elapsed.TotalSeconds, hook?.Calls ?? 0L, checksum);
    }

    private static double MeasureYieldInstructionCount()
    {
        const int resumes = 20_000;
        CoroutineFixture fixture = CreateCoroutineFixture();
        CountingHook hook = new CountingHook();
        long total = 0;
        for (int i = 0; i < resumes; i++)
        {
            hook.Reset();
            fixture.Coroutine.SetHook(hook.Function, string.Empty, 1);
            fixture.Coroutine.ResumeAsync(fixture.Stack, CancellationToken.None).GetAwaiter().GetResult();
            fixture.Coroutine.SetHook(null, string.Empty, 0);
            total += hook.Calls;
        }

        return (double)total / resumes;
    }

    private static ResumeResult MeasureResumeOverhead(ResumeMode mode)
    {
        int probeResumes = 2000;
        ResumeResult probe = MeasureResumeOverheadOnce(mode, probeResumes);
        int resumes = (int)Math.Clamp(
            Math.Round(probeResumes * TargetTrialSeconds / probe.Seconds),
            20_000d,
            1_000_000d);

        MeasureResumeOverheadOnce(mode, Math.Min(10_000, resumes));
        List<ResumeResult> samples = new List<ResumeResult>();
        for (int trial = 0; trial < TrialCount; trial++)
        {
            samples.Add(MeasureResumeOverheadOnce(mode, resumes));
        }

        return samples.OrderBy(item => item.NanosecondsPerResume).ElementAt(TrialCount / 2);
    }

    private static ResumeResult MeasureResumeOverheadOnce(ResumeMode mode, int resumes)
    {
        CoroutineFixture fixture = CreateCoroutineFixture();
        ProductionHook reusedHook = mode == ResumeMode.ReusedBatch4 ? new ProductionHook(ProductionBatch) : null;
        long hookCalls = 0;
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int i = 0; i < resumes; i++)
        {
            if (mode == ResumeMode.Raw)
            {
                fixture.Coroutine.ResumeAsync(fixture.Stack, CancellationToken.None).GetAwaiter().GetResult();
                continue;
            }

            if (mode == ResumeMode.ReusedBatch4)
            {
                reusedHook.Reset();
                fixture.Coroutine.SetHook(reusedHook.Function, string.Empty, ProductionBatch);
                fixture.Coroutine.ResumeAsync(fixture.Stack, CancellationToken.None).GetAwaiter().GetResult();
                fixture.Coroutine.SetHook(null, string.Empty, 0);
                hookCalls += reusedHook.Calls;
                continue;
            }

            if (mode == ResumeMode.NewBatch4)
            {
                ProductionHook newHook = new ProductionHook(ProductionBatch);
                newHook.Reset();
                fixture.Coroutine.SetHook(newHook.Function, string.Empty, ProductionBatch);
                fixture.Coroutine.ResumeAsync(fixture.Stack, CancellationToken.None).GetAwaiter().GetResult();
                fixture.Coroutine.SetHook(null, string.Empty, 0);
                hookCalls += newHook.Calls;
                continue;
            }

            long steps = 0;
            Stopwatch resumeStopwatch = Stopwatch.StartNew();
            LuaFunction handleHook = new LuaFunction("handle_guard", (context, cancellationToken) =>
            {
                steps++;
                if (resumeStopwatch.ElapsedMilliseconds > 500)
                {
                    throw new TimeoutException();
                }

                return new ValueTask<int>(context.Return());
            });
            fixture.Coroutine.SetHook(handleHook, string.Empty, 1);
            fixture.Coroutine.ResumeAsync(fixture.Stack, CancellationToken.None).GetAwaiter().GetResult();
            fixture.Coroutine.SetHook(null, string.Empty, 0);
            hookCalls += steps;
        }

        stopwatch.Stop();
        return new ResumeResult(mode, resumes, stopwatch.Elapsed.TotalSeconds, hookCalls);
    }

    private static CoroutineFixture CreateCoroutineFixture()
    {
        LuaState state = CreateState(true);
        LuaClosure closure = state.Load(YieldLoop, "yield_loop");
        LuaValue[] values = Execute(state, closure);
        LuaFunction function = values[0].Read<LuaFunction>();
        LuaState coroutine = state.CreateCoroutine(function, true);
        LuaStack stack = new LuaStack(8);
        return new CoroutineFixture(state, coroutine, stack);
    }

    private static void PrintDerived(
        string name,
        ThroughputResult throughput,
        ResumeResult resume,
        double yieldInstructions,
        double yieldInstructionRate)
    {
        double instructionNanoseconds = 1_000_000_000d / throughput.InstructionsPerSecond;
        double yieldInstructionNanoseconds = 1_000_000_000d / yieldInstructionRate;
        double fixedNanoseconds = Math.Max(0d,
            resume.NanosecondsPerResume - yieldInstructions * yieldInstructionNanoseconds);
        double tenThousandNanoseconds = fixedNanoseconds + 10_000d * instructionNanoseconds;
        double tenThousandMilliseconds = tenThousandNanoseconds / 1_000_000d;
        double in4ms = 4d / tenThousandMilliseconds;
        double in8ms = 8d / tenThousandMilliseconds;
        double instructionsIn4ms = Math.Max(0d, (4_000_000d - fixedNanoseconds) / instructionNanoseconds);
        Console.WriteLine(
            $"DERIVED mode={name} ns_per_instruction={instructionNanoseconds:F3} " +
            $"fixed_ns={fixedNanoseconds:F1} cost_10000_ms={tenThousandMilliseconds:F6} " +
            $"resumes_in_4ms={in4ms:F6} resumes_in_8ms={in8ms:F6} " +
            $"instructions_one_resume_in_4ms={instructionsIn4ms:F0}");
    }

    private sealed class CountingHook
    {
        public CountingHook()
        {
            Function = new LuaFunction("instruction_counter", Invoke);
        }

        public LuaFunction Function { get; }

        public long Calls { get; private set; }

        public void Reset()
        {
            Calls = 0;
        }

        private ValueTask<int> Invoke(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return new ValueTask<int>(context.Return());
        }
    }

    private sealed class ProductionHook
    {
        private readonly int _batch;
        private readonly long _timeoutTicks;
        private long _steps;
        private long _startTimestamp;
        private long _allocBaseline;

        public ProductionHook(int batch)
        {
            _batch = batch;
            _timeoutTicks = 600L * Stopwatch.Frequency;
            Function = new LuaFunction("production_equivalent_guard", Invoke);
        }

        public LuaFunction Function { get; }

        public long Calls { get; private set; }

        public void Reset()
        {
            Calls = 0;
            _steps = 0;
            _startTimestamp = Stopwatch.GetTimestamp();
            _allocBaseline = GC.GetTotalMemory(false);
        }

        private ValueTask<int> Invoke(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            _steps += _batch;
            if (_steps > long.MaxValue / 2L)
            {
                throw new InvalidOperationException();
            }

            if (Stopwatch.GetTimestamp() - _startTimestamp > _timeoutTicks)
            {
                throw new TimeoutException();
            }

            long allocated = GC.GetTotalMemory(false) - _allocBaseline;
            if (allocated == long.MinValue)
            {
                throw new InvalidOperationException();
            }

            return new ValueTask<int>(context.Return());
        }
    }

    private sealed class HandleHook
    {
        private readonly Stopwatch _stopwatch;

        public HandleHook()
        {
            _stopwatch = Stopwatch.StartNew();
            Function = new LuaFunction("handle_guard", Invoke);
        }

        public LuaFunction Function { get; }

        public long Calls { get; private set; }

        private ValueTask<int> Invoke(LuaFunctionExecutionContext context, CancellationToken cancellationToken)
        {
            Calls++;
            if (_stopwatch.ElapsedMilliseconds > 600_000)
            {
                throw new TimeoutException();
            }

            return new ValueTask<int>(context.Return());
        }
    }

    private sealed record InstructionFormula(long InstructionsPerIteration, long FixedInstructions)
    {
        public long ForIterations(int iterations)
        {
            return InstructionsPerIteration * iterations + FixedInstructions;
        }
    }

    private sealed record RunSample(double Seconds, long HookCalls, double Checksum);

    private sealed record ThroughputResult(
        int Batch,
        int Iterations,
        long Instructions,
        double MedianSeconds,
        double MinSeconds,
        double MaxSeconds,
        long MedianHookCalls,
        double Checksum)
    {
        public double IterationsPerSecond => Iterations / MedianSeconds;

        public double InstructionsPerSecond => Instructions / MedianSeconds;
    }

    private sealed record ResumeResult(ResumeMode Mode, int ResumeCount, double Seconds, long HookCalls)
    {
        public double NanosecondsPerResume => Seconds * 1_000_000_000d / ResumeCount;
    }

    private sealed record CoroutineFixture(LuaState Owner, LuaState Coroutine, LuaStack Stack);

    private enum ResumeMode
    {
        Raw,
        ReusedBatch4,
        NewBatch4,
        HandleBatch1
    }
}
