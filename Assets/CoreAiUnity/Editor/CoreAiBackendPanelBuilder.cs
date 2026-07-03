#nullable enable
using System.IO;
using CoreAI.UI;
using TMPro;
using UnityEditor;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.Editor
{
    /// <summary>
    /// Builds the drop-in <see cref="CoreAiBackendPanel"/> Canvas hierarchy: a menu item to add it to
    /// the open scene, and a utility to (re)generate the shipped prefab at
    /// <c>Assets/CoreAiUnity/Prefabs/CoreAiBackendPanel.prefab</c>.
    /// </summary>
    public static class CoreAiBackendPanelBuilder
    {
        private const string PrefabDir = "Assets/CoreAiUnity/Prefabs";
        private const string PrefabPath = PrefabDir + "/CoreAiBackendPanel.prefab";

        [MenuItem("GameObject/CoreAI/Backend Panel (Canvas)", false, 10)]
        public static void CreateInScene()
        {
            GameObject root = BuildHierarchy();
            Undo.RegisterCreatedObjectUndo(root, "Create CoreAI Backend Panel");
            Selection.activeGameObject = root;
        }

        [MenuItem("CoreAI/UI/Regenerate Backend Panel Prefab")]
        public static void RegeneratePrefab()
        {
            GameObject root = BuildHierarchy();
            try
            {
                if (!Directory.Exists(PrefabDir))
                {
                    Directory.CreateDirectory(PrefabDir);
                    AssetDatabase.Refresh();
                }

                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out bool success);
                Debug.Log(success
                    ? $"[CoreAI] Backend panel prefab saved: {PrefabPath}"
                    : $"[CoreAI] Backend panel prefab save FAILED: {PrefabPath}");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        /// <summary>Builds the full Canvas + panel hierarchy and returns the Canvas root.</summary>
        public static GameObject BuildHierarchy()
        {
            // Canvas root.
            GameObject canvasGo = new("CoreAiBackendPanel", typeof(Canvas), typeof(CanvasScaler),
                typeof(GraphicRaycaster));
            Canvas canvas = canvasGo.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;
            CanvasScaler scaler = canvasGo.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);

            // Panel background.
            GameObject panel = CreateUiObject("Panel", canvasGo.transform);
            Image bg = panel.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.09f, 0.11f, 0.95f);
            RectTransform panelRt = panel.GetComponent<RectTransform>();
            panelRt.anchorMin = new Vector2(0.5f, 0.5f);
            panelRt.anchorMax = new Vector2(0.5f, 0.5f);
            panelRt.pivot = new Vector2(0.5f, 0.5f);
            panelRt.sizeDelta = new Vector2(560, 420);

            VerticalLayoutGroup layout = panel.AddComponent<VerticalLayoutGroup>();
            layout.padding = new RectOffset(20, 20, 16, 16);
            layout.spacing = 10;
            layout.childControlWidth = true;
            layout.childControlHeight = false;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;

            CreateLabel(panel.transform, "Title", "CoreAI Backend", 24, FontStyles.Bold);

            // Backend dropdown row.
            TMP_Dropdown dropdown = CreateDropdown(panel.transform, "BackendDropdown");

            // Input rows.
            TMP_InputField baseUrl = CreateInputRow(panel.transform, "BaseUrl", "Base URL",
                "http://127.0.0.1:1234/v1");
            TMP_InputField apiKey = CreateInputRow(panel.transform, "ApiKey", "API key (kept if empty)", "");
            apiKey.contentType = TMP_InputField.ContentType.Password;
            TMP_InputField model = CreateInputRow(panel.transform, "Model", "Model", "");

            // Buttons row.
            GameObject buttonsRow = CreateUiObject("Buttons", panel.transform);
            SetHeight(buttonsRow, 44);
            HorizontalLayoutGroup buttonsLayout = buttonsRow.AddComponent<HorizontalLayoutGroup>();
            buttonsLayout.spacing = 10;
            buttonsLayout.childControlWidth = true;
            buttonsLayout.childControlHeight = true;
            buttonsLayout.childForceExpandWidth = true;

            Button apply = CreateButton(buttonsRow.transform, "ApplyButton", "Apply");
            Button test = CreateButton(buttonsRow.transform, "TestButton", "Test connection");

            // Status label.
            TMP_Text status = CreateLabel(panel.transform, "Status", "", 16, FontStyles.Normal);
            status.color = new Color(0.75f, 0.8f, 0.85f, 1f);
            status.textWrappingMode = TextWrappingModes.Normal;
            SetHeight(status.gameObject, 64);

            // Wire the component.
            CoreAiBackendPanel panelComponent = panel.AddComponent<CoreAiBackendPanel>();
            panelComponent.Wire(dropdown, baseUrl, apiKey, model, apply, test, status);

            return canvasGo;
        }

        private static GameObject CreateUiObject(string name, Transform parent)
        {
            GameObject go = new(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            go.layer = LayerMask.NameToLayer("UI");
            return go;
        }

        private static TMP_Text CreateLabel(Transform parent, string name, string text, int size,
            FontStyles style)
        {
            GameObject go = CreateUiObject(name, parent);
            SetHeight(go, size + 12);
            TextMeshProUGUI label = go.AddComponent<TextMeshProUGUI>();
            label.text = text;
            label.fontSize = size;
            label.fontStyle = style;
            label.color = Color.white;
            return label;
        }

        private static TMP_Dropdown CreateDropdown(Transform parent, string name)
        {
            GameObject go = CreateUiObject(name, parent);
            SetHeight(go, 44);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.16f, 0.18f, 0.22f, 1f);

            TMP_Dropdown dropdown = go.AddComponent<TMP_Dropdown>();
            dropdown.targetGraphic = image;

            // Caption label.
            GameObject captionGo = CreateUiObject("Label", go.transform);
            StretchWithPadding(captionGo, 12, 30, 4, 4);
            TextMeshProUGUI caption = captionGo.AddComponent<TextMeshProUGUI>();
            caption.fontSize = 18;
            caption.color = Color.white;
            caption.alignment = TextAlignmentOptions.MidlineLeft;
            dropdown.captionText = caption;

            // Template (minimal working TMP_Dropdown template).
            GameObject template = CreateUiObject("Template", go.transform);
            RectTransform templateRt = template.GetComponent<RectTransform>();
            templateRt.anchorMin = new Vector2(0, 0);
            templateRt.anchorMax = new Vector2(1, 0);
            templateRt.pivot = new Vector2(0.5f, 1f);
            templateRt.sizeDelta = new Vector2(0, 180);
            Image templateBg = template.AddComponent<Image>();
            templateBg.color = new Color(0.13f, 0.15f, 0.18f, 1f);
            ScrollRect scroll = template.AddComponent<ScrollRect>();

            GameObject viewport = CreateUiObject("Viewport", template.transform);
            StretchFull(viewport);
            viewport.AddComponent<RectMask2D>();
            Image viewportImage = viewport.AddComponent<Image>();
            viewportImage.color = Color.clear;

            GameObject content = CreateUiObject("Content", viewport.transform);
            RectTransform contentRt = content.GetComponent<RectTransform>();
            contentRt.anchorMin = new Vector2(0, 1);
            contentRt.anchorMax = new Vector2(1, 1);
            contentRt.pivot = new Vector2(0.5f, 1f);
            contentRt.sizeDelta = new Vector2(0, 44);

            GameObject item = CreateUiObject("Item", content.transform);
            RectTransform itemRt = item.GetComponent<RectTransform>();
            itemRt.anchorMin = new Vector2(0, 0.5f);
            itemRt.anchorMax = new Vector2(1, 0.5f);
            itemRt.sizeDelta = new Vector2(0, 40);
            Toggle itemToggle = item.AddComponent<Toggle>();

            GameObject itemBg = CreateUiObject("Item Background", item.transform);
            StretchFull(itemBg);
            Image itemBgImage = itemBg.AddComponent<Image>();
            itemBgImage.color = new Color(0.2f, 0.24f, 0.3f, 1f);
            itemToggle.targetGraphic = itemBgImage;

            GameObject itemLabelGo = CreateUiObject("Item Label", item.transform);
            StretchWithPadding(itemLabelGo, 12, 12, 2, 2);
            TextMeshProUGUI itemLabel = itemLabelGo.AddComponent<TextMeshProUGUI>();
            itemLabel.fontSize = 18;
            itemLabel.color = Color.white;
            itemLabel.alignment = TextAlignmentOptions.MidlineLeft;

            scroll.content = contentRt;
            scroll.viewport = viewport.GetComponent<RectTransform>();
            scroll.horizontal = false;

            dropdown.template = templateRt;
            dropdown.itemText = itemLabel;
            template.SetActive(false);

            return dropdown;
        }

        private static TMP_InputField CreateInputRow(Transform parent, string name, string placeholder,
            string initial)
        {
            GameObject go = CreateUiObject(name, parent);
            SetHeight(go, 44);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.16f, 0.18f, 0.22f, 1f);

            TMP_InputField input = go.AddComponent<TMP_InputField>();
            input.targetGraphic = image;

            GameObject textArea = CreateUiObject("Text Area", go.transform);
            StretchWithPadding(textArea, 12, 12, 6, 6);
            textArea.AddComponent<RectMask2D>();

            GameObject placeholderGo = CreateUiObject("Placeholder", textArea.transform);
            StretchFull(placeholderGo);
            TextMeshProUGUI placeholderLabel = placeholderGo.AddComponent<TextMeshProUGUI>();
            placeholderLabel.text = placeholder;
            placeholderLabel.fontSize = 18;
            placeholderLabel.fontStyle = FontStyles.Italic;
            placeholderLabel.color = new Color(1f, 1f, 1f, 0.35f);
            placeholderLabel.alignment = TextAlignmentOptions.MidlineLeft;

            GameObject textGo = CreateUiObject("Text", textArea.transform);
            StretchFull(textGo);
            TextMeshProUGUI textLabel = textGo.AddComponent<TextMeshProUGUI>();
            textLabel.fontSize = 18;
            textLabel.color = Color.white;
            textLabel.alignment = TextAlignmentOptions.MidlineLeft;

            input.textViewport = textArea.GetComponent<RectTransform>();
            input.textComponent = textLabel;
            input.placeholder = placeholderLabel;
            input.text = initial;
            return input;
        }

        private static Button CreateButton(Transform parent, string name, string caption)
        {
            GameObject go = CreateUiObject(name, parent);
            Image image = go.AddComponent<Image>();
            image.color = new Color(0.2f, 0.45f, 0.85f, 1f);
            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;

            GameObject labelGo = CreateUiObject("Label", go.transform);
            StretchFull(labelGo);
            TextMeshProUGUI label = labelGo.AddComponent<TextMeshProUGUI>();
            label.text = caption;
            label.fontSize = 18;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.Center;
            return button;
        }

        private static void SetHeight(GameObject go, float height)
        {
            LayoutElement le = go.GetComponent<LayoutElement>() ?? go.AddComponent<LayoutElement>();
            le.minHeight = height;
            le.preferredHeight = height;
        }

        private static void StretchFull(GameObject go)
        {
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        private static void StretchWithPadding(GameObject go, float left, float right, float top,
            float bottom)
        {
            RectTransform rt = go.GetComponent<RectTransform>();
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = new Vector2(left, bottom);
            rt.offsetMax = new Vector2(-right, -top);
        }
    }
}
