using System;
using System.Collections.Generic;

namespace CoreAI.CodeEditing
{
    public readonly struct CodeEditorSnapshot
    {
        public readonly string Text;
        public readonly int CaretIndex;
        public readonly int AnchorIndex;

        public CodeEditorSnapshot(string text, int caretIndex, int anchorIndex = -1)
        {
            Text = text ?? "";
            CaretIndex = caretIndex;
            AnchorIndex = anchorIndex < 0 ? caretIndex : anchorIndex;
        }
    }

    public sealed class CodeEditorHistory
    {
        public const int DefaultCoalesceIntervalMs = 750;
        public const int DefaultDepthLimit = 200;
        public const int MaximumRetainedCharacters = 4 * 1024 * 1024;

        private readonly List<CodeEditorSnapshot> _undo = new();
        private readonly List<CodeEditorSnapshot> _redo = new();
        private readonly long _coalesceIntervalMs;
        private readonly int _depthLimit;
        private bool _lastWasPush;
        private long _lastPushTimestampMs;
        private long _retainedCharacters;

        public CodeEditorHistory(long coalesceIntervalMs = DefaultCoalesceIntervalMs, int depthLimit = DefaultDepthLimit)
        {
            _coalesceIntervalMs = Math.Max(0, coalesceIntervalMs);
            _depthLimit = Math.Max(1, depthLimit);
        }

        public int Count => _undo.Count;
        public int Version { get; private set; }
        public bool CanUndo => _undo.Count > 1;
        public bool CanRedo => _redo.Count > 0;
        public long RetainedCharacterCount => _retainedCharacters;
        public string CurrentText => _undo.Count > 0 ? _undo[_undo.Count - 1].Text : null;

        public void Push(string text, int caretIndex, long timestampMs, bool forceNewEntry = false, int anchorIndex = -1)
        {
            text = ValidateText(text);
            CodeEditorSnapshot snapshot = new(text, caretIndex, anchorIndex);
            ClearRedo();
            if (!forceNewEntry && _lastWasPush && _undo.Count > 0 &&
                timestampMs >= _lastPushTimestampMs && timestampMs - _lastPushTimestampMs < _coalesceIntervalMs &&
                !CrossesNewlineBoundary(_undo[_undo.Count - 1].Text, text))
            {
                _retainedCharacters -= _undo[_undo.Count - 1].Text.Length;
                _undo[_undo.Count - 1] = snapshot;
            }
            else _undo.Add(snapshot);
            _retainedCharacters += text.Length;
            TrimToLimits();
            _lastWasPush = !forceNewEntry;
            _lastPushTimestampMs = timestampMs;
            Version++;
        }

        public void AmendTop(string text, int caretIndex)
        {
            text = ValidateText(text);
            if (_undo.Count == 0) { Push(text, caretIndex, _lastPushTimestampMs, true); return; }
            ClearRedo();
            _retainedCharacters -= _undo[_undo.Count - 1].Text.Length;
            _undo[_undo.Count - 1] = new CodeEditorSnapshot(text, caretIndex);
            _retainedCharacters += text.Length;
            TrimToLimits();
            Version++;
        }

        public CodeEditorSnapshot? Undo(string currentText, int currentCaret, int currentAnchor = -1)
        {
            if (!CanUndo) return null;
            currentText = ValidateText(currentText);
            _retainedCharacters -= _undo[_undo.Count - 1].Text.Length;
            _undo.RemoveAt(_undo.Count - 1);
            _redo.Add(new CodeEditorSnapshot(currentText, currentCaret, currentAnchor));
            _retainedCharacters += currentText.Length;
            TrimToLimits();
            _lastWasPush = false;
            Version++;
            return _undo[_undo.Count - 1];
        }

        public CodeEditorSnapshot? Redo()
        {
            if (_redo.Count == 0) return null;
            CodeEditorSnapshot snapshot = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _undo.Add(snapshot);
            TrimToLimits();
            _lastWasPush = false;
            Version++;
            return snapshot;
        }

        public void Clear()
        {
            _undo.Clear();
            _redo.Clear();
            _retainedCharacters = 0;
            _lastWasPush = false;
            _lastPushTimestampMs = 0;
            Version++;
        }

        private void ClearRedo()
        {
            foreach (CodeEditorSnapshot snapshot in _redo) _retainedCharacters -= snapshot.Text.Length;
            _redo.Clear();
        }

        private void TrimToLimits()
        {
            while (_undo.Count > _depthLimit || _retainedCharacters > MaximumRetainedCharacters)
            {
                // Keep the current snapshot; prune the oldest undo or farthest redo first.
                List<CodeEditorSnapshot> oldest = _undo.Count > 1 ? _undo : _redo;
                if (oldest.Count == 0) break;
                _retainedCharacters -= oldest[0].Text.Length;
                oldest.RemoveAt(0);
            }
        }

        private static string ValidateText(string text)
        {
            text ??= "";
            if (text.Length > CodeEditorLimits.MaximumDocumentCharacters)
                throw new ArgumentException("The document exceeds the configured editor limit.", nameof(text));
            return text;
        }

        private static bool CrossesNewlineBoundary(string previous, string current) => CountNewlines(previous) != CountNewlines(current);

        private static int CountNewlines(string text)
        {
            int count = 0;
            foreach (char character in text ?? "") if (character == '\n') count++;
            return count;
        }
    }
}