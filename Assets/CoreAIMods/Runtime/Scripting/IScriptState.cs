using System;

namespace CoreAI.Scripting
{
    /// <summary>
    /// Opaque handle to one sandboxed script environment (one mod = one state). The host never touches
    /// the VM object behind it; all execution goes through <see cref="IScriptEngine"/> /
    /// <see cref="IScriptExecutionGuard"/> and all callback registration through
    /// <see cref="IScriptFunctionRegistry.ApplyTo"/>.
    /// </summary>
    public interface IScriptState : IDisposable
    {
    }
}
