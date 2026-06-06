#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using System;
using System.Collections.Generic;
using System.IO;
using CoreAI.Infrastructure.Logging;
using LLMUnity;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Bootstraps local LLMUnity model loading from CoreAI settings.
    /// </summary>
    public static class LlmUnityModelBootstrap
    {
        /// <summary>Returns the file name part of a GGUF path or hint.</summary>
        public static string NormalizeGgufHintToFileName(string ggufHint)
        {
            return string.IsNullOrWhiteSpace(ggufHint) ? string.Empty : Path.GetFileName(ggufHint.Trim());
        }

        /// <summary>Assigns a matching LLMUnity model when the configured GGUF hint resolves to a known model entry.</summary>
        public static bool TryAssignModelFromGgufHint(LLM llm, IGameLogger logger, string ggufHint)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (llm == null || string.IsNullOrWhiteSpace(ggufHint))
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(llm.model))
            {
                return true;
            }

            string trimmed = ggufHint.Trim();
            if (File.Exists(trimmed))
            {
                try
                {
                    llm.SetModel(trimmed);
                    return !string.IsNullOrWhiteSpace(llm.model);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(GameLogFeature.Llm,
                        "LLMUnity: failed to assign GGUF path from settings: " + ex.Message);
                    return false;
                }
            }

            string fileName = NormalizeGgufHintToFileName(trimmed);
            return TryAssignModelMatchingFilename(llm, logger, fileName);
        }

        /// <summary>Assigns the only resolvable non-LoRA model, or an included-in-build candidate when several exist.</summary>
        public static bool TryAutoAssignResolvableModel(LLM llm, IGameLogger logger)
        {
            if (logger == null)
            {
                throw new ArgumentNullException(nameof(logger));
            }

            if (llm == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(llm.model))
            {
                return true;
            }

            List<ModelEntry> candidates = CollectResolvableNonLoraEntries();
            if (candidates.Count == 0)
            {
                return false;
            }

            ModelEntry chosen = null;
            foreach (ModelEntry candidate in candidates)
            {
                if (candidate.includeInBuild)
                {
                    chosen = candidate;
                    break;
                }
            }

            chosen ??= candidates[0];

            if (candidates.Count > 1)
            {
                logger.LogWarning(
                    GameLogFeature.Llm,
                    "LLMUnity: multiple .gguf files are available in Model Manager and LLM.model is empty; temporarily selected: " +
                    chosen.filename +
                    ". Select the desired model in Model Manager and save the scene.");
            }

            return TrySetModelFromEntry(llm, chosen, logger);
        }

        /// <summary>Assigns the first resolvable model whose file name contains all provided tokens.</summary>
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
            foreach (string token in filenameSubstringsMustContainAll)
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    tokens.Add(token.Trim().ToLowerInvariant());
                }
            }

            if (tokens.Count == 0)
            {
                return false;
            }

            List<ModelEntry> matched = new();
            foreach (ModelEntry entry in CollectResolvableNonLoraEntries())
            {
                string fileName = Path.GetFileName(entry.filename ?? string.Empty).ToLowerInvariant();
                bool matches = true;
                foreach (string token in tokens)
                {
                    if (!fileName.Contains(token))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    matched.Add(entry);
                }
            }

            if (matched.Count == 0)
            {
                return false;
            }

            ModelEntry chosen = null;
            foreach (ModelEntry entry in matched)
            {
                if (entry.includeInBuild)
                {
                    chosen = entry;
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
            foreach (ModelEntry entry in LLMManager.modelEntries)
            {
                if (entry == null || entry.lora || string.IsNullOrWhiteSpace(entry.filename))
                {
                    continue;
                }

                if (TryResolveModelFilePath(entry, out string fullPath) && File.Exists(fullPath))
                {
                    candidates.Add(entry);
                }
            }

            return candidates;
        }

        private static bool TrySetModelFromEntry(LLM llm, ModelEntry chosen, IGameLogger logger)
        {
            if (chosen == null || string.IsNullOrWhiteSpace(chosen.filename))
            {
                return false;
            }

            try
            {
                llm.SetModel(chosen.filename);
            }
            catch (Exception ex)
            {
                logger.LogWarning(GameLogFeature.Llm,
                    "LLMUnity: failed to assign model from Model Manager: " + ex.Message);
                return false;
            }

            if (string.IsNullOrWhiteSpace(llm.model))
            {
                return false;
            }

            logger.LogInfo(GameLogFeature.Llm,
                "LLMUnity: model was empty; assigned model from Model Manager: " + llm.model);
            return true;
        }

        private static bool TryResolveModelFilePath(ModelEntry entry, out string fullPath)
        {
            fullPath = null;
            if (!string.IsNullOrWhiteSpace(entry.path) && File.Exists(entry.path))
            {
                fullPath = entry.path;
                return true;
            }

            string managerPath = LLMManager.GetAssetPath(entry.filename);
            if (!string.IsNullOrWhiteSpace(managerPath) && File.Exists(managerPath))
            {
                fullPath = managerPath;
                return true;
            }

            string assetPath = LLMUnitySetup.GetAssetPath(entry.filename);
            if (!string.IsNullOrWhiteSpace(assetPath) && File.Exists(assetPath))
            {
                fullPath = assetPath;
                return true;
            }

            string downloadPath = LLMUnitySetup.GetDownloadAssetPath(entry.filename);
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