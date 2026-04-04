#if !COREAI_NO_LLM
using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Infrastructure.Logging;
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
        /// <summary>
        /// Если <see cref="LLM.model"/> пусто, подставляет первую подходящую модель из Model Manager (файл на диске).
        /// </summary>
        /// <returns><c>true</c>, если модель уже была или успешно назначена; иначе <c>false</c>.</returns>
        public static bool TryAutoAssignResolvableModel(LLM llm, IGameLogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (llm == null || !string.IsNullOrWhiteSpace(llm.model))
            {
                return true;
            }

            List<ModelEntry> candidates = CollectResolvableNonLoraEntries();
            if (candidates.Count == 0)
            {
                return false;
            }

            // Среди моделей с реальным файлом: сначала первая с includeInBuild (колонка Build), иначе первая в списке.
            ModelEntry chosen = null;
            foreach (ModelEntry c in candidates)
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
                logger.LogWarning(
                    GameLogFeature.Llm,
                    "LLMUnity: в Model Manager несколько .gguf с файлами, а LLM.model в сцене пусто — " +
                    "временно выбрано: «" + chosen.filename +
                    "». Нажмите радиокнопку у нужной модели и сохраните сцену.");
            }

            return TrySetModelFromEntry(llm, chosen, logger);
        }

        /// <summary>
        /// Назначает модель из Model Manager, если имя файла .gguf (без учёта регистра) содержит все непустые токены.
        /// Удобно для Play Mode тестов (например Qwen3.5 0.8B: токены «qwen», «0.8»).
        /// </summary>
        public static bool TryAssignModelMatchingFilename(LLM llm, IGameLogger logger,
            params string[] filenameSubstringsMustContainAll)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (llm == null || filenameSubstringsMustContainAll == null || filenameSubstringsMustContainAll.Length == 0)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(llm.model))
            {
                return true;
            }

            List<string> tokens = new();
            foreach (string t in filenameSubstringsMustContainAll)
            {
                if (string.IsNullOrWhiteSpace(t))
                {
                    continue;
                }

                tokens.Add(t.Trim().ToLowerInvariant());
            }

            if (tokens.Count == 0)
            {
                return false;
            }

            List<ModelEntry> candidates = CollectResolvableNonLoraEntries();
            List<ModelEntry> matched = new();
            foreach (ModelEntry e in candidates)
            {
                string fn = Path.GetFileName(e.filename ?? "").ToLowerInvariant();
                bool ok = true;
                foreach (string tok in tokens)
                {
                    if (!fn.Contains(tok))
                    {
                        ok = false;
                        break;
                    }
                }

                if (ok)
                {
                    matched.Add(e);
                }
            }

            if (matched.Count == 0)
            {
                return false;
            }

            ModelEntry chosen = null;
            foreach (ModelEntry m in matched)
            {
                if (m.includeInBuild)
                {
                    chosen = m;
                    break;
                }
            }

            chosen ??= matched[0];
            return TrySetModelFromEntry(llm, chosen, logger);
        }

        private static List<ModelEntry> CollectResolvableNonLoraEntries()
        {
            try
            {
                LLMManager.LoadFromDisk();
            }
            catch
            {
                return new List<ModelEntry>();
            }

            List<ModelEntry> candidates = new();
            foreach (ModelEntry e in LLMManager.modelEntries)
            {
                if (e == null || e.lora)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(e.filename))
                {
                    continue;
                }

                if (!TryResolveModelFilePath(e, out string fullPath) || !File.Exists(fullPath))
                {
                    continue;
                }

                candidates.Add(e);
            }

            return candidates;
        }

        private static bool TrySetModelFromEntry(LLM llm, ModelEntry chosen, IGameLogger logger)
        {
            try
            {
                llm.SetModel(chosen.filename);
            }
            catch (Exception ex)
            {
                logger.LogWarning(GameLogFeature.Llm, "LLMUnity: SetModel не удался: " + ex.Message);
                return false;
            }

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                return false;
            }

            logger.LogInfo(
                GameLogFeature.Llm,
                "LLMUnity: поле model было пусто — назначена модель из Model Manager: " + llm.model);
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
            string managerPath = LLMManager.GetAssetPath(e.filename);
            if (!string.IsNullOrWhiteSpace(managerPath) && File.Exists(managerPath))
            {
                fullPath = managerPath;
                return true;
            }

            // Файл в StreamingAssets / persistent (как после скачивания через LLMUnity).
            string assetPath = LLMUnitySetup.GetAssetPath(e.filename);
            if (!string.IsNullOrWhiteSpace(assetPath) && File.Exists(assetPath))
            {
                fullPath = assetPath;
                return true;
            }

            string downloadPath = LLMUnitySetup.GetDownloadAssetPath(e.filename);
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