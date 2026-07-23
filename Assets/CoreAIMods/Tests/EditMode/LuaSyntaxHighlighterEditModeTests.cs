#if COREAI_HAS_HUB
using CoreAI.Ai.Hub;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Pure markup assertions for the Hub mod editor's <see cref="LuaSyntaxHighlighter"/>: keywords,
    /// comments, strings and numbers get wrapped in color tags, hostile '&lt;' input can never form a
    /// real rich-text tag, and null/empty input yields "".
    /// </summary>
    public sealed class LuaSyntaxHighlighterEditModeTests
    {
        [Test]
        public void Highlight_Keyword_IsWrappedInColorTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("local x = y");

            StringAssert.Contains("<color=#61AFEF>local</color>", result);
        }

        [Test]
        public void Highlight_LineComment_IsWrappedInColorTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("--comment\nprint(1)");

            StringAssert.Contains("<color=#8A929E>--comment</color>", result);
        }

        [Test]
        public void Highlight_StringLiteral_IsWrappedInColorTag()
        {
            string doubleQuoted = LuaSyntaxHighlighter.Highlight("local s = \"hello\"");
            string singleQuoted = LuaSyntaxHighlighter.Highlight("local s = 'hi'");

            StringAssert.Contains("<color=#98C379>\"hello\"</color>", doubleQuoted);
            StringAssert.Contains("<color=#98C379>'hi'</color>", singleQuoted);
        }

        [Test]
        public void Highlight_NumberLiteral_IsWrappedInColorTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("local n = 42");

            StringAssert.Contains("<color=#D19A66>42</color>", result);
        }

        [Test]
        public void Highlight_LessThanInSource_CannotFormAnInjectedTag()
        {
            string result = LuaSyntaxHighlighter.Highlight("local s = \"<color=red>evil</color>\"");

            // WHY: every '<' from user source must be followed by a zero-width space, so the literal
            // "<color"/"</color" runs can never be parsed as real rich-text tags; only the highlighter's
            // own "<color=#RRGGBB>" tags may survive intact.
            StringAssert.DoesNotContain("<color=red", result);
            StringAssert.Contains("<​color=red", result);
        }

        [Test]
        public void Highlight_ComparisonOperator_KeepsSourceCharactersVisible()
        {
            string result = LuaSyntaxHighlighter.Highlight("if a < b then end");

            // WHY: escaping only inserts invisible zero-width spaces — stripping them back out must
            // reproduce the visible source (minus the highlighter's own tags), so nothing the user typed
            // is lost or mangled on screen.
            string withoutMarkup = System.Text.RegularExpressions.Regex
                .Replace(result, "</?color[^>]*>", "")
                .Replace("​", "");
            Assert.AreEqual("if a < b then end", withoutMarkup);
        }

        [Test]
        public void Highlight_NullOrEmpty_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, LuaSyntaxHighlighter.Highlight(null));
            Assert.AreEqual(string.Empty, LuaSyntaxHighlighter.Highlight(""));
        }

        [Test]
        public void Highlight_IsDeterministic()
        {
            const string source = "-- tick\nlocal t = 1.5\nhooks_every(t, function() end)";

            Assert.AreEqual(LuaSyntaxHighlighter.Highlight(source), LuaSyntaxHighlighter.Highlight(source));
        }
    }
}
#endif
