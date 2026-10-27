#if COREAI_HAS_HUB || !UNITY_5_3_OR_NEWER
using System;
using CoreAI.Ai.Hub.CodeEditing;
using CoreAI.CodeEditing;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Protects Lua lexical context and the additive language-aware pair helper.</summary>
    public sealed class CodeEditorLanguagePolicyEditModeTests
    {
        [TestCase("local value = [[\nthen\n]]", "then", "")]
        [TestCase("local value = [=[\ndo\n]=]", "do", "")]
        [TestCase("local value = `body\nthen\n`", "then", "")]
        [TestCase("--[[\nfunction f()\n]]", "function f()", "")]
        [TestCase("--[==[\nelse\n]==]", "else", "")]
        [TestCase("local value = [[\n    then\n]]", "    then", "    ")]
        public void LuaEnter_DoesNotInterpretLongLiteralOrCommentBodyAsCode(string source, string line, string indent)
        {
            int caret = source.IndexOf(line, StringComparison.Ordinal) + line.Length;
            CodeEnterPlan plan = new LuaCodeLanguageProfile().PlanEnter(source, caret, caret);
            Assert.AreEqual(indent, plan.Indent);
            Assert.AreEqual("", plan.Tail);
            Assert.AreEqual(0, plan.TrimLength);
        }

        [TestCase("if ready then", "    ")]
        [TestCase("if ready then -- note", "    ")]
        [TestCase("    if ready then -- note", "        ")]
        [TestCase("for i = 1, 2 do -- note", "    ")]
        [TestCase("function f() -- note", "    ")]
        [TestCase("local value = 'then'", "")]
        public void LuaEnter_RetainsActualBlockOpenersAndInlineCommentBehavior(string source, string indent)
        {
            CodeEnterPlan plan = new LuaCodeLanguageProfile().PlanEnter(source, source.Length, source.Length);
            Assert.AreEqual(indent, plan.Indent);
        }

        [Test]
        public void LuaEnter_StopsAnalysisAtTheExistingDocumentBound()
        {
            string source = new string('x', CodeEditorLimits.MaximumAnalysisCharacters + 1) + " then";
            Assert.IsTrue(new LuaCodeLanguageProfile().PlanEnter(source, source.Length, source.Length).IsEmpty);
        }

        [Test]
        public void CallerLuaContextAllowsPairingAfterLengthOperator_WithoutChangingLegacyPythonComments()
        {
            const string lua = "local n = #f";
            CodeEditorTextUtility.AutoCloseEdit inserted = CodeEditorTextUtility.AutoClosePair(
                '(', lua, lua.Length, lua.Length, false, true,
                out string paired, out int first, out int second);
            Assert.AreEqual(CodeEditorTextUtility.AutoCloseEdit.Inserted, inserted);
            Assert.AreEqual(lua + "()", paired);
            Assert.AreEqual(lua.Length + 1, first);
            Assert.AreEqual(first, second);

            const string python = "# comment";
            CodeEditorTextUtility.AutoCloseEdit skipped = CodeEditorTextUtility.AutoClosePair(
                '(', python, python.Length, python.Length, false, out string unchanged, out _, out _);
            Assert.AreEqual(CodeEditorTextUtility.AutoCloseEdit.None, skipped);
            Assert.AreEqual(python, unchanged);
        }

        [Test]
        public void CallerNonCodeContextStillPreventsPairing_AndLegacyTypeOverRemainsAvailable()
        {
            CodeEditorTextUtility.AutoCloseEdit skipped = CodeEditorTextUtility.AutoClosePair(
                '(', "text", 4, 4, false, false, out string unchanged, out _, out _);
            Assert.AreEqual(CodeEditorTextUtility.AutoCloseEdit.None, skipped);
            Assert.AreEqual("text", unchanged);

            CodeEditorTextUtility.AutoCloseEdit typeOver = CodeEditorTextUtility.AutoClosePair(
                ')', "()", 1, 1, true, out string retained, out int first, out int second);
            Assert.AreEqual(CodeEditorTextUtility.AutoCloseEdit.TypeOver, typeOver);
            Assert.AreEqual("()", retained);
            Assert.AreEqual(2, first);
            Assert.AreEqual(first, second);
        }
    }
}
#endif
