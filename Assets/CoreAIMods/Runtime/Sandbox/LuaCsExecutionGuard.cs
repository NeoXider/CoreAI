using System;
using System.Diagnostics;
using System.Threading;
using Lua;
using Lua.Runtime;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Runs Lua-CSharp chunks/functions with timeout and instruction-step limits.
    /// </summary>
    public sealed class LuaCsExecutionGuard
    {
        private readonly int _timeoutMs;
        private readonly long _maxSteps;

        /// <param name="timeoutMs">Maximum wall-clock time allowed for one guarded call.</param>
        /// <param name="maxSteps">Maximum Lua-CSharp instruction steps allowed for one guarded call.</param>
        public LuaCsExecutionGuard(int timeoutMs = 2000, long maxSteps = 200_000)
        {
            _timeoutMs = timeoutMs;
            _maxSteps = maxSteps;
        }

        /// <summary>Runs a loaded Lua-CSharp chunk synchronously under the guard.</summary>
        public LuaValue[] Execute(LuaState state, LuaClosure closure, CancellationToken cancellationToken = default)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (closure == null)
            {
                throw new ArgumentNullException(nameof(closure));
            }

            return ExecuteGuarded(state, ct => state.ExecuteAsync(closure, ct).GetAwaiter().GetResult(),
                cancellationToken);
        }

        /// <summary>Calls a Lua-CSharp function synchronously under the guard.</summary>
        public LuaValue[] Execute(
            LuaState state,
            LuaFunction function,
            CancellationToken cancellationToken = default,
            params LuaValue[] args)
        {
            if (state == null)
            {
                throw new ArgumentNullException(nameof(state));
            }

            if (function == null)
            {
                throw new ArgumentNullException(nameof(function));
            }

            args ??= Array.Empty<LuaValue>();
            return ExecuteGuarded(state,
                ct => state.CallAsync(new LuaValue(function), args.AsSpan(), ct).GetAwaiter().GetResult(),
                cancellationToken);
        }

        /// <summary>Runs a loaded Lua-CSharp chunk and reads the first returned value as <typeparamref name="T"/>.</summary>
        public T Execute<T>(LuaState state, LuaClosure closure, CancellationToken cancellationToken = default)
        {
            LuaValue[] results = Execute(state, closure, cancellationToken);
            return results.Length == 0 ? default : results[0].Read<T>();
        }

        private LuaValue[] ExecuteGuarded(
            LuaState state,
            Func<CancellationToken, LuaValue[]> body,
            CancellationToken cancellationToken)
        {
            Stopwatch sw = Stopwatch.StartNew();
            long steps = 0;
            long maxSteps = _maxSteps < 1 ? 1 : _maxSteps;
            int timeoutMs = _timeoutMs < 1 ? 1 : _timeoutMs;

            LuaFunction hook = new("coreai_instruction_guard", (ctx, ct) =>
            {
                steps++;
                if (steps > maxSteps)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new InvalidOperationException($"LuaCsSecureEnvironment: EXCEEDED_HARD_LIMIT_STEPS ({maxSteps})"));
                }

                if (sw.ElapsedMilliseconds > timeoutMs)
                {
                    throw new LuaRuntimeException(ctx.State,
                        new TimeoutException($"Lua exceeded {timeoutMs} ms."));
                }

                return new System.Threading.Tasks.ValueTask<int>(ctx.Return());
            });

            state.SetHook(hook, string.Empty, 1);
            try
            {
                return body(cancellationToken);
            }
            catch (LuaRuntimeException)
            {
                throw;
            }
            finally
            {
                try
                {
                    state.SetHook(null, string.Empty, 0);
                }
                catch
                {
                    /* ignore */
                }
            }
        }
    }
}
