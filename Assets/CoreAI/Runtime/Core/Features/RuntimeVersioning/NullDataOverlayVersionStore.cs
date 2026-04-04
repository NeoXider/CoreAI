using System.Collections.Generic;

namespace CoreAI.Ai
{
    public sealed class NullDataOverlayVersionStore : IDataOverlayVersionStore
    {
        public bool TryGetSnapshot(string overlayKey, out DataOverlayVersionRecord snapshot)
        {
            snapshot = null;
            return false;
        }

        public void RecordSuccessfulApply(string overlayKey, string jsonOrTextPayload)
        {
        }

        public void SeedOriginal(string overlayKey, string originalPayload, bool overwriteExistingOriginal = false)
        {
        }

        public void ResetToOriginal(string overlayKey)
        {
        }

        public void ResetToRevision(string overlayKey, int revisionIndex)
        {
        }

        public void ResetAllToOriginal()
        {
        }

        public bool TryGetCurrentPayload(string overlayKey, out string currentPayload)
        {
            currentPayload = null;
            return false;
        }

        public IReadOnlyList<string> GetKnownKeys()
        {
            return System.Array.Empty<string>();
        }

        public string BuildProgrammerPromptSection(string overlayKey)
        {
            return "";
        }
    }
}