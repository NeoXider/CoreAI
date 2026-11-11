using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Llm;
using CoreAI.Mods.Performance;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting.LuaCs;
using Lua;
using Lua.Runtime;

namespace CoreAI.PerfBench
{
    /// <summary>
    /// The micro-benchmarks of the engine-free hot paths. Every case drives production code compiled from
    /// Assets/ (no copy of a production body lives here, unlike tools/vmbench, which mirrors the guard hook).
    /// </summary>
    internal static class MicroCases
    {
        private const string NumericLoopFunction =
            "return function(n)\n" +
            "  local acc = 0.0\n" +
            "  for i = 1, n do\n" +
            "    acc = acc + i * 3 - (i % 7) + (i * i) % 101\n" +
            "  end\n" +
            "  return acc\n" +
            "end";

        /// <summary>VM instructions one iteration of <see cref="NumericLoopFunction"/> executes (tools/vmbench calibration).</summary>
        public const int InstructionsPerLoopIteration = 8;

        public static List<MicroCase> Build()
        {
            List<MicroCase> cases = new();
            AddDatatypeCases(cases);
            AddSignalCases(cases);
            AddPartWriteCases(cases);
            AddMarshallingCases(cases);
            AddGuardCases(cases);
            AddSchedulerCases(cases);
            AddLegacyDispatchCases(cases);
            AddWorldCaptureCases(cases);
            AddSseCases(cases);
            StreamingCases.Add(cases);
            LuauStdlibCases.Add(cases);
            TerrainCases.Add(cases);
            return cases;
        }

        private static void AddDatatypeCases(List<MicroCase> cases)
        {
            cases.Add(new MicroCase("datatypes", "Vector3 add+scale+Magnitude", ops =>
            {
                RbxVector3 accumulator = new(0f, 0f, 0f);
                RbxVector3 step = new(0.5f, 1.25f, -2f);
                float sum = 0f;
                for (int i = 0; i < ops; i++)
                {
                    accumulator = (accumulator + step) * 0.999f;
                    sum += accumulator.Magnitude;
                }

                return (long)sum;
            }));
            cases.Add(new MicroCase("datatypes", "CFrame * CFrame", ops =>
            {
                RbxCFrame frame = RbxCFrame.Identity;
                RbxCFrame step = RbxCFrame.FromEulerAnglesXYZ(0.01f, 0.02f, 0.03f) + new RbxVector3(1f, 0f, 0f);
                for (int i = 0; i < ops; i++)
                {
                    frame = frame * step;
                }

                return (long)frame.Position.X;
            }));
            cases.Add(new MicroCase("datatypes", "CFrame * Vector3 (PointToWorldSpace)", ops =>
            {
                RbxCFrame frame = RbxCFrame.FromEulerAnglesXYZ(0.3f, 0.2f, 0.1f) + new RbxVector3(5f, 2f, 1f);
                RbxVector3 point = new(1f, 2f, 3f);
                float sum = 0f;
                for (int i = 0; i < ops; i++)
                {
                    point = frame * point * 0.5f;
                    sum += point.X;
                }

                return (long)sum;
            }));
            cases.Add(new MicroCase("datatypes", "CFrame.Inverse", ops =>
            {
                RbxCFrame frame = RbxCFrame.FromEulerAnglesXYZ(0.3f, 0.2f, 0.1f) + new RbxVector3(5f, 2f, 1f);
                float sum = 0f;
                for (int i = 0; i < ops; i++)
                {
                    frame = frame.Inverse();
                    sum += frame.Position.Y;
                }

                return (long)sum;
            }));
            cases.Add(new MicroCase("datatypes", "CFrame.GetComponents (LOW finding)", ops =>
            {
                RbxCFrame frame = RbxCFrame.FromEulerAnglesXYZ(0.3f, 0.2f, 0.1f);
                float sum = 0f;
                for (int i = 0; i < ops; i++)
                {
                    sum += frame.GetComponents()[3];
                }

                return (long)sum;
            }));
            cases.Add(new MicroCase("datatypes", "CFrame.ToString (LOW finding)", ops =>
            {
                RbxCFrame frame = RbxCFrame.FromEulerAnglesXYZ(0.3f, 0.2f, 0.1f);
                long length = 0;
                for (int i = 0; i < ops; i++)
                {
                    length += frame.ToString().Length;
                }

                return length;
            }));
        }

