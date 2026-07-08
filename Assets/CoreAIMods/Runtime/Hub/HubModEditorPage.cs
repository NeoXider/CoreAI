using System;
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
        private Label _status;
        private Label _diagnostics;
        private VisualElement _headerBox;
        private Label _titleLabel;

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
            VisualElement root = new() { name = "coreai-hub-mod-editor" };
            root.style.flexGrow = 1f;
            root.style.flexDirection = FlexDirection.Column;

            // Header row: a Back button next to the title so returning to the list is always one click away
            // and always on-screen (previously the only way back — "Close" — sat below the tall code field).
            VisualElement headerRow = new();
            headerRow.style.flexDirection = FlexDirection.Row;
            headerRow.style.alignItems = Align.Center;
            headerRow.style.marginBottom = 6f;
            headerRow.Add(HubModWidgets.MakeButton("← Back", Close));
            _titleLabel = HubModWidgets.MakeTitle(_isNew ? "New mod" : $"Edit mod: {_modId}");
            _titleLabel.style.marginBottom = 0f;
            _titleLabel.style.marginLeft = 4f;
            headerRow.Add(_titleLabel);
            root.Add(headerRow);

            _headerBox = HubModWidgets.MakePanel();
            root.Add(_headerBox);

            root.Add(HubModWidgets.MakeNote(
                "Manifest fields below are read from the mod's @coreai header — edit them in the code."));

            // Primary actions above the code field so they never scroll off-screen.
            VisualElement actions = new();
            actions.style.flexDirection = FlexDirection.Row;
            actions.style.flexWrap = Wrap.Wrap;
            actions.style.marginBottom = 4f;
            actions.Add(HubModWidgets.MakeButton("Save & run", Save));
            actions.Add(HubModWidgets.MakeButton("Copy", Copy));
            actions.Add(HubModWidgets.MakeButton("Refresh diagnostics", RefreshDiagnostics));
            root.Add(actions);

            _codeField = new TextField { name = "coreai-hub-mod-code", multiline = true };
            _codeField.value = _initialSource;
            _codeField.style.flexGrow = 1f;
            _codeField.style.minHeight = 220f;
            _codeField.style.whiteSpace = WhiteSpace.Normal;
            _codeField.style.marginTop = 6f;
            _codeField.style.marginBottom = 6f;
            VisualElement input = _codeField.Q("unity-text-input");
            if (input != null)
            {
                input.style.unityTextAlign = TextAnchor.UpperLeft;
                input.style.whiteSpace = WhiteSpace.Normal;
            }

            root.Add(_codeField);

            _status = HubModWidgets.MakeStatus();
            root.Add(_status);

            _diagnostics = HubModWidgets.MakeMutedLabel(string.Empty);
            _diagnostics.style.fontSize = 11f;
            _diagnostics.style.marginTop = 4f;
            root.Add(_diagnostics);

            RefreshHeaderBox(_initialSource);
            RefreshDiagnostics();
            return root;
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
