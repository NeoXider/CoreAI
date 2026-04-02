#if !COREAI_NO_LLM
using System.Collections.Generic;
using System.IO;
using LLMUnity;
using UnityEngine;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// В инспекторе LLMUnity список моделей (Model Manager) может быть заполнен, а поле <see cref="LLM.model"/> в сцене —
    /// пустым (сцена не сохранена, не нажата радиокнопка и т.д.). Тогда CoreAI честно уходит в <see cref="StubLlmClient"/>.
    /// Пытаемся подставить модель из Model Manager (см. README LLMUnity «LLM model management»):
    /// при нескольких кандидатах с файлом на диске предпочитаем записи с <see cref="ModelEntry.includeInBuild"/>
    /// (колонка Build в инспекторе), иначе первую по порядку в списке. Эталонный способ — радиокнопка у модели и Save сцены.
    /// </summary>
    public static class LlmUnityModelBootstrap
    {
        public static bool TryAutoAssignResolvableModel(LLM llm)
        {
            if (llm == null || !string.IsNullOrWhiteSpace(llm.model))
                return true;

            try
            {
                LLMManager.LoadFromDisk();
            }
            catch
            {
                return false;
            }

            var candidates = new List<ModelEntry>();
            foreach (var e in LLMManager.modelEntries)
            {
                if (e == null || e.lora)
                    continue;
                if (string.IsNullOrWhiteSpace(e.filename))
                    continue;
                if (!TryResolveModelFilePath(e, out var fullPath) || !File.Exists(fullPath))
                    continue;
                candidates.Add(e);
            }

            if (candidates.Count == 0)
                return false;

            // Среди моделей с реальным файлом: сначала первая с includeInBuild (колонка Build), иначе первая в списке.
            ModelEntry chosen = null;
            foreach (var c in candidates)
            {
                if (c.includeInBuild)
                {
                    chosen = c;
                    break;
                }
            }

            chosen ??= candidates[0];

            if (candidates.Count > 1)
            {
                Debug.LogWarning(
                    "[CoreAI] LLMUnity: в Model Manager несколько .gguf с файлами, а LLM.model в сцене пусто — " +
                    "временно выбрано: «" + chosen.filename + "». Нажмите радиокнопку у нужной модели и сохраните сцену.");
            }

            try
            {
                llm.SetModel(chosen.filename);
            }
            catch
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(llm.model))
                return false;

            Debug.Log(
                "[CoreAI] LLMUnity: поле model было пусто — назначена модель из Model Manager: " + llm.model);
            return true;
        }

        private static bool TryResolveModelFilePath(ModelEntry e, out string fullPath)
        {
            fullPath = null;
            if (!string.IsNullOrWhiteSpace(e.path) && File.Exists(e.path))
            {
                fullPath = e.path;
                return true;
            }

            // Как в LLM.GetLLMManagerAssetRuntime: путь через Model Manager.
            var managerPath = LLMManager.GetAssetPath(e.filename);
            if (!string.IsNullOrWhiteSpace(managerPath) && File.Exists(managerPath))
            {
                fullPath = managerPath;
                return true;
            }

            // Файл в StreamingAssets / persistent (как после скачивания через LLMUnity).
            var assetPath = LLMUnitySetup.GetAssetPath(e.filename);
            if (!string.IsNullOrWhiteSpace(assetPath) && File.Exists(assetPath))
            {
                fullPath = assetPath;
                return true;
            }

            var downloadPath = LLMUnitySetup.GetDownloadAssetPath(e.filename);
            if (!string.IsNullOrWhiteSpace(downloadPath) && File.Exists(downloadPath))
            {
                fullPath = downloadPath;
                return true;
            }

            return false;
        }
    }
}
#endif
