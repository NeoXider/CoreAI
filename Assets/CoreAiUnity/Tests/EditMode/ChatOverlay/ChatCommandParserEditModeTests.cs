using System.Collections.Generic;
using CoreAI.ChatOverlay;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.ChatOverlay
{
    /// <summary>
    /// The engine-free slash-command parser: what is a command, what is text for the model, and how arguments
    /// split. Runs in Unity EditMode and in the portable suite.
    /// </summary>
    [TestFixture]
    public sealed class ChatCommandParserEditModeTests
    {
        [Test]
        public void Classify_PlainText_IsTextForTheModel_Trimmed()
        {
            ChatInputKind kind = ChatCommandParser.Classify("  build a tower here  ", '/', out _, out string text);

            Assert.AreEqual(ChatInputKind.Text, kind);
            Assert.AreEqual("build a tower here", text);
        }

        [Test]
        public void Classify_WhitespaceOnly_IsEmpty()
        {
            Assert.AreEqual(ChatInputKind.Empty, ChatCommandParser.Classify("   ", '/', out _, out _));
            Assert.AreEqual(ChatInputKind.Empty, ChatCommandParser.Classify(null, '/', out _, out _));
        }

        [Test]
        public void Classify_SlashLine_IsCommandWithNameAndArguments()
        {
            ChatInputKind kind = ChatCommandParser.Classify("/agent  Programmer", '/',
                out ChatCommandInvocation invocation, out string text);

            Assert.AreEqual(ChatInputKind.Command, kind);
            Assert.AreEqual("", text);
            Assert.AreEqual("agent", invocation.Name);
            Assert.AreEqual("Programmer", invocation.ArgumentText);
            CollectionAssert.AreEqual(new[] { "Programmer" }, invocation.Arguments);
            Assert.AreEqual("/agent  Programmer", invocation.RawText);
        }

        [Test]
        public void Classify_SlashInsideText_IsStillText()
        {
            ChatInputKind kind = ChatCommandParser.Classify("make it 1/2 the size", '/', out _, out string text);

            Assert.AreEqual(ChatInputKind.Text, kind);
            Assert.AreEqual("make it 1/2 the size", text);
        }

        [Test]
        public void Classify_DoubledPrefix_EscapesToTextWithOneSlash()
        {
            ChatInputKind kind = ChatCommandParser.Classify("//help is not a command", '/', out _, out string text);

            Assert.AreEqual(ChatInputKind.Text, kind);
            Assert.AreEqual("/help is not a command", text);
        }

        [Test]
        public void Classify_BarePrefix_IsCommandWithEmptyName()
        {
            ChatInputKind kind = ChatCommandParser.Classify("/", '/', out ChatCommandInvocation invocation, out _);

            Assert.AreEqual(ChatInputKind.Command, kind);
            Assert.AreEqual("", invocation.Name);
            Assert.IsFalse(invocation.HasArguments);
        }

        [Test]
        public void Classify_CustomPrefix_OnlyThatPrefixStartsCommands()
        {
            Assert.AreEqual(ChatInputKind.Command, ChatCommandParser.Classify("!fly", '!', out _, out _));
            Assert.AreEqual(ChatInputKind.Text, ChatCommandParser.Classify("/fly", '!', out _, out _));
        }

        [Test]
        public void Tokenize_QuotedRun_IsOneArgumentWithoutQuotes()
        {
            IReadOnlyList<string> tokens = ChatCommandParser.Tokenize("say \"hello there\" twice");

            CollectionAssert.AreEqual(new[] { "say", "hello there", "twice" }, tokens);
        }

        [Test]
        public void Tokenize_UnterminatedQuote_RunsToTheEnd()
        {
            IReadOnlyList<string> tokens = ChatCommandParser.Tokenize("name \"Big tower");

            CollectionAssert.AreEqual(new[] { "name", "Big tower" }, tokens);
        }

        [Test]
        public void ArgumentAt_OutOfRange_IsEmpty()
        {
            ChatCommandInvocation invocation = ChatCommandParser.ParseInvocation("/time noon");

            Assert.AreEqual("noon", invocation.ArgumentAt(0));
            Assert.AreEqual("", invocation.ArgumentAt(1));
            Assert.AreEqual("", invocation.ArgumentAt(-1));
        }

        [Test]
        public void IsCommand_MatchesClassify()
        {
            Assert.IsTrue(ChatCommandParser.IsCommand("/stop"));
            Assert.IsFalse(ChatCommandParser.IsCommand("stop"));
            Assert.IsFalse(ChatCommandParser.IsCommand("//stop"));
        }
    }
}
