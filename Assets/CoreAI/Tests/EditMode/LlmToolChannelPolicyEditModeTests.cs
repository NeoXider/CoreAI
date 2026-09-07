using CoreAI.Ai;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The prose-vs-native tool-channel decision exists in exactly one spelling:
    /// <see cref="LlmToolChannelPolicy.InterpretsProse"/>. The streaming loop
    /// (<c>SmartToolCallingChatClient</c>), the coarse request gate
    /// (<c>MeaiLlmClient.InterpretsProseAsToolCalls</c>) and the orchestration gate
    /// (<c>AiOrchestrator.IsTextShapedToolChannel</c>) all feed this predicate; the
    /// call sites only add their own inputs (tools declared? native capability of
    /// this role?). This fixture pins the shared truth table so a "small clarification"
    /// in one gate cannot silently diverge from the other two.
    /// </summary>
    [TestFixture]
    public sealed class LlmToolChannelPolicyEditModeTests
    {
        [Test]
        public void NoneMode_NeverInterpretsProse(
            [Values(true, false)] bool native,
            [Values(true, false)] bool? fallback)
        {
            Assert.IsFalse(LlmToolChannelPolicy.InterpretsProse(native, fallback, LlmToolChoiceMode.None));
        }

        [Test]
        public void TextEndpoint_InterpretsProse_InEveryToolModeExceptNone(
            [Values(LlmToolChoiceMode.Auto, LlmToolChoiceMode.RequireAny, LlmToolChoiceMode.RequireSpecific)] LlmToolChoiceMode mode)
        {
            Assert.IsTrue(LlmToolChannelPolicy.InterpretsProse(supportsNativeToolCalling: false, explicitNativeFallback: null, toolMode: mode));
            Assert.IsTrue(LlmToolChannelPolicy.InterpretsProse(supportsNativeToolCalling: false, explicitNativeFallback: false, toolMode: mode));
        }

        [Test]
        public void NativeEndpoint_WithoutExplicitFallback_NeverInterpretsProse(
            [Values(LlmToolChoiceMode.Auto, LlmToolChoiceMode.RequireAny, LlmToolChoiceMode.RequireSpecific)] LlmToolChoiceMode mode)
        {
            Assert.IsFalse(LlmToolChannelPolicy.InterpretsProse(supportsNativeToolCalling: true, explicitNativeFallback: null, toolMode: mode));
            Assert.IsFalse(LlmToolChannelPolicy.InterpretsProse(supportsNativeToolCalling: true, explicitNativeFallback: false, toolMode: mode));
        }

        [Test]
        public void NativeEndpoint_WithExplicitFallback_InterpretsProse_InEveryToolModeExceptNone(
            [Values(LlmToolChoiceMode.Auto, LlmToolChoiceMode.RequireAny, LlmToolChoiceMode.RequireSpecific)] LlmToolChoiceMode mode)
        {
            Assert.IsTrue(LlmToolChannelPolicy.InterpretsProse(supportsNativeToolCalling: true, explicitNativeFallback: true, toolMode: mode));
        }

        [Test]
        public void DefaultToolMode_IsAuto()
        {
            Assert.IsTrue(LlmToolChannelPolicy.InterpretsProse(supportsNativeToolCalling: false, explicitNativeFallback: null));
            Assert.IsFalse(LlmToolChannelPolicy.InterpretsProse(supportsNativeToolCalling: true, explicitNativeFallback: null));
        }
    }
}
