using System;

namespace CoreAI.Mods.Rbx.Instances.Scheduling
{
    /// <summary>Terminal states for an engine-free scheduler completion token.</summary>
    public enum RbxSchedulerCompletionStatus
    {
        Pending,
        Succeeded,
        Faulted,
        Canceled
    }

    /// <summary>
    /// Nonblocking host-completion primitive. Future adapters complete this token from callbacks or
    /// async operations; the scheduler resumes its caller at the next deferred drain.
    /// </summary>
    public sealed class RbxSchedulerCompletion
    {
        private static readonly object[] EmptyArguments = Array.Empty<object>();
        private object[] _resumeArguments = EmptyArguments;

        /// <summary>Current completion state.</summary>
        public RbxSchedulerCompletionStatus Status { get; private set; }

        /// <summary>True after success, fault, or cancellation.</summary>
        public bool IsCompleted => Status != RbxSchedulerCompletionStatus.Pending;

        /// <summary>Arguments passed to the waiting thread after successful completion.</summary>
        public object[] ResumeArguments => (object[])_resumeArguments.Clone();

        /// <summary>Structured failure when <see cref="Status"/> is Faulted.</summary>
        public RbxError Error { get; private set; }

        /// <summary>Completes successfully with raw seam values for the waiting thread.</summary>
        public void Complete(params object[] resumeArguments)
        {
            ThrowIfCompleted();
            _resumeArguments = resumeArguments == null
                ? EmptyArguments
                : (object[])resumeArguments.Clone();
            Status = RbxSchedulerCompletionStatus.Succeeded;
        }

        /// <summary>Completes with a structured host-operation failure.</summary>
        public void Fail(RbxError error)
        {
            ThrowIfCompleted();
            if (error == null)
            {
                throw RbxError.BadArgument(
                    "RbxSchedulerCompletion.Fail requires an RbxError",
                    "pass the structured error raised by the host operation");
            }

            Error = error;
            Status = RbxSchedulerCompletionStatus.Faulted;
        }

        /// <summary>Cancels the host operation's scheduler wait.</summary>
        public void Cancel()
        {
            ThrowIfCompleted();
            Status = RbxSchedulerCompletionStatus.Canceled;
        }

        private void ThrowIfCompleted()
        {
            if (IsCompleted)
            {
                throw RbxError.BadArgument(
                    "RbxSchedulerCompletion is already " + Status,
                    "create a new completion token for each host operation");
            }
        }
    }
}
