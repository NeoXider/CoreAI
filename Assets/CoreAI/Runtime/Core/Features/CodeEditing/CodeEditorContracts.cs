using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace CoreAI.CodeEditing
{
    public static class CodeEditorLimits
    {
        public const int MaximumDocumentCharacters = 256 * 1024;
        public const int MaximumAnalysisCharacters = 50_000;
        public const int MaximumHighlightCharacters = 512 * 1024;
        public const int MaximumOutputCharacters = 16 * 1024;
        public const int MaximumDiagnostics = 32;
    }

    public readonly struct CodeSelection
    {
        public CodeSelection(int caret, int anchor) { Caret = caret; Anchor = anchor; }
        public int Caret { get; }
        public int Anchor { get; }
        public CodeSelection Clamp(int length) => new(Math.Max(0, Math.Min(length, Caret)), Math.Max(0, Math.Min(length, Anchor)));
    }

    public readonly struct CodeErrorLocation
    {
        public CodeErrorLocation(int? line, int? column, string message) { Line = line; Column = column; Message = message ?? ""; }
        public int? Line { get; }
        public int? Column { get; }
        public string Message { get; }
    }

    public interface ICodeEditorView
    {
        string GetText();
        void SetText(string text);
        void SetCurrentLineMarker(int? line);
        void SetErrorLine(int? line);
        void SetHintLines(IReadOnlyList<int> lines, string hint);
        void ClearHintLines();
        void SetEditable(bool editable);
    }

    public interface ICodeEditorErrorView { void ShowError(CodeErrorLocation location); }

    public interface ICodeLanguageProfile
    {
        string LanguageId { get; }
        string DisplayName { get; }
        string LineCommentToken { get; }
        IReadOnlyList<string> CompletionWords { get; }
        string[] HighlightLines(string[] lines);
        CodeEnterPlan PlanEnter(string text, int selectionStart, int selectionEnd);
    }

    public static class CodeEditorMarkup
    {
        public static string Escape(string text) => (text ?? "").Replace("<", "<\u200B");
        public static string[] EscapeLines(string[] lines)
        {
            string[] result = new string[lines?.Length ?? 0];
            for (int index = 0; index < result.Length; index++) result[index] = Escape(lines[index]);
            return result;
        }
    }

    public readonly struct CodeCompletionResult
    {
        public CodeCompletionResult(int replaceStart, string prefix, IReadOnlyList<string> items, bool prefixIsCompleteWord = false)
        { ReplaceStart = replaceStart; Prefix = prefix ?? ""; Items = items ?? Array.Empty<string>(); PrefixIsCompleteWord = prefixIsCompleteWord; }
        public int ReplaceStart { get; }
        public string Prefix { get; }
        public IReadOnlyList<string> Items { get; }
        public bool PrefixIsCompleteWord { get; }
        public bool HasItems => Items != null && Items.Count > 0;
    }

    public sealed class CodeCompletionEngine
    {
        public const int MaxItems = 8;
        public ICodeLanguageProfile Profile { get; set; }
        private string _cachedText;
        private readonly HashSet<string> _words = new(StringComparer.Ordinal);

        public static bool EnterAccepts(CodeCompletionResult result, bool navigated) => result.HasItems && (navigated || !result.PrefixIsCompleteWord);

        public CodeCompletionResult GetCompletions(string text, int caret, bool explicitMode = false)
        {
            text ??= "";
            caret = Math.Max(0, Math.Min(text.Length, caret));
            int start = caret;
            while (start > 0 && caret - start < 128 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
            if (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_'))
                return new CodeCompletionResult(caret, "", Array.Empty<string>());
            string prefix = text.Substring(start, caret - start);
            if (prefix.Length == 0 && !explicitMode) return new CodeCompletionResult(start, prefix, Array.Empty<string>());
            if (!string.Equals(_cachedText, text, StringComparison.Ordinal))
            {
                _cachedText = text;
                _words.Clear();
                int limit = Math.Min(text.Length, CodeEditorLimits.MaximumAnalysisCharacters);
                for (int index = 0; index < limit && _words.Count < 2048;)
                {
                    if (!char.IsLetter(text[index]) && text[index] != '_') { index++; continue; }
                    int wordStart = index++;
                    while (index < limit && (char.IsLetterOrDigit(text[index]) || text[index] == '_')) index++;
                    if (index - wordStart <= 128) _words.Add(text.Substring(wordStart, index - wordStart));
                }
            }
            SortedSet<string> candidates = new(StringComparer.Ordinal);
            if (Profile?.CompletionWords != null)
                foreach (string word in Profile.CompletionWords)
                    if (word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) candidates.Add(word);
            foreach (string word in _words)
                if (!string.Equals(word, prefix, StringComparison.Ordinal) &&
                    word.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) candidates.Add(word);
            List<string> items = new();
            bool complete = candidates.Contains(prefix);
            foreach (string candidate in candidates) { if (items.Count == MaxItems) break; items.Add(candidate); }
            return new CodeCompletionResult(start, prefix, items, complete);
        }
    }

    public sealed class CodeEditorDocument
    {
        private sealed class Draft { public string Text = ""; public CodeSelection Selection; public long Revision; }
        private readonly Dictionary<string, Draft> _drafts = new(StringComparer.Ordinal);
        public CodeEditorDocument(string documentId, string initialLua)
        {
            if (string.IsNullOrWhiteSpace(documentId)) throw new ArgumentException("Document id is required.", nameof(documentId));
            if ((initialLua?.Length ?? 0) > CodeEditorLimits.MaximumDocumentCharacters)
                throw new ArgumentException("The document exceeds the configured editor limit.", nameof(initialLua));
            DocumentId = documentId;
            _drafts.Add("lua", new Draft { Text = initialLua ?? "" });
            _drafts.Add("python", new Draft { Text = "# Python execution is not supported.\n" });
        }
        public string DocumentId { get; }
        public string LanguageId { get; private set; } = "lua";
        public string Text => Current.Text;
        public CodeSelection Selection => Current.Selection;
        public long Revision => Current.Revision;
        private Draft Current => _drafts[LanguageId];
        public string LuaText => _drafts["lua"].Text;
        public void Update(string text, CodeSelection selection)
        {
            text ??= "";
            if (text.Length > CodeEditorLimits.MaximumDocumentCharacters) throw new ArgumentException("The document exceeds the configured editor limit.", nameof(text));
            if (!string.Equals(Current.Text, text, StringComparison.Ordinal)) { Current.Text = text; Current.Revision++; }
            Current.Selection = selection.Clamp(text.Length);
        }
        public void SelectLanguage(string languageId)
        {
            if (!_drafts.ContainsKey(languageId ?? "")) throw new ArgumentException("The editor language is unsupported.", nameof(languageId));
            LanguageId = languageId;
        }
        public bool Matches(string documentId, string languageId, long revision) =>
            DocumentId == documentId && LanguageId == languageId && Revision == revision;
    }

    public enum CodeRunStatus { Success, Unsupported, Cancelled, Failed, BudgetExceeded, Conflict, Unconfirmed }
    public enum CodeRunOperation { Run, SaveAndRun }
    [Flags] public enum CodeRuntimeCapabilities { None = 0, Run = 1, SaveAndRun = 2 }
    public enum CodeRuntimeStatus { Uninitialized, Ready, Running, Disposed }

    /// <summary>An exact source expectation checked before an explicit save begins.</summary>
    public sealed class CodeSourceExpectation
    {
        public CodeSourceExpectation(bool exists, string source)
        { Exists = exists; Source = source ?? ""; }
        public bool Exists { get; }
        public string Source { get; }
    }

    public sealed class CodeRunRequest
    {
        public CodeRunRequest(string documentId, string languageId, long revision, string modId, string code,
            CodeRunOperation operation = CodeRunOperation.Run, int timeoutMs = 100, long maxSteps = 500_000)
            : this(documentId, languageId, revision, modId, code, operation, timeoutMs, maxSteps, null, null)
        {
        }

        public CodeRunRequest(string documentId, string languageId, long revision, string modId, string code,
            CodeRunOperation operation, int timeoutMs, long maxSteps, CodeSourceExpectation expectedSource, string runId = null)
        {
            DocumentId = documentId ?? throw new ArgumentNullException(nameof(documentId));
            LanguageId = languageId ?? throw new ArgumentNullException(nameof(languageId));
            ModId = modId ?? ""; Code = code ?? ""; Revision = revision; Operation = operation;
            if (timeoutMs < 1 || timeoutMs > 1000) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
            if (maxSteps < 1) throw new ArgumentOutOfRangeException(nameof(maxSteps));
            if (operation != CodeRunOperation.Run && operation != CodeRunOperation.SaveAndRun)
                throw new ArgumentOutOfRangeException(nameof(operation));
            if (Code.Length > CodeEditorLimits.MaximumDocumentCharacters) throw new ArgumentException("The source exceeds the editor run limit.", nameof(code));
            TimeoutMs = timeoutMs; MaxSteps = maxSteps;
            ExpectedSource = expectedSource;
            RunId = string.IsNullOrEmpty(runId) ? Guid.NewGuid().ToString("N") : runId;
        }
        public string DocumentId { get; }
        public string LanguageId { get; }
        public string ModId { get; }
        public string Code { get; }
        public long Revision { get; }
        public CodeRunOperation Operation { get; }
        public int TimeoutMs { get; }
        public long MaxSteps { get; }
        public CodeSourceExpectation ExpectedSource { get; }
        public string RunId { get; }
    }
    public sealed class CodeRunResult
    {
        public CodeRunResult(CodeRunStatus status, string message, bool executed = false, bool persisted = false,
            IReadOnlyList<CodeErrorLocation> diagnostics = null)
            : this(null, status, message, executed, persisted, diagnostics)
        {
        }

        public CodeRunResult(CodeRunRequest request, CodeRunStatus status, string message, bool executed = false,
            bool persisted = false, IReadOnlyList<CodeErrorLocation> diagnostics = null)
        {
            Status = status; Message = Bound(message); Executed = executed; Persisted = persisted;
            List<CodeErrorLocation> retained = new();
            if (diagnostics != null) foreach (CodeErrorLocation diagnostic in diagnostics)
            {
                if (retained.Count == CodeEditorLimits.MaximumDiagnostics) break;
                retained.Add(new CodeErrorLocation(diagnostic.Line, diagnostic.Column, Bound(diagnostic.Message)));
            }
            Diagnostics = retained.ToArray();
            DocumentId = request?.DocumentId ?? "";
            LanguageId = request?.LanguageId ?? "";
            ModId = request?.ModId ?? "";
            Revision = request?.Revision ?? 0;
            RunId = request?.RunId ?? "";
        }
        public CodeRunStatus Status { get; }
        public string Message { get; }
        public bool Executed { get; }
        public bool Persisted { get; }
        public IReadOnlyList<CodeErrorLocation> Diagnostics { get; }
        public string DocumentId { get; }
        public string LanguageId { get; }
        public string ModId { get; }
        public long Revision { get; }
        public string RunId { get; }
        public bool Matches(CodeRunRequest request) => request != null && RunId == request.RunId &&
            DocumentId == request.DocumentId && LanguageId == request.LanguageId && ModId == request.ModId && Revision == request.Revision;
        private static string Bound(string text) => string.IsNullOrEmpty(text) ? "" :
            text.Length <= CodeEditorLimits.MaximumOutputCharacters ? text : text.Substring(0, CodeEditorLimits.MaximumOutputCharacters) + "\n[output truncated]";
    }
    public interface ICodeInterpreter
    {
        string LanguageId { get; }
        CodeRuntimeCapabilities Capabilities { get; }
        Task<CodeRunResult> RunAsync(CodeRunRequest request, CancellationToken cancellationToken = default);
    }

    /// <summary>Revision-fenced runtime event emitted by an explicit editor command.</summary>
    public sealed class CodeRuntimeMessage
    {
        public CodeRuntimeMessage(CodeRunResult result) { Result = result ?? throw new ArgumentNullException(nameof(result)); }
        public CodeRunResult Result { get; }
        public string RunId => Result.RunId;
    }

    /// <summary>Interpreter-facing port adapted from the Redo Code Playground lifecycle.</summary>
    public interface ICodePlaygroundRuntime : ICodeInterpreter, IDisposable
    {
        CodeRuntimeStatus Status { get; }
        event Action<CodeRuntimeStatus> StatusChanged;
        event Action<CodeRuntimeMessage> MessageReceived;
        void Initialize();
        bool Stop(string runId);
    }
    public sealed class PythonCodeInterpreterStub : ICodeInterpreter
    {
        public string LanguageId => "python";
        public CodeRuntimeCapabilities Capabilities => CodeRuntimeCapabilities.None;
        public Task<CodeRunResult> RunAsync(CodeRunRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CodeRunResult(request, CodeRunStatus.Unsupported, "Python execution is not supported. No code was run or saved."));
    }
}