        private static void AddSignalCases(List<MicroCase> cases)
        {
            RbxScriptSignal idleSignal = new("Perf.Unconnected");
            object[] idleArgs = { 0.016d };
            cases.Add(new MicroCase("signal", "Fire, no connections", ops =>
            {
                for (int i = 0; i < ops; i++)
                {
                    idleSignal.Fire(idleArgs);
                }

                return ops;
            }, "the early return every unlistened fire takes"));

            LuaCsRbxApiBindings bindings = null;
            RbxScriptSignal signal = null;
            long delivered = 0;
            object[] args = { 0.016d };
            cases.Add(new MicroCase("signal", "Fire + deferred drain, 1 C# connection", ops =>
            {
                int pending = 0;
                for (int i = 0; i < ops; i++)
                {
                    signal.Fire(args);
                    pending++;
                    if (pending == 32)
                    {
                        bindings.Scheduler.Advance(0d);
                        pending = 0;
                    }
                }

                bindings.Scheduler.Advance(0d);
                return delivered;
            }, "per fire: SignalInvocation + chain array + argument copy, then the drain; one Advance(0) per 32 fires is included",
                setup: () =>
                {
                    bindings = new LuaCsRbxApiBindings();
                    signal = new RbxScriptSignal("Perf.Signal");
                    signal.BindScheduler(bindings.Scheduler);
                    signal.Connect((Action<object[]>)(_ => delivered++));
                },
                teardown: () => bindings.Dispose()));

            foreach (int handlers in new[] { 1, 8, 64 })
            {
                HeadlessWorld world = null;
                int handlerCount = handlers;
                cases.Add(new MicroCase("signal", "Heartbeat frame, " + handlers + " Lua handler(s) (n = n + 1)", ops =>
                {
                    for (int i = 0; i < ops; i++)
                    {
                        world.World.AdvanceFrame(1d / 60d);
                    }

                    return world.Counters.ThreadResumes;
                }, "one op = one scheduler frame; divide by the handler count for the per-fire cost",
                    setup: () =>
                    {
                        world = HeadlessWorld.Create();
                        world.World.LoadMod("perf-heartbeat", BuildCounterHandlers(handlerCount), null);
                        RequireLoaded(world, "perf-heartbeat");
                    },
                    teardown: () => world.Dispose()));
            }
        }

        private static void AddPartWriteCases(List<MicroCase> cases)
        {
            HeadlessWorld world = null;
            cases.Add(new MicroCase("part-write", "Heartbeat frame, 256 Parts moved by 4 handlers (Position = Vector3.new)", ops =>
            {
                for (int i = 0; i < ops; i++)
                {
                    world.World.AdvanceFrame(1d / 60d);
                }

                return world.Counters.GuardedSteps;
            }, "the parts-*-heartbeat workload as one op; divide by 256 for the per-write cost",
                setup: () =>
                {
                    world = HeadlessWorld.Create();
                    PerfScenarioResult result = new(PerfScenarioCatalog.Parts2048Heartbeat, "micro");
                    world.World.LoadMovers(PerfScenarioCatalog.Parts2048Heartbeat, result);
                    if (result.FailedMods.Count > 0)
                    {
                        throw new InvalidOperationException(string.Join("; ", result.FailedMods));
                    }
                }, teardown: () => world.Dispose()));
        }

