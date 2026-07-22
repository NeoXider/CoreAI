using System.Threading;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Runs script callables under an <see cref="IExecutionBudget"/> (created via
    /// <see cref="IScriptEngine.CreateGuard"/>). Arguments may be host values (marshalled with
    /// <see cref="IValueMarshaller.ToScriptArgument"/>) or raw script values (passed through);
    /// results are raw script values.
    /// </summary>
    public interface IScriptExecutionGuard
    {
        /// <summary>Calls a script function synchronously under the guard's budget.</summary>
        object[] Invoke(IScriptState state, object callable, params object[] args);

        /// <summary>Calls a script function synchronously under the guard's budget with a cancellation token.</summary>
        object[] Invoke(IScriptState state, object callable, CancellationToken cancellationToken, object[] args);
    }
}
