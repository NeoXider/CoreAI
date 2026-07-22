using System;
using System.Collections.Generic;

namespace CoreAI.Scripting
{
    /// <summary>Engine-neutral coroutine lifecycle states (Lua thread-status shaped).</summary>
    public enum ScriptCoroutineStatus
    {
        Suspended,
        Running,
        Normal,
        Dead
    }

    /// <summary>Outcome of one <see cref="IScriptCoroutine.Resume"/>: ok flag, yielded values, error text.</summary>
    public readonly struct ScriptResumeResult
    {
        private static readonly object[] None = Array.Empty<object>();

        public ScriptResumeResult(bool ok, object[] values, string error)
        {
            Ok = ok;
            _values = values;
            Error = error ?? "";
        }

        private readonly object[] _values;

        /// <summary>False when the resume raised a script error and the coroutine died.</summary>
        public bool Ok { get; }

        /// <summary>Values yielded or returned by the resume (raw script values; empty on error).</summary>
        public IReadOnlyList<object> Values => _values ?? None;

        /// <summary>Human-readable error text when <see cref="Ok"/> is false; otherwise empty.</summary>
        public string Error { get; }
    }

    /// <summary>
    /// One host-driven script coroutine advanced one yield per <see cref="Resume"/>, with per-resume and
    /// lifetime budgets enforced by the engine adapter. Created via
    /// <see cref="IScriptEngine.CreateCoroutine"/>.
    /// </summary>
    public interface IScriptCoroutine
    {
        /// <summary>Current lifecycle status.</summary>
        ScriptCoroutineStatus Status { get; }

        /// <summary>True when a further <see cref="Resume"/> is legal.</summary>
        bool CanResume { get; }

        /// <summary>True once the coroutine has finished or been killed.</summary>
        bool IsFinished { get; }

        /// <summary>Advances to the next yield (or completion), passing resume arguments.</summary>
        ScriptResumeResult Resume(params object[] args);

        /// <summary>Stops the coroutine permanently.</summary>
        void Kill();
    }
}
