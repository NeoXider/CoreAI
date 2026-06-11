#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Infrastructure.Logging;
using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Unity component that resumes active sandboxed Lua coroutines across frames.
    /// </summary>
    public sealed class LuaCoroutineRunner : MonoBehaviour
    {
        /// <summary>Default cap on simultaneously registered coroutine handles.</summary>
        public const int DefaultMaxActiveCoroutines = 64;

        private readonly List<LuaCoroutineHandle> _handles = new();
        private readonly List<LuaCoroutineHandle> _toRemove = new();
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
        public void Register(LuaCoroutineHandle handle)
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
                    $"[LuaCoroutineRunner] Coroutine limit reached ({_maxActiveCoroutines}); registration rejected.");
                throw new InvalidOperationException(
                    $"LuaCoroutineRunner limit of {_maxActiveCoroutines} active coroutines reached. " +
                    "Registration rejected.");
            }

            _handles.Add(handle);
        }

        private void PruneDeadHandles()
        {
            for (int i = _handles.Count - 1; i >= 0; i--)
            {
                if (!_handles[i].IsAlive)
                {
                    _handles.RemoveAt(i);
                }
            }
        }

        /// <summary>
        /// Stops and removes a coroutine handle from the frame update loop.
        /// </summary>
        public void Unregister(LuaCoroutineHandle handle)
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
            foreach (LuaCoroutineHandle h in _handles)
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
                LuaCoroutineHandle h = _handles[i];

                if (!h.IsAlive)
                {
                    _toRemove.Add(h);
                    continue;
                }

                try
                {
                    h.Resume();

                    if (!h.IsAlive)
                    {
                        _toRemove.Add(h);
                    }
                }
                catch (MoonSharp.Interpreter.ScriptRuntimeException ex)
                {
                    Logger.LogError(GameLogFeature.Core, $"[LuaCoroutineRunner] Lua error: {ex}");
                    _toRemove.Add(h);
                }
                catch (Exception ex)
                {
                    Logger.LogError(GameLogFeature.Core, $"[LuaCoroutineRunner] Unexpected error: {ex}");
                    _toRemove.Add(h);
                }
            }

            // Iterate through the data sequence.
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
#endif
