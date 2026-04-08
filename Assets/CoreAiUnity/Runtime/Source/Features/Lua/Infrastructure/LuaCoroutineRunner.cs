using System;
using System.Collections.Generic;
using CoreAI.Sandbox;
using UnityEngine;

namespace CoreAI.Infrastructure.Lua
{
    /// <summary>
    /// MonoBehaviour, который тикает долгоживущие Lua-корутины каждый кадр.
    /// <para>
    /// Каждый <see cref="LuaCoroutineHandle"/> получает бюджет инструкций на кадр
    /// через <see cref="LuaCoroutineHandle.Resume"/>. Корутина выполняется до
    /// <c>coroutine.yield()</c> и отдаёт управление Unity.
    /// </para>
    /// <para>
    /// Мёртвые корутины (завершённые или убитые) автоматически удаляются из очереди.
    /// </para>
    /// </summary>
    public sealed class LuaCoroutineRunner : MonoBehaviour
    {
        private readonly List<LuaCoroutineHandle> _handles = new();
        private readonly List<LuaCoroutineHandle> _toRemove = new();

        /// <summary>Количество активных корутин.</summary>
        public int ActiveCount => _handles.Count;

        /// <summary>
        /// Зарегистрировать корутину для покадрового выполнения.
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
        /// Снять корутину с выполнения и убить её.
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
        /// Убить и снять с выполнения все корутины.
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

            // Удаляем завершённые/ошибочные корутины
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