using System;
using System.Collections.Generic;
using CoreAI.LuaAssets;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class LuaRichTextFormatterEditModeTests
    {
        [Test]
        public void Escape_TextWithoutAngleBrackets_IsUnchanged()
        {
            Assert.AreEqual("local x = 1", LuaRichTextFormatter.Escape("local x = 1"));
        }

        [Test]
        public void Escape_LessThan_InsertsZeroWidthSpaceRightAfterIt()
        {
            string result = LuaRichTextFormatter.Escape("a < b");

            Assert.AreEqual("a <​ b", result);
        }

        [Test]
        public void Escape_LiteralColorTagLookingText_CanNeverFormARealTag()
        {
            string result = LuaRichTextFormatter.Escape("<color=red>evil</color>");

            // WHY: the escaped text must not contain an intact "<color" or "</color" run: a zero-width
            // space always follows every '<', which is exactly what breaks tag matching.
            StringAssert.DoesNotContain("<color", result);
            StringAssert.DoesNotContain("</color", result);
            // WHY: the visible characters (once the zero-width markers are stripped back out) are
            // untouched, so nothing is lost from what the user sees.
            Assert.AreEqual("<color=red>evil</color>", result.Replace("​", ""));
        }

        [Test]
        public void Escape_GreaterThanAndAmpersand_PassThroughUnchanged()
        {
            Assert.AreEqual("a > b", LuaRichTextFormatter.Escape("a > b"));
            Assert.AreEqual("a & b", LuaRichTextFormatter.Escape("a & b"));
        }

        [Test]
        public void Escape_NullOrEmpty_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, LuaRichTextFormatter.Escape(null));
            Assert.AreEqual(string.Empty, LuaRichTextFormatter.Escape(""));
        }

        [Test]
        public void Format_KeywordToken_IsWrappedInColorTag()
        {
            const string source = "local";
            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);

            string richText = LuaRichTextFormatter.Format(source, tokens, _ => "FF00FF");

            Assert.AreEqual("<color=#FF00FF>local</color>", richText);
        }

        [Test]
        public void Format_ColorLookupReturnsNull_RendersTokenUnstyled()
        {
            const string source = "local";
            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);

            string richText = LuaRichTextFormatter.Format(source, tokens, _ => null);

            Assert.AreEqual("local", richText);
        }

        [Test]
        public void Format_WhitespaceToken_IsNeverColorTagged()
        {
            const string source = "a b";
            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);

            string richText = LuaRichTextFormatter.Format(source, tokens, _ => "ABCDEF");

            StringAssert.Contains(" ", richText);
            // WHY: the space between the two colored identifiers is never inside a <color> tag of its own.
            Assert.AreEqual(2, CountOccurrences(richText, "<color=#ABCDEF>"));
        }

        [Test]
        public void Format_SourceContainingFakeTagInsideCode_StaysSafeAfterFormatting()
        {
            const string source = "local s = \"<color=red>evil</color>\"";
            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);

            string richText = LuaRichTextFormatter.Format(source, tokens, kind =>
                kind == LuaTokenKind.String ? "CE9178" : null);

            // WHY: our own wrapper tag for the string token is the only "<color" that should survive
            // intact; the user's embedded fake tag must have been neutralized by Escape.
            Assert.AreEqual(1, CountOccurrences(richText, "<color=#CE9178>"));
            StringAssert.Contains("<​color=red>", richText);
        }

        [Test]
        public void Format_EmptyTokenList_ReturnsEscapedSourceVerbatim()
        {
            const string source = "raw text";
            string richText = LuaRichTextFormatter.Format(source, new List<LuaToken>(), _ => "FFFFFF");

            Assert.AreEqual(source, richText);
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
