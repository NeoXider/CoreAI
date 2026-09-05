using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.Demos.Shared
{
    /// <summary>
    /// The one panel every CoreAI demo draws on: a title, a scrolling log, and a row of buttons.
    /// </summary>
    /// <remarks>
    /// WHY a shared panel and not per-demo UI: ten demos each hand-building a Canvas is ten places to
    /// get the anchors wrong, and the resulting scenes look like ten different products. More
    /// importantly it is what makes the IMGUI migration finishable — a controller stops drawing and
    /// starts calling four methods, so the work per demo is small and the result is uniform.
    /// <para>
    /// It builds itself at runtime rather than shipping a prefab: a prefab is a binary asset whose
    /// diff nobody can read, and every demo would need its own copy wired by hand. This way the
    /// layout lives in reviewable code and a demo needs one <c>AddComponent</c>.
    /// </para>
    /// </remarks>
    [AddComponentMenu("CoreAI/Demos/Demo Panel")]
    public sealed class CoreAiDemoPanel : MonoBehaviour
    {
        private const int MaxLogLines = 14;

        private readonly List<string> _lines = new();
        private readonly Dictionary<string, Button> _buttons = new(StringComparer.Ordinal);
        private readonly List<GameObject> _rows = new();
        private RectTransform _buttonRow;
        private RectTransform _rowList;
        private TextMeshProUGUI _titleLabel;
        private TextMeshProUGUI _subtitleLabel;
        private TextMeshProUGUI _logLabel;
        private TMP_InputField _input;

        /// <summary>Creates the panel on a fresh Canvas and returns it, ready to use.</summary>
        public static CoreAiDemoPanel Create(string title, string subtitle = "")
        {
            GameObject canvasObject = new("CoreAI_DemoPanel");
            Canvas canvas = canvasObject.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            CanvasScaler scaler = canvasObject.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            canvasObject.AddComponent<GraphicRaycaster>();
            EnsureEventSystem();

            CoreAiDemoPanel panel = canvasObject.AddComponent<CoreAiDemoPanel>();
            panel.Build(title, subtitle);
            return panel;
        }

        /// <summary>Replaces the subtitle, for a demo that explains what to press next.</summary>
        public void SetSubtitle(string subtitle)
        {
            if (_subtitleLabel != null)
            {
                _subtitleLabel.text = subtitle ?? "";
            }
        }

        /// <summary>Appends one line to the log, oldest lines falling off the top.</summary>
        public void Log(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            _lines.Add(line);
            if (_lines.Count > MaxLogLines)
            {
                _lines.RemoveAt(0);
            }

            if (_logLabel != null)
            {
                _logLabel.text = string.Join("\n", _lines);
            }
        }

        /// <summary>Replaces the whole log with one block of text.</summary>
        public void SetLog(string text)
        {
            _lines.Clear();
            _lines.Add(text ?? "");
            if (_logLabel != null)
            {
                _logLabel.text = text ?? "";
            }
        }

        /// <summary>Adds a button, or re-points an existing one with the same caption.</summary>
        public Button AddButton(string caption, Action onClick)
        {
            if (_buttons.TryGetValue(caption, out Button existing))
            {
                existing.onClick.RemoveAllListeners();
                existing.onClick.AddListener(() => onClick?.Invoke());
                return existing;
            }

            Button button = CreateButton(_buttonRow, caption);
            button.onClick.AddListener(() => onClick?.Invoke());
            _buttons.Add(caption, button);
            return button;
        }

        /// <summary>Enables or disables one button by caption.</summary>
        public void SetButtonInteractable(string caption, bool interactable)
        {
            if (_buttons.TryGetValue(caption, out Button button))
            {
                button.interactable = interactable;
            }
        }

        /// <summary>
        /// Adds a row of "label plus a few small buttons" to the list area, for a panel that shows
        /// a collection whose entries each have their own actions.
        /// </summary>
        /// <remarks>
        /// WHY rows rather than one text blob: a mod manager's entries are things you act on, not
        /// things you read. Rendering them as text would force the demo to ask which one you meant,
        /// and typing an id instead of clicking a row is a different demo.
        /// </remarks>
        public void AddRow(string label, params (string Caption, Action OnClick)[] actions)
        {
            EnsureRowList();

            GameObject rowObject = new("Row");
            rowObject.transform.SetParent(_rowList, false);
            HorizontalLayoutGroup layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childForceExpandHeight = true;
            layout.childControlHeight = true;
            layout.childControlWidth = true;
            LayoutElement height = rowObject.AddComponent<LayoutElement>();
            height.minHeight = 34f;
            height.preferredHeight = 34f;

            TextMeshProUGUI text = CreateLabel(rowObject.transform, label, 17f,
                new Color(0.90f, 0.92f, 0.96f));
            text.alignment = TextAlignmentOptions.Left;
            LayoutElement textWidth = text.gameObject.AddComponent<LayoutElement>();
            textWidth.flexibleWidth = 1f;

            foreach ((string caption, Action onClick) in actions)
            {
                Button button = CreateButton(rowObject.transform, caption);
                Action captured = onClick;
                button.onClick.AddListener(() => captured?.Invoke());
                LayoutElement width = button.gameObject.AddComponent<LayoutElement>();
                width.minWidth = 88f;
                width.flexibleWidth = 0f;
            }

            _rows.Add(rowObject);
        }

        /// <summary>Removes every row, for a list that is rebuilt when its source changes.</summary>
        public void ClearRows()
        {
            for (int index = 0; index < _rows.Count; index++)
            {
                if (_rows[index] != null)
                {
                    Destroy(_rows[index]);
                }
            }

            _rows.Clear();
        }

        private void EnsureRowList()
        {
            if (_rowList != null)
            {
                return;
            }

            GameObject listObject = new("Rows");
            listObject.transform.SetParent(transform.GetChild(0), false);
            _rowList = listObject.AddComponent<RectTransform>();
            _rowList.anchorMin = new Vector2(0f, 0f);
            _rowList.anchorMax = new Vector2(1f, 1f);
            _rowList.offsetMin = new Vector2(24f, 150f);
            _rowList.offsetMax = new Vector2(-24f, -320f);
            VerticalLayoutGroup layout = listObject.AddComponent<VerticalLayoutGroup>();
            layout.spacing = 4f;
            layout.childForceExpandHeight = false;
            layout.childControlHeight = true;
            layout.childControlWidth = true;

            // The log keeps the space below the rows, so the two never overlap.
            if (_logLabel != null)
            {
                _logLabel.rectTransform.offsetMax = new Vector2(-24f, -330f);
            }
        }

        /// <summary>
        /// Removes every button, for a panel whose actions depend on a list that changes.
        /// </summary>
        /// <remarks>
        /// WHY it exists: a demo that offers one button per loaded mod has to drop the buttons for
        /// mods that were unloaded. Without this the row would only ever grow, and a click would
        /// reach a mod that is no longer there.
        /// </remarks>
        public void ClearButtons()
        {
            foreach (KeyValuePair<string, Button> pair in _buttons)
            {
                if (pair.Value != null)
                {
                    Destroy(pair.Value.gameObject);
                }
            }

            _buttons.Clear();
        }

        /// <summary>Adds a multi-line editor and returns it; null until this is called.</summary>
        public TMP_InputField AddEditor(string placeholder)
        {
            TMP_InputField editor = AddInput(placeholder);
            editor.lineType = TMP_InputField.LineType.MultiLineNewline;
            RectTransform rect = editor.GetComponent<RectTransform>();
            rect.offsetMin = new Vector2(24f, 86f);
            rect.offsetMax = new Vector2(-24f, 320f);
            return editor;
        }

        /// <summary>Adds a single-line text field and returns it; null until this is called.</summary>
        public TMP_InputField AddInput(string placeholder)
        {
            if (_input != null)
            {
                return _input;
            }

            GameObject fieldObject = new("Input");
            fieldObject.transform.SetParent(transform.GetChild(0), false);
            Image background = fieldObject.AddComponent<Image>();
            background.color = new Color(0.12f, 0.13f, 0.18f, 1f);
            RectTransform rect = fieldObject.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 0f);
            rect.pivot = new Vector2(0.5f, 0f);
            rect.offsetMin = new Vector2(24f, 86f);
            rect.offsetMax = new Vector2(-24f, 130f);

            GameObject textObject = new("Text");
            textObject.transform.SetParent(fieldObject.transform, false);
            TextMeshProUGUI text = textObject.AddComponent<TextMeshProUGUI>();
            text.fontSize = 20f;
            text.color = new Color(0.93f, 0.95f, 0.98f);
            text.alignment = TextAlignmentOptions.Left;
            Stretch(text.rectTransform, 10f);

            GameObject placeholderObject = new("Placeholder");
            placeholderObject.transform.SetParent(fieldObject.transform, false);
            TextMeshProUGUI hint = placeholderObject.AddComponent<TextMeshProUGUI>();
            hint.text = placeholder ?? "";
            hint.fontSize = 20f;
            hint.color = new Color(0.55f, 0.58f, 0.64f);
            hint.alignment = TextAlignmentOptions.Left;
            Stretch(hint.rectTransform, 10f);

            _input = fieldObject.AddComponent<TMP_InputField>();
            _input.textViewport = text.rectTransform;
            _input.textComponent = text;
            _input.placeholder = hint;
            _input.lineType = TMP_InputField.LineType.SingleLine;
            return _input;
        }

        private void Build(string title, string subtitle)
        {
            GameObject panelObject = new("Panel");
            panelObject.transform.SetParent(transform, false);
            Image background = panelObject.AddComponent<Image>();
            background.color = new Color(0.05f, 0.06f, 0.09f, 0.88f);
            RectTransform panel = panelObject.GetComponent<RectTransform>();
            panel.anchorMin = new Vector2(0f, 0f);
            panel.anchorMax = new Vector2(0f, 1f);
            panel.pivot = new Vector2(0f, 0.5f);
            panel.anchoredPosition = new Vector2(24f, 0f);
            panel.sizeDelta = new Vector2(700f, -48f);

            _titleLabel = CreateLabel(panel, title, 30f, new Color(0.66f, 0.84f, 1f));
            _titleLabel.fontStyle = FontStyles.Bold;
            Anchor(_titleLabel.rectTransform, -24f, 44f);

            _subtitleLabel = CreateLabel(panel, subtitle, 19f, new Color(0.74f, 0.78f, 0.85f));
            Anchor(_subtitleLabel.rectTransform, -74f, 60f);

            _logLabel = CreateLabel(panel, "", 20f, new Color(0.92f, 0.94f, 0.97f));
            RectTransform log = _logLabel.rectTransform;
            log.anchorMin = new Vector2(0f, 0f);
            log.anchorMax = new Vector2(1f, 1f);
            log.offsetMin = new Vector2(24f, 150f);
            log.offsetMax = new Vector2(-24f, -142f);

            GameObject rowObject = new("Buttons");
            rowObject.transform.SetParent(panelObject.transform, false);
            _buttonRow = rowObject.AddComponent<RectTransform>();
            _buttonRow.anchorMin = new Vector2(0f, 0f);
            _buttonRow.anchorMax = new Vector2(1f, 0f);
            _buttonRow.pivot = new Vector2(0.5f, 0f);
            _buttonRow.offsetMin = new Vector2(24f, 20f);
            _buttonRow.offsetMax = new Vector2(-24f, 76f);
            HorizontalLayoutGroup layout = rowObject.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 10f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = true;
            layout.childControlWidth = true;
            layout.childControlHeight = true;
        }

        private static void EnsureEventSystem()
        {
            if (FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null)
            {
                return;
            }

            GameObject events = new("EventSystem");
            events.AddComponent<UnityEngine.EventSystems.EventSystem>();
            events.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
        }

        private static TextMeshProUGUI CreateLabel(Transform parent, string text, float size,
            Color color)
        {
            GameObject labelObject = new("Label");
            labelObject.transform.SetParent(parent, false);
            TextMeshProUGUI label = labelObject.AddComponent<TextMeshProUGUI>();
            label.text = text ?? "";
            label.fontSize = size;
            label.color = color;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.richText = true;
            return label;
        }

        private static Button CreateButton(Transform parent, string caption)
        {
            GameObject buttonObject = new("Button " + caption);
            buttonObject.transform.SetParent(parent, false);
            Image background = buttonObject.AddComponent<Image>();
            background.color = new Color(0.16f, 0.34f, 0.58f);
            Button button = buttonObject.AddComponent<Button>();

            GameObject captionObject = new("Caption");
            captionObject.transform.SetParent(buttonObject.transform, false);
            TextMeshProUGUI label = captionObject.AddComponent<TextMeshProUGUI>();
            label.text = caption;
            label.fontSize = 18f;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            Stretch(label.rectTransform, 4f);
            return button;
        }

        private static void Anchor(RectTransform rect, float top, float height)
        {
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.offsetMin = new Vector2(24f, top - height);
            rect.offsetMax = new Vector2(-24f, top);
        }

        private static void Stretch(RectTransform rect, float padding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
        }
    }
}
