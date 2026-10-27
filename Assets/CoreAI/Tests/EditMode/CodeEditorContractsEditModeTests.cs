using System;
using System.Collections.Generic;
using System.Threading;
using CoreAI.CodeEditing;
using NUnit.Framework;

namespace CoreAI.Core.Tests.EditMode
{
    public sealed class CodeEditorContractsEditModeTests
    {
        private SynchronizationContext _previousSynchronizationContext;

        [SetUp]
        public void DetachSynchronizationContext()
        {
            _previousSynchronizationContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_previousSynchronizationContext);
        }

        [Test]
        public void TypedPrefixDoesNotReplaceTheFirstUsefulCompletionWithItself()
        {
            CodeCompletionEngine engine = new() { Profile = new TestProfile() };
            CodeCompletionResult result = engine.GetCompletions("alp", 3, true);
            Assert.AreEqual("alpha1", result.Items[0]);
            CollectionAssert.DoesNotContain(result.Items, "alp");
            Assert.IsFalse(result.PrefixIsCompleteWord);
        }

        [Test]
        public void EditorNormalizationPreservesWhitespaceInsideLuaStringData()
        {
            const string source = "local text = [=[a\tb\u00a0c]=]\n";
            string normalized = CodeEditorTextUtility.NormalizeEditorText(source, source.Length, out int caret);
            Assert.AreEqual(source, normalized);
            Assert.AreEqual(source.Length, caret);
            Assert.IsFalse(CodeEditorTextUtility.NeedsEditorNormalization(source));
            Assert.AreEqual(source, CodeEditorTextUtility.NormalizeEditorText(source.Replace("\n", "\r\n")));
        }

        [Test]
        public void ResultFromAnotherModCannotReuseTheSameRunFence()
        {
            CodeRunRequest first = new("doc", "lua", 2, "first", "return 1", CodeRunOperation.SaveAndRun,
                100, 500_000, null, "shared-run");
            CodeRunRequest second = new("doc", "lua", 2, "second", "return 1", CodeRunOperation.SaveAndRun,
                100, 500_000, null, "shared-run");
            CodeRunResult result = new(first, CodeRunStatus.Success, "ok");
            Assert.IsTrue(result.Matches(first));
            Assert.IsFalse(result.Matches(second));
        }

        [Test]
        public void LegacyRequestDefaultsRemainBoundedAndResultsKeepTheRevisionFence()
        {
            CodeRunRequest request = new("document", "lua", 7, "mod", "return true");
            Assert.AreEqual(CodeRunOperation.Run, request.Operation);
            Assert.AreEqual(100, request.TimeoutMs);
            Assert.AreEqual(500_000, request.MaxSteps);
            Assert.IsNull(request.ExpectedSource);
            CodeRunResult result = new(request, CodeRunStatus.Success, "ok");
            Assert.IsTrue(result.Matches(request));
            Assert.IsFalse(result.Matches(new CodeRunRequest("document", "lua", 8, "mod", request.Code)));
            Assert.IsFalse(result.Matches(new CodeRunRequest("document", "lua", 7, "mod", request.Code)));
        }

