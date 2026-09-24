using System;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Engine-neutral CLR exception for script execution failures raised by the seam itself (bad
    /// callable, foreign state, invalid registration). Engine adapters surface the VM's own runtime
    /// exception types unchanged so existing error text and handling stay identical; hosts catch
    /// <see cref="Exception"/> at the seam and classify via <see cref="ScriptExecutionErrors"/>.
    /// </summary>
    public class ScriptRuntimeException : Exception
    {
        public ScriptRuntimeException(string message, Exception inner = null) : base(message, inner)
        {
        }
    }

    /// <summary>
    /// Marker implemented by an engine adapter's memory-budget exception type, so a trip is classified
    /// by unforgeable TYPE (never by message text a script could imitate) without referencing VM types.
    /// </summary>
    public interface IScriptMemoryBudgetTrip
    {
    }

    /// <summary>
    /// Implemented by an engine adapter's VM error that carries the host exception it was raised for
    /// outside <see cref="Exception.InnerException"/>, so engine-neutral cause walkers
    /// (<see cref="ScriptExecutionErrors.NextCause"/>) still reach the cause without referencing VM types.
    /// </summary>
    public interface IScriptHostFailure
    {
        /// <summary>The host exception the VM error was raised for; null when there is none.</summary>
        Exception HostException { get; }
    }

    /// <summary>Engine-neutral failure classification helpers.</summary>
    public static class ScriptExecutionErrors
    {
        /// <summary>
        /// True when <paramref name="ex"/> (or any exception it wraps) is a memory-budget trip raised by
        /// an execution guard. Type-based, so a script cannot forge the classification via error text.
        /// </summary>
        public static bool IsMemoryBudgetTrip(Exception ex)
        {
            // WHY NextCause and not InnerException: an adapter's VM error can carry its cause as
            // IScriptHostFailure.HostException instead, and an InnerException walk would stop there.
            for (Exception e = ex; e != null; e = NextCause(e))
            {
                if (e is IScriptMemoryBudgetTrip)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Next link of a cause chain: <see cref="IScriptHostFailure.HostException"/> for an error that
        /// implements <see cref="IScriptHostFailure"/>, otherwise <see cref="Exception.InnerException"/>.
        /// Walkers that classify a failure by the TYPE of a wrapped cause step with this.
        /// </summary>
        public static Exception NextCause(Exception exception)
        {
            return exception is IScriptHostFailure host
                ? host.HostException
                : exception?.InnerException;
        }
    }
}
