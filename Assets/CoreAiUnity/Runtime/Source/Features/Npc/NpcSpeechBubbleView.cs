using System;
using System.Collections.Generic;
using CoreAI.ChatOverlay;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Ai.Npc
{
    /// <summary>
    /// UI Toolkit view of the speech bubbles over NPC heads. It draws an <see cref="NpcSpeechBubbleBoard"/>: each
    /// visible bubble is an element placed over its NPC's anchor (projected through the camera every frame), and it
    /// grows with the text because its size follows its content up to a maximum width. A bubble whose NPC is behind
    /// the camera, farther than the limit or without an anchor is hidden. No IMGUI, no per-frame tree queries.
    /// </summary>
    public sealed class NpcSpeechBubbleView : IDisposable
    {
        /// <summary>Class of the full-screen container.</summary>
        public const string RootClass = "coreai-npc-bubbles";

        /// <summary>Class of one bubble.</summary>
        public const string BubbleClass = "coreai-npc-bubble";

        /// <summary>Class of the speaker name label.</summary>
        public const string NameClass = "coreai-npc-bubble__name";

        /// <summary>Class of the text label.</summary>
        public const string TextClass = "coreai-npc-bubble__text";

        /// <summary>Added while the NPC thinks.</summary>
        public const string ThinkingClass = "coreai-npc-bubble--thinking";

        /// <summary>Added while the bubble shows a fallback line.</summary>
        public const string FallbackClass = "coreai-npc-bubble--fallback";

        private readonly NpcSpeechBubbleBoard _board;
        private readonly INpcSpeechAnchors _anchors;
        private readonly Dictionary<string, BubbleElement> _elements = new(StringComparer.Ordinal);
        private readonly float _maxDistance;
        private VisualElement _host;

        /// <summary>Creates the view; attach it to a host element with <see cref="Attach"/>.</summary>
        public NpcSpeechBubbleView(NpcSpeechBubbleBoard board, INpcSpeechAnchors anchors, float maxDistance = 40f)
        {
            _board = board ?? throw new ArgumentNullException(nameof(board));
            _anchors = anchors ?? new NpcSpeechAnchorRegistry();
            _maxDistance = maxDistance;
            Root = new VisualElement { name = "CoreAiNpcBubbles", pickingMode = PickingMode.Ignore };
            Root.AddToClassList(RootClass);
            Root.style.position = Position.Absolute;
            Root.style.left = 0f;
            Root.style.top = 0f;
            Root.style.right = 0f;
            Root.style.bottom = 0f;
        }

        /// <summary>The container all bubbles live in.</summary>
        public VisualElement Root { get; }

        /// <summary>Bubbles drawn on the last <see cref="Render"/>.</summary>
        public int VisibleCount { get; private set; }

        /// <summary>Adds the container to <paramref name="host"/> (the bubble document's root).</summary>
        public void Attach(VisualElement host)
        {
            Detach();
            _host = host;
            _host?.Add(Root);
        }

        /// <summary>Removes the container from its host.</summary>
        public void Detach()
        {
            if (_host != null)
            {
                Root.RemoveFromHierarchy();
                _host = null;
            }
        }

        /// <summary>The element of <paramref name="npcId"/>'s bubble, once its NPC has spoken.</summary>
        public bool TryGetElement(string npcId, out VisualElement element)
        {
            if (_elements.TryGetValue(npcId ?? "", out BubbleElement bubble))
            {
                element = bubble.Container;
                return true;
            }

            element = null;
            return false;
        }

        /// <summary>
        /// Syncs every bubble with the board and places it over its anchor as seen by <paramref name="camera"/>.
        /// Call once per frame after the board ticked; a null camera hides every bubble.
        /// </summary>
        public void Render(Camera camera)
        {
            int visible = 0;
            IReadOnlyList<NpcSpeechBubble> bubbles = _board.Bubbles;
            for (int index = 0; index < bubbles.Count; index++)
            {
                NpcSpeechBubble bubble = bubbles[index];
                BubbleElement element = GetOrCreate(bubble);
                element.Sync(bubble);
                bool shown = bubble.IsVisible && TryPlace(element, bubble.NpcId, camera);
                element.SetShown(shown);
                if (shown)
                {
                    visible++;
                }
            }

            VisibleCount = visible;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            Detach();
            Root.Clear();
            _elements.Clear();
        }

        private bool TryPlace(BubbleElement element, string npcId, Camera camera)
        {
            if (camera == null || !_anchors.TryGetAnchor(npcId, out NpcPosition anchor))
            {
                return false;
            }

            Vector3 world = new((float)anchor.X, (float)anchor.Y, (float)anchor.Z);
            Vector3 viewport = camera.WorldToViewportPoint(world);
            if (viewport.z <= 0f || (_maxDistance > 0f && viewport.z > _maxDistance))
            {
                return false;
            }

            IPanel panel = Root.panel;
            if (panel == null)
            {
                return true;
            }

            Vector2 position = RuntimePanelUtils.CameraTransformWorldToPanel(panel, world, camera);
            element.MoveTo(position);
            return true;
        }

        private BubbleElement GetOrCreate(NpcSpeechBubble bubble)
        {
            if (!_elements.TryGetValue(bubble.NpcId, out BubbleElement element))
            {
                element = new BubbleElement();
                _elements.Add(bubble.NpcId, element);
                Root.Add(element.Container);
            }

            return element;
        }

        private sealed class BubbleElement
        {
            private readonly Label _name;
            private readonly Label _text;
            private int _version = -1;
            private bool _shown = true;
            private Vector2 _position = new(float.NaN, float.NaN);

            public BubbleElement()
            {
                Container = new VisualElement { pickingMode = PickingMode.Ignore };
                Container.AddToClassList(BubbleClass);
                IStyle style = Container.style;
                style.position = Position.Absolute;
                style.maxWidth = 360f;
                style.paddingLeft = 12f;
                style.paddingRight = 12f;
                style.paddingTop = 6f;
                style.paddingBottom = 8f;
                style.backgroundColor = new Color(1f, 1f, 1f, 0.92f);
                style.borderTopLeftRadius = 10f;
                style.borderTopRightRadius = 10f;
                style.borderBottomLeftRadius = 10f;
                style.borderBottomRightRadius = 10f;
                style.translate = new Translate(Length.Percent(-50f), Length.Percent(-100f));
                _name = new Label { pickingMode = PickingMode.Ignore };
                _name.AddToClassList(NameClass);
                _name.style.unityFontStyleAndWeight = FontStyle.Bold;
                _name.style.fontSize = 13f;
                _name.style.color = new Color(0.25f, 0.32f, 0.45f, 1f);
                _text = new Label { pickingMode = PickingMode.Ignore };
                _text.AddToClassList(TextClass);
                _text.style.fontSize = 17f;
                _text.style.whiteSpace = WhiteSpace.Normal;
                _text.style.color = new Color(0.08f, 0.08f, 0.1f, 1f);
                Container.Add(_name);
                Container.Add(_text);
                SetShown(false);
            }

            public VisualElement Container { get; }

            public void Sync(NpcSpeechBubble bubble)
            {
                if (bubble.Version == _version)
                {
                    return;
                }

                _version = bubble.Version;
                _name.text = bubble.NpcName;
                _text.text = bubble.Text;
                Container.EnableInClassList(ThinkingClass, bubble.IsThinking);
                Container.EnableInClassList(FallbackClass, bubble.IsFallback);
            }

            public void SetShown(bool shown)
            {
                if (_shown == shown)
                {
                    return;
                }

                _shown = shown;
                Container.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
            }

            public void MoveTo(Vector2 position)
            {
                if ((position - _position).sqrMagnitude < 0.25f)
                {
                    return;
                }

                _position = position;
                Container.style.left = position.x;
                Container.style.top = position.y;
            }
        }
    }

    /// <summary>
    /// The bubbles' own UI Toolkit document: a GameObject with a <see cref="UIDocument"/> on the assigned panel or on
    /// one created at runtime (scale with screen size). Works in a built player and on WebGL; Dispose destroys what
    /// it created.
    /// </summary>
    public sealed class NpcSpeechBubbleSurface : IDisposable
    {
        /// <summary>Name of the GameObject that holds the bubble document.</summary>
        public const string GameObjectName = "CoreAI NPC Speech";

        private GameObject _gameObject;
        private UIDocument _document;
        private PanelSettings _ownedPanel;

        private NpcSpeechBubbleSurface()
        {
        }

        /// <summary>The document's root element; null once disposed.</summary>
        public VisualElement Root => _document != null ? _document.rootVisualElement : null;

        /// <summary>The panel the bubbles are drawn on: the settings' own, or one made for them.</summary>
        public PanelSettings Panel => _document != null ? _document.panelSettings : null;

        /// <summary>Creates the document described by <paramref name="settings"/>.</summary>
        public static NpcSpeechBubbleSurface Create(CoreAiNpcSettings settings)
        {
            CoreAiNpcSettings resolved = settings ?? new CoreAiNpcSettings();
            NpcSpeechBubbleSurface surface = new();
            PanelSettings panel = resolved.PanelSettings;
            if (panel == null)
            {
                panel = ScriptableObject.CreateInstance<PanelSettings>();
                panel.name = "CoreAI NPC Speech Panel";
                panel.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                panel.referenceResolution = new Vector2Int(1920, 1080);
                panel.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
                panel.match = 0.5f;
                panel.sortingOrder = resolved.SortingOrder;
                // WHY a theme: a panel without one logs "No Theme Style Sheet set" and lacks the default text
                // settings; the bubbles take the runtime theme the chat overlay and the runtime UI use.
                ThemeStyleSheet theme = Resources.Load<ThemeStyleSheet>(CoreAiChatOverlaySettings.DefaultThemeResourcePath);
                if (theme != null)
                {
                    panel.themeStyleSheet = theme;
                }

                surface._ownedPanel = panel;
            }

            GameObject gameObject = new(GameObjectName);
            gameObject.SetActive(false);
            UIDocument document = gameObject.AddComponent<UIDocument>();
            document.panelSettings = panel;
            if (Application.isPlaying)
            {
                UnityEngine.Object.DontDestroyOnLoad(gameObject);
            }

            gameObject.SetActive(true);
            surface._gameObject = gameObject;
            surface._document = document;
            return surface;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            GameObject gameObject = _gameObject;
            PanelSettings owned = _ownedPanel;
            _gameObject = null;
            _document = null;
            _ownedPanel = null;
            Destroy(gameObject);
            Destroy(owned);
        }

        private static void Destroy(UnityEngine.Object target)
        {
            if (target == null)
            {
                return;
            }

            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(target);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(target);
            }
        }
    }
}
