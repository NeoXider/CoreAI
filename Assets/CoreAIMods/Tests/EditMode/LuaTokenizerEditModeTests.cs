using System.Collections.Generic;
using CoreAI.LuaAssets;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Classification truth table for <see cref="LuaTokenizer"/>: for a given snippet, the first
    /// non-whitespace token of interest must carry the expected <see cref="LuaTokenKind"/> and cover the
    /// expected source span.
    /// </summary>
    public sealed class LuaTokenizerEditModeTests
    {
        private static List<LuaToken> Significant(string source)
        {
            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);
            tokens.RemoveAll(t => t.Kind == LuaTokenKind.Whitespace);
            return tokens;
        }

        [Test]
        public void Tokenize_ReservedWord_IsKeyword()
        {
            List<LuaToken> tokens = Significant("local");
            Assert.AreEqual(1, tokens.Count);
            Assert.AreEqual(LuaTokenKind.Keyword, tokens[0].Kind);
        }

        [Test]
        public void Tokenize_OrdinaryWord_IsIdentifier()
        {
            List<LuaToken> tokens = Significant("localVariable");
            Assert.AreEqual(1, tokens.Count);
            Assert.AreEqual(LuaTokenKind.Identifier, tokens[0].Kind);
        }

        [Test]
        public void Tokenize_RobloxGlobal_IsGlobalNotIdentifier()
        {
            List<LuaToken> tokens = Significant("task.wait(1)");
            Assert.AreEqual(LuaTokenKind.Global, tokens[0].Kind);
            Assert.AreEqual("task", tokens[0].GetText("task.wait(1)"));
        }

        [Test]
        public void Tokenize_IdentifierFollowedByParen_IsFunctionCall()
        {
            List<LuaToken> tokens = Significant("print(\"hi\")");
            Assert.AreEqual(LuaTokenKind.FunctionCall, tokens[0].Kind);
        }

        [Test]
        public void Tokenize_IdentifierNotFollowedByParen_IsIdentifier()
        {
            List<LuaToken> tokens = Significant("print = nil");
            Assert.AreEqual(LuaTokenKind.Identifier, tokens[0].Kind);
        }

        [Test]
        public void Tokenize_StringWithEmbeddedCommentMarker_StaysOneStringToken()
        {
            const string source = "local s = \"a -- not a comment\"";
            List<LuaToken> tokens = Significant(source);

            LuaToken stringToken = tokens[tokens.Count - 1];
            Assert.AreEqual(LuaTokenKind.String, stringToken.Kind);
            Assert.AreEqual("\"a -- not a comment\"", stringToken.GetText(source));
        }

        [Test]
        public void Tokenize_LineCommentContainingQuotes_StaysOneCommentToken()
        {
            const string source = "-- this has \"quotes\" and 'apostrophes' inside\nlocal x = 1";
            List<LuaToken> tokens = Significant(source);

            Assert.AreEqual(LuaTokenKind.Comment, tokens[0].Kind);
            Assert.AreEqual("-- this has \"quotes\" and 'apostrophes' inside", tokens[0].GetText(source));
            // WHY: the quotes inside the comment must not have started a string token.
            Assert.IsFalse(tokens.Exists(t => t.Kind == LuaTokenKind.String));
        }

        [Test]
        public void Tokenize_LongComment_IsSingleCommentToken()
        {
            const string source = "--[[ this is\na block comment ]]\nlocal x = 1";
            List<LuaToken> tokens = Significant(source);

            Assert.AreEqual(LuaTokenKind.Comment, tokens[0].Kind);
            Assert.AreEqual("--[[ this is\na block comment ]]", tokens[0].GetText(source));
        }

        [Test]
        public void Tokenize_LongCommentWithEqualsLevel_MatchesCorrectCloser()
        {
            const string source = "--[==[ contains ]] and ]=] but not the real end ]==]\nlocal x = 1";
            List<LuaToken> tokens = Significant(source);

            Assert.AreEqual(LuaTokenKind.Comment, tokens[0].Kind);
            Assert.AreEqual(
                "--[==[ contains ]] and ]=] but not the real end ]==]",
                tokens[0].GetText(source));
        }

        [Test]
        public void Tokenize_LongString_IsLongStringToken()
        {
            const string source = "local s = [[multi\nline]]";
            List<LuaToken> tokens = Significant(source);

            LuaToken longString = tokens[tokens.Count - 1];
            Assert.AreEqual(LuaTokenKind.LongString, longString.Kind);
            Assert.AreEqual("[[multi\nline]]", longString.GetText(source));
        }

        [Test]
        public void Tokenize_LongStringWithEqualsLevel_MatchesCorrectCloser()
        {
            const string source = "local s = [=[ has ]] inside ]=]";
            List<LuaToken> tokens = Significant(source);

            LuaToken longString = tokens[tokens.Count - 1];
            Assert.AreEqual(LuaTokenKind.LongString, longString.Kind);
            Assert.AreEqual("[=[ has ]] inside ]=]", longString.GetText(source));
        }

        [TestCase("42")]
        [TestCase("3.14")]
        [TestCase(".5")]
        [TestCase("1e10")]
        [TestCase("1.5e-3")]
        [TestCase("0x1A")]
        [TestCase("0XFF")]
        [TestCase("100_000")]
        public void Tokenize_NumberLiterals_AreClassifiedAsNumber(string literal)
        {
            List<LuaToken> tokens = Significant(literal);
            Assert.AreEqual(1, tokens.Count, $"expected a single token for '{literal}'");
            Assert.AreEqual(LuaTokenKind.Number, tokens[0].Kind);
            Assert.AreEqual(literal, tokens[0].GetText(literal));
        }

        [Test]
        public void Tokenize_HexNumber_DoesNotSwallowTrailingIdentifier()
        {
            const string source = "0x1A + x";
            List<LuaToken> tokens = Significant(source);

            Assert.AreEqual(LuaTokenKind.Number, tokens[0].Kind);
            Assert.AreEqual("0x1A", tokens[0].GetText(source));
        }

        [Test]
        public void Tokenize_BacktickInterpolatedString_IsSingleInterpolatedToken()
        {
            const string source = "local s = `Hello {name}, you are {age} years old`";
            List<LuaToken> tokens = Significant(source);

            LuaToken interpolated = tokens[tokens.Count - 1];
            Assert.AreEqual(LuaTokenKind.InterpolatedString, interpolated.Kind);
            Assert.AreEqual(
                "`Hello {name}, you are {age} years old`",
                interpolated.GetText(source));
        }

        [Test]
        public void Tokenize_UnterminatedInterpolatedString_ConsumesToEndWithoutHanging()
        {
            const string source = "local s = `unterminated {expr}";
            List<LuaToken> tokens = LuaTokenizer.Tokenize(source);

            Assert.Greater(tokens.Count, 0);
            LuaToken last = tokens[tokens.Count - 1];
            Assert.AreEqual(source.Length, last.Start + last.Length);
        }

        [Test]
        public void Tokenize_MethodCallColon_IsOperatorNotTypeAnnotation()
        {
            const string source = "obj:method()";
            List<LuaToken> tokens = Significant(source);

            LuaToken colon = tokens.Find(t => t.GetText(source) == ":");
            Assert.AreEqual(LuaTokenKind.Operator, colon.Kind);
        }

        [Test]
        public void Tokenize_LocalTypeAnnotationColon_IsTypeAnnotation()
        {
            const string source = "local x: number = 5";
            List<LuaToken> tokens = Significant(source);

            LuaToken colon = tokens.Find(t => t.GetText(source) == ":");
            Assert.AreEqual(LuaTokenKind.TypeAnnotation, colon.Kind);
        }

        [Test]
        public void Tokenize_ParameterTypeAnnotationColon_IsTypeAnnotation()
        {
            const string source = "function foo(x: number) end";
            List<LuaToken> tokens = Significant(source);

            LuaToken colon = tokens.Find(t => t.GetText(source) == ":");
            Assert.AreEqual(LuaTokenKind.TypeAnnotation, colon.Kind);
        }

        [Test]
        public void Tokenize_ReturnTypeAnnotationColon_IsTypeAnnotation()
        {
            const string source = "function foo(): string return \"\" end";
            List<LuaToken> tokens = Significant(source);

            // WHY: the colon immediately after the closing ')' of the parameter list is the return-type colon.
            int closeParenIndex = tokens.FindIndex(t => t.GetText(source) == ")");
            LuaToken colon = tokens[closeParenIndex + 1];
            Assert.AreEqual(":", colon.GetText(source));
            Assert.AreEqual(LuaTokenKind.TypeAnnotation, colon.Kind);
        }

        [Test]
        public void Tokenize_ContinueKeyword_IsKeyword()
        {
            List<LuaToken> tokens = Significant("continue");
            Assert.AreEqual(LuaTokenKind.Keyword, tokens[0].Kind);
        }

        [Test]
        public void Tokenize_GotoKeyword_IsKeyword()
        {
            List<LuaToken> tokens = Significant("goto");
            Assert.AreEqual(LuaTokenKind.Keyword, tokens[0].Kind);
        }

        [Test]
        public void Tokenize_CompoundAssignPlusEquals_IsSingleOperatorToken()
        {
            const string source = "x += 1";
            List<LuaToken> tokens = Significant(source);

            LuaToken op = tokens[1];
            Assert.AreEqual(LuaTokenKind.Operator, op.Kind);
            Assert.AreEqual("+=", op.GetText(source));
        }

        [Test]
        public void Tokenize_EmptySource_ReturnsNoTokens()
        {
            Assert.AreEqual(0, LuaTokenizer.Tokenize("").Count);
            Assert.AreEqual(0, LuaTokenizer.Tokenize(null).Count);
        }
    }
}
