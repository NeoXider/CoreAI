#if COREAI_HAS_HUB
using System;
using System.Collections;
using System.Linq;
using CoreAI.Ai.Hub.CodeEditing;
using CoreAI.CodeEditing;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>Exercises the adapted editor through a real attached UI Toolkit panel and native keys.</summary>
    public sealed class CodeEditorUiEditModeTests
    {
        private sealed class EditorHostWindow : EditorWindow { }
        private EditorHostWindow _window;
        private CodeEditorSurface _editor;

        [TearDown]
        public void TearDown()
        {
            _editor?.Dispose();
            if (_window != null) _window.Close();
            _editor = null;
            _window = null;
        }

        [UnityTest]
        public IEnumerator NativeEnter_UsesLuaIndentAndSingleUndoRedo()
        {
            const string initial = "if ready then";
            yield return Attach(initial);
            _editor.View.Selection = new CodeSelection(initial.Length, initial.Length);
            Key(KeyCode.Return, '\n');
            yield return SettleText(initial + "\n    ");
            Assert.AreEqual(initial.Length + 5, _editor.TextField.cursorIndex);

            Key(KeyCode.Z, '\0', EventModifiers.Control);
            yield return SettleText(initial);
            Key(KeyCode.Y, '\0', EventModifiers.Control);
            yield return SettleText(initial + "\n    ");
        }

        [UnityTest]
        public IEnumerator NativeTab_AndLuaLineCommentAreReversible()
        {
            yield return Attach("local n = 1");
            _editor.View.Selection = new CodeSelection(0, 0);
            Key(KeyCode.Tab, '\t');
            yield return SettleText("    local n = 1");
            VisualElement focused = _editor.TextField.panel.focusController.focusedElement as VisualElement;
            Assert.IsTrue(focused != null && (focused == _editor.TextField || _editor.TextField.Contains(focused)),
                "Handled Tab indentation must keep native keyboard focus inside the source editor.");
            Key(KeyCode.Tab, '\t', EventModifiers.Shift);
            yield return SettleText("local n = 1");
            Key(KeyCode.Slash, '/', EventModifiers.Control);
            yield return SettleText("-- local n = 1");
            Key(KeyCode.Slash, '/', EventModifiers.Control);
            yield return SettleText("local n = 1");
        }

        [UnityTest]
        public IEnumerator NativeBrackets_AndCompletionUseBoundedPopup()
        {
            yield return Attach("");
            Key(KeyCode.None, '(');
            yield return SettleText("()");
            Assert.AreEqual(1, _editor.TextField.cursorIndex);
            Assert.AreEqual(1, _editor.TextField.selectIndex,
                "An auto-inserted pair must leave a collapsed native selection inside the brackets.");
            Key(KeyCode.None, ')');
            Assert.AreEqual("()", _editor.TextField.value, "Typing an auto-inserted closer must advance the caret.");
            Assert.AreEqual(2, _editor.TextField.cursorIndex);

            _editor.View.SetText("loc", "completion");
            _editor.View.Selection = new CodeSelection(3, 3);
            Key(KeyCode.Space, ' ', EventModifiers.Control);
            VisualElement popup = null;
            for (int frame = 0; frame < 30; frame++)
            {
                popup = _editor.Root.Q("editor-completion");
                if (popup != null && popup.style.display == DisplayStyle.Flex) break;
                yield return null;
            }
            Assert.IsNotNull(popup, "The native completion shortcut must open the real ported popup.");
            Assert.AreEqual(DisplayStyle.Flex, popup.style.display.value);
            Assert.That(popup.childCount, Is.InRange(1, CodeCompletionEngine.MaxItems));
            Assert.IsTrue(popup.Children().OfType<Label>().Any(label => label.text == "local"));
            Key(KeyCode.Tab, '\t');
            Key(KeyCode.LeftArrow, '\0', EventModifiers.FunctionKey);
            yield return SettleText("local");
            Assert.AreEqual(4, _editor.View.Selection.Caret,
                "The deferred completion focus restore must not undo an immediate native caret move.");
            Key(KeyCode.None, 'x');
            yield return SettleText("locaxl");
        }

        [UnityTest]
        public IEnumerator NativeQuoteClosers_SkipTrackedSingleAndDoubleQuotes()
        {
            yield return Attach("");
            foreach (char quote in new[] { '\'', '"' })
            {
                foreach (string content in new[] { "", "text" })
                {
                    _editor.View.SetText("", "quote-closer-" + quote + content);
                    Key(KeyCode.None, quote);
                    yield return SettleText(new string(quote, 2));
                    foreach (char character in content) Key(KeyCode.None, character);
                    string expected = quote + content + quote;
                    yield return SettleText(expected);

                    Key(KeyCode.None, quote);
                    yield return SettleText(expected);
                    Assert.AreEqual(expected.Length, _editor.View.Selection.Caret);
                    Assert.AreEqual(expected.Length, _editor.View.Selection.Anchor);
                    Key(KeyCode.None, ';');
                    yield return SettleText(expected + ";");
                }
            }
        }

        [UnityTest]
        public IEnumerator NativeQuoteEscapes_InsertOddBackslashQuotesAndSkipEvenBackslashClosers()
        {
            yield return Attach("");
            foreach (char quote in new[] { '\'', '"' })
            {
                foreach (int count in new[] { 1, 2, 3, 4 })
                {
                    _editor.View.SetText("", "quote-escape-" + quote + count);
                    Key(KeyCode.None, quote);
                    yield return SettleText(new string(quote, 2));
                    for (int index = 0; index < count; index++) Key(KeyCode.None, '\\');
                    string before = quote + new string('\\', count) + quote;
                    yield return SettleText(before);
                    bool escaped = (count & 1) != 0;
                    string expected = escaped ? before.Insert(count + 1, quote.ToString()) : before;

                    Key(KeyCode.None, quote);
                    yield return SettleText(expected);
                    Assert.AreEqual(count + 2, _editor.View.Selection.Caret);
                    Assert.AreEqual(count + 2, _editor.View.Selection.Anchor);
                    if (escaped)
                    {
                        Key(KeyCode.None, quote);
                        yield return SettleText(expected);
                    }
                    Assert.AreEqual(expected.Length, _editor.View.Selection.Caret);
                    Assert.AreEqual(expected.Length, _editor.View.Selection.Anchor);
                    Key(KeyCode.None, ';');
                    yield return SettleText(expected + ";");
                }
            }
        }

        [Test]
        public void AutoCloseTracker_ValidatesInsertionHintsAndPreservesLegacyChanges()
        {
            CodeEditorTextUtility.AutoCloseTracker tracker = new();
            string before = new(new[] { '"', '\\', '"' });
            string after = before.Insert(2, "\"");
            tracker.Track(0, 2, '"', before);
            tracker.OnTextChanged(before, after, 2);
            Assert.IsFalse(tracker.IsAutoClosed(after, 2), "The inserted escaped quote must not become the owned closer.");
            Assert.IsTrue(tracker.IsAutoClosed(after, 3), "The original closer must move after the literal insertion.");

            tracker.Clear();
            tracker.Track(0, 1, ')', "()");
            tracker.OnTextChanged("()", "(x)", 0);
            Assert.IsFalse(tracker.IsAutoClosed("(x)", 1));
            Assert.IsTrue(tracker.IsAutoClosed("(x)", 2), "An invalid insertion hint must fall back to the validated text diff.");
            tracker.Clear();
            tracker.Track(0, 1, ')', "()");
            tracker.OnTextChanged("()", "(x)");
            Assert.IsFalse(tracker.IsAutoClosed("(x)", 1));
            Assert.IsTrue(tracker.IsAutoClosed("(x)", 2), "The existing public overload must retain its behavior.");
        }

        [UnityTest]
        public IEnumerator NativeOppositeQuotesAndBrackets_StayLiteralInsideStrings()
        {
            yield return Attach("");
            string python = _editor.LanguageDropdown.choices.Single(label => label.StartsWith("Python", StringComparison.Ordinal));
            foreach (string language in new[] { "Lua", python })
            {
                _editor.LanguageDropdown.value = language;
                foreach (char quote in new[] { '\'', '"' })
                {
                    _editor.View.SetText("", "opposite-quote-" + language + quote);
                    Key(KeyCode.None, quote);
                    yield return SettleText(new string(quote, 2));
                    char opposite = quote == '\'' ? '"' : '\'';
                    Key(KeyCode.None, opposite);
                    Key(KeyCode.None, '(');
                    string expected = quote + opposite.ToString() + "(" + quote;
                    yield return SettleText(expected);
                    Assert.AreEqual(expected.Length - 1, _editor.View.Selection.Caret);

                    Key(KeyCode.None, quote);
                    yield return SettleText(expected);
                    Assert.AreEqual(expected.Length, _editor.View.Selection.Caret);
                    Assert.AreEqual(expected.Length, _editor.View.Selection.Anchor);
                    Key(KeyCode.None, ';');
                    yield return SettleText(expected + ";");
                }
            }
        }

        [UnityTest]
        public IEnumerator NativeTrackedQuotesMovedIntoComments_RemainLiteral()
        {
            yield return Attach("");
            string python = _editor.LanguageDropdown.choices.Single(label => label.StartsWith("Python", StringComparison.Ordinal));
            foreach (string language in new[] { "Lua", python })
            {
                _editor.LanguageDropdown.value = language;
                string prefix = language == "Lua" ? "-- " : "# ";
                foreach (char quote in new[] { '\'', '"' })
                {
                    foreach (bool afterNewline in new[] { false, true })
                    {
                        _editor.View.SetText("", "comment-quote-" + language + quote + afterNewline);
                        Key(KeyCode.None, quote);
                        string pair = new(quote, 2);
                        yield return SettleText(pair);
                        if (afterNewline)
                        {
                            Key(KeyCode.Return, '\n');
                            yield return SettleText(quote + "\n" + quote);
                        }
                        else _editor.View.Selection = new CodeSelection(0, 0);
                        foreach (char character in prefix) Key(KeyCode.None, character);
                        string before = afterNewline ? quote + "\n" + prefix + quote : prefix + pair;
                        _editor.View.Selection = new CodeSelection(before.Length - 1, before.Length - 1);
                        yield return SettleText(before);

                        Key(KeyCode.None, quote);
                        string expected = before.Insert(before.Length - 1, quote.ToString());
                        yield return SettleText(expected);
                        Assert.AreEqual(expected.Length - 1, _editor.View.Selection.Caret);
                        Assert.AreEqual(expected.Length - 1, _editor.View.Selection.Anchor);
                        Key(KeyCode.None, '(');
                        yield return SettleText(expected.Insert(expected.Length - 1, "("));
                    }
                }
            }
        }

        [UnityTest]
        public IEnumerator NativeTrackedQuotesMovedIntoLuaLongLiterals_RemainLiteral()
        {
            yield return Attach("");
            foreach (char quote in new[] { '\'', '"' })
            {
                _editor.View.SetText("", "long-literal-quote-" + quote);
                Key(KeyCode.None, quote);
                string pair = new(quote, 2);
                yield return SettleText(pair);
                _editor.View.Selection = new CodeSelection(0, 0);
                foreach (char character in "[=[") Key(KeyCode.None, character);
                int end = _editor.TextField.value.Length;
                _editor.View.Selection = new CodeSelection(end, end);
                foreach (char character in "]=]") Key(KeyCode.None, character);
                string before = "[=[" + pair + "]=]";
                int closer = "[=[".Length + 1;
                _editor.View.Selection = new CodeSelection(closer, closer);
                yield return SettleText(before);

                Key(KeyCode.None, quote);
                string expected = before.Insert(closer, quote.ToString());
                yield return SettleText(expected);
                Assert.AreEqual(closer + 1, _editor.View.Selection.Caret);
                Assert.AreEqual(closer + 1, _editor.View.Selection.Anchor);
                Key(KeyCode.None, '(');
                yield return SettleText(expected.Insert(closer + 1, "("));
            }
        }

        [UnityTest]
        public IEnumerator NativeTrackedParenthesisAfterQuoteCloser_UsesCurrentCodeContext()
        {
            yield return Attach("");
            foreach (char quote in new[] { '\'', '"' })
            {
                _editor.View.SetText("", "quoted-argument-" + quote);
                foreach (char character in "print") Key(KeyCode.None, character);
                Key(KeyCode.None, '(');
                yield return SettleText("print()");
                Key(KeyCode.None, quote);
                yield return SettleText("print(" + new string(quote, 2) + ")");
                foreach (char character in "text") Key(KeyCode.None, character);
                string expected = "print(" + quote + "text" + quote + ")";
                yield return SettleText(expected);

                Key(KeyCode.None, quote);
                yield return SettleText(expected);
                Assert.AreEqual(expected.Length - 1, _editor.View.Selection.Caret);
                Key(KeyCode.None, ')');
                yield return SettleText(expected);
                Assert.AreEqual(expected.Length, _editor.View.Selection.Caret);
                Assert.AreEqual(expected.Length, _editor.View.Selection.Anchor);
                Key(KeyCode.None, ';');
                yield return SettleText(expected + ";");
            }
        }

        [UnityTest]
        public IEnumerator LanguageDropdown_PreservesSeparateDraftSelectionAndUndo()
        {
            const string initialLua = "local n = 1";
            yield return Attach(initialLua);
            _editor.View.Selection = new CodeSelection(initialLua.Length, initialLua.Length);
            Key(KeyCode.None, 'x');
            yield return SettleText(initialLua + "x");
            _editor.View.Selection = new CodeSelection(3, 1);
            string pythonLabel = _editor.LanguageDropdown.choices.Single(label => label.StartsWith("Python", StringComparison.Ordinal));
            _editor.LanguageDropdown.value = pythonLabel;
            Assert.AreEqual("python", _editor.Document.LanguageId);
            string initialPython = _editor.TextField.value;
            _editor.View.Selection = new CodeSelection(initialPython.Length, initialPython.Length);
            Key(KeyCode.None, 'p');
            yield return SettleText(initialPython + "p");
            _editor.View.Selection = new CodeSelection(2, 0);

            _editor.LanguageDropdown.value = "Lua";
            Assert.AreEqual(initialLua + "x", _editor.TextField.value);
            Assert.AreEqual(3, _editor.View.Selection.Caret);
            Assert.AreEqual(1, _editor.View.Selection.Anchor);
            Key(KeyCode.Z, '\0', EventModifiers.Control);
            yield return SettleText(initialLua);

            _editor.LanguageDropdown.value = pythonLabel;
            Assert.AreEqual(initialPython + "p", _editor.TextField.value);
            Assert.AreEqual(2, _editor.View.Selection.Caret);
            Assert.AreEqual(0, _editor.View.Selection.Anchor);
            Key(KeyCode.Z, '\0', EventModifiers.Control);
            yield return SettleText(initialPython);
            Assert.AreEqual(initialLua, _editor.LuaDraft);
        }

        [UnityTest]
        public IEnumerator HighlightEventsRemainSiblingsAndCannotReplaceRawSource()
        {
            const string source = "local text = \"<color=red>\" -- \u03bb";
            yield return Attach(source);
            Label mirror = _editor.Root.Q<Label>(CodeEditorSurface.MirrorName);
            Assert.IsNotNull(mirror);
            Assert.AreSame(_editor.TextField.parent, mirror.parent);
            Assert.IsFalse(_editor.TextField.Contains(mirror));
            TextElement line = mirror.Query<TextElement>(className: "coreai-code-editor__line").ToList().First();
            int documentChanges = 0;
            _editor.DraftChanged += () => documentChanges++;
            using (ChangeEvent<string> decorative = ChangeEvent<string>.GetPooled(source, "<color=red>decorative</color>"))
            {
                decorative.target = line;
                line.SendEvent(decorative);
            }
            yield return null;
            Assert.AreEqual(0, documentChanges);
            Assert.AreEqual(source, _editor.TextField.value);
            Assert.AreEqual(source, _editor.LuaDraft);
            Assert.IsFalse(Editable().enableRichText);
        }

        [UnityTest]
        public IEnumerator LargeSource_UsesBoundedPlainTextAndRestoresDecoration()
        {
            yield return Attach("local n = 1");
            string manyLines = string.Concat(Enumerable.Repeat("x\n", CodeEditorView.MaximumDecoratedLines + 1));
            _editor.View.ReplaceTextUndoable(manyLines);
            Assert.IsTrue(_editor.View.IsPlainTextMode, "The line bound must apply synchronously, before a deferred presentation.");
            yield return null;
            Assert.AreEqual(manyLines, _editor.TextField.value);
            Assert.AreEqual(manyLines, _editor.LuaDraft);
            Assert.AreEqual(0, _editor.Root.Query(className: "coreai-code-editor__line").ToList().Count);
            Assert.AreEqual(0, _editor.Root.Query(className: "coreai-code-editor__guide").ToList().Count);

            string large = new('x', CodeEditorLimits.MaximumAnalysisCharacters + 1);
            _editor.View.ReplaceTextUndoable(large);
            Assert.IsTrue(_editor.View.IsPlainTextMode);
            Assert.AreEqual(large, _editor.LuaDraft);
            Assert.Throws<ArgumentException>(() => _editor.View.ReplaceTextUndoable(new string('x', CodeEditorLimits.MaximumDocumentCharacters + 1)));
            Assert.AreEqual(large, _editor.LuaDraft, "Oversized replacements preserve the exact current draft.");
            _editor.View.ReplaceTextUndoable("local restored = 2");
            double deadline = EditorApplication.timeSinceStartup + 2;
            while (!Rendered().Contains("restored") && EditorApplication.timeSinceStartup < deadline)
            {
                _window.Repaint();
                yield return null;
            }
            Assert.IsFalse(_editor.View.IsPlainTextMode);
            Assert.IsTrue(Rendered().Contains("restored"));
            Assert.That(_editor.Root.Query(className: "coreai-code-editor__line").ToList().Count,
                Is.InRange(1, CodeEditorView.MaximumDecoratedLines * 2));
        }

        [UnityTest]
        public IEnumerator DetachReattachAndDispose_InvalidatePendingDocumentWork()
        {
            yield return Attach("if ready then");
            _editor.View.Selection = new CodeSelection(_editor.TextField.value.Length, _editor.TextField.value.Length);
            Key(KeyCode.Return, '\n');
            _editor.LanguageDropdown.value = _editor.LanguageDropdown.choices.Single(label => label.StartsWith("Python", StringComparison.Ordinal));
            string python = _editor.TextField.value;
            _editor.Root.RemoveFromHierarchy();
            yield return null;
            _window.rootVisualElement.Add(_editor.Root);
            yield return null;
            Assert.AreEqual(python, _editor.TextField.value, "Queued Lua indentation must not touch the Python draft.");
            _editor.View.Selection = new CodeSelection(python.Length, python.Length);
            Key(KeyCode.None, 'p');
            yield return SettleText(python + "p");
            int changesAfterDisposal = 0;
            _editor.DraftChanged += () => changesAfterDisposal++;
            _editor.Dispose();
            _editor.Dispose();
            Assert.IsTrue(_editor.View.IsReleased);
            Assert.IsNull(_editor.Root.Q("editor-completion"));
            _editor.TextField.value = "detached callbacks";
            yield return null;
            Assert.AreEqual(0, changesAfterDisposal, "Disposal removes source and language callbacks.");
            Assert.AreEqual(0, _editor.Root.Query(className: "coreai-code-editor__line").ToList().Count);
        }

        [UnityTest]
        public IEnumerator UnfocusedSelectionAfterReattach_PreservesRangeAndDropdownFocus()
        {
            const string source = "local text = \"before\tafter\u00a0\u03bb\"";
            yield return Attach(source);
            _editor.Root.RemoveFromHierarchy();
            yield return null;
            _window.rootVisualElement.Add(_editor.Root);
            yield return null;
            _editor.LanguageDropdown.Focus();
            yield return null;
            Assert.AreSame(_editor.LanguageDropdown, _editor.TextField.panel.focusController.focusedElement);
            int start = source.IndexOf("before", StringComparison.Ordinal);
            _editor.View.Selection = new CodeSelection(start + "before".Length, start);
            Assert.AreSame(_editor.LanguageDropdown, _editor.TextField.panel.focusController.focusedElement,
                "Restoring code selection must leave the language dropdown focused.");

            Key(KeyCode.None, 'x');
            string expected = source.Remove(start, "before".Length).Insert(start, "x");
            yield return SettleText(expected);
            Assert.AreEqual(start + 1, _editor.View.Selection.Caret);
            Assert.AreEqual(start + 1, _editor.View.Selection.Anchor);
            Assert.AreEqual(expected, _editor.LuaDraft);
        }

        [UnityTest]
        public IEnumerator DocumentReplacement_InvalidatesUnfocusedSelectionRequest()
        {
            const string source = "local previous = 1";
            yield return Attach(source);
            _editor.LanguageDropdown.Focus();
            yield return null;
            _editor.View.Selection = new CodeSelection(source.Length, 0);
            _editor.View.SetText("abc", "replacement");
            _editor.TextField.SelectRange(0, 0);

            Key(KeyCode.None, 'x');
            yield return SettleText("xabc");
            Assert.AreEqual(1, _editor.View.Selection.Caret);
            Assert.AreEqual(1, _editor.View.Selection.Anchor);
        }

        [UnityTest]
        public IEnumerator CompletionKeyPair_DoesNotSuppressTabAfterDocumentOrPanelChange()
        {
            yield return Attach("loc");
            foreach (string boundary in new[] { "language", "source", "panel" })
            {
                _editor.LanguageDropdown.value = "Lua";
                _editor.View.SetText("loc", "split-key-" + boundary);
                _editor.View.Selection = new CodeSelection(3, 3);
                Key(KeyCode.Space, ' ', EventModifiers.Control);
                VisualElement popup = null;
                for (int frame = 0; frame < 30; frame++)
                {
                    popup = _editor.Root.Q("editor-completion");
                    if (popup != null && popup.style.display == DisplayStyle.Flex) break;
                    yield return null;
                }
                Assert.IsNotNull(popup);
                Assert.AreEqual(DisplayStyle.Flex, popup.style.display.value);
                Key(KeyCode.Tab, '\0');
                yield return SettleText("local");

                if (boundary == "language")
                    _editor.LanguageDropdown.value = _editor.LanguageDropdown.choices.Single(label => label.StartsWith("Python", StringComparison.Ordinal));
                else if (boundary == "source")
                    _editor.View.SetText("replacement", "replacement");
                else
                {
                    _editor.Root.RemoveFromHierarchy();
                    yield return null;
                    _window.rootVisualElement.Add(_editor.Root);
                    yield return null;
                }
                string source = _editor.TextField.value;
                int expectedColumn = boundary == "language" ? 0 : boundary == "source" ? 11 : 5;
                string expectedPadding = boundary == "language" ? "    " : boundary == "source" ? " " : "   ";
                Assert.AreEqual(expectedColumn, source.Length - source.LastIndexOf('\n') - 1,
                    "Each boundary must exercise its explicit four-column tab-stop position.");
                _editor.View.Selection = new CodeSelection(source.Length, source.Length);
                Key(KeyCode.Tab, '\t');
                yield return SettleText(source + expectedPadding);
                Assert.AreEqual(source.Length + expectedPadding.Length, _editor.View.Selection.Caret);
                Assert.AreEqual(source.Length + expectedPadding.Length, _editor.View.Selection.Anchor);
            }
        }

        [UnityTest]
        public IEnumerator ProgrammaticSelectionAfterLayout_DoesNotOverrideFollowingNativeNavigation()
        {
            yield return Attach("old");
            const string source = "local text = \"abcd\"";
            _editor.View.SetText(source, "layout-replacement");
            _editor.View.Selection = new CodeSelection(source.Length, source.Length);
            _window.Repaint();
            yield return null;

            Key(KeyCode.LeftArrow, '\0', EventModifiers.FunctionKey);
            Assert.AreEqual(source.Length - 1, _editor.View.Selection.Caret);
            Assert.AreEqual(source.Length - 1, _editor.View.Selection.Anchor);
            yield return null;
            Key(KeyCode.None, 'x');
            yield return SettleText(source.Insert(source.Length - 1, "x"));
        }

        [UnityTest]
        public IEnumerator ExplicitSelection_ClosesOldCompletionBeforeNativeTab()
        {
            const string source = "replacement";
            yield return Attach(source);
            _editor.View.Selection = new CodeSelection(3, 3);
            yield return RequestNativeCompletion();
            VisualElement popup = _editor.Root.Q("editor-completion");
            _editor.View.Selection = new CodeSelection(source.Length, source.Length);
            Assert.AreEqual(DisplayStyle.None, popup.style.display.value,
                "A completion at the old prefix cannot accept input after explicit selection moves the caret.");

            Key(KeyCode.Tab, '\t');
            yield return SettleText("replacement ");
            Assert.AreEqual(12, _editor.View.Selection.Caret);
            Assert.AreEqual(12, _editor.View.Selection.Anchor);
        }

        [UnityTest]
        public IEnumerator NativeTypingBeforeDeferredCompletionRefresh_DoesNotLoseFollowingTab()
        {
            yield return Attach("loc");
            _editor.View.Selection = new CodeSelection(3, 3);
            yield return RequestNativeCompletion();
            Key(KeyCode.None, 'x');
            Assert.AreEqual("locx", _editor.TextField.value);
            Key(KeyCode.Tab, '\t');
            yield return SettleText("locx    ");
            Assert.AreEqual(8, _editor.View.Selection.Caret);
            Assert.AreEqual(8, _editor.View.Selection.Anchor);
        }

        private IEnumerator RequestNativeCompletion()
        {
            Key(KeyCode.Space, ' ', EventModifiers.Control);
            for (int frame = 0; frame < 30; frame++)
            {
                VisualElement popup = _editor.Root.Q("editor-completion");
                if (popup != null && popup.style.display == DisplayStyle.Flex) yield break;
                _window.Repaint();
                yield return null;
            }
            Assert.Fail("The native completion shortcut must open its attached popup.");
        }

        private IEnumerator Attach(string source)
        {
            _editor = new CodeEditorSurface("attached-editor", source);
            _window = ScriptableObject.CreateInstance<EditorHostWindow>();
            _window.titleContent = new GUIContent("CoreAI editor regression");
            _window.position = new Rect(100, 100, 760, 620);
            _window.Show();
            _window.rootVisualElement.Add(_editor.Root);
            for (int frame = 0; frame < 30 && _editor.TextField.panel == null; frame++) yield return null;
            Assert.IsNotNull(_editor.TextField.panel, "The fixture must exercise an attached native panel.");
            _window.Focus();
            _editor.TextField.Focus();
            yield return null;
        }

        private TextElement Editable()
        {
            VisualElement input = _editor.TextField.Q("unity-text-input");
            Assert.IsNotNull(input);
            TextElement editable = input.Query<TextElement>().ToList().FirstOrDefault();
            Assert.IsNotNull(editable);
            return editable;
        }

        private void Key(KeyCode code, char character, EventModifiers modifiers = EventModifiers.None)
        {
            TextElement editable = Editable();
            editable.Focus();
            using KeyDownEvent key = KeyDownEvent.GetPooled(new Event
                { type = EventType.KeyDown, keyCode = code, character = character, modifiers = modifiers });
            editable.SendEvent(key);
        }

        private IEnumerator SettleText(string expected)
        {
            double deadline = EditorApplication.timeSinceStartup + 2;
            while (_editor.TextField.value != expected && EditorApplication.timeSinceStartup < deadline)
            {
                _window.Repaint();
                yield return null;
            }
            Assert.AreEqual(expected, _editor.TextField.value, "The native key must perform the expected editing operation.");
            yield return null;
        }

        private string Rendered() => string.Join("\n", _editor.Root.Q<Label>(CodeEditorSurface.MirrorName)
            .Query<TextElement>(className: "coreai-code-editor__line").ToList().Select(line => line.text));
    }
}
#endif