        [Test]
        public void RequestRejectsOversizedDocumentsAndUnboundedLimits()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new CodeRunRequest("d", "lua", 0, "m", "", timeoutMs: 0));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CodeRunRequest("d", "lua", 0, "m", "", timeoutMs: 1001));
            Assert.Throws<ArgumentOutOfRangeException>(() => new CodeRunRequest("d", "lua", 0, "m", "", maxSteps: 0));
            Assert.Throws<ArgumentException>(() => new CodeRunRequest("d", "lua", 0, "m",
                new string('a', CodeEditorLimits.MaximumDocumentCharacters + 1)));
        }

        [Test]
        public void PythonStubAlwaysReportsUnsupportedWithoutExecutionOrPersistence()
        {
            PythonCodeInterpreterStub interpreter = new();
            using CancellationTokenSource cancellation = new();
            cancellation.Cancel();
            CodeRunRequest request = new("document", "python", 3, "mod", "raise Exception()", CodeRunOperation.SaveAndRun);
            CodeRunResult result = interpreter.RunAsync(request, cancellation.Token).GetAwaiter().GetResult();
            Assert.AreEqual(CodeRuntimeCapabilities.None, interpreter.Capabilities);
            Assert.AreEqual(CodeRunStatus.Unsupported, result.Status);
            Assert.IsFalse(result.Executed);
            Assert.IsFalse(result.Persisted);
            Assert.IsTrue(result.Matches(request));
        }

        [Test]
        public void LanguageDraftsKeepIndependentTextSelectionsAndRevisions()
        {
            CodeEditorDocument document = new("doc", "local value = 1");
            document.Update("local value = 2", new CodeSelection(8, 2));
            long luaRevision = document.Revision;
            document.SelectLanguage("python");
            document.Update("value = 9", new CodeSelection(5, 1));
            document.SelectLanguage("lua");
            Assert.AreEqual("local value = 2", document.Text);
            Assert.AreEqual(luaRevision, document.Revision);
            Assert.AreEqual(8, document.Selection.Caret);
            Assert.AreEqual(2, document.Selection.Anchor);
        }

        [Test]
        public void HistoryCapsCombinedUndoAndRedoOnPushCoalesceAmendUndoAndRedo()
        {
            CodeEditorHistory history = new();
            string large = new('x', CodeEditorLimits.MaximumDocumentCharacters);
            for (int index = 0; index < 40; index++)
                history.Push(large, index, index * 1000L, true);
            Assert.LessOrEqual(history.RetainedCharacterCount, CodeEditorHistory.MaximumRetainedCharacters);
            while (history.CanUndo)
            {
                Assert.IsNotNull(history.Undo(large, 3, 1));
                Assert.LessOrEqual(history.RetainedCharacterCount, CodeEditorHistory.MaximumRetainedCharacters);
            }
            while (history.CanRedo)
            {
                Assert.IsNotNull(history.Redo());
                Assert.LessOrEqual(history.RetainedCharacterCount, CodeEditorHistory.MaximumRetainedCharacters);
            }
            history.Push("x", 1, 50_000);
            history.Push(large, 5, 50_001);
            Assert.LessOrEqual(history.RetainedCharacterCount, CodeEditorHistory.MaximumRetainedCharacters);
            history.AmendTop(large, 7);
            Assert.LessOrEqual(history.RetainedCharacterCount, CodeEditorHistory.MaximumRetainedCharacters);
            history.Clear();
            Assert.AreEqual(0, history.RetainedCharacterCount);
            Assert.IsFalse(history.CanUndo);
            Assert.IsFalse(history.CanRedo);
        }

        [Test]
        public void HistoryRoundTripsSelectionAndRejectsOversizedTextBeforeMutation()
        {
            CodeEditorHistory history = new();
            history.Push("first", 3, 0, true, 1);
            history.Push("second", 5, 1, true, 2);
            CodeEditorSnapshot? previous = history.Undo("second", 5, 2);
            Assert.AreEqual("first", previous.Value.Text);
            Assert.AreEqual(1, previous.Value.AnchorIndex);
            CodeEditorSnapshot? restored = history.Redo();
            Assert.AreEqual("second", restored.Value.Text);
            Assert.AreEqual(2, restored.Value.AnchorIndex);
            long retained = history.RetainedCharacterCount;
            Assert.Throws<ArgumentException>(() => history.AmendTop(
                new string('x', CodeEditorLimits.MaximumDocumentCharacters + 1), 0));
            Assert.AreEqual(retained, history.RetainedCharacterCount);
            Assert.AreEqual("second", history.CurrentText);
        }

        [Test]
        public void CompletionBoundsHugePrefixesAndUsesDeterministicMaximumItems()
        {
            CodeCompletionEngine completion = new() { Profile = new TestProfile() };
            string longIdentifier = new('a', CodeEditorLimits.MaximumDocumentCharacters);
            Assert.IsFalse(completion.GetCompletions(longIdentifier, longIdentifier.Length).HasItems);
            CodeCompletionResult result = completion.GetCompletions("al", 2);
            Assert.AreEqual(CodeCompletionEngine.MaxItems, result.Items.Count);
            Assert.AreEqual("alpha1", result.Items[0]);
            Assert.IsFalse(result.PrefixIsCompleteWord);
            Assert.IsTrue(CodeCompletionEngine.EnterAccepts(result, false));
            Assert.IsTrue(CodeCompletionEngine.EnterAccepts(result, true));
            CodeCompletionResult completeWord = completion.GetCompletions("alpha1", 6);
            Assert.IsTrue(completeWord.PrefixIsCompleteWord);
            Assert.IsFalse(CodeCompletionEngine.EnterAccepts(completeWord, false));
            Assert.IsTrue(CodeCompletionEngine.EnterAccepts(completeWord, true));
        }

        [Test]
        public void DiagnosticsAndOutputAreBoundedAndCopied()
        {
            List<CodeErrorLocation> diagnostics = new();
            for (int index = 0; index < 100; index++) diagnostics.Add(new CodeErrorLocation(index, 1, "error"));
            CodeRunResult result = new(CodeRunStatus.Failed, new string('x', 100_000), diagnostics: diagnostics);
            diagnostics.Clear();
            Assert.AreEqual(CodeEditorLimits.MaximumDiagnostics, result.Diagnostics.Count);
            Assert.Less(result.Message.Length, CodeEditorLimits.MaximumOutputCharacters + 100);
        }

        private sealed class TestProfile : ICodeLanguageProfile
        {
            public string LanguageId => "test";
            public string DisplayName => "test";
            public string LineCommentToken => "#";
            public IReadOnlyList<string> CompletionWords => new[] { "alpha9", "alpha8", "alpha7", "alpha6", "alpha5", "alpha4", "alpha3", "alpha2", "alpha1" };
            public string[] HighlightLines(string[] lines) => lines;
            public CodeEnterPlan PlanEnter(string text, int selectionStart, int selectionEnd) => default;
        }
    }
}
