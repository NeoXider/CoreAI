using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Tracks original and current Lua script versions.
    /// </summary>
    public interface ILuaScriptVersionStore
    {
        /// <summary>Attempts to read the current version snapshot for a Lua script.</summary>
        bool TryGetSnapshot(string scriptKey, out LuaScriptVersionRecord snapshot);

        /// <summary>
/// Executes RecordSuccessfulExecution API operation.
        ///
        /// </summary>
        void RecordSuccessfulExecution(string scriptKey, string executedLuaSource);

        /// <summary>
/// Executes SeedOriginal API operation.
        ///
        /// </summary>
        void SeedOriginal(string scriptKey, string originalLuaSource, bool overwriteExistingOriginal = false);

        /// <summary>Restores the requested script to its original version.</summary>
        void ResetToOriginal(string scriptKey);

        /// <summary>
/// Executes ResetToRevision API operation.
        ///
        /// </summary>
        void ResetToRevision(string scriptKey, int revisionIndex);

        /// <summary>Restores all tracked versioned values to their original payloads.</summary>
        void ResetAllToOriginal();

        /// <summary>Gets all script keys known to the version store.</summary>
        IReadOnlyList<string> GetKnownKeys();

        /// <summary>Builds the programmer prompt section that describes the current script version.</summary>
        string BuildProgrammerPromptSection(string scriptKey);
    }
}
