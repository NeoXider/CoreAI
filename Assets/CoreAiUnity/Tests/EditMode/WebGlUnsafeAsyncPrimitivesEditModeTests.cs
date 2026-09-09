using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Forbids, in code that ships into a Unity WebGL player, the async primitives that do not work
    /// there at all. Scanned area: the portable core <c>Assets/CoreAI/Runtime</c>, the Unity layer
    /// <c>Assets/CoreAiUnity/Runtime</c> and the mods layer <c>Assets/CoreAIMods/Runtime</c>.
    /// <para>
    /// <b>Why a guard and not "remember".</b> WebGL has neither a thread pool nor a
    /// <c>System.Threading.Timer</c>. A continuation handed to the pool
    /// (<c>RunContinuationsAsynchronously</c> consumed by a context-free awaiter, <c>Task.Run</c>) never
    /// runs there, and <c>CancelAfter</c>/<c>Task.Delay</c> simply never fire. The failure is SILENT: not
    /// an exception, an eternal wait. This defect class was fixed in this repository three times (the SSE
    /// transport, request timeouts, tool drain plus per-call timeout) and came back every time, because
    /// nothing caught it.
    /// </para>
    /// <para>
    /// <b>Blocking waits are here too.</b> The same rule covers the other half of the family: a wait that
    /// parks the calling thread (<c>.Wait()</c>, <c>Monitor.Wait</c>, <c>WaitOne</c>, <c>Thread.Sleep</c>,
    /// <c>Task.WaitAll/WaitAny</c>, <c>.GetAwaiter().GetResult()</c>, <c>).Result</c>). On a desktop player
    /// that is a stutter; on the single thread of a WebGL player it is a permanent freeze, because the
    /// continuation that would release the wait can only run on the very thread that is parked.
    /// </para>
    /// <para>
    /// <b>What this guard does not replace.</b> The CAIU001 analyzer is about <c>ConfigureAwait</c>, and
    /// <see cref="CoreAiWebGlAsyncGuardEditModeTests"/> is about <c>UniTask.SwitchToThreadPool</c> and one
    /// specific loop in <c>LlmClientRegistry</c>. Neither sees these primitives.
    /// </para>
    /// <para>
    /// <b>What is not a violation.</b> Comments and string literals are stripped before the scan, and
    /// preprocessor branches unreachable in a WebGL player (<c>#if UNITY_EDITOR</c>, the <c>#else</c> of
    /// <c>#if UNITY_WEBGL &amp;&amp; !UNITY_EDITOR</c> and the like) are skipped whole: an explicit
    /// platform fork IS the right answer, not a violation.
    /// </para>
    /// <para>
    /// <b>Exceptions carry a CLAIM, not just prose.</b> Every entry in <see cref="Allowlist"/> names a
    /// <see cref="Claim"/> kind, and most claim kinds are verified mechanically by the tests below. This
    /// exists because prose rots silently: the entries for the timeout / streaming-retry decorators used
    /// to say "not on the browser-verified path", while <c>LlmPipelineInstaller</c> wrapped the routing
    /// client in exactly those two decorators, so every WebGL chat request went through them. A stale
    /// justification must go red, not stay quiet.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class WebGlUnsafeAsyncPrimitivesEditModeTests
    {
        /// <summary>A primitive that is dead in a WebGL player. Exceptions key on the id, not on regex text.</summary>
        private enum Primitive
        {
            PoolContinuations,
            CancelAfter,
            TaskDelay,
            TaskRun,
            ConfigureAwaitFalse,
            BlockingWait,
            BlockingWaitTimeout,
            MonitorWait,
            WaitHandle,
            ThreadSleep,
            TaskWaitAllAny,
            GetAwaiterGetResult,
            TaskResultProperty
        }

        private static readonly (Primitive Id, Regex Rx, string Why)[] Forbidden =
        {
            (Primitive.PoolContinuations,
                new Regex(@"\bRunContinuationsAsynchronously\b", RegexOptions.Compiled),
                "forbids inline resumption; a context-free awaiter (Task.WhenAny above all) then hands the " +
                "continuation to a thread pool WebGL does not have"),
            (Primitive.CancelAfter,
                new Regex(@"\.CancelAfter\s*\(", RegexOptions.Compiled),
                "relies on System.Threading.Timer - never fires in WebGL, so the deadline is a fiction"),
            (Primitive.TaskDelay,
                new Regex(@"\bTask\.Delay\s*\(", RegexOptions.Compiled),
                "same timer; a delay must be scheduled by the host (ILlmAsyncMarshaler.DelayAsync) or UniTask.Delay"),
            (Primitive.TaskRun,
                new Regex(@"\bTask\.Run\s*\(", RegexOptions.Compiled),
                "needs a thread pool, which WebGL does not have"),
            (Primitive.ConfigureAwaitFalse,
                new Regex(@"\.ConfigureAwait\s*\(\s*false\s*\)", RegexOptions.Compiled),
                "under UnitySynchronizationContext the continuation is declared non-inlinable and queued to " +
                "the thread pool - absent in WebGL, so the wait is eternal (7.0.5 ToolExecutionPolicy, " +
                "7.3.1 execute_lua path, and the whole LLM decorator chain)"),
            (Primitive.BlockingWait,
                new Regex(@"\.Wait\s*\(\s*\)", RegexOptions.Compiled),
                "parks the calling thread; on the single WebGL thread the completion that would release it " +
                "can never run. Use WaitAsync, or the Wait(0)-or-throw pattern the other stores use"),
            (Primitive.BlockingWaitTimeout,
                new Regex(@"\.Wait\s*\(\s*(?:TimeSpan\.|[1-9])", RegexOptions.Compiled),
                "a bounded block is still a block: it freezes the frame for the whole timeout and cannot " +
                "succeed at all on a single thread. Wait(0) is the non-blocking probe"),
            (Primitive.MonitorWait,
                new Regex(@"\bMonitor\.Wait\s*\(", RegexOptions.Compiled),
                "releases the lock and parks the thread until another THREAD pulses it; there is no other thread"),
            (Primitive.WaitHandle,
                new Regex(@"\.WaitOne\s*\(", RegexOptions.Compiled),
                "kernel wait handle: blocks the only thread a WebGL player has"),
            (Primitive.ThreadSleep,
                new Regex(@"\bThread\.Sleep\s*\(", RegexOptions.Compiled),
                "freezes the frame outright; a delay belongs on the host loop"),
            (Primitive.TaskWaitAllAny,
                new Regex(@"\bTask\.Wait(?:All|Any)\s*\(", RegexOptions.Compiled),
                "the blocking siblings of WhenAll/WhenAny - use the awaitable ones"),
            (Primitive.GetAwaiterGetResult,
                new Regex(@"\.GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(", RegexOptions.Compiled),
                "synchronous unwrap of an awaitable: deadlocks on a captured context and cannot ever " +
                "complete when the awaited work needs the same thread"),
            (Primitive.TaskResultProperty,
                new Regex(@"\)\s*\.Result\b", RegexOptions.Compiled),
                "blocking read of a task result straight off a call. A DTO property named Result reads " +
                "`value.Result`, never `Foo().Result`, so this shape is a task and only a task")
        };

        private static readonly string[] ScannedRoots =
        {
            "Assets/CoreAI/Runtime",
            "Assets/CoreAiUnity/Runtime",
            // A blind spot closed by the G11 §6.5 QA pass: the CoreAI.Mods assembly also ships into the
            // WebGL player and carries the whole Lua/Rbx contour the gate exercises in the browser.
            "Assets/CoreAIMods/Runtime"
        };

        /// <summary>
        /// Why an exception is allowed to exist. Most kinds are checked by a test, so an entry cannot
        /// keep claiming something that stopped being true.
        /// </summary>
        private enum Claim
        {
            /// <summary>
            /// The primitive IS the portable default behind a host hook that a Unity host overrides.
            /// Prose only - there is nothing mechanical to compare it against.
            /// </summary>
            PortableDefaultBehindHostHook,

            /// <summary>
            /// The type is never constructed by production runtime code.
            /// CHECKED by <see cref="Allowlist_ProductionGraphClaims_AreTrue"/>.
            /// </summary>
            NotConstructedInProduction,

            /// <summary>
            /// Every production construction of the type sits in a preprocessor branch unreachable in a
            /// WebGL player. CHECKED by <see cref="Allowlist_ProductionGraphClaims_AreTrue"/>.
            /// </summary>
            ConstructedOnlyOffWebGl,

            /// <summary>
            /// The pool-continuation flag is harmless because every awaiter in the file captures the host
            /// context: the file contains no WebGL-reachable <c>ConfigureAwait(false)</c> and no
            /// <c>Task.WhenAny</c> (whose internal continuation captures nothing).
            /// CHECKED by <see cref="Allowlist_HostContextClaims_AreTrue"/>.
            /// </summary>
            PoolContinuationsAwaitedOnHostContext,

            /// <summary>
            /// Argued safe and pinned by the test named in the reason.
            /// CHECKED by <see cref="Allowlist_PinnedByTestClaims_NameALiveTest"/>.
            /// </summary>
            PinnedByTest,

            /// <summary>
            /// No safety argument at all: a known risk with an owner. The reason must open with
            /// <see cref="RiskMarker"/> so nobody mistakes it for a proof.
            /// CHECKED by <see cref="Allowlist_RiskEntries_SaySoOutLoud"/>.
            /// </summary>
            RiskAcceptedNotEnforced
        }

        /// <summary>Mandatory opening of a <see cref="Claim.RiskAcceptedNotEnforced"/> reason.</summary>
        private const string RiskMarker = "NOT PROVEN SAFE:";

        private readonly struct Allowed
        {
            public Allowed(Claim claim, string reason)
            {
                Kind = claim;
                Reason = reason;
            }

            public Claim Kind { get; }
            public string Reason { get; }
        }

        /// <summary>
        /// Frozen exceptions: "file + primitive -> claim + reason". The key is the primitive id, not the
        /// regex text, so editing a pattern does not kill the whole list.
        /// <para>
        /// <b>Guard boundary.</b> There is no occurrence counter here, so one entry covers the whole file
        /// for that primitive: another identical occurrence added to an already-listed file slips past.
        /// That is a deliberate trade - a counter would go red on every edit above it in the file. The
        /// guarantee reads: no new occurrence in a CLEAN file gets through.
        /// </para>
        /// </summary>
        private static readonly Dictionary<(string Path, Primitive Id), Allowed> Allowlist = new()
        {
            [("Assets/CoreAI/Runtime/Core/ILlmAsyncMarshaler.cs", Primitive.TaskDelay)] = new(
                Claim.PortableDefaultBehindHostHook,
                "this Task.Delay IS the portable default body of the DelayAsync hook; a host with a frame " +
                "loop overrides it (UnityMainThreadLlmAsyncMarshaler), which is what makes WebGL work"),

            [("Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiTransport.cs", Primitive.CancelAfter)] = new(
                Claim.ConstructedOnlyOffWebGl,
                "the HttpClient transport is built only in non-WebGL preprocessor branches " +
                "(MeaiLlmClient #else, MeaiOpenAiChatClient #if !UNITY_WEBGL || UNITY_EDITOR); the browser " +
                "gets FetchSse/UnityWebRequest instead"),

            [("Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs", Primitive.TaskDelay)] = new(
                Claim.PinnedByTest,
                "VERIFIED in a browser 2026-09-02: retries and the read timeout go through HostDelayAsync " +
                "-> ILlmAsyncMarshaler.DelayAsync (the Unity installer sets DefaultAsyncMarshaler); the " +
                "remaining Task.Delay is the portable fallback inside HostDelayAsync. Pinned by " +
                "MeaiOpenAiChatClientWebGlDelayEditModeTests"),

            [("Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs", Primitive.PoolContinuations)] = new(
                Claim.PoolContinuationsAwaitedOnHostContext,
                "the AsyncChunkQueue wake-up promise, introduced as the WebGL FIX that replaced " +
                "SemaphoreSlim.WaitAsync. The reader awaits it directly, so the awaiter captured the host " +
                "SynchronizationContext and banning inline resumption means a post into the Unity loop. " +
                "Do NOT drop the flag naively: these sources settle next to a shared lock"),

            [("Assets/CoreAI/Runtime/Core/Features/AgentMemory/AgentConfigExtensions.cs", Primitive.PoolContinuations)] = new(
                Claim.PoolContinuationsAwaitedOnHostContext,
                "the role-registration promise is settled UNDER the role table lock, so inline resumption " +
                "would run waiter code inside that lock. Every waiter awaits it directly instead of racing " +
                "it through Task.WhenAny, which is what makes the flag safe in a browser"),

            [("Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/UnityWebRequestOpenAiTransport.cs",
                Primitive.PoolContinuations)] = new(
                Claim.PoolContinuationsAwaitedOnHostContext,
                "the UnityWebRequest completion promise. The previous reason - 'the browser uses " +
                "FetchSseOpenAiTransport instead' - was FALSE: on WebGL MeaiLlmClient composes " +
                "FetchSse WITH UnityWebRequest so non-streaming completions keep working. The flag is safe " +
                "for the real reason: AwaitCompletionAsync awaits the promise directly on the host context"),

            [("Assets/CoreAI/Runtime/Core/Features/Orchestration/ScriptedLlmClient.cs", Primitive.TaskDelay)] = new(
                Claim.NotConstructedInProduction,
                "test double: Task.Delay(0) as a yield point, never constructed by production code"),

            [("Assets/CoreAI/Runtime/Core/Features/Orchestration/ScriptedLlmClient.cs", Primitive.ConfigureAwaitFalse)] = new(
                Claim.NotConstructedInProduction,
                "test double: never constructed by production code"),

            [("Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiReadinessProbe.cs", Primitive.ConfigureAwaitFalse)] = new(
                Claim.NotConstructedInProduction,
                "the HttpClient readiness probe is never constructed by production code; the installer " +
                "registers UnityWebRequestOpenAiReadinessProbe"),

            [("Assets/CoreAIMods/Runtime/Diagnostics/G10/G10MeasurementComposition.cs", Primitive.TaskDelay)] = new(
                Claim.NotConstructedInProduction,
                "the G10 measurement rig: compiled into the WebGL build but never constructed outside its " +
                "own Diagnostics/G10 folder"),

            [("Assets/CoreAIMods/Runtime/Diagnostics/G10/G10MeasurementRunner.cs", Primitive.TaskDelay)] = new(
                Claim.NotConstructedInProduction,
                "the same G10 rig, the same conditions"),

            // ---- Risks that are NOT claimed to be safe. Each has an owner and an open task. ----

            [("Assets/CoreAI/Runtime/Core/Features/AgentMemory/IAgentMemoryStore.cs", Primitive.ConfigureAwaitFalse)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " agent memory/context contour, owned by the open memory-boundary task. The " +
                "earlier reason ('not on the browser-verified G11 path') was not a proof - these extensions " +
                "run on every turn that touches memory"),

            [("Assets/CoreAI/Runtime/Core/Features/AgentMemory/LlmAssistedConversationContextManager.cs", Primitive.ConfigureAwaitFalse)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " agent memory/context contour, owned by the open memory-boundary task"),

            [("Assets/CoreAI/Runtime/Core/Features/AgentMemory/SelectingConversationContextManager.cs", Primitive.ConfigureAwaitFalse)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " agent memory/context contour, owned by the open memory-boundary task; it IS " +
                "constructed in production by ConversationContextManagerFactories"),

            [("Assets/CoreAI/Runtime/Core/Features/AgentMemory/InventoryTool.cs", Primitive.ConfigureAwaitFalse)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " tool body of the same class as execute_lua before 7.3.1; fixed after the " +
                "LuaTool pattern (drop ConfigureAwait(false) + MeaiToolTaskBridge.Publish at the MEAI border)"),

            [("Assets/CoreAI/Runtime/Core/Features/AgentMemory/MemoryTool.cs", Primitive.ConfigureAwaitFalse)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " tool body of the same class as execute_lua before 7.3.1; fixed after the " +
                "LuaTool pattern (drop ConfigureAwait(false) + MeaiToolTaskBridge.Publish at the MEAI border)"),

            // The ConfigureAwaitFalse exception for this file is GONE ON PURPOSE: all 17 occurrences were
            // removed and the file now carries the rule in its own header. Keeping the entry would have left
            // standing permission for a primitive nobody uses any more — and permission outlives the reason
            // it was granted unless someone takes it away.
            [("Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs",
                Primitive.PoolContinuations)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " one TaskCompletionSource still asks for asynchronous continuations. WebGL has " +
                "no pool to run them on, so whoever awaits that promise resumes nowhere. The reason this " +
                "entry used to give (ConfigureAwait(false) elsewhere in the file) no longer exists, but the " +
                "flag does; it belongs to the open memory-boundary task with the blocking waits below"),

            [("Assets/CoreAiUnity/Runtime/Source/Features/AgentMemory/Infrastructure/FileAgentMemoryStore.cs",
                Primitive.BlockingWait)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " the synchronous store API parks on the SAME SemaphoreSlim its async siblings " +
                "hold across an await (TryLoadDetailed, Save, Clear, AppendChatMessage, ...). On the single " +
                "WebGL thread the release can then never run. Every other store in the repo uses the " +
                "Wait(0)-or-throw pattern; this one is the exception and belongs to the open " +
                "memory-boundary task"),

            [("Assets/CoreAI/Runtime/Core/Features/Llm/ISkillStore.cs", Primitive.MonitorWait)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " SkillOperationGate.EnterSync parks a second SYNCHRONOUS holder. It is " +
                "unreachable on a single-threaded player (a lease held by an async user throws one line " +
                "above, and same-thread re-entry is counted), but that is an argument about the platform, " +
                "not an enforced invariant, and concurrent EditMode tests do rely on the parking"),

            [("Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsExecutionGuard.cs", Primitive.GetAwaiterGetResult)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " the Lua-CSharp bridge assumes a well-behaved handler reaches coroutine.yield " +
                "synchronously, so GetResult returns without parking, and arms an instruction/time budget " +
                "for a runaway. That is a correctness invariant, not an enforcement: a genuinely async " +
                "LuaFunction would freeze the WASM player loop"),

            [("Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsSecureEnvironment.cs", Primitive.GetAwaiterGetResult)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " same bridge, same synchronous-completion assumption (coroutine resume and " +
                "the string.format wrapper)"),

            [("Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsCoroutineHandle.cs", Primitive.GetAwaiterGetResult)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " same bridge; Resume relies on the documented synchronous yield plus the hook budget"),

            [("Assets/CoreAIMods/Runtime/Scripting/LuaCs/LuaCsRbxSignalRunner.cs", Primitive.GetAwaiterGetResult)] = new(
                Claim.RiskAcceptedNotEnforced,
                RiskMarker + " same bridge, and the least protected of the four: the call sits in a " +
                "constructor with CancellationToken.None and no hook budget armed around it")
        };

        [Test]
        public void WebGlReachableCode_DoesNotUseAsyncPrimitivesThatAreDeadInWebGl()
        {
            List<string> violations = new();
            int scannedFiles = 0;

            foreach (string root in ScannedRoots)
            {
                string absoluteRoot = ToAbsolute(root);
                Assert.IsTrue(Directory.Exists(absoluteRoot), $"Scan directory not found: {absoluteRoot}");

                foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
                {
                    scannedFiles++;
                    string relative = ToRelative(file);
                    foreach (Hit hit in Scan(File.ReadAllText(file)))
                    {
                        if (Allowlist.ContainsKey((relative, hit.Id)))
                        {
                            continue;
                        }

                        violations.Add($"{relative}({hit.Line}): {hit.Text} - {hit.Why}");
                    }
                }
            }

            Assert.Greater(scannedFiles, 0, "The scan found no files at all - the project path is broken.");
            Assert.IsEmpty(
                violations,
                "Code that ships into the WebGL player uses primitives that do not work there and produce " +
                "a silent eternal wait:\n" + string.Join("\n", violations));
        }

        /// <summary>
        /// A seeded violation: without it, breaking the scanner itself (a dead key, an eaten regex) would
        /// look like "no violations found", i.e. green.
        /// </summary>
        [Test]
        public void Scanner_FindsSeededViolations_AndIgnoresCommentsStringsAndPlatformBranches()
        {
            List<Hit> seeded = Scan(SeededViolationProbe);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    Primitive.PoolContinuations, Primitive.CancelAfter, Primitive.TaskDelay, Primitive.TaskRun,
                    Primitive.ConfigureAwaitFalse, Primitive.BlockingWait, Primitive.BlockingWaitTimeout,
                    Primitive.MonitorWait, Primitive.WaitHandle, Primitive.ThreadSleep,
                    Primitive.TaskWaitAllAny, Primitive.GetAwaiterGetResult, Primitive.TaskResultProperty
                },
                seeded.ConvertAll(h => h.Id),
                "The scanner must find every one of the forbidden primitives in the seeded code. Found: " +
                Describe(seeded));

            List<Hit> decoys = Scan(NonViolationProbe);
            Assert.IsEmpty(
                decoys,
                "The scanner fired on something that is not a violation (a comment, a string, UniTask, a " +
                "preprocessor branch unreachable in WebGL, Wait(0), or a DTO property named Result): " +
                Describe(decoys));

            // An unrecognized multiline literal eats a newline and shifts the numbering, and with it the
            // reachability map. Both prefix orders ($@ and @$) are legal C#.
            List<Hit> afterVerbatim = Scan("var a = $@\"line one\nline two\";\n" +
                                           "var b = @$\"line three\nline four\";\nTask.Run(x);\n");
            CollectionAssert.AreEquivalent(
                new[] { 5 },
                afterVerbatim.ConvertAll(h => h.Line),
                "A violation after multiline verbatim literals must stay on its own line: " +
                Describe(afterVerbatim));
        }

        /// <summary>
        /// The condition evaluator: precedence, parentheses and - above all - three-valued logic. Otherwise
        /// nothing holds precedence correct, and "unknown" under negation easily degenerates back into
        /// "false", at which point the guard goes silently blind over whole regions.
        /// </summary>
        [Test]
        public void PreprocessorEvaluator_ResolvesOnlyWhatItCanProve()
        {
            List<string> mismatches = new();
            foreach ((string condition, bool? expected) in EvaluatorCases)
            {
                bool? actual = EvaluateForWebGlPlayer(condition);
                if (actual != expected)
                {
                    mismatches.Add($"'{condition}': expected {Tri(expected)}, got {Tri(actual)}");
                }
            }

            Assert.IsEmpty(mismatches, "The preprocessor condition evaluator disagrees with expectations:\n" +
                                       string.Join("\n", mismatches));
        }

        /// <summary>
        /// Reachability marking at the directive level: <c>#if</c> / <c>#elif</c> / <c>#else</c> / nesting.
        /// A branch is dropped only on a provably false condition - an unknown define and the <c>#else</c>
        /// to it must stay under the scan.
        /// </summary>
        [Test]
        public void PreprocessorReachability_DropsOnlyBranchesProvenAbsentFromWebGl()
        {
            List<string> mismatches = new();
            foreach ((string name, string source, bool expected) in ReachabilityCases)
            {
                bool actual = Scan(source).Count > 0;
                if (actual != expected)
                {
                    mismatches.Add($"{name}: expected {(expected ? "seen" : "not seen")}, got " +
                                   $"{(actual ? "seen" : "not seen")}");
                }
            }

            Assert.IsEmpty(mismatches, "Reachability marking disagrees with expectations:\n" +
                                       string.Join("\n", mismatches));
        }

        /// <summary>
        /// Every exception must correspond to a match that actually exists. A rotten or mistyped entry is
        /// a hole visible only this way.
        /// </summary>
        [Test]
        public void Allowlist_HasNoStaleEntries()
        {
            List<string> stale = new();
            foreach (KeyValuePair<(string Path, Primitive Id), Allowed> entry in Allowlist)
            {
                if (string.IsNullOrWhiteSpace(entry.Value.Reason))
                {
                    stale.Add($"{entry.Key.Path} [{entry.Key.Id}]: exception without a reason");
                    continue;
                }

                string absolute = ToAbsolute(entry.Key.Path);
                if (!File.Exists(absolute))
                {
                    stale.Add($"{entry.Key.Path} [{entry.Key.Id}]: the file is gone");
                    continue;
                }

                if (!Scan(File.ReadAllText(absolute)).Exists(h => h.Id == entry.Key.Id))
                {
                    stale.Add($"{entry.Key.Path} [{entry.Key.Id}]: the primitive is gone from the file - drop the entry");
                }
            }

            Assert.IsEmpty(
                stale,
                "The frozen exception list drifted away from the code:\n" + string.Join("\n", stale));
        }

        /// <summary>
        /// Verifies the two claims that talk about the production object graph. THIS is the check the
        /// timeout / streaming-retry decorators failed: their entries claimed they were off the
        /// browser-verified path while <c>LlmPipelineInstaller</c> wrapped the routing client in both.
        /// <para>
        /// Evidence used: a production <c>new &lt;Type&gt;(</c> anywhere under the scanned roots, outside
        /// the file that declares the type. That proves a claim FALSE; it cannot prove one true (a type
        /// resolved purely through DI shows no <c>new</c>), and the reason text must carry that argument.
        /// </para>
        /// </summary>
        [Test]
        public void Allowlist_ProductionGraphClaims_AreTrue()
        {
            List<string> broken = new();
            foreach (KeyValuePair<(string Path, Primitive Id), Allowed> entry in Allowlist)
            {
                Claim kind = entry.Value.Kind;
                if (kind != Claim.NotConstructedInProduction && kind != Claim.ConstructedOnlyOffWebGl)
                {
                    continue;
                }

                string typeName = Path.GetFileNameWithoutExtension(entry.Key.Path);
                List<string> sites = FindProductionConstructionSites(typeName, entry.Key.Path,
                    kind == Claim.ConstructedOnlyOffWebGl);
                if (sites.Count > 0)
                {
                    broken.Add($"{entry.Key.Path} [{entry.Key.Id}] claims {kind}, but '{typeName}' is " +
                               $"constructed by production code at: {string.Join(", ", sites)}");
                }
            }

            Assert.IsEmpty(
                broken,
                "An exception claims the type is off the production path, and the code says otherwise. " +
                "Fix the code or restate the claim - do not widen the list:\n" + string.Join("\n", broken));
        }

        /// <summary>
        /// Positive control for the checker above. Without it, a broken regex or a wrong root would make
        /// <see cref="FindProductionConstructionSites"/> find nothing, and every production-graph claim
        /// would pass for free - the same "green because the scanner died" failure the seeded-violation
        /// test exists to prevent.
        /// <para>
        /// <c>TimeoutLlmClientDecorator</c> is the type whose FALSE claim started all this:
        /// <c>LlmPipelineInstaller</c> wraps the routing client in it, so every WebGL chat request goes
        /// through it. If it ever stops being composed, this control must be re-pointed deliberately.
        /// </para>
        /// </summary>
        [Test]
        public void ProductionGraphEvidence_SeesATypeThatIsActuallyComposed()
        {
            List<string> sites = FindProductionConstructionSites(
                "TimeoutLlmClientDecorator",
                "Assets/CoreAI/Runtime/Core/Features/Llm/TimeoutLlmClientDecorator.cs",
                onlyWebGlReachable: true);

            CollectionAssert.IsNotEmpty(
                sites,
                "The production-graph checker cannot see the decorator the WebGL chat chain actually " +
                "composes. While that is true, every claim it is supposed to verify passes for free.");
        }

        /// <summary>
        /// Verifies <see cref="Claim.PoolContinuationsAwaitedOnHostContext"/>: the argument is "every
        /// awaiter in this file captures the host context", so the file must contain neither a
        /// WebGL-reachable <c>ConfigureAwait(false)</c> nor a <c>Task.WhenAny</c> - WhenAny's own internal
        /// continuation captures nothing, which is exactly how a pool-bound continuation sneaks back in.
        /// </summary>
        [Test]
        public void Allowlist_HostContextClaims_AreTrue()
        {
            Regex whenAny = new(@"\bTask\.When(?:Any|All)\s*\(", RegexOptions.Compiled);
            List<string> broken = new();
            foreach (KeyValuePair<(string Path, Primitive Id), Allowed> entry in Allowlist)
            {
                if (entry.Value.Kind != Claim.PoolContinuationsAwaitedOnHostContext)
                {
                    continue;
                }

                string absolute = ToAbsolute(entry.Key.Path);
                if (!File.Exists(absolute))
                {
                    continue; // Allowlist_HasNoStaleEntries reports the missing file.
                }

                string code = StripCommentsAndStringLiterals(File.ReadAllText(absolute));
                int[] lineStarts = BuildLineStarts(code);
                bool[] reachable = BuildWebGlReachability(code, lineStarts);

                if (Scan(File.ReadAllText(absolute)).Exists(h => h.Id == Primitive.ConfigureAwaitFalse))
                {
                    broken.Add($"{entry.Key.Path}: claims the host-context defence but still contains a " +
                               "WebGL-reachable ConfigureAwait(false)");
                }

                foreach (Match match in whenAny.Matches(code))
                {
                    if (reachable[LineOf(lineStarts, match.Index) - 1])
                    {
                        broken.Add($"{entry.Key.Path}({LineOf(lineStarts, match.Index)}): claims the " +
                                   "host-context defence but races the promise through " +
                                   $"{match.Value.Trim()} - that continuation captures no context");
                        break;
                    }
                }
            }

            Assert.IsEmpty(
                broken,
                "A pool-continuation exception rests on 'awaited on the host context', and the file no " +
                "longer honours it:\n" + string.Join("\n", broken));
        }

        /// <summary>
        /// Verifies <see cref="Claim.PinnedByTest"/>: the reason must name a test type that still exists.
        /// A pin nobody can find is prose.
        /// </summary>
        [Test]
        public void Allowlist_PinnedByTestClaims_NameALiveTest()
        {
            Regex testName = new(@"\b(\w+(?:EditMode|PlayMode)Tests)\b", RegexOptions.Compiled);
            List<string> broken = new();
            foreach (KeyValuePair<(string Path, Primitive Id), Allowed> entry in Allowlist)
            {
                if (entry.Value.Kind != Claim.PinnedByTest)
                {
                    continue;
                }

                Match match = testName.Match(entry.Value.Reason);
                if (!match.Success)
                {
                    broken.Add($"{entry.Key.Path} [{entry.Key.Id}]: claims PinnedByTest but names no test type");
                    continue;
                }

                if (FindTestFile(match.Groups[1].Value) == null)
                {
                    broken.Add($"{entry.Key.Path} [{entry.Key.Id}]: the pinning test " +
                               $"'{match.Groups[1].Value}' does not exist any more");
                }
            }

            Assert.IsEmpty(broken, "A PinnedByTest exception points at a test that is not there:\n" +
                                   string.Join("\n", broken));
        }

        /// <summary>
        /// Verifies <see cref="Claim.RiskAcceptedNotEnforced"/>: such a reason must open with
        /// <see cref="RiskMarker"/>, and no other claim kind may borrow the marker. This is the anti-lie
        /// rule made mechanical - an entry with no safety argument has to say so in its first words.
        /// </summary>
        [Test]
        public void Allowlist_RiskEntries_SaySoOutLoud()
        {
            List<string> broken = new();
            foreach (KeyValuePair<(string Path, Primitive Id), Allowed> entry in Allowlist)
            {
                bool marked = entry.Value.Reason.StartsWith(RiskMarker, StringComparison.Ordinal);
                if (entry.Value.Kind == Claim.RiskAcceptedNotEnforced && !marked)
                {
                    broken.Add($"{entry.Key.Path} [{entry.Key.Id}]: an unproven risk must open with " +
                               $"'{RiskMarker}'");
                }
                else if (entry.Value.Kind != Claim.RiskAcceptedNotEnforced && marked)
                {
                    broken.Add($"{entry.Key.Path} [{entry.Key.Id}]: '{RiskMarker}' belongs only to " +
                               "RiskAcceptedNotEnforced");
                }
            }

            Assert.IsEmpty(broken, "An exception disagrees with its own claim kind:\n" +
                                   string.Join("\n", broken));
        }

        // ============ production graph evidence ============

        /// <summary>
        /// Production <c>new &lt;typeName&gt;(</c> sites outside <paramref name="declaringPath"/>. When
        /// <paramref name="onlyWebGlReachable"/> is set, sites in preprocessor branches unreachable in a
        /// WebGL player are not evidence - that is exactly what "constructed only off WebGL" means.
        /// </summary>
        private static List<string> FindProductionConstructionSites(string typeName, string declaringPath,
            bool onlyWebGlReachable)
        {
            Regex construction = new(@"\bnew\s+" + Regex.Escape(typeName) + @"\s*[\(\{]", RegexOptions.Compiled);
            List<string> sites = new();
            foreach (string root in ScannedRoots)
            {
                string absoluteRoot = ToAbsolute(root);
                if (!Directory.Exists(absoluteRoot))
                {
                    continue;
                }

                foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
                {
                    string relative = ToRelative(file);
                    if (string.Equals(relative, declaringPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string code = StripCommentsAndStringLiterals(File.ReadAllText(file));
                    int[] lineStarts = BuildLineStarts(code);
                    bool[] reachable = null;
                    foreach (Match match in construction.Matches(code))
                    {
                        int line = LineOf(lineStarts, match.Index);
                        if (onlyWebGlReachable)
                        {
                            reachable ??= BuildWebGlReachability(code, lineStarts);
                            if (!reachable[line - 1])
                            {
                                continue;
                            }
                        }

                        sites.Add($"{relative}({line})");
                    }
                }
            }

            return sites;
        }

        private static string FindTestFile(string testTypeName)
        {
            string assets = Application.dataPath;
            foreach (string file in Directory.EnumerateFiles(assets, testTypeName + ".cs",
                         SearchOption.AllDirectories))
            {
                return file;
            }

            return null;
        }

        // ============ scanner ============

        private readonly struct Hit
        {
            public Hit(Primitive id, int line, string text, string why)
            {
                Id = id;
                Line = line;
                Text = text;
                Why = why;
            }

            public Primitive Id { get; }
            public int Line { get; }
            public string Text { get; }
            public string Why { get; }
        }

        /// <summary>
        /// Matches in code reachable from the WebGL player: comments and string literals removed,
        /// unreachable preprocessor branches skipped.
        /// </summary>
        private static List<Hit> Scan(string source)
        {
            string code = StripCommentsAndStringLiterals(source);
            int[] lineStarts = BuildLineStarts(code);
            bool[] reachable = BuildWebGlReachability(code, lineStarts);

            List<Hit> hits = new();
            foreach ((Primitive id, Regex rx, string why) in Forbidden)
            {
                foreach (Match match in rx.Matches(code))
                {
                    int line = LineOf(lineStarts, match.Index);
                    if (!reachable[line - 1])
                    {
                        continue;
                    }

                    hits.Add(new Hit(id, line, match.Value.Trim(), why));
                }
            }

            return hits;
        }

        /// <summary>
        /// Strips <c>//</c> tails, <c>/* */</c> and literal bodies while preserving newlines (and with
        /// them line numbers) and preprocessor directives. Otherwise the guard trips over its own
        /// comments like "WHY NOT RunContinuationsAsynchronously" - i.e. over the very file it exists for.
        /// </summary>
        private static string StripCommentsAndStringLiterals(string text)
        {
            StringBuilder code = new(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n')
                        {
                            code.Append('\n');
                        }

                        i++;
                    }

                    i = Math.Min(i + 2, text.Length);
                    continue;
                }

                if (c == '\'')
                {
                    i = SkipCharLiteral(text, i);
                    continue;
                }

                int verbatimBody = VerbatimBodyStart(text, i);
                if (verbatimBody >= 0)
                {
                    i = SkipVerbatimString(text, verbatimBody, code);
                    continue;
                }

                if (c == '"')
                {
                    i = SkipRegularString(text, i + 1);
                    continue;
                }

                code.Append(c);
                i++;
            }

            return code.ToString();
        }

        /// <summary>
        /// Body index of a verbatim literal - <c>@"…"</c>, <c>$@"…"</c> and <c>@$"…"</c> (both orders are
        /// legal) - or <c>-1</c> when no literal starts here. An unrecognized multiline verbatim would be
        /// read as a plain string, eat a newline and shift the whole numbering.
        /// </summary>
        private static int VerbatimBodyStart(string text, int i)
        {
            if (i + 1 < text.Length && text[i] == '@' && text[i + 1] == '"')
            {
                return i + 2;
            }

            bool prefixed = i + 2 < text.Length && text[i + 2] == '"' &&
                            ((text[i] == '$' && text[i + 1] == '@') ||
                             (text[i] == '@' && text[i + 1] == '$'));
            return prefixed ? i + 3 : -1;
        }

        private static int SkipCharLiteral(string text, int start)
        {
            int i = start + 1;
            while (i < text.Length && text[i] != '\'' && text[i] != '\n')
            {
                i += text[i] == '\\' ? 2 : 1;
            }

            return Math.Min(i + 1, text.Length);
        }

        /// <summary>Skips <c>@"…"</c> (<c>""</c> is an escaped quote) while preserving newlines.</summary>
        private static int SkipVerbatimString(string text, int start, StringBuilder code)
        {
            int i = start;
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    break;
                }

                if (text[i] == '\n')
                {
                    code.Append('\n');
                }

                i++;
            }

            return Math.Min(i + 1, text.Length);
        }

        private static int SkipRegularString(string text, int start)
        {
            int i = start;
            while (i < text.Length && text[i] != '"' && text[i] != '\n')
            {
                i += text[i] == '\\' ? 2 : 1;
            }

            return Math.Min(i + 1, text.Length);
        }

        // ============ reachability from the WebGL player ============

        /// <summary>
        /// For every line: can it end up in the WebGL player. Conditions are evaluated three-valued with
        /// <c>UNITY_WEBGL = true</c>, <c>UNITY_EDITOR = false</c>; any other symbol is "unknown"
        /// (<c>null</c>).
        /// <para>
        /// A branch is dropped ONLY when the condition is provably false. The rule "unknown symbol = true"
        /// would be conservative for a positive occurrence and would invert under negation:
        /// <c>#if !COREAI_LLM</c> would be declared unreachable although it compiles in a build without
        /// that define, and the same would happen to the <c>#else</c> of any unknown condition - i.e. the
        /// guard would go blind exactly where the forbidden primitives already live.
        /// </para>
        /// </summary>
        private static bool[] BuildWebGlReachability(string code, int[] lineStarts)
        {
            bool[] reachable = new bool[lineStarts.Length];
            List<(bool Active, bool AnyBranchCertain)> stack = new();

            for (int lineIndex = 0; lineIndex < lineStarts.Length; lineIndex++)
            {
                string line = LineText(code, lineStarts, lineIndex).Trim();
                if (line.StartsWith("#if", StringComparison.Ordinal))
                {
                    bool? value = EvaluateForWebGlPlayer(line.Substring("#if".Length));
                    stack.Add((value != false, value == true));
                }
                else if (line.StartsWith("#elif", StringComparison.Ordinal) && stack.Count > 0)
                {
                    bool certain = stack[stack.Count - 1].AnyBranchCertain;
                    bool? value = EvaluateForWebGlPlayer(line.Substring("#elif".Length));
                    stack[stack.Count - 1] = (!certain && value != false, certain || value == true);
                }
                else if (line.StartsWith("#else", StringComparison.Ordinal) && stack.Count > 0)
                {
                    // #else is unreachable only when some branch above was taken WITH CERTAINTY.
                    bool certain = stack[stack.Count - 1].AnyBranchCertain;
                    stack[stack.Count - 1] = (!certain, true);
                }
                else if (line.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                bool active = true;
                foreach ((bool frameActive, bool _) in stack)
                {
                    active &= frameActive;
                }

                reachable[lineIndex] = active;
            }

            return reachable;
        }

        /// <summary>
        /// Evaluates a preprocessor condition for the WebGL player: <c>true</c> / <c>false</c> /
        /// <c>null</c> - "provably unknown". Supports <c>!</c>, <c>&amp;&amp;</c>, <c>||</c> and
        /// parentheses; anything not understood (unknown symbol, foreign operator, truncated expression)
        /// gives <c>null</c>, i.e. the branch stays under the scan.
        /// </summary>
        private static bool? EvaluateForWebGlPlayer(string condition)
        {
            List<string> tokens = Tokenize(condition);
            if (tokens == null)
            {
                return null;
            }

            int position = 0;
            bool? value = ParseOr(tokens, ref position);
            return position == tokens.Count ? value : null;
        }

        private static List<string> Tokenize(string condition)
        {
            List<string> tokens = new();
            int i = 0;
            while (i < condition.Length)
            {
                char c = condition[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '(' || c == ')' || c == '!')
                {
                    tokens.Add(c.ToString());
                    i++;
                }
                else if ((c == '&' || c == '|') && i + 1 < condition.Length && condition[i + 1] == c)
                {
                    tokens.Add(condition.Substring(i, 2));
                    i += 2;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < condition.Length && (char.IsLetterOrDigit(condition[i]) || condition[i] == '_'))
                    {
                        i++;
                    }

                    tokens.Add(condition.Substring(start, i - start));
                }
                else
                {
                    // An operator we do not parse (==, defined(...) and so on): the whole condition is "unknown".
                    return null;
                }
            }

            return tokens;
        }

        private static bool? ParseOr(List<string> tokens, ref int position)
        {
            bool? value = ParseAnd(tokens, ref position);
            while (position < tokens.Count && tokens[position] == "||")
            {
                position++;
                bool? right = ParseAnd(tokens, ref position);
                value = value == true || right == true ? true
                    : value == false && right == false ? false
                    : (bool?)null;
            }

            return value;
        }

        private static bool? ParseAnd(List<string> tokens, ref int position)
        {
            bool? value = ParseUnary(tokens, ref position);
            while (position < tokens.Count && tokens[position] == "&&")
            {
                position++;
                bool? right = ParseUnary(tokens, ref position);
                value = value == false || right == false ? false
                    : value == true && right == true ? true
                    : (bool?)null;
            }

            return value;
        }

        /// <summary>Always consumes at least one token, so parsing a malformed condition terminates.</summary>
        private static bool? ParseUnary(List<string> tokens, ref int position)
        {
            if (position >= tokens.Count)
            {
                return null;
            }

            string token = tokens[position++];
            if (token == "!")
            {
                return !ParseUnary(tokens, ref position);
            }

            if (token == "(")
            {
                bool? value = ParseOr(tokens, ref position);
                if (position < tokens.Count && tokens[position] == ")")
                {
                    position++;
                    return value;
                }

                return null;
            }

            switch (token)
            {
                case "UNITY_WEBGL":
                case "true":
                    return true;
                case "UNITY_EDITOR":
                case "false":
                    return false;
                case ")":
                case "&&":
                case "||":
                    return null;
                default:
                    // An unknown define (COREAI_LLM, UNITY_6000_5_OR_NEWER and the like) may be either on
                    // or off in a WebGL build - there is no provable answer.
                    return null;
            }
        }

        // ============ lines and paths ============

        private static int[] BuildLineStarts(string text)
        {
            List<int> starts = new() { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            return starts.ToArray();
        }

        /// <summary>Line number (one-based) for an offset - by binary search, not by counting from the start.</summary>
        private static int LineOf(int[] lineStarts, int index)
        {
            int found = Array.BinarySearch(lineStarts, index);
            return found >= 0 ? found + 1 : ~found;
        }

        private static string LineText(string text, int[] lineStarts, int lineIndex)
        {
            int start = lineStarts[lineIndex];
            int end = lineIndex + 1 < lineStarts.Length ? lineStarts[lineIndex + 1] : text.Length;
            return text.Substring(start, end - start).TrimEnd('\n', '\r');
        }

        private static string ToAbsolute(string relative)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ToRelative(string absolute)
        {
            string assets = Application.dataPath.Replace('\\', '/');
            string normalized = absolute.Replace('\\', '/');
            return normalized.StartsWith(assets, StringComparison.Ordinal)
                ? "Assets" + normalized.Substring(assets.Length)
                : normalized;
        }

        private static string Describe(List<Hit> hits)
        {
            if (hits.Count == 0)
            {
                return "(empty)";
            }

            return string.Join(", ", hits.ConvertAll(h => $"{h.Id}@{h.Line}:{h.Text}"));
        }

        private static string Tri(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "unknown";
        }

        // ============ seeded material for self-checking ============

        /// <summary>Preprocessor condition -> the provable answer for the WebGL player (<c>null</c> = "unknown").</summary>
        private static readonly (string Condition, bool? Expected)[] EvaluatorCases =
        {
            ("UNITY_WEBGL", true),
            ("UNITY_EDITOR", false),
            ("!UNITY_EDITOR", true),
            ("true", true),
            ("false", false),
            ("UNITY_WEBGL && !UNITY_EDITOR", true),
            ("!UNITY_WEBGL || UNITY_EDITOR", false),

            // An unknown define pretends to be neither truth nor falsehood - on its own or under "!".
            ("COREAI_LLM", null),
            ("!COREAI_LLM", null),

            // Real conditions from the repository.
            ("COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM", false),
            ("!COREAI_HAS_LLMUNITY || UNITY_WEBGL || !COREAI_LLM", true),

            // Parentheses and three-valued logic.
            ("(UNITY_WEBGL || COREAI_LLM) && UNITY_EDITOR", false),
            ("UNITY_WEBGL && (UNITY_EDITOR || COREAI_LLM)", null),
            ("!(UNITY_WEBGL)", false),
            ("!(UNITY_EDITOR && COREAI_LLM)", true),

            // "&&" binds tighter than "||": a flat left-to-right parse would answer false here.
            ("UNITY_WEBGL || UNITY_WEBGL && UNITY_EDITOR", true),
            ("UNITY_EDITOR && UNITY_WEBGL || UNITY_WEBGL", true),

            // Everything not understood is "unknown", not a silent falsehood.
            ("UNITY_WEBGL == 1", null),
            ("defined(UNITY_WEBGL)", null),
            ("UNITY_WEBGL &&", null),
            ("(UNITY_WEBGL", null),
            ("", null)
        };

        /// <summary>A source fragment -> does the scanner see the <c>Task.Run(</c> hidden in it.</summary>
        private static readonly (string Name, string Source, bool Expected)[] ReachabilityCases =
        {
            ("code without directives", "void M() { Task.Run(x); }\n", true),
            ("#if UNITY_EDITOR", "#if UNITY_EDITOR\nTask.Run(x);\n#endif\n", false),
            ("#else of #if UNITY_EDITOR", "#if UNITY_EDITOR\nNo();\n#else\nTask.Run(x);\n#endif\n", true),
            ("#if UNITY_WEBGL && !UNITY_EDITOR",
                "#if UNITY_WEBGL && !UNITY_EDITOR\nTask.Run(x);\n#endif\n", true),
            ("#else of the WebGL fork",
                "#if UNITY_WEBGL && !UNITY_EDITOR\nNo();\n#else\nTask.Run(x);\n#endif\n", false),
            ("#if UNKNOWN", "#if COREAI_LLM\nTask.Run(x);\n#endif\n", true),
            ("#if !UNKNOWN", "#if !COREAI_LLM\nTask.Run(x);\n#endif\n", true),
            ("#else of #if UNKNOWN", "#if COREAI_LLM\nNo();\n#else\nTask.Run(x);\n#endif\n", true),
            ("#if false", "#if false\nTask.Run(x);\n#endif\n", false),
            ("#elif, the taken branch", "#if UNITY_EDITOR\nNo();\n#elif UNITY_WEBGL\nTask.Run(x);\n#endif\n", true),
            ("#elif after a certainly taken branch",
                "#if UNITY_WEBGL\nNo();\n#elif COREAI_LLM\nTask.Run(x);\n#endif\n", false),
            ("#else after a certain #elif",
                "#if UNITY_EDITOR\nNo();\n#elif UNITY_WEBGL\nNo();\n#else\nTask.Run(x);\n#endif\n", false),
            ("nested #if inside a reachable one",
                "#if UNITY_WEBGL\n#if UNITY_EDITOR\nTask.Run(x);\n#endif\n#endif\n", false),
            ("nested #if inside an unreachable one",
                "#if UNITY_EDITOR\n#if UNITY_WEBGL\nTask.Run(x);\n#endif\n#endif\n", false),
            ("code after an unreachable region closes",
                "#if UNITY_EDITOR\nNo();\n#endif\nTask.Run(x);\n", true),
            ("a malformed condition keeps the branch under the scan",
                "#if UNITY_WEBGL == 1\nTask.Run(x);\n#endif\n", true)
        };

        private const string SeededViolationProbe = @"
class Seeded
{
    void Bad()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        cts.CancelAfter(1000);
        await Task.Delay(5, token);
        Task.Run(() => Work());
        await Work().ConfigureAwait(false);
        gate.Wait();
        gate.Wait(250);
        Monitor.Wait(state);
        handle.WaitOne(100);
        Thread.Sleep(10);
        Task.WaitAll(a, b);
        Work().GetAwaiter().GetResult();
        var value = Work().Result;
    }
}
";

        private const string NonViolationProbe = @"
class NotAViolation
{
    // WHY NOT RunContinuationsAsynchronously: a comment is not code.
    // await x.ConfigureAwait(false) in a comment is not code either.
    /* Task.Delay( and cts.CancelAfter( inside a block comment are not code. */
    private const string Message = ""Task.Run( inside a string is not code either"";

    /// <summary>A reference <see cref=""Task.Delay(int, CancellationToken)""/> in xml-doc.</summary>
    void Ok()
    {
        UniTask.Delay(5);
        if (!gate.Wait(0)) throw new InvalidOperationException(""busy"");
        var stored = mutation.Result;
        var served = response.Result;
#if UNITY_EDITOR
        Task.Run(() => Work());
        await Work().ConfigureAwait(false);
        gate.Wait();
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
        Work();
#else
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        cts.CancelAfter(1000);
        Thread.Sleep(5);
#endif
    }
}
";
    }
}
