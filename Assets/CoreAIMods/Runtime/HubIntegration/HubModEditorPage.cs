using System;
using System.Collections.Generic;
using CoreAI.CodeEditing;
using CoreAI.Ai.Hub.CodeEditing;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// Hub-wide mod editor preferences (one value for every mod, not per mod). The default store is
    /// <see cref="PlayerPrefsHubModEditorPreferences"/>; a host with its own settings store supplies its own.
    /// </summary>
    public interface IHubModEditorPreferences
    {
        /// <summary>
        /// When true, "Save &amp; run" reloads a loaded mod in <see cref="ModReloadMode.KeepObjects"/>;
        /// when false (the default) in <see cref="ModReloadMode.CleanStartupObjects"/>.
        /// </summary>
        bool KeepObjectsOnSave { get; set; }
    }

    /// <summary><see cref="IHubModEditorPreferences"/> kept in Unity's PlayerPrefs, so it survives restarts on every platform.</summary>
    public sealed class PlayerPrefsHubModEditorPreferences : IHubModEditorPreferences
    {
        /// <summary>The PlayerPrefs key of <see cref="KeepObjectsOnSave"/>.</summary>
        public const string KeepObjectsOnSaveKey = "CoreAI.Hub.Mods.KeepObjectsOnSave";

        /// <summary>The shared instance the Hub pages use by default.</summary>
        public static readonly PlayerPrefsHubModEditorPreferences Instance = new();

        /// <inheritdoc />
        public bool KeepObjectsOnSave
        {
            get => PlayerPrefs.GetInt(KeepObjectsOnSaveKey, 0) == 1;
            set
            {
                PlayerPrefs.SetInt(KeepObjectsOnSaveKey, value ? 1 : 0);
                PlayerPrefs.Save();
            }
        }
    }

    /// <summary>
    /// The Lua mod code editor shown inside the <see cref="HubModsPage"/>: a multiline source field,
    /// the mod's parsed <c>@coreai</c> header fields, and Save / Copy / Close / Refresh-diagnostics
    /// actions. Save persists and (re)runs the mod through <see cref="IHubModService.SaveOrReload"/>,
    /// which validates the Lua by executing it — a compile/run error is caught and shown in the status
    /// line, which also says what the reload did with the previous run's objects. A "Keep objects on
    /// Save &amp; run" toggle (off by default, one Hub preference for every mod) picks the reload mode.
    /// Runtime hook/timer failures are surfaced from
    /// <see cref="IHubModService.RecentErrors"/>. New/pasted mods derive their id from the header on save.
    /// </summary>
    public sealed class HubModEditorPage
    {
        /// <summary>
        /// The legacy starter source (<see cref="HubModTemplates.Legacy"/>). The Mods page "Add" button
        /// opens <see cref="IHubModService.NewModTemplate"/>, which is the Roblox API template wherever
        /// that API is wired.
        /// </summary>
        public const string NewModTemplate = HubModTemplates.Legacy;

        /// <summary>Element name of the "Keep objects on Save &amp; run" toggle.</summary>
        public const string KeepObjectsToggleName = "coreai-hub-mod-keep-objects";

        /// <summary>Element name of the editor's status line.</summary>
        public const string StatusLabelName = "coreai-hub-mod-status";

        private readonly IHubModService _service;
        private readonly IHubModEditorPreferences _preferences;
        private readonly string _modId;
        private readonly bool _isNew;
        private readonly string _initialSource;
        private readonly Action _onClose;

        private TextField _codeField;
        private Toggle _keepObjectsToggle;
        private CodeEditorSurface _editor;
        private EventCallback<ChangeEvent<bool>> _onKeepObjectsChanged;
        private Label _status;
        private Label _diagnostics;
        private VisualElement _headerBox;
        private Label _titleLabel;
        private VisualElement _historyBox;
        private bool _historyOpen;
        private VisualElement _builtRoot;
        private string _baseSource;
        private string _expectedSource;
        private bool _expectedExists;
        private string _savedId;
        private bool _saving;
        private bool _saveUnconfirmed;
        private bool _destroyed;
        private string _conflictSource;
        private bool _conflictExists;
        private Button _overwriteButton;
        private Button _discardButton;
        private Button _saveButton;
        private LuaCodePlaygroundRuntime _activeRuntime;

        /// <summary>The current unsaved buffer, available to a host before ending this view lifetime.</summary>
        public string DraftSource => _editor?.LuaDraft ?? _initialSource;

        /// <summary>The active document language; Lua remains the default.</summary>
        public string SelectedLanguage => _editor?.Document.LanguageId ?? "lua";

        /// <summary>The most recent explicit execution outcome.</summary>
        public CodeRunResult LastRunResult { get; private set; }

        /// <summary>Selects an installed editing language without executing or saving a draft.</summary>
        public void SelectLanguage(string languageId) => _editor?.SelectLanguage(languageId);

        /// <summary>Dismisses editor transients without closing the draft or changing Hub policy.</summary>
        public bool TryHandleEscape() => !_destroyed && (_editor?.TryHandleEscape() ?? false);

        /// <summary>Whether the buffer has changes or its last save remains unacknowledged.</summary>
        public bool IsDirty => _saveUnconfirmed || (_editor?.HasPythonDraftChanges ?? false) || !string.Equals(DraftSource, _baseSource, StringComparison.Ordinal);

        /// <param name="service">CRUD surface used to load/save/validate the mod.</param>
        /// <param name="modId">Existing mod id to edit; null/empty for a new or pasted mod.</param>
        /// <param name="initialSource">
        /// Pre-filled source (a pasted clipboard mod or the "Add" template). When null and
        /// <paramref name="modId"/> is set, the current source is fetched from the service.
        /// </param>
        /// <param name="onClose">Invoked when the user closes the editor (returns to the list).</param>
        public HubModEditorPage(IHubModService service, string modId, string initialSource, Action onClose)
            : this(service, modId, initialSource, onClose, null)
        {
        }

        /// <param name="service">CRUD surface used to load/save/validate the mod.</param>
        /// <param name="modId">Existing mod id to edit; null/empty for a new or pasted mod.</param>
        /// <param name="initialSource">Pre-filled source; see the other constructor.</param>
        /// <param name="onClose">Invoked when the user closes the editor (returns to the list).</param>
        /// <param name="preferences">
        /// Hub-wide editor preferences; null uses <see cref="PlayerPrefsHubModEditorPreferences.Instance"/>.
        /// </param>
        public HubModEditorPage(IHubModService service, string modId, string initialSource, Action onClose,
            IHubModEditorPreferences preferences)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
            _preferences = preferences ?? PlayerPrefsHubModEditorPreferences.Instance;
            _modId = (modId ?? "").Trim();
            _isNew = _modId.Length == 0;
            _onClose = onClose;

            if (initialSource != null)
            {
                _initialSource = initialSource;
            }
            else if (!_isNew && _service.TryGetSource(_modId, out string existing))
            {
                _initialSource = existing;
            }
            else
            {
                if (!_isNew)
                {
                    throw new InvalidOperationException("The mod source could not be read. No editable replacement was created.");
                }

                _initialSource = NewModTemplate;
            }

            _baseSource = _isNew ? null : _initialSource;
            _expectedSource = _baseSource;
            _expectedExists = !_isNew;
            _savedId = _modId;
        }

        /// <summary>Ends this editor lifetime without authorizing any further source mutation.</summary>
        internal void OnDestroyed()
        {
            if (_destroyed) return;
            _destroyed = true;
            _activeRuntime?.Dispose();
            if (_editor != null) _editor.LanguageChanged -= OnLanguageChanged;
            _editor?.Dispose();
            if (_keepObjectsToggle != null && _onKeepObjectsChanged != null)
                _keepObjectsToggle.UnregisterValueChangedCallback(_onKeepObjectsChanged);
            _builtRoot?.SetEnabled(false);
        }

        /// <summary>Builds the editor VisualElement.</summary>
        public VisualElement Build()
        {
            if (_builtRoot != null)
            {
                return _builtRoot;
            }

            ScrollView scroll = new(ScrollViewMode.Vertical) { name = "coreai-hub-mod-editor-scroll" };
            _builtRoot = scroll;
            scroll.style.flexGrow = 1f;
            scroll.style.minHeight = 0f;

            VisualElement root = scroll.contentContainer;
            root.name = "coreai-hub-mod-editor";
            root.style.flexGrow = 1f;
            root.style.flexDirection = FlexDirection.Column;

            VisualElement headerRow = new();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.marginBottom = 6f;
            headerRow.style.flexShrink = 0f;
            headerRow.Add(HubModWidgets.MakeButton("← Back", Close));
            _titleLabel = HubModWidgets.MakeTitle(_isNew ? "New mod" : $"Edit mod: {_modId}");
            _titleLabel.style.marginBottom = 0f;
            _titleLabel.style.marginLeft = 4f;
            headerRow.Add(_titleLabel);
            root.Add(headerRow);

            _headerBox = HubModWidgets.MakePanel();
            _headerBox.style.flexShrink = 0f;
            root.Add(_headerBox);

            Label manifestNote = HubModWidgets.MakeNote(
                "Manifest fields below are read from the mod's @coreai header — edit them in the code.");
            manifestNote.style.flexShrink = 0f;
            root.Add(manifestNote);

            VisualElement actions = new();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.marginBottom = 4f;
            actions.style.flexShrink = 0f;
            _saveButton = HubModWidgets.MakeButton("Save & run", Save);
            _saveButton.name = "coreai-hub-mod-save-run";
            actions.Add(_saveButton);
            actions.Add(HubModWidgets.MakeButton("Copy", Copy));
            actions.Add(HubModWidgets.MakeButton("Paste", Paste));
            actions.Add(HubModWidgets.MakeButton("Refresh diagnostics", RefreshDiagnostics));
            actions.Add(HubModWidgets.MakeButton("History", ToggleHistory));
            _overwriteButton = HubModWidgets.MakeDangerButton("Overwrite reviewed version", () => SaveCore(true));
            _overwriteButton.name = "coreai-hub-mod-overwrite";
            _overwriteButton.style.display = DisplayStyle.None;
            actions.Add(_overwriteButton);
            _discardButton = HubModWidgets.MakeDangerButton("Discard draft and go back", () => _onClose?.Invoke());
            _discardButton.name = "coreai-hub-mod-discard";
            _discardButton.style.display = DisplayStyle.None;
            actions.Add(_discardButton);
            root.Add(actions);

            _keepObjectsToggle = new Toggle("Keep objects on Save & run")
            {
                name = KeepObjectsToggleName,
                value = _preferences.KeepObjectsOnSave,
                tooltip = "Off (default): Save & run first removes the objects the previous run's main chunk " +
                          "built, so the new code builds into a clean world; objects created later by handlers " +
                          "or players stay. On: every object stays and the new code builds next to them. " +
                          "One setting for every mod."
            };
            _keepObjectsToggle.style.flexShrink = 0f;
            _keepObjectsToggle.style.marginBottom = 4f;
            HubModWidgets.StyleToggleLabel(_keepObjectsToggle);
            _onKeepObjectsChanged = evt =>
            {
                if (!_destroyed && ReferenceEquals(evt.target, _keepObjectsToggle)) OnKeepObjectsToggled(evt.newValue);
            };
            _keepObjectsToggle.RegisterValueChangedCallback(_onKeepObjectsChanged);
            root.Add(_keepObjectsToggle);

            _historyBox = HubModWidgets.MakePanel();
            _historyBox.style.flexShrink = 0f;
            _historyBox.style.display = DisplayStyle.None;
            root.Add(_historyBox);

            _editor = new CodeEditorSurface(string.IsNullOrEmpty(_modId) ? "new-mod" : _modId, _initialSource);
            _codeField = _editor.TextField;
            _editor.LanguageChanged += OnLanguageChanged;
            root.Add(_editor.Root);

            _status = HubModWidgets.MakeStatus();
            _status.name = StatusLabelName;
            root.Add(_status);

            _diagnostics = HubModWidgets.MakeMutedLabel(string.Empty);
            _diagnostics.style.fontSize = 11f;
            _diagnostics.style.marginTop = 4f;
            root.Add(_diagnostics);

            RefreshHeaderBox(_initialSource);
            RefreshDiagnostics();
            return scroll;
        }

        private void OnLanguageChanged()
        {
            bool lua = SelectedLanguage == "lua";
            _keepObjectsToggle?.SetEnabled(lua);
            _saveButton?.SetEnabled(lua && !_saving);
            if (_overwriteButton != null) _overwriteButton.style.display = DisplayStyle.None;
            SetStatus(lua ? "Lua draft restored. Save & run executes only this Lua draft."
                : "Python editing only. Execution and Save & run are unavailable; the Lua draft is preserved.", false);
        }

        private bool RequireLuaAction()
        {
            if (SelectedLanguage == "lua") return true;
            LastRunResult = new CodeRunResult(CodeRunStatus.Unsupported,
                "Python execution is not supported. No code was run or saved.");
            SetStatus(LastRunResult.Message, true);
            return false;
        }

        /// <summary>Records the toggle's new state as the Hub-wide preference.</summary>
        internal void OnKeepObjectsToggled(bool keepObjects)
        {
            _preferences.KeepObjectsOnSave = keepObjects;
        }

        /// <summary>
        /// The "Save &amp; run" action: persists and (re)runs the mod in the mode the toggle shows and says
        /// in the status line what the reload did with the previous run's objects.
        /// </summary>
        /// <remarks>The explicit invocation uses a short per-call budget on the caller thread.</remarks>
        internal void Save()
        {
            SaveCore(false);
        }

        private void SaveCore(bool overwrite)
        {
            if (_destroyed || _saving)
            {
                return;
            }

            if (!RequireLuaAction()) return;
            if (_service is not IBudgetedHubModService)
            {
                LastRunResult = new CodeRunResult(CodeRunStatus.Unsupported,
                    "The configured service does not support bounded editor execution. No code was run or saved.");
                SetStatus(LastRunResult.Message, true);
                return;
            }
            string code = DraftSource ?? "";
            string id = _isNew && string.IsNullOrEmpty(_savedId) ? ParseId(code) : _savedId;
            if (string.IsNullOrWhiteSpace(id))
            {
                SetStatus("Cannot save: set 'id:' in the @coreai header.", true);
                return;
            }

            bool keepObjects = _keepObjectsToggle?.value ?? _preferences.KeepObjectsOnSave;
            _editor.CaptureCurrent();
            CodeRunRequest request = null;
            _saving = true;
            try
            {
                bool exists = _service.TryGetSource(id, out string currentSource);
                bool sameBase = exists == _expectedExists && (!exists ||
                    string.Equals(currentSource, _expectedSource, StringComparison.Ordinal));
                bool reviewedOverwrite = overwrite && exists == _conflictExists && (!exists ||
                    string.Equals(currentSource, _conflictSource, StringComparison.Ordinal));
                if (!sameBase && !reviewedOverwrite)
                {
                    LastRunResult = new CodeRunResult(CodeRunStatus.Conflict,
                        "The source changed outside this editor. Your draft is preserved.");
                    _conflictSource = currentSource;
                    _conflictExists = exists;
                    _overwriteButton.style.display = DisplayStyle.Flex;
                    SetStatus("The source changed outside this editor. Your draft is preserved. Copy it or explicitly overwrite the reviewed version.", true);
                    return;
                }

                request = new CodeRunRequest(_editor.Document.DocumentId, "lua", _editor.Document.Revision,
                    id, code, CodeRunOperation.SaveAndRun, 100, 500_000,
                    new CodeSourceExpectation(exists, currentSource));
                _activeRuntime = new LuaCodePlaygroundRuntime(_service,
                    keepObjects ? ModReloadMode.KeepObjects : ModReloadMode.CleanStartupObjects);
                _activeRuntime.Initialize();
                _editor.View.SetEditable(false);
                _editor.LanguageDropdown.SetEnabled(false);
                _saveButton.SetEnabled(false);
                CodeRunResult result = _activeRuntime.Run(request);
                _editor.CaptureCurrent();
                if (_destroyed || !result.Matches(request) ||
                    !_editor.Document.Matches(request.DocumentId, request.LanguageId, request.Revision))
                {
                    _saveUnconfirmed = true;
                    return;
                }
                LastRunResult = result;
                if (result.Status == CodeRunStatus.Conflict)
                {
                    _conflictExists = _service.TryGetSource(id, out _conflictSource);
                    _overwriteButton.style.display = DisplayStyle.Flex;
                    SetStatus(result.Message + " Review the current version before overwriting it.", true);
                    return;
                }
                if (result.Status == CodeRunStatus.Unsupported)
                {
                    SetStatus(result.Message, true);
                    return;
                }
                bool saved = result.Persisted;
                _saveUnconfirmed = !saved;
                SetStatus(result.Message, !saved);
                if (saved)
                {
                    _baseSource = code;
                    _expectedSource = code;
                    _expectedExists = true;
                    _savedId = id;
                    _overwriteButton.style.display = DisplayStyle.None;
                    _discardButton.style.display = DisplayStyle.None;
                }
                else
                {
                    CaptureOwnLiveSource(id, code);
                }

                if (!_isNew)
                {
                    _titleLabel.text = $"Edit mod: {id}";
                }

                RefreshHeaderBox(code);
                RefreshDiagnostics();
                if (result.Diagnostics.Count > 0) _editor.View.ShowError(result.Diagnostics[0]);
            }
            catch (OperationCanceledException)
            {
                _saveUnconfirmed = true;
                CaptureOwnLiveSource(id, code);
                LastRunResult = new CodeRunResult(request, CodeRunStatus.Cancelled,
                    "Save cancelled. Your draft is preserved; live state may already have changed.");
                SetStatus(LastRunResult.Message, true);
            }
            catch (NotSupportedException ex)
            {
                LastRunResult = new CodeRunResult(request, CodeRunStatus.Unsupported, ex.Message);
                SetStatus(LastRunResult.Message, true);
            }
            catch (Exception ex)
            {
                LastRunResult = new CodeRunResult(request, CodeRunStatus.Failed, ex.Message);
                _saveUnconfirmed = true;
                CaptureOwnLiveSource(id, code);
                SetStatus($"Save failed: {ex.Message}", true);
            }
            finally
            {
                _activeRuntime?.Dispose();
                _activeRuntime = null;
                _saving = false;
                if (!_destroyed)
                {
                    _editor.View.SetEditable(true);
                    _editor.LanguageDropdown.SetEnabled(true);
                    _saveButton.SetEnabled(SelectedLanguage == "lua");
                }
            }
        }

        private void CaptureOwnLiveSource(string id, string code)
        {
            try
            {
                if (_service.TryGetSource(id, out string liveSource) &&
                    string.Equals(liveSource, code, StringComparison.Ordinal))
                {
                    _expectedExists = true;
                    _expectedSource = liveSource;
                    _savedId = id;
                }
            }
            catch
            {
                // WHY: an unreadable live baseline must not replace the original save failure or clear the draft.
            }
        }

        private void Copy()
        {
            if (_destroyed) return;
            GUIUtility.systemCopyBuffer = _codeField.value ?? "";
            SetStatus("Copied source to clipboard.", false);
        }

        private void Paste()
        {
            if (_destroyed) return;
            string clip = GUIUtility.systemCopyBuffer ?? "";
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetStatus("Clipboard is empty — nothing to paste.", true);
                return;
            }

            string normalized = CodeEditorTextUtility.NormalizeEditorText(clip);
            if (normalized.Length > CodeEditorLimits.MaximumDocumentCharacters)
            {
                SetStatus("The clipboard exceeds the editor source limit. The current draft is preserved.", true);
                return;
            }
            _editor.View.ReplaceTextUndoable(normalized);
            if (SelectedLanguage == "lua") RefreshHeaderBox(normalized);
            SetStatus("Pasted clipboard into the editor. Review, then Save & run.", false);
        }

        private void RefreshDiagnostics()
        {
            string id = _isNew ? ParseId(DraftSource ?? "") : _modId;
            if (string.IsNullOrWhiteSpace(id))
            {
                _diagnostics.text = "";
                return;
            }

            string errors = _service.RecentErrors(id);
            _diagnostics.text = string.IsNullOrEmpty(errors)
                ? "No recent runtime errors."
                : "Recent runtime errors:\n" + errors;
            _diagnostics.style.color = string.IsNullOrEmpty(errors) ? HubModWidgets.Muted : HubModWidgets.Danger;
        }

        private void ToggleHistory()
        {
            _historyOpen = !_historyOpen;
            if (_historyOpen)
            {
                RefreshHistory();
            }

            _historyBox.style.display = _historyOpen ? DisplayStyle.Flex : DisplayStyle.None;
        }

        private void RefreshHistory()
        {
            _historyBox.Clear();
            string id = _isNew ? ParseId(DraftSource ?? "") : _modId;
            if (string.IsNullOrWhiteSpace(id))
            {
                _historyBox.Add(HubModWidgets.MakeNote("Save the mod first to track revision history."));
                return;
            }

            IReadOnlyList<LuaScriptRevision> revisions;
            try
            {
                revisions = _service.ListModVersions(id);
            }
            catch (Exception ex)
            {
                _historyBox.Add(HubModWidgets.MakeNote($"Failed to load history: {ex.Message}"));
                return;
            }

            if (revisions == null || revisions.Count == 0)
            {
                _historyBox.Add(HubModWidgets.MakeNote("No revision history recorded for this mod."));
                return;
            }

            for (int i = revisions.Count - 1; i >= 0; i--)
            {
                _historyBox.Add(BuildRevisionRow(id, revisions[i]));
            }
        }

        private VisualElement BuildRevisionRow(string id, LuaScriptRevision revision)
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.alignItems = Align.Center;
            row.style.marginBottom = 2f;

            string when = new DateTime(revision.UtcTicks, DateTimeKind.Utc).ToLocalTime()
                .ToString("yyyy-MM-dd HH:mm:ss");
            string preview = FirstLinePreview(revision.Source);
            Label label = HubModWidgets.MakeMutedLabel(
                $"#{revision.Index}  {when}  ({revision.Source.Length} chars)  {preview}");
            label.style.flexGrow = 1f;
            label.style.flexShrink = 1f;
            row.Add(label);

            row.Add(HubModWidgets.MakeButton("Revert", () => Revert(id, revision.Index)));
            return row;
        }

        private static string FirstLinePreview(string source)
        {
            string trimmed = (source ?? "").TrimStart();
            int newline = trimmed.IndexOfAny(new[] { '\r', '\n' });
            string firstLine = (newline >= 0 ? trimmed.Substring(0, newline) : trimmed).Trim();
            const int maxLength = 60;
            return firstLine.Length > maxLength ? firstLine.Substring(0, maxLength) + "…" : firstLine;
        }

        private void Revert(string id, int revisionIndex)
        {
            if (!RequireLuaAction()) return;
            if (_destroyed)
            {
                return;
            }

            try
            {
                if (!_service.TryRevertMod(id, revisionIndex, out string restored))
                {
                    SetStatus($"Revision #{revisionIndex} not found for '{id}'.", true);
                    return;
                }

                _editor.View.ReplaceTextUndoable(restored);
                _expectedExists = true;
                _expectedSource = restored;
                RefreshHeaderBox(restored);
                RefreshDiagnostics();
                RefreshHistory();
                SetStatus($"Reverted '{id}' to revision #{revisionIndex}.", false);
            }
            catch (Exception ex)
            {
                SetStatus($"Revert failed: {ex.Message}", true);
            }
        }

        private void Close()
        {
            if (_destroyed)
            {
                return;
            }

            if (IsDirty)
            {
                _discardButton.style.display = DisplayStyle.Flex;
                SetStatus("Unsaved draft preserved. Save, copy, or choose Discard draft and go back.", true);
                return;
            }

            _onClose?.Invoke();
        }

        private void RefreshHeaderBox(string code)
        {
            _headerBox.Clear();
            LuaModHeader header = LuaModHeader.Parse(code ?? "", _isNew ? ParseId(code ?? "") : _modId);
            _headerBox.Add(HeaderRow("Id", string.IsNullOrWhiteSpace(header.Id) ? "(none)" : header.Id));
            _headerBox.Add(HeaderRow("Name", header.Name));
            _headerBox.Add(HeaderRow("Version", header.Version));
            _headerBox.Add(HeaderRow("Capabilities", header.Capabilities));
            _headerBox.Add(HeaderRow("Category", string.IsNullOrWhiteSpace(header.Category) ? "-" : header.Category));
            _headerBox.Add(HeaderRow("Tags", string.IsNullOrWhiteSpace(header.Tags) ? "-" : header.Tags));
            _headerBox.Add(HeaderRow("Author", string.IsNullOrWhiteSpace(header.Author) ? "-" : header.Author));
            if (!string.IsNullOrWhiteSpace(header.Description))
            {
                _headerBox.Add(HeaderRow("Description", header.Description));
            }
        }

        private static VisualElement HeaderRow(string key, string value)
        {
            VisualElement row = new();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginBottom = 1f;

            Label keyLabel = HubModWidgets.MakeMutedLabel(key);
            keyLabel.style.width = 100f;
            keyLabel.style.flexShrink = 0f;

            Label valueLabel = HubModWidgets.MakeFieldLabel(value ?? "");
            valueLabel.style.flexGrow = 1f;
            valueLabel.style.flexShrink = 1f;

            row.Add(keyLabel);
            row.Add(valueLabel);
            return row;
        }

        private static string ParseId(string code)
        {
            return LuaModHeader.Parse(code ?? "", "").Id?.Trim() ?? "";
        }

        private void SetStatus(string message, bool error)
        {
            _status.text = message;
            _status.style.color = error ? HubModWidgets.Danger : HubModWidgets.Accent;
        }
    }
}
