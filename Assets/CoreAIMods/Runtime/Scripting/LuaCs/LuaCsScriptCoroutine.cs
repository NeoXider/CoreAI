using System;
using System.Collections.Generic;
using CoreAI.Sandbox.LuaCs;
using CoreAI.Scripting;
using Lua;

namespace CoreAI.Scripting.LuaCs
{
    /// <summary>
    /// Lua-CSharp adapter for <see cref="IScriptCoroutine"/> wrapping the budgeted
    /// <see cref="LuaCsCoroutineHandle"/>.
    /// </summary>
    public sealed class LuaCsScriptCoroutine : IScriptCoroutine
    {
        private readonly LuaCsCoroutineHandle _handle;

        internal LuaCsScriptCoroutine(LuaCsCoroutineHandle handle)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
        }

        /// <summary>The wrapped concrete handle (adapter-internal; frame runners use it directly).</summary>
        internal LuaCsCoroutineHandle Handle => _handle;

        /// <inheritdoc />
        public ScriptCoroutineStatus Status => _handle.Status switch
        {
            LuaThreadStatus.Suspended => ScriptCoroutineStatus.Suspended,
            LuaThreadStatus.Running => ScriptCoroutineStatus.Running,
            LuaThreadStatus.Normal => ScriptCoroutineStatus.Normal,
            _ => ScriptCoroutineStatus.Dead
        };

        /// <inheritdoc />
        public bool CanResume => _handle.CanResume;

        /// <inheritdoc />
        public bool IsFinished => _handle.IsFinished;

        /// <inheritdoc />
        public ScriptResumeResult Resume(params object[] args)
        {
            args ??= Array.Empty<object>();
            LuaValue[] luaArgs = new LuaValue[args.Length];
            for (int i = 0; i < args.Length; i++)
            {
                luaArgs[i] = LuaCsValueMarshaller.Unbox(args[i]);
            }

            _handle.Resume(luaArgs);

            IReadOnlyList<LuaValue> values = _handle.LastValues;
            object[] boxed = new object[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                boxed[i] = LuaCsValueMarshaller.Box(values[i]);
            }

            return new ScriptResumeResult(_handle.LastOk, boxed, _handle.LastErrorText);
        }

        /// <inheritdoc />
        public void Kill()
        {
            _handle.Kill();
        }
    }
}
