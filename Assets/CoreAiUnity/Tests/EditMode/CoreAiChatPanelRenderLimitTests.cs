using System;
using CoreAI.Chat;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode tests for <see cref="CoreAiChatPanel.ClampAssistantForRender"/> — the WebGL GPU-buffer
    /// backstop that hard-caps oversized assistant text before it is rendered into a single bubble.
    /// Pure string logic, so these run on any supported Unity (6.0+).
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelRenderLimitTests
    {
        [Test]
        public void ClampAssistantForRender_ShortText_ReturnedUnchanged()
        {
            const string text = "A normal answer with `code`.";
            Assert.AreEqual(text, CoreAiChatPanel.ClampAssistantForRender(text));
        }

        [Test]
        public void ClampAssistantForRender_NullOrEmpty_PassesThrough()
        {
            Assert.IsNull(CoreAiChatPanel.ClampAssistantForRender(null));
            Assert.AreEqual(string.Empty, CoreAiChatPanel.ClampAssistantForRender(string.Empty));
        }

        [Test]
        public void ClampAssistantForRender_AtLimit_NotTruncated()
        {
            string text = new('a', CoreAiChatPanel.MaxAssistantRenderChars);
            Assert.AreEqual(text, CoreAiChatPanel.ClampAssistantForRender(text));
        }

        [Test]
        public void ClampAssistantForRender_OverLimit_Shortened()
        {
            // Mimics the prod incident: a ~16k-char model dump.
            string text = new('x', 16002);
            string result = CoreAiChatPanel.ClampAssistantForRender(text);

            Assert.Less(result.Length, text.Length, "Oversized assistant text must be shortened.");
            StringAssert.StartsWith(new string('x', CoreAiChatPanel.MaxAssistantRenderChars), result);
        }

        [Test]
        public void ClampAssistantForRender_TruncationInsideCodeFence_ClosesFence()
        {
            string text = "```python\n" + new string('y', CoreAiChatPanel.MaxAssistantRenderChars);
            string result = CoreAiChatPanel.ClampAssistantForRender(text);

            int fences = CountOccurrences(result, "```");
            Assert.AreEqual(0, fences % 2, "A truncated code fence must be closed so markdown stays valid.");
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            int count = 0;
            int index = 0;
            while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += needle.Length;
            }

            return count;
        }
    }
}
