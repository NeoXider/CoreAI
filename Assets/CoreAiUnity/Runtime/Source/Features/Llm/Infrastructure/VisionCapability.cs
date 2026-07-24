using System;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Model capability gate for vision / multimodal requests. Resolves whether images may be attached to
    /// user messages and whether vision tools (e.g. <c>capture_camera</c>) should be registered, based on
    /// an explicit <see cref="VisionSupportMode"/> plus a model-name heuristic for the
    /// <see cref="VisionSupportMode.Auto"/> case.
    /// </summary>
    public static class VisionCapability
    {
        // WHY: model ids that are clearly NOT chat models — embeddings, audio, rerank, moderation. In
        // Auto mode these are the only ids treated as text-only; everything else defaults to vision ON,
        // because modern local/chat models are overwhelmingly multimodal and a text-only turn never
        // attaches an image regardless (vision only matters once the camera path is actually used).
        private static readonly string[] NonVisionUtilityMarkers =
        {
            "embed", "embedding", "nomic-embed", "-bge", "gte-", "e5-",
            "rerank", "reranker",
            "whisper", "tts", "text-to-speech", "speech",
            "moderation", "-guard"
        };

        // Substrings of OpenAI-compatible / common provider model ids that ship vision today. Matched
        // case-insensitively against the configured model name. Used by the explicit "Detect vision"
        // name heuristic; Auto mode no longer relies on it (Auto defaults ON — see IsEnabled).
        private static readonly string[] VisionModelMarkers =
        {
            "gpt-4o", // gpt-4o, gpt-4o-mini (multimodal)
            "gpt-4.1", // gpt-4.1 family
            "gpt-4-turbo", // gpt-4-turbo (vision)
            "gpt-4-vision",
            "o1", // o1 / o1-mini reasoning (vision-capable)
            "o3",
            "o4",
            "vision",
            "-vl", // qwen-vl, qwen2-vl, internvl, etc.
            "vl-",
            "llava",
            "gemini", // gemini 1.5/2.x are multimodal
            "claude-3", // claude 3 / 3.5 / 3.7 are multimodal
            "claude-4",
            "claude-opus",
            "claude-sonnet",
            "pixtral",
            "llama-3.2", // llama 3.2 vision variants
            "phi-3-vision",
            "phi-4-multimodal"
        };

        /// <summary>
        /// Resolves the effective vision capability for the given mode and model name.
        /// <see cref="VisionSupportMode.On"/> / <see cref="VisionSupportMode.Off"/> are explicit;
        /// <see cref="VisionSupportMode.Auto"/> defaults to ON and only treats obvious non-chat utility
        /// models (embeddings/audio/rerank/moderation, see <see cref="AutoAssumesVisionCapable"/>) as
        /// text-only — so a multimodal local model whose name isn't in the marker list still gets vision.
        /// </summary>
        public static bool IsEnabled(VisionSupportMode mode, string modelName)
        {
            switch (mode)
            {
                case VisionSupportMode.On:
                    return true;
                case VisionSupportMode.Off:
                    return false;
                default:
                    return AutoAssumesVisionCapable(modelName);
            }
        }

        /// <summary>
        /// Auto-mode resolution: vision is ON by default, disabled only when the model name identifies a
        /// non-chat utility model (embeddings, audio, rerank, moderation). An empty name defaults to ON.
        /// </summary>
        public static bool AutoAssumesVisionCapable(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return true;
            }

            foreach (string marker in NonVisionUtilityMarkers)
            {
                if (modelName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Heuristic: returns <c>true</c> when <paramref name="modelName"/> contains a known
        /// vision-capable marker. Conservative — unknown models are treated as text-only.
        /// </summary>
        public static bool ModelLooksVisionCapable(string modelName)
        {
            if (string.IsNullOrWhiteSpace(modelName))
            {
                return false;
            }

            foreach (string marker in VisionModelMarkers)
            {
                if (modelName.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
