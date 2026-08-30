namespace CoreAI.Mods.Rbx.Instances.Scheduling
{
    /// <summary>Engine-neutral lifecycle states exposed by a scheduler-owned script thread.</summary>
    public enum RbxScriptThreadStatus
    {
        Suspended,
        Running,
        Dead
    }

    /// <summary>Structured outcome of resuming one script thread to its next yield or completion.</summary>
    public readonly struct RbxScriptThreadResumeResult
    {
        private RbxScriptThreadResumeResult(bool succeeded, RbxError error)
        {
            Succeeded = succeeded;
            Error = error;
        }

        /// <summary>True when the resume yielded or completed without a script fault.</summary>
        public bool Succeeded { get; }

        /// <summary>Structured failure when <see cref="Succeeded"/> is false.</summary>
        public RbxError Error { get; }

        /// <summary>Creates a successful resume result.</summary>
        public static RbxScriptThreadResumeResult Success()
        {
            return new RbxScriptThreadResumeResult(true, null);
        }

        /// <summary>Creates a failed resume result carrying the required structured error.</summary>
        public static RbxScriptThreadResumeResult Failure(RbxError error)
        {
            if (error == null)
            {
                throw RbxError.BadArgument(
                    "RbxScriptThreadResumeResult.Failure requires an RbxError",
                    "pass the structured error raised while resuming the script thread");
            }

            return new RbxScriptThreadResumeResult(false, error);
        }
    }

    /// <summary>
    /// Engine-free port for one resumable script thread. The later LuaCs adapter implements this
    /// over its scripting coroutine without exposing that outer assembly to the Domain layer.
    /// </summary>
    public interface IRbxScriptThread
    {
        /// <summary>Current lifecycle status.</summary>
        RbxScriptThreadStatus Status { get; }

        /// <summary>True after normal completion, failure, cancellation, or owner teardown.</summary>
        bool IsDead { get; }

        /// <summary>Resumes execution to the next yield or completion with raw seam arguments.</summary>
        RbxScriptThreadResumeResult Resume(params object[] args);

        /// <summary>Stops the thread permanently.</summary>
        void Kill();
    }

    /// <summary>Creates engine-neutral script threads from opaque callable handles.</summary>
    public interface IRbxScriptThreadFactory
    {
        /// <summary>Creates a suspended thread attributed to the required owner mod.</summary>
        IRbxScriptThread Create(string ownerModId, object callable);
    }
}
