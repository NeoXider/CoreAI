using System.Collections.Generic;
using CoreAI.Logging;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// <see cref="ILog"/> test double that keeps every message it is given.
    /// <para>
    /// Registering this in a test container instead of the ambient <see cref="Log.Instance"/> makes a
    /// diagnostic assertable on its own terms: whether the message reaches Unity's console depends on
    /// the process-wide logger and the live <c>GameLogFilter</c>, both of which earlier tests in the
    /// same run are free to change, so <c>LogAssert.Expect</c> over a CoreAI <see cref="ILog"/> call is
    /// order-dependent by construction.
    /// </para>
    /// </summary>
    internal sealed class RecordingLog : ILog
    {
        private readonly List<string> _errors = new();

        /// <summary>Every error message logged so far, in order.</summary>
        public IReadOnlyList<string> Errors => _errors;

        public void Debug(string message, string tag = null)
        {
        }

        public void Info(string message, string tag = null)
        {
        }

        public void Warn(string message, string tag = null)
        {
        }

        public void Error(string message, string tag = null)
        {
            _errors.Add(message ?? string.Empty);
        }

        /// <summary>Reports whether any recorded error contains <paramref name="fragment"/>.</summary>
        public bool HasError(string fragment)
        {
            foreach (string error in _errors)
            {
                if (error.Contains(fragment))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
