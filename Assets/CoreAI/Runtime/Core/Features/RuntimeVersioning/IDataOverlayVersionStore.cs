using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Tracks original and current data overlay payloads.
    /// </summary>
    public interface IDataOverlayVersionStore
    {
        bool TryGetSnapshot(string overlayKey, out DataOverlayVersionRecord snapshot);

        /// <summary>Records the payload that was successfully applied for a data overlay.</summary>
        void RecordSuccessfulApply(string overlayKey, string jsonOrTextPayload);

        void SeedOriginal(string overlayKey, string originalPayload, bool overwriteExistingOriginal = false);

        void ResetToOriginal(string overlayKey);

        /// <summary>
        /// Restores a tracked overlay payload to a specific revision index.
        /// </summary>
        void ResetToRevision(string overlayKey, int revisionIndex);

        /// <summary>Restores all tracked versioned values to their original payloads.</summary>
        void ResetAllToOriginal();

        /// <summary>Attempts to read the current payload for a data overlay.</summary>
        bool TryGetCurrentPayload(string overlayKey, out string currentPayload);

        IReadOnlyList<string> GetKnownKeys();

        string BuildProgrammerPromptSection(string overlayKey);
    }
}