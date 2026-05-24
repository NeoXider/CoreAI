#if COREAI_HAS_LLMUNITY && !UNITY_WEBGL
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Регрессия: GGUF из Core AI Settings должен маппиться на имя файла до fallback Model Manager.
    /// </summary>
    public sealed class LlmUnityGgufHintNormalizationEditModeTests
    {
        [Test]
        public void NormalizeGgufHintToFileName_TrimsWhitespace()
        {
            Assert.AreEqual("Qwen3.5-9B-Q4_K_M.gguf",
                LlmUnityModelBootstrap.NormalizeGgufHintToFileName("  Qwen3.5-9B-Q4_K_M.gguf  "));
        }

        [Test]
        public void NormalizeGgufHintToFileName_UsesBaseNameFromPath()
        {
            Assert.AreEqual("model.gguf",
                LlmUnityModelBootstrap.NormalizeGgufHintToFileName(@"C:\Users\me\models\model.gguf"));
        }

        [Test]
        public void NormalizeGgufHintToFileName_EmptyInput_ReturnsEmpty()
        {
            Assert.AreEqual("", LlmUnityModelBootstrap.NormalizeGgufHintToFileName(null));
            Assert.AreEqual("", LlmUnityModelBootstrap.NormalizeGgufHintToFileName(""));
            Assert.AreEqual("", LlmUnityModelBootstrap.NormalizeGgufHintToFileName("   "));
        }
    }
}
#endif