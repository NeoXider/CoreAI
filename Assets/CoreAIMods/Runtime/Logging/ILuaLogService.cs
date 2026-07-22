using System;
using System.Collections.Generic;

namespace CoreAI.Ai.Logging
{
    /// <summary>
    /// Captures Lua mod log output (<c>print</c>/<c>warn</c>/<c>error</c>, uncaught runtime errors)
    /// independent of the Unity console, so it can be read back through a plain C# API and, from
    /// there, surfaced to an in-game LLM agent (see <c>GetModLogsLlmTool</c>) that needs to inspect
    /// what a mod said and self-repair it while the game is running. Implementations keep one ring
    /// buffer per mod id plus one global ring buffer spanning all mods; both are bounded so
    /// long-running sessions cannot leak memory.
    /// </summary>
    public interface ILuaLogService
    {
        /// <summary>
        /// Appends an entry. <see cref="LuaLogEntry.Sequence"/> and <see cref="LuaLogEntry.UtcTime"/>
        /// are assigned by the implementation (overwriting whatever the caller set); every other field
        /// is taken as-is. Safe to call from any thread.
        /// </summary>
        void Append(LuaLogEntry entry);

        /// <summary>
        /// Returns entries matching <paramref name="query"/>, oldest-first, capped to the newest
        /// <see cref="LuaLogQuery.MaxCount"/> matches. Safe to call from any thread, including
        /// concurrently with <see cref="Append"/>.
        /// </summary>
        IReadOnlyList<LuaLogEntry> Query(LuaLogQuery query);

        /// <summary>Raised synchronously, after storage, every time an entry is appended.</summary>
        event Action<LuaLogEntry> EntryAppended;

        /// <summary>
        /// Clears logs. With a <paramref name="modId"/>, only that mod's per-mod buffer is cleared
        /// (its already-appended entries remain visible through the unfiltered global buffer). With
        /// no <paramref name="modId"/>, every per-mod buffer and the global buffer are cleared.
        /// </summary>
        void Clear(string modId = null);
    }
}
