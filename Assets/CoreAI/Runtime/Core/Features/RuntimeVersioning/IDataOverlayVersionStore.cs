using System.Collections.Generic;

namespace CoreAI.Ai
{
    /// <summary>
    /// Версионирование данных, которые Programmer меняет из Lua (JSON и т.п.): baseline, текущее значение, история, сброс по ключу или целиком.
    /// </summary>
    public interface IDataOverlayVersionStore
    {
        bool TryGetSnapshot(string overlayKey, out DataOverlayVersionRecord snapshot);

        /// <summary>Зафиксировать успешно применённый payload (после валидации игрой или ядром).</summary>
        void RecordSuccessfulApply(string overlayKey, string jsonOrTextPayload);

        void SeedOriginal(string overlayKey, string originalPayload, bool overwriteExistingOriginal = false);

        void ResetToOriginal(string overlayKey);

        /// <summary>Сбросить все известные ключи к их baseline (или удалить слоты без baseline — см. реализацию).</summary>
        void ResetAllToOriginal();

        /// <summary>Текущее значение для чтения игрой/Lua; <c>false</c> если ключа нет.</summary>
        bool TryGetCurrentPayload(string overlayKey, out string currentPayload);

        IReadOnlyList<string> GetKnownKeys();

        string BuildProgrammerPromptSection(string overlayKey);
    }
}
