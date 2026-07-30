using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Hub
{
    /// <summary>
    /// The Lua mod code editor shown inside the <see cref="HubModsPage"/>: a multiline source field,
    /// the mod's parsed <c>@coreai</c> header fields, and Save / Copy / Close / Refresh-diagnostics
    /// actions. Save persists and (re)runs the mod through <see cref="IHubModService.SaveOrReload"/>,
    /// which validates the Lua by executing it — a compile/run error is caught and shown in the status
    /// line. Runtime hook/timer failures are surfaced from
    /// <see cref="IHubModService.RecentErrors"/>. New/pasted mods derive their id from the header on save.
    /// </summary>
    public sealed class HubModEditorPage
    {
        /// <summary>Starter source offered by the Mods page "Add" button.</summary>
        public const string NewModTemplate =
            "--[[@coreai\n" +
            "id: new_mod\n" +
            "name: New Mod\n" +
            "version: 1.0.0\n" +
            "active: true\n" +
            "capabilities: All\n" +
            "author: \n" +
            "description: A new CoreAI Lua mod.\n" +
            "]]\n" +
            "\n" +
            "-- Registered once on load; the host then drives your hooks.\n" +
            "hooks_every(1.0, function()\n" +
            "  report(\"new_mod tick\")\n" +
            "end)\n";

        private readonly IHubModService _service;
        private readonly string _modId;
        private readonly bool _isNew;
        private readonly string _initialSource;
        private readonly Action _onClose;

        private TextField _codeField;
        private Label _highlightOverlay;
        private Label _status;
        private Label _diagnostics;
        private VisualElement _headerBox;
        private Label _titleLabel;
        private VisualElement _historyBox;
        private bool _historyOpen;

        /// <param name="service">CRUD surface used to load/save/validate the mod.</param>
        /// <param name="modId">Existing mod id to edit; null/empty for a new or pasted mod.</param>
        /// <param name="initialSource">
        /// Pre-filled source (a pasted clipboard mod or the "Add" template). When null and
        /// <paramref name="modId"/> is set, the current source is fetched from the service.
        /// </param>
        /// <param name="onClose">Invoked when the user closes the editor (returns to the list).</param>
        public HubModEditorPage(IHubModService service, string modId, string initialSource, Action onClose)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));
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
                _initialSource = NewModTemplate;
            }
        }

        /// <summary>Builds the editor VisualElement.</summary>
        public VisualElement Build()
        {
            ScrollView scroll = new(ScrollViewMode.Vertical) { name = "coreai-hub-mod-editor-scroll" };
            scroll.style.flexGrow = 1f;
            scroll.style.minHeight = 0f;

            VisualElement root = scroll.contentContainer;
            root.name = "coreai-hub-mod-editor";
            root.style.flexGrow = 1f;
            root.style.flexDirection = FlexDirection.Column;

            // Header row: a Back button next to the title so returning to the list is always one click away
            // and always on-screen (previously the only way back — "Close" — sat below the tall code field).
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

            // Primary actions above the code field so they never scroll off-screen.
            VisualElement actions = new();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.marginBottom = 4f;
            actions.style.flexShrink = 0f;
            actions.Add(HubModWidgets.MakeButton("Save & run", Save));
            actions.Add(HubModWidgets.MakeButton("Copy", Copy));
            actions.Add(HubModWidgets.MakeButton("Paste", Paste));
            actions.Add(HubModWidgets.MakeButton("Refresh diagnostics", RefreshDiagnostics));
            actions.Add(HubModWidgets.MakeButton("History", ToggleHistory));
            root.Add(actions);

            _historyBox = HubModWidgets.MakePanel();
            _historyBox.style.flexShrink = 0f;
            _historyBox.style.display = DisplayStyle.None;
            root.Add(_historyBox);

            _codeField = new TextField { name = "coreai-hub-mod-code", multiline = true };
            _codeField.value = _initialSource;
            _codeField.style.flexGrow = 0f;
            _codeField.style.minHeight = 280f;
            _codeField.style.whiteSpace = WhiteSpace.Normal;
            _codeField.style.marginTop = 6f;
            _codeField.style.marginBottom = 6f;
            HubModWidgets.StyleCodeField(_codeField);
            BuildHighlightOverlay();

            root.Add(_codeField);

            _status = HubModWidgets.MakeStatus();
            root.Add(_status);

            _diagnostics = HubModWidgets.MakeMutedLabel(string.Empty);
            _diagnostics.style.fontSize = 11f;
            _diagnostics.style.marginTop = 4f;
            root.Add(_diagnostics);

            RefreshHeaderBox(_initialSource);
            RefreshDiagnostics();
            return scroll;
        }

        /// <summary>
        /// Adds a Lua-highlighted rich-text overlay on top of the code field's input area. UI Toolkit's
        /// editable TextField cannot render rich text itself, so the raw input glyphs are made
        /// transparent (caret and selection stay visible via <see cref="TextField.textSelection"/>) and a
        /// picking-ignored Label child paints the same text through
        /// <see cref="LuaSyntaxHighlighter.Highlight"/>. Typing still goes into <see cref="_codeField"/>;
        /// the overlay re-renders on every value change.
        /// </summary>
        private void BuildHighlightOverlay()
        {
            VisualElement input = _codeField.Q("unity-text-input");
            if (input == null)
            {
                // WHY: no known Unity version lacks this element, but if it is ever renamed the editor
                // must degrade to a plain (uncolored) yet fully working field rather than a blank one.
                return;
            }

            Color codeTextColor = new(0.86f, 0.93f, 0.86f, 1f);

            _highlightOverlay = new Label
            {
                name = "coreai-hub-mod-code-highlight",
                enableRichText = true,
                pickingMode = PickingMode.Ignore
            };
            _highlightOverlay.style.position = Position.Absolute;
            // WHY: UI Toolkit positions absolute children against the parent's padding box, not its
            // content box, so a zero inset ignores the input's own left/top padding (see
            // HubModWidgets.StyleCodeField). Matching that padding here lines the overlay text up with
            // where the real (transparent) glyphs would render.
            _highlightOverlay.style.left = 0f;
            _highlightOverlay.style.top = 0f;
            _highlightOverlay.style.right = 0f;
            _highlightOverlay.style.bottom = 0f;
            _highlightOverlay.style.marginLeft = 0f;
            _highlightOverlay.style.marginRight = 0f;
            _highlightOverlay.style.marginTop = 0f;
            _highlightOverlay.style.marginBottom = 0f;
            _highlightOverlay.style.paddingLeft = 10f;
            _highlightOverlay.style.paddingRight = 10f;
            _highlightOverlay.style.paddingTop = 8f;
            _highlightOverlay.style.paddingBottom = 8f;
            _highlightOverlay.style.whiteSpace = WhiteSpace.Normal;
            _highlightOverlay.style.unityTextAlign = TextAnchor.UpperLeft;
            _highlightOverlay.style.color = codeTextColor;
            // TODO: if the input ever scrolls internally (it normally auto-grows inside the page's
            // ScrollView instead), the overlay does not track that inner scroll offset; typing
            // correctness is prioritized over pixel-perfect alignment in that edge case.
            input.Add(_highlightOverlay);

            // WHY: hide only the raw glyphs — the overlay repaints them colored in the same spots; the
            // caret/selection would inherit the transparent color, so they are re-pinned explicitly.
            input.style.color = new Color(0f, 0f, 0f, 0f);
            _codeField.textSelection.cursorColor = codeTextColor;
            _codeField.textSelection.selectionColor =
                new Color(HubModWidgets.Accent.r, HubModWidgets.Accent.g, HubModWidgets.Accent.b, 0.35f);

            _highlightOverlay.text = LuaSyntaxHighlighter.Highlight(_codeField.value);
            _codeField.RegisterValueChangedCallback(evt =>
                _highlightOverlay.text = LuaSyntaxHighlighter.Highlight(evt.newValue));
        }

        private void Save()
        {
            string code = _codeField.value ?? "";
            string id = _isNew ? ParseId(code) : _modId;
            if (string.IsNullOrWhiteSpace(id))
            {
                SetStatus("Cannot save: set 'id:' in the @coreai header.", true);
                return;
            }

            try
            {
                _service.SaveOrReload(id, code);
                SetStatus($"Saved & ran '{id}'.", false);
                if (!_isNew)
                {
                    _titleLabel.text = $"Edit mod: {id}";
                }

                RefreshHeaderBox(code);
                RefreshDiagnostics();
            }
            catch (Exception ex)
            {
                SetStatus($"Save failed: {ex.Message}", true);
            }
        }

        private void Copy()
        {
            GUIUtility.systemCopyBuffer = _codeField.value ?? "";
            SetStatus("Copied source to clipboard.", false);
        }

        private void Paste()
        {
            string clip = GUIUtility.systemCopyBuffer ?? "";
            if (string.IsNullOrWhiteSpace(clip))
            {
                SetStatus("Clipboard is empty — nothing to paste.", true);
                return;
            }

            _codeField.value = clip;
            RefreshHeaderBox(clip);
            SetStatus("Pasted clipboard into the editor. Review, then Save & run.", false);
        }

        private void RefreshDiagnostics()
        {
            string id = _isNew ? ParseId(_codeField?.value ?? "") : _modId;
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
            string id = _isNew ? ParseId(_codeField?.value ?? "") : _modId;
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

            // Newest first for readability; ListModVersions itself is oldest-first (revision 0 = original).
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
            try
            {
                if (!_service.TryRevertMod(id, revisionIndex, out string restored))
                {
                    SetStatus($"Revision #{revisionIndex} not found for '{id}'.", true);
                    return;
                }

                _codeField.value = restored;
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
