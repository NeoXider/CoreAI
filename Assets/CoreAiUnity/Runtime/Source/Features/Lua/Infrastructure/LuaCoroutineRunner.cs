#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System;
using System.Collections.Generic;
using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// Unity component that resumes active sandboxed Lua coroutines across frames.
    /// </summary>
    public sealed class LuaCoroutineRunner : MonoBehaviour
    {
        private readonly List<LuaCoroutineHandle> _handles = new();
        private readonly List<LuaCoroutineHandle> _toRemove = new();

        /// <summary>Number of coroutine handles currently managed by this runner.</summary>
        public int ActiveCount => _handles.Count;

        /// <summary>
        /// Adds a coroutine handle to the frame update loop.
        /// </summary>
        public void Register(LuaCoroutineHandle handle)
        {
            if (handle == null)
            {
                throw new ArgumentNullException(nameof(handle));
            }

            _handles.Add(handle);
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
                    Debug.LogError($"[LuaCoroutineRunner] Lua error: {ex.Message}");
                    _toRemove.Add(h);
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[LuaCoroutineRunner] Unexpected error: {ex.Message}");
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
