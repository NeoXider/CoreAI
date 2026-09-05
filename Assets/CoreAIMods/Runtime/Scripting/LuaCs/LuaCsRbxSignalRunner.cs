using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using CoreAI.Scripting.LuaCs;
using Lua;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// One parked Lua coroutine that runs signal handlers back to back on the same VM thread, the way
    /// Roblox recycles the thread of a handler that returned without yielding. The coroutine body is a
    /// Lua closure (<c>while true do run() yield() end</c>) rather than C# code because Lua-CSharp only
    /// accepts a yield whose immediate caller is a Lua closure; <c>run</c> is the C# function below that
    /// executes whatever handler is armed and flags completion, and <c>yield</c> is the native
    /// <c>coroutine.yield</c> captured as an upvalue when the body is built. A handler that yields
    /// (task.wait, signal:Wait, RemoteFunction) suspends inside <c>run</c> exactly as it would on a
    /// dedicated thread; a handler that throws kills the coroutine in protected mode, so the runner is
    /// never reused after an error.
    /// </summary>
    internal sealed class LuaCsRbxSignalRunner
    {
        /// <summary>Lua source of the body factory; compiled once per owner state.</summary>
        internal const string BodyFactorySource =
            "local run, yield = ...\n" +
            "return function()\n" +
            "    while true do\n" +
            "        run()\n" +
            "        yield()\n" +
            "    end\n" +
            "end";

        private readonly LuaState _ownerState;
        private readonly LuaCsCoroutineHandle _handle;
        private readonly LuaCsScriptCoroutine _coroutine;
        private LuaValue _pendingCallable;
        private LuaValue[] _pendingArguments = Array.Empty<LuaValue>();
        private int _pendingArgumentCount;
        private bool _hasPending;
        private bool _iterationCompleted;

        internal LuaCsRbxSignalRunner(LuaState ownerState, LuaValue bodyFactory)
        {
            _ownerState = ownerState ?? throw new ArgumentNullException(nameof(ownerState));
            LuaFunction run = new("signal_runner.run", RunPendingAsync);
            LuaValue yield = ReadNativeYield(ownerState);
            LuaValue[] made = ownerState.CallAsync(
                    bodyFactory, new[] { new LuaValue(run), yield }.AsSpan(), CancellationToken.None)
                .GetAwaiter().GetResult();
            if (made.Length == 0 || made[0].Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    "signal runner body factory did not return a function",
                    "keep LuaCsRbxSignalRunner.BodyFactorySource returning the runner closure");
            }

            _handle = new LuaCsCoroutineHandle(ownerState, made[0].Read<LuaFunction>());
            _coroutine = new LuaCsScriptCoroutine(_handle);
            // WHY: the envelope takes a Func; one delegate per runner instead of one per fire.
            ResumeDelegate = Resume;
        }

        /// <summary>Stable delegate over <see cref="Resume"/> for the per-resume envelope call.</summary>
        internal Func<ScriptResumeResult> ResumeDelegate { get; }

        /// <summary>The mod state this runner was built on; it never runs another state's handlers.</summary>
        internal LuaState OwnerState => _ownerState;

        /// <summary>Seam view of the parked coroutine for status/kill queries.</summary>
        internal IScriptCoroutine Coroutine => _coroutine;

        /// <summary>True once the armed handler returned (not merely yielded) inside the last resume.</summary>
        internal bool IterationCompleted => _iterationCompleted;

        /// <summary>True when the runner is parked at its yield with nothing armed and can take a handler.</summary>
        internal bool CanRun => !_hasPending && _handle.CanResume;

        /// <summary>Compiles the body factory chunk on <paramref name="ownerState"/>.</summary>
        internal static LuaValue LoadBodyFactory(LuaState ownerState)
        {
            if (ownerState == null)
            {
                throw new ArgumentNullException(nameof(ownerState));
            }

            return new LuaValue(ownerState.Load(BodyFactorySource, "signal_runner"));
        }

        /// <summary>Arms the next handler; the following <see cref="Resume"/> executes it.</summary>
        internal void Arm(LuaValue callable, object[] arguments)
        {
            if (callable.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    "signal runner expects a Lua function handler",
                    "connect a function to the signal");
            }

            int count = arguments?.Length ?? 0;
            if (_pendingArguments.Length < count)
            {
                _pendingArguments = new LuaValue[Math.Max(count, 4)];
            }

            for (int index = 0; index < count; index++)
            {
                _pendingArguments[index] = LuaCsValueMarshaller.Unbox(arguments[index]);
            }

            _pendingArgumentCount = count;
            _pendingCallable = callable;
            _hasPending = true;
            _iterationCompleted = false;
        }

        /// <summary>Drops any armed handler and its argument references (retire/kill path).</summary>
        internal void Disarm()
        {
            _hasPending = false;
            _pendingCallable = LuaValue.Nil;
            Array.Clear(_pendingArguments, 0, _pendingArguments.Length);
            _pendingArgumentCount = 0;
        }

        /// <summary>Restarts the lifetime step budget for the next handler (see <see cref="LuaCsCoroutineHandle.ResetLifetime"/>).</summary>
        internal void ResetLifetime()
        {
            _handle.ResetLifetime();
        }

        /// <summary>Resumes the parked coroutine once, without boxing results.</summary>
        internal ScriptResumeResult Resume()
        {
            _handle.Resume();
            // WHY: no result boxing — the runner yields no values and the scheduler only reads the ok
            // flag; LastErrorText is string.Empty on success so this allocates nothing on the hot path.
            return new ScriptResumeResult(_handle.LastOk, null, _handle.LastErrorText);
        }

        private async ValueTask<int> RunPendingAsync(LuaFunctionExecutionContext ctx, CancellationToken ct)
        {
            if (!_hasPending)
            {
                // WHY: a mod that captured coroutine.running() inside a handler can coroutine.resume the
                // parked runner later. That resume must be a no-op: nothing is replayed, and the Lua loop
                // simply parks again.
                return ctx.Return();
            }

            LuaValue callable = _pendingCallable;
            int count = _pendingArgumentCount;
            _hasPending = false;
            _pendingCallable = LuaValue.Nil;
            _pendingArgumentCount = 0;
            await ctx.State.CallAsync(callable, _pendingArguments.AsSpan(0, count), ct);
            // WHY: drop the handler's argument references as soon as it returns; a parked runner must not
            // keep instances or userdata from the last fire alive.
            Array.Clear(_pendingArguments, 0, count);
            _iterationCompleted = true;
            return ctx.Return();
        }

        private static LuaValue ReadNativeYield(LuaState ownerState)
        {
            LuaValue coroutineLibrary = ownerState.Environment["coroutine"];
            LuaValue yield = coroutineLibrary.Type == LuaValueType.Table
                ? coroutineLibrary.Read<LuaTable>()["yield"]
                : LuaValue.Nil;
            if (yield.Type != LuaValueType.Function)
            {
                throw RbxError.BadArgument(
                    "coroutine.yield is unavailable in the mod environment",
                    "keep the sandbox's coroutine library intact before connecting signal handlers");
            }

            return yield;
        }
    }
}
