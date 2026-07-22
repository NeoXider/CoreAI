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

    /// <summary>Engine-neutral failure classification helpers.</summary>
    public static class ScriptExecutionErrors
    {
        /// <summary>
        /// True when <paramref name="ex"/> (or any exception it wraps) is a memory-budget trip raised by
        /// an execution guard. Type-based, so a script cannot forge the classification via error text.
        /// </summary>
        public static bool IsMemoryBudgetTrip(Exception ex)
        {
            for (Exception e = ex; e != null; e = e.InnerException)
            {
                if (e is IScriptMemoryBudgetTrip)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
