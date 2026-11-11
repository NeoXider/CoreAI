using System.Collections.Generic;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatMarkdown
{
    /// <summary>
    /// The engine-free Markdown rules of chat answers: plain text for the overlay feed, render-safe Markdown for the
    /// optional renderer (finished and mid-stream), the stable prefix a streaming answer is rendered in, and pipe
    /// tables. Engine-free, so it also runs in the portable suite.
    /// </summary>
    [TestFixture]
    public sealed class ChatMarkdownTextEditModeTests
    {
        private const string Zw = "​";

        [Test]
        public void ToPlainText_TextWithoutMarkdown_IsReturnedUnchanged()
        {
            const string text = "Placing the block. Done, it is in front of you.";
            Assert.AreSame(text, ChatMarkdownText.ToPlainText(text));
            Assert.AreEqual("Hello ", ChatMarkdownText.ToPlainText("Hello "));
            Assert.AreEqual("", ChatMarkdownText.ToPlainText(null));
        }

        [Test]
        public void ToPlainText_RemovesInlineMarkers()
        {
            Assert.AreEqual("Use bold, italic and code here.",
                ChatMarkdownText.ToPlainText("Use **bold**, *italic* and `code` here."));
            Assert.AreEqual("See the docs and the logo.",
                ChatMarkdownText.ToPlainText("See [the docs](https://example.com) and ![the logo](logo.png)."));
            Assert.AreEqual("gone", ChatMarkdownText.ToPlainText("~~gone~~"));
            Assert.AreEqual("https://example.com", ChatMarkdownText.ToPlainText("<https://example.com>"));
            Assert.AreEqual("a*b", ChatMarkdownText.ToPlainText("a\\*b"));
        }

        [Test]
        public void ToPlainText_KeepsWhatIsNotAMarker()
        {
            Assert.AreEqual("call spawn_enemy_wave now", ChatMarkdownText.ToPlainText("call spawn_enemy_wave now"));
            Assert.AreEqual("5 * 3 = 15", ChatMarkdownText.ToPlainText("5 * 3 = 15"));
            Assert.AreEqual("C# and C++", ChatMarkdownText.ToPlainText("C# and C++"));
            Assert.AreEqual("well-known -- really", ChatMarkdownText.ToPlainText("well-known -- really"));
        }

        [Test]
        public void ToPlainText_RemovesBlockPrefixes_AndKeepsTheWords()
        {
            string plain = ChatMarkdownText.ToPlainText("## Plan\n\n> quoted\n- first\n* second\n1. third\n---\nend");
            Assert.AreEqual("Plan\n\nquoted\n• first\n• second\n1. third\nend", plain);
        }

        [Test]
        public void ToPlainText_FencedCode_KeepsTheCodeAndDropsTheFences()
        {
            string plain = ChatMarkdownText.ToPlainText("Run:\n```lua\nprint(\"**hi**\")\n```\nok");
            Assert.AreEqual("Run:\nprint(\"**hi**\")\nok", plain);
        }

        [Test]
        public void ToPlainText_UnterminatedFence_KeepsTheCodeVerbatim()
        {
            Assert.AreEqual("Code:\nlocal x = 1 -- *not italic*",
                ChatMarkdownText.ToPlainText("Code:\n```lua\nlocal x = 1 -- *not italic*"));
        }

        [Test]
        public void ToPlainText_MidStream_DropsMarkersThatAreStillOpen()
        {
            Assert.AreEqual("This is bol", ChatMarkdownText.ToPlainText("This is **bol"));
            Assert.AreEqual("use foo", ChatMarkdownText.ToPlainText("use `foo"));
            Assert.AreEqual("see the docs", ChatMarkdownText.ToPlainText("see [the docs](https://exa"));
        }

        [Test]
        public void ToPlainText_Table_KeepsCellsAndDropsTheDelimiterRow()
        {
            Assert.AreEqual("Name | Score\nAda | 3", ChatMarkdownText.ToPlainText("| Name | Score |\n|---|--:|\n| **Ada** | 3 |"));
        }

        [Test]
        public void PrepareForRender_UnterminatedFence_IsClosed()
        {
            Assert.AreEqual("```python\nprint(1)\n```", ChatMarkdownText.PrepareForRender("```python\nprint(1)", false));
            Assert.AreEqual("```python\nprint(1)\n```", ChatMarkdownText.PrepareForRender("```python\nprint(1)", true));
            Assert.AreEqual("~~~~\nx\n~~~~", ChatMarkdownText.PrepareForRender("~~~~\nx", true));
        }

        [Test]
        public void PrepareForRender_ClosedFence_IsLeftAlone()
        {
            const string markdown = "Text\n\n```cs\nvar a = \"<b>\";\n```\n\nMore";
            Assert.AreEqual(markdown, ChatMarkdownText.PrepareForRender(markdown, false));
        }

        [Test]
        public void PrepareForRender_Streaming_PartialClosingFenceWaits_ButTheOpenerStays()
        {
            Assert.AreEqual("```\ncode\n\n```", ChatMarkdownText.PrepareForRender("```\ncode\n``", true));
            Assert.AreEqual("```\n```", ChatMarkdownText.PrepareForRender("```", true));
            Assert.AreEqual("```\ncode\n```", ChatMarkdownText.PrepareForRender("```\ncode\n```", true),
                "A closing fence that just arrived is not a dangling backtick run.");
        }

        [Test]
        public void PrepareForRender_Images_BecomeLinks_UnlessMediaIsAllowed()
        {
            Assert.AreEqual("Look [cat](https://example.com/cat.png)",
                ChatMarkdownText.PrepareForRender("Look ![cat](https://example.com/cat.png)", false));
            Assert.AreEqual("Look ![cat](cat.png)", ChatMarkdownText.PrepareForRender("Look ![cat](cat.png)", false, true));
            Assert.AreEqual("`![cat](cat.png)`", ChatMarkdownText.PrepareForRender("`![cat](cat.png)`", false));
        }

        [Test]
        public void PrepareForRender_TagsCannotRestyleTheUiOrHideText()
        {
            Assert.AreEqual("big <" + Zw + "size=500>x", ChatMarkdownText.PrepareForRender("big <size=500>x", false));
            Assert.AreEqual("<" + Zw + "#ff0000>red", ChatMarkdownText.PrepareForRender("<#ff0000>red", false));
            Assert.AreEqual("List<" + Zw + "int> works", ChatMarkdownText.PrepareForRender("List<int> works", false));
            Assert.AreEqual("<" + Zw + "div>block", ChatMarkdownText.PrepareForRender("<div>block", false));
            Assert.AreEqual("a b", ChatMarkdownText.PrepareForRender("a<br/>b", false));
            Assert.AreEqual("`<size=9>`", ChatMarkdownText.PrepareForRender("`<size=9>`", false));
            Assert.AreEqual("a < b", ChatMarkdownText.PrepareForRender("a < b", false));
        }

        [Test]
        public void PrepareForRender_AutolinksAndEntities_BecomeWhatTheRendererCanShow()
        {
            Assert.AreEqual("go [https://a.io/x\\_y](<https://a.io/x_y>)",
                ChatMarkdownText.PrepareForRender("go <https://a.io/x_y>", false));
            Assert.AreEqual("Tom \\& Jerry \\> <" + Zw + "3",
                ChatMarkdownText.PrepareForRender("Tom &amp; Jerry &gt; &lt;3", false));
        }

        [Test]
        public void PrepareForRender_FrontMatterFence_IsDefused()
        {
            Assert.AreEqual("\n---\nuss: Assets/x.uss\n---", ChatMarkdownText.PrepareForRender("---\nuss: Assets/x.uss\n---", false));
        }

        [Test]
        public void PrepareForRender_FenceInsideListItem_IsLiftedToTheTopLevel()
        {
            string prepared = ChatMarkdownText.PrepareForRender("1. Install:\n   ```bash\n   npm i\n   ```\n2. Run", false);
            Assert.AreEqual("1. Install:\n\n```bash\nnpm i\n```\n\n2. Run", prepared);
        }

        [Test]
        public void PrepareForRender_Streaming_HidesMarkersStillBeingTyped()
        {
            Assert.AreEqual("This is bol", ChatMarkdownText.PrepareForRender("This is **bol", true));
            Assert.AreEqual("Hello ", ChatMarkdownText.PrepareForRender("Hello **", true));
            Assert.AreEqual("use foo", ChatMarkdownText.PrepareForRender("use `foo", true));
            Assert.AreEqual("see docs", ChatMarkdownText.PrepareForRender("see [docs](https://exa", true));
            Assert.AreEqual("**done** and more", ChatMarkdownText.PrepareForRender("**done** and more", true));
            Assert.AreEqual("- item one\n- it", ChatMarkdownText.PrepareForRender("- item one\n- *it", true));
        }

        [Test]
        public void PrepareForRender_Streaming_LoneBlockMarkerWaitsForTheNextChunk()
        {
            Assert.AreEqual("Title\n", ChatMarkdownText.PrepareForRender("Title\n---", true));
            Assert.AreEqual("Intro\n", ChatMarkdownText.PrepareForRender("Intro\n##", true));
            Assert.AreEqual("Steps\n", ChatMarkdownText.PrepareForRender("Steps\n1.", true));
        }

        [Test]
        public void PrepareForRender_Finished_KeepsAnUnmatchedMarkerAsText()
        {
            Assert.AreEqual("use *args here", ChatMarkdownText.PrepareForRender("use *args here", false));
            Assert.AreEqual("Title\n---", ChatMarkdownText.PrepareForRender("Title\n---", false));
        }

        [Test]
        public void FindStablePrefixLength_SingleOpenBlock_IsZero()
        {
            Assert.AreEqual(0, ChatMarkdownText.FindStablePrefixLength("Only one paragraph so far"));
            Assert.AreEqual(0, ChatMarkdownText.FindStablePrefixLength("Para one.\n\n"));
            Assert.AreEqual(0, ChatMarkdownText.FindStablePrefixLength(null));
        }

        [Test]
        public void FindStablePrefixLength_EndsWhereTheLastBlockAfterABlankLineStarts()
        {
            const string text = "Para one.\n\nPara two.\n\nPara thr";
            Assert.AreEqual(text.IndexOf("Para two.", System.StringComparison.Ordinal),
                ChatMarkdownText.FindStablePrefixLength(text),
                "The line still being typed cannot start a stable block yet.");
            Assert.AreEqual(text.IndexOf("Para thr", System.StringComparison.Ordinal),
                ChatMarkdownText.FindStablePrefixLength(text + "ee.\n"));
        }

        [Test]
        public void FindStablePrefixLength_NeverInsideAnOpenFence()
        {
            const string text = "Intro\n\n```lua\nlocal a = 1\n\nlocal b = 2\n";
            Assert.AreEqual(text.IndexOf("```", System.StringComparison.Ordinal),
                ChatMarkdownText.FindStablePrefixLength(text));
        }

        [Test]
        public void FindStablePrefixLength_AfterAClosedFence_MovesPastIt()
        {
            const string text = "```lua\nlocal a = 1\n\nlocal b = 2\n```\n\nNext\n";
            Assert.AreEqual(text.IndexOf("Next", System.StringComparison.Ordinal),
                ChatMarkdownText.FindStablePrefixLength(text));
        }

        [Test]
        public void FindStablePrefixLength_NeverSplitsAListOrAnIndentedContinuation()
        {
            Assert.AreEqual(0, ChatMarkdownText.FindStablePrefixLength("1. one\n\n2. two\n\n   more of two"));
            Assert.AreEqual(0, ChatMarkdownText.FindStablePrefixLength("Intro\n\n- a\n\n- b"),
                "A list item never starts a stable block: the renderer numbers a list on its own.");
            const string afterList = "- a\n- b\n\nAfter the list\n";
            Assert.AreEqual(afterList.IndexOf("After", System.StringComparison.Ordinal),
                ChatMarkdownText.FindStablePrefixLength(afterList));
        }

        [Test]
        public void EveryPrefixOfAStreamedAnswer_KeepsFencesClosed_AndTheStablePrefixOnlyGrows()
        {
            const string answer = "## Build plan\n\n1. Spawn a **red** block.\n2. Attach the `Spinner` script:\n\n" +
                                  "```lua\nlocal part = script.Parent\n\nprint(\"hit\")\n```\n\n" +
                                  "| Step | Status |\n|---|:-:|\n| Block | done |\n\n> Tip: use `/undo` and [docs](https://example.com).\n";
            int previousStable = 0;
            for (int length = 1; length <= answer.Length; length++)
            {
                string prefix = answer.Substring(0, length);
                int stable = ChatMarkdownText.FindStablePrefixLength(prefix);
                Assert.GreaterOrEqual(stable, previousStable, "The stable prefix shrank at length " + length);
                Assert.IsTrue(stable == 0 || prefix.Substring(0, stable).EndsWith("\n\n", System.StringComparison.Ordinal),
                    "A stable prefix ends after a blank line (length " + length + ")");
                previousStable = stable;

                string tail = ChatMarkdownText.PrepareForRender(prefix.Substring(stable), true);
                Assert.AreEqual(0, CountFenceLines(tail) % 2, "An open fence reached the renderer at length " + length);
                Assert.IsFalse(tail.TrimEnd().EndsWith("**", System.StringComparison.Ordinal) &&
                               !prefix.TrimEnd().EndsWith("**red**", System.StringComparison.Ordinal),
                    "A bare emphasis marker reached the renderer at length " + length);
            }

            Assert.AreEqual(answer.IndexOf("> Tip", System.StringComparison.Ordinal),
                ChatMarkdownText.FindStablePrefixLength(answer));
        }

        private static int CountFenceLines(string markdown)
        {
            int count = 0;
            foreach (string line in markdown.Split('\n'))
            {
                if (line.TrimStart().StartsWith("```", System.StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        [Test]
        public void StreamingRefreshSeconds_GrowsWithTheTail_WithinBounds()
        {
            Assert.AreEqual(ChatMarkdownText.MinStreamingRefreshSeconds, ChatMarkdownText.StreamingRefreshSeconds(0), 1e-9);
            Assert.Greater(ChatMarkdownText.StreamingRefreshSeconds(2000), ChatMarkdownText.StreamingRefreshSeconds(10));
            Assert.AreEqual(ChatMarkdownText.MaxStreamingRefreshSeconds, ChatMarkdownText.StreamingRefreshSeconds(1000000), 1e-9);
        }

        [Test]
        public void SplitTables_ReadsHeaderAlignmentAndRows()
        {
            IReadOnlyList<ChatMarkdownSegment> segments =
                ChatMarkdownText.SplitTables("Scores:\n\n| Name | Score | Note |\n|:--|--:|:-:|\n| Ada | 3 | `a|b` |\n| Bo |\n\nAfter");

            Assert.AreEqual(3, segments.Count);
            Assert.IsFalse(segments[0].IsTable);
            Assert.AreEqual("Scores:\n", segments[0].Markdown);
            ChatMarkdownSegment table = segments[1];
            Assert.IsTrue(table.IsTable);
            CollectionAssert.AreEqual(new[] { "Name", "Score", "Note" }, table.Header);
            CollectionAssert.AreEqual(new[]
            {
                ChatMarkdownTableAlignment.Left, ChatMarkdownTableAlignment.Right, ChatMarkdownTableAlignment.Center
            }, table.Alignments);
            Assert.AreEqual(2, table.Rows.Count);
            CollectionAssert.AreEqual(new[] { "Ada", "3", "`a|b`" }, table.Rows[0]);
            CollectionAssert.AreEqual(new[] { "Bo", "", "" }, table.Rows[1], "Short rows are padded to the header.");
            Assert.AreEqual("\nAfter", segments[2].Markdown);
        }

        [Test]
        public void SplitTables_PipesWithoutADelimiterRow_OrInsideAFence_AreNotATable()
        {
            Assert.IsFalse(ChatMarkdownText.SplitTables("a | b\nc | d")[0].IsTable);
            IReadOnlyList<ChatMarkdownSegment> fenced = ChatMarkdownText.SplitTables("```\n| a | b |\n|---|---|\n```");
            Assert.AreEqual(1, fenced.Count);
            Assert.IsFalse(fenced[0].IsTable);
            Assert.IsFalse(ChatMarkdownText.SplitTables("| a | b |\n|---|")[0].IsTable,
                "A delimiter row with fewer cells than the header is not a table yet.");
        }
    }
}
