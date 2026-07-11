using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using UnityEngine;

namespace CoreAI.Sandbox.LuaCs
{
    /// <summary>
    /// Unity component that resumes active sandboxed Lua-CSharp coroutines across frames. This is the
    /// Lua-CSharp counterpart of <c>CoreAI.Infrastructure.Lua.LuaCoroutineRunner</c> and mirrors its
    /// public surface (<see cref="Register"/>/<see cref="Unregister"/>/<see cref="UnregisterAll"/>/
    /// <see cref="ActiveCount"/>/<see cref="MaxActiveCoroutines"/>/<see cref="SetLogger"/>) so the tick
    /// runtime can switch VMs by swapping the runner type.
    ///
    /// Each frame every SUSPENDED handle is advanced exactly one step (to its next <c>coroutine.yield</c>).
    /// A handle that is still suspended afterwards is simply left for the next frame — the runner NEVER
    /// blocks waiting on a yielded coroutine, which is what keeps a single-threaded WebGL/WASM player loop
    /// from deadlocking. Finished, killed and faulted handles are removed.
    /// </summary>
    public sealed class LuaCsCoroutineRunner : MonoBehaviour
    {
        /// <summary>Default cap on simultaneously registered coroutine handles.</summary>
        public const int DefaultMaxActiveCoroutines = 64;

        private readonly List<LuaCsCoroutineHandle> _handles = new();
        private readonly List<LuaCsCoroutineHandle> _toRemove = new();
        private int _maxActiveCoroutines = DefaultMaxActiveCoroutines;
        private IGameLogger _logger;

        private IGameLogger Logger => _logger ??= GameLoggerUnscopedFallback.Instance;

        /// <summary>Overrides the logger (e.g. a scoped DI logger); null keeps the fallback.</summary>
        public void SetLogger(IGameLogger logger)
        {
            _logger = logger;
        }

        /// <summary>Number of coroutine handles currently managed by this runner.</summary>
        public int ActiveCount => _handles.Count;

        /// <summary>
        /// Maximum number of coroutine handles this runner accepts at once.
        /// Registrations beyond the limit are rejected so runaway scripts cannot spam coroutines.
        /// </summary>
        public int MaxActiveCoroutines
        {
            get => _maxActiveCoroutines;
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value),
                        "MaxActiveCoroutines must be greater than zero.");
                }

                _maxActiveCoroutines = value;
            }
        }

        /// <summary>
        /// Adds a coroutine handle to the frame update loop.
        /// Throws <see cref="InvalidOperationException"/> when <see cref="MaxActiveCoroutines"/> is exceeded.
        /// </summary>
        public void Register(LuaCsCoroutineHandle handle)
        {
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            if (_handles.Count >= _maxActiveCoroutines)
            {
                // Free slots held by already-finished coroutines before rejecting.
                PruneDeadHandles();
            }

            if (_handles.Count >= _maxActiveCoroutines)
            {
                Logger.LogError(GameLogFeature.Core,
                    $"[LuaCsCoroutineRunner] Coroutine limit reached ({_maxActiveCoroutines}); registration rejected.");
                throw new InvalidOperationException(
                    $"LuaCsCoroutineRunner limit of {_maxActiveCoroutines} active coroutines reached. " +
                    "Registration rejected.");
            }

            _handles.Add(handle);
        }

        private void PruneDeadHandles()
        {
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                if (_handles[i].IsFinished)
                {
                    _handles.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Stops and removes a coroutine handle from the frame update loop.
        /// </summary>
        public void Unregister(LuaCsCoroutineHandle handle)
        {
            if (handle == null)
            {
                return;
            }

            handle.Kill();
            _handles.Remove(handle);
        }

        /// <summary>
        /// Stops and removes every coroutine handle managed by this runner.
        /// </summary>
        public void UnregisterAll()
        {
            foreach (LuaCsCoroutineHandle h in _handles)
            {
                h.Kill();
            }

            _handles.Clear();
        }

        private void Update()
        {
            if (_handles.Count == 0)
            {
                return;
            }

            _toRemove.Clear();

            for (int i = 0; i < _handles.Count; i++)
            {
                LuaCsCoroutineHandle h = _handles[i];

                if (h.IsFinished)
                {
                    _toRemove.Add(h);
                    continue;
                }

                if (!h.CanResume)
                {
                    continue;
                }

                try
                {
                    // Advance one step. If the coroutine yields it stays suspended and is left for the
                    // next frame; the runner never waits on it (WebGL-safe, non-blocking).
                    h.Resume();

                    if (!h.LastOk)
                    {
                        // Protected-mode Lua error: the handle already transitioned to Dead and
                        // captured the error object; log it and drop the handle.
                        Logger.LogError(GameLogFeature.Core,
                            $"[LuaCsCoroutineRunner] Lua error: {h.LastErrorText}");
                        _toRemove.Add(h);
                        continue;
                    }

                    if (h.IsFinished)
                    {
                        _toRemove.Add(h);
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(GameLogFeature.Core, $"[LuaCsCoroutineRunner] Unexpected error: {ex}");
                    h.Kill();
                    _toRemove.Add(h);
                }
            }

            for (int i = _toRemove.Count - 1; i >= 0; i--)
            {
                _handles.Remove(_toRemove[i]);
            }
        }

        private void OnDestroy()
        {
            UnregisterAll();
        }
    }
}