        private static void AddMarshallingCases(List<MicroCase> cases)
        {
            LuaValue number = new(42.5d);
            cases.Add(new MicroCase("marshalling", "LuaCsValueMarshaller.Box(number)", ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    object boxed = LuaCsValueMarshaller.Box(number);
                    sum += boxed == null ? 0 : 1;
                }

                return sum;
            }, "the var-args seam boxes every LuaValue it hands to host code"));
            object hostNumber = 42.5d;
            cases.Add(new MicroCase("marshalling", "LuaCsValueMarshaller.Unbox(double)", ops =>
            {
                double sum = 0d;
                for (int i = 0; i < ops; i++)
                {
                    sum += LuaCsValueMarshaller.Unbox(hostNumber).Read<double>();
                }

                return (long)sum;
            }));

            LuaState state = null;
            LuaFunction identity = null;
            LuaCsScriptExecutionGuard seamGuard = null;
            LuaCsScriptState seamState = null;
            object[] seamArgs = { 1.5d };
            cases.Add(new MicroCase("marshalling", "IScriptExecutionGuard.Invoke(fn, 1 arg) -> 1 result", ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    object[] results = seamGuard.Invoke(seamState, identity, CancellationToken.None, seamArgs);
                    sum += results.Length;
                }

                return sum;
            }, "the seam the legacy hooks_on/hooks_every dispatch calls: LuaValue[] args + object[] results + a box per result",
                setup: () =>
                {
                    state = new LuaCsSecureEnvironment().Create();
                    identity = LoadFunction(state, "return function(x) return x end");
                    seamGuard = new LuaCsScriptExecutionGuard(new LuaCsExecutionGuard());
                    seamState = new LuaCsScriptState(state);
                }));
        }

        private static void AddGuardCases(List<MicroCase> cases)
        {
            LuaState state = null;
            LuaFunction empty = null;
            LuaFunction loop = null;
            LuaCsExecutionGuard guard = null;
            cases.Add(new MicroCase("guard", "LuaCsExecutionGuard.Execute(empty function)", ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    sum += guard.Execute(state, empty, CancellationToken.None).Length;
                }

                return sum;
            }, "fixed per-call cost of a guarded call: pooled hook, nested-run registry, heap baseline read, SetHook x2",
                setup: () =>
                {
                    state = new LuaCsSecureEnvironment().Create();
                    empty = LoadFunction(state, "return function() end");
                    loop = LoadFunction(state, NumericLoopFunction);
                    guard = new LuaCsExecutionGuard();
                }));
            cases.Add(new MicroCase("guard", "LuaCsExecutionGuard.Execute(numeric loop), per iteration", ops =>
            {
                LuaValue[] result = guard.Execute(state, loop, CancellationToken.None, new LuaValue(ops));
                return (long)result[0].Read<double>();
            }, "8 VM instructions per iteration; instructions/s = 8e9 / ns-per-op; production hook batch 4",
                setup: () =>
                {
                    state = new LuaCsSecureEnvironment().Create();
                    empty = LoadFunction(state, "return function() end");
                    loop = LoadFunction(state, NumericLoopFunction);
                    guard = new LuaCsExecutionGuard();
                }, fixedOps: 400_000));

            LuaState handleState = null;
            LuaCsCoroutineHandle yielder = null;
            cases.Add(new MicroCase("guard", "LuaCsCoroutineHandle.Resume, immediate yield", ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    sum += yielder.Resume().Length;
                }

                return sum;
            }, "fixed per-resume cost of a scheduler thread: count-1 hook, heap baseline read per resume, SetHook x2",
                setup: () =>
                {
                    handleState = new LuaCsSecureEnvironment().Create();
                    LuaFunction body = LoadFunction(handleState,
                        "return function() while true do coroutine.yield() end end");
                    yielder = new LuaCsCoroutineHandle(handleState, body,
                        totalLifetimeSteps: LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
                }));

            LuaCsCoroutineHandle looper = null;
            cases.Add(new MicroCase("guard", "LuaCsCoroutineHandle.Resume(numeric loop), per iteration", ops =>
            {
                LuaValue[] yielded = looper.Resume(new LuaValue(ops));
                return yielded.Length > 0 ? (long)yielded[0].Read<double>() : 0;
            }, "the scheduler-thread hook fires on EVERY instruction (count 1); compare with the batch-4 guard row",
                setup: () =>
                {
                    LuaState loopState = new LuaCsSecureEnvironment().Create();
                    LuaFunction body = LoadFunction(loopState,
                        "return function(n)\n" +
                        "  while true do\n" +
                        "    local acc = 0.0\n" +
                        "    for i = 1, n do\n" +
                        "      acc = acc + i * 3 - (i % 7) + (i * i) % 101\n" +
                        "    end\n" +
                        "    n = coroutine.yield(acc)\n" +
                        "  end\n" +
                        "end");
                    looper = new LuaCsCoroutineHandle(loopState, body, budgetPerResume: 50_000_000,
                        resumeTimeoutMs: 120_000, totalLifetimeSteps: LuaCsCoroutineHandle.UnlimitedLifetimeSteps);
                }, fixedOps: 400_000));
        }

        private static void AddSchedulerCases(List<MicroCase> cases)
        {
            HeadlessWorld idle = null;
            cases.Add(new MicroCase("scheduler", "Advance, idle world", ops =>
            {
                for (int i = 0; i < ops; i++)
                {
                    idle.World.AdvanceFrame(1d / 60d);
                }

                return idle.Counters.ThreadResumes;
            }, setup: () => idle = HeadlessWorld.Create(), teardown: () => idle.Dispose()));

            foreach (int parts in new[] { 2048, 16384 })
            {
                HeadlessWorld world = null;
                int partCount = parts;
                cases.Add(new MicroCase("scheduler", "Advance, " + parts + " static Parts", ops =>
                {
                    for (int i = 0; i < ops; i++)
                    {
                        world.World.AdvanceFrame(1d / 60d);
                    }

                    return world.World.Registry.Count;
                }, "ProcessPreSimulation visits Models only (M1-36), so this should match the idle row",
                    setup: () =>
                    {
                        world = HeadlessWorld.Create();
                        world.World.BuildStaticGrid(partCount);
                    },
                    teardown: () => world.Dispose()));
            }

            foreach (int threads in new[] { 64, 200 })
            {
                HeadlessWorld parked = null;
                int threadCount = threads;
                cases.Add(new MicroCase("scheduler", "Advance, " + threads + " threads parked in task.wait(3600)", ops =>
                {
                    for (int i = 0; i < ops; i++)
                    {
                        parked.World.AdvanceFrame(1d / 60d);
                    }

                    return parked.World.Bindings.Scheduler.LiveThreadCount;
                }, setup: () =>
                {
                    parked = HeadlessWorld.Create();
                    parked.World.LoadMod("perf-parked", BuildThreads(threadCount, "task.wait(3600)"), null);
                    RequireLoaded(parked, "perf-parked");
                }, teardown: () => parked.Dispose()));

                HeadlessWorld awake = null;
                cases.Add(new MicroCase("scheduler", "Advance, " + threads + " threads resumed every frame (task.wait())", ops =>
                {
                    for (int i = 0; i < ops; i++)
                    {
                        awake.World.AdvanceFrame(1d / 60d);
                    }

                    return awake.Counters.ThreadResumes;
                }, "one op = one frame with " + threads + " resumes",
                    setup: () =>
                    {
                        awake = HeadlessWorld.Create();
                        awake.World.LoadMod("perf-awake", BuildThreads(threadCount, "task.wait()"), null);
                        RequireLoaded(awake, "perf-awake");
                    }, teardown: () => awake.Dispose()));
            }
        }

        private static void AddLegacyDispatchCases(List<MicroCase> cases)
        {
            HeadlessWorld world = null;
            LuaCsModRuntime runtime = null;
            cases.Add(new MicroCase("dispatch", "events_emit -> hooks_on handler via LuaCsModRuntime.Tick", ops =>
            {
                for (int i = 0; i < ops; i++)
                {
                    runtime.EmitEvent("perf.ping", "p");
                    runtime.Tick(0.016d);
                }

                return ops;
            }, "per event: handlers.ToArray, params object[], envelope closure + string concat, seam Invoke (finding MEDIUM-1)",
                setup: () =>
                {
                    world = HeadlessWorld.Create();
                    runtime = world.World.Stack.Runtime;
                    runtime.LoadMod("perf-legacy-events",
                        "local n = 0\nhooks_on('perf.ping', function(name, payload) n = n + 1 end)");
                }, teardown: () => world.Dispose()));

            HeadlessWorld timerWorld = null;
            LuaCsModRuntime timerRuntime = null;
            cases.Add(new MicroCase("dispatch", "hooks_every timer via LuaCsModRuntime.Tick", ops =>
            {
                for (int i = 0; i < ops; i++)
                {
                    timerRuntime.Tick(LuaCsModRuntime.MinTimerIntervalSeconds);
                }

                return ops;
            }, "one timer invocation per op",
                setup: () =>
                {
                    timerWorld = HeadlessWorld.Create();
                    timerRuntime = timerWorld.World.Stack.Runtime;
                    timerRuntime.LoadMod("perf-legacy-timer",
                        "local n = 0\nhooks_every(0.05, function() n = n + 1 end)");
                }, teardown: () => timerWorld.Dispose()));
        }

        private static void AddWorldCaptureCases(List<MicroCase> cases)
        {
            foreach (int parts in new[] { 2048, 16384 })
            {
                HeadlessWorld world = null;
                int partCount = parts;
                cases.Add(new MicroCase("edit-gate", "world capture (Capture), " + parts + " Parts", ops =>
                {
                    long sum = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        RbxWorldPackagePayload payload = RbxWorldPackageSerializer.Capture(world.CaptureContext());
                        sum += payload.Tree.Instances.Count;
                    }

                    return sum;
                }, "the synchronous capture ConfirmedWorldMutationGate runs before every AI edit",
                    setup: () =>
                    {
                        world = HeadlessWorld.Create();
                        world.World.BuildStaticGrid(partCount);
                    }, teardown: () => world.Dispose(), fixedOps: 1));

                HeadlessWorld encodeWorld = null;
                RbxWorldPackagePayload captured = null;
                cases.Add(new MicroCase("edit-gate", "world package encode (WritePackage), " + parts + " Parts", ops =>
                {
                    long sum = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        sum += RbxWorldPackageSerializer.WritePackage(captured).Length;
                    }

                    return sum;
                }, "the encode the durable autosave write performs (file I/O not included)",
                    setup: () =>
                    {
                        encodeWorld = HeadlessWorld.Create();
                        encodeWorld.World.BuildStaticGrid(partCount);
                        captured = RbxWorldPackageSerializer.Capture(encodeWorld.CaptureContext());
                    }, teardown: () => encodeWorld.Dispose(), fixedOps: 1));

                HeadlessWorld proofWorld = null;
                RbxWorldPackagePayload proofPayload = null;
                cases.Add(new MicroCase("edit-gate", "write-behind pre-check (TryProveEncodable), " + parts + " Parts", ops =>
                {
                    long sum = 0;
                    for (int i = 0; i < ops; i++)
                    {
                        if (!RbxWorldPackageSerializer.TryProveEncodable(
                                proofPayload, RbxWorldPackageLimits.Format, out long bound))
                        {
                            throw new InvalidOperationException("the reference world was not proven encodable");
                        }

                        sum += bound;
                    }

                    return sum;
                }, "what a write-behind autosave costs in front of the edit when the encode is deferred behind it",
                    setup: () =>
                    {
                        proofWorld = HeadlessWorld.Create();
                        proofWorld.World.BuildStaticGrid(partCount);
                        proofPayload = RbxWorldPackageSerializer.Capture(proofWorld.CaptureContext());
                    }, teardown: () => proofWorld.Dispose(), fixedOps: 1));
            }
        }

        private static void AddSseCases(List<MicroCase> cases)
        {
            const string line =
                "data: {\"id\":\"chatcmpl-1\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"m\"," +
                "\"choices\":[{\"index\":0,\"delta\":{\"content\":\"hello\"},\"finish_reason\":null}]}";
            cases.Add(new MicroCase("llm", "SSE content delta parse (ParseSseLine)", ops =>
            {
                long sum = 0;
                for (int i = 0; i < ops; i++)
                {
                    sum += MeaiOpenAiChatClient.ParseSseLineForTests(line) == null ? 0 : 1;
                }

                return sum;
            }, "one streamed token through ParseSseLineForTests, which builds a fresh accumulator per call"));

            foreach (bool fastPath in new[] { false, true })
            {
                bool usesFastPath = fastPath;
                List<string> stream = new();
                cases.Add(new MicroCase("llm", "SSE content delta in a streamed attempt, " + (fastPath ? "fast path" : "full JObject parse"), ops =>
                {
                    if (stream.Count != ops)
                    {
                        stream.Clear();
                        for (int i = 0; i < ops; i++)
                        {
                            stream.Add(line);
                        }
                    }

                    return MeaiOpenAiChatClient.ParseSseLinesForTests(stream, usesFastPath).Count;
                }, "one accumulator per attempt, as the streaming loop uses it; one op = one token line"));
            }
        }

        /// <summary>A Lua chunk that connects <paramref name="handlers"/> trivial Heartbeat handlers.</summary>
        public static string BuildCounterHandlers(int handlers)
        {
            return "local RunService = game:GetService('RunService')\n" +
                   "local n = 0\n" +
                   "for h = 1, " + handlers + " do\n" +
                   "  RunService.Heartbeat:Connect(function(dt) n = n + 1 end)\n" +
                   "end";
        }

        private static string BuildThreads(int count, string wait)
        {
            return "for i = 1, " + count + " do\n" +
                   "  task.spawn(function() while true do " + wait + " end end)\n" +
                   "end";
        }

        private static void RequireLoaded(HeadlessWorld world, string id)
        {
            if (!world.World.Stack.Runtime.IsLoaded(world.World.Actor, id))
            {
                throw new InvalidOperationException("mod '" + id + "' did not load");
            }
        }

        private static LuaFunction LoadFunction(LuaState state, string source)
        {
            LuaClosure chunk = state.Load(source, "perfbench");
            LuaValue[] results = state.ExecuteAsync(chunk, CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return results[0].Read<LuaFunction>();
        }
    }

    /// <summary>An in-memory world (no engine): the registry, Part store and mod stack the scenes use.</summary>
    internal sealed class HeadlessWorld : IDisposable
    {
        private HeadlessWorld(InstanceRegistry registry, RbxDataModel game, InMemoryPartPropertySink sink,
            PerfRuntimeCounters counters)
        {
            Counters = counters;
            Sink = sink;
            World = new PerfModWorld(registry, game, sink, counters);
        }

        public PerfModWorld World { get; }

        public PerfRuntimeCounters Counters { get; }

        public InMemoryPartPropertySink Sink { get; }

        public static HeadlessWorld Create()
        {
            InstanceRegistry registry = new(binder: new InMemoryInstanceBackingBinder());
            RbxDataModel game = DataModelBootstrap.CreateGame(registry);
            return new HeadlessWorld(registry, game, new InMemoryPartPropertySink(registry), new PerfRuntimeCounters());
        }

        public RbxWorldPackageCaptureContext CaptureContext()
        {
            return new RbxWorldPackageCaptureContext(World.Registry, World.Game, Sink, new RbxWorldSettings());
        }

        public void Dispose()
        {
            World.Dispose();
        }
    }
}
