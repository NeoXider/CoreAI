using System;
using System.Reflection;
using CoreAI.Chat;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Panel state versus the visual tree it is drawn into.
    /// <para>
    /// A <c>PanelRenderer</c> host rebuilds the UI tree whenever it reloads: the panel drops every
    /// element reference and re-resolves the whole tree from freshly created elements. Those elements
    /// carry only what the UXML declares, so anything the panel had toggled on the OLD tree - the
    /// collapsed class above all - simply vanished. <c>IsCollapsed</c> kept saying "collapsed" while a
    /// fully expanded chat, complete with history, was on screen; whether it happened at all depended
    /// on whether the rebuild landed before or after <c>SetCollapsed</c>.
    /// </para>
    /// <para>
    /// Every assertion here therefore reads the CURRENT tree, never the field: the field was already
    /// correct while the bug was live.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatPanelStateRebindEditModeTests
    {
        private const string CollapsedClass = "coreai-collapsed";

        [Test]
        public void CollapsedPanel_AfterTreeRebuild_StaysCollapsedOnScreen()
        {
            using PanelCtx ctx = NewPanel();
            BindFreshTree(ctx);
            ctx.Panel.SetCollapsed(true, false);
            Assert.IsTrue(
                CurrentContainer(ctx.Panel).ClassListContains(CollapsedClass),
                "Sanity: collapsing must mark the container that is on screen right now.");

            VisualElement rebuilt = BindFreshTree(ctx);

            Assert.IsTrue(
                rebuilt.Q<VisualElement>("coreai-chat-root").ClassListContains(CollapsedClass),
                "After a PanelRenderer rebuild the panel is still collapsed, so the container the player " +
                "actually sees must carry the collapsed class - otherwise a chat that is supposed to be " +
                "hidden renders on top of whatever replaced it.");
            Assert.AreSame(
                rebuilt.Q<VisualElement>("coreai-chat-root"), CurrentContainer(ctx.Panel),
                "The panel must be bound to the rebuilt tree, not still holding the discarded one.");
        }

        [Test]
        public void CollapsedPanel_AfterTreeRebuild_ShowsTheFloatingActionButton()
        {
            using PanelCtx ctx = NewPanel();
            BindFreshTree(ctx);
            ctx.Panel.SetCollapsed(true, false);

            VisualElement rebuilt = BindFreshTree(ctx);

            Assert.AreEqual(
                DisplayStyle.Flex,
                rebuilt.Q<Button>("coreai-chat-fab").resolvedStyle.display,
                "The collapsed panel is reachable only through the FAB; a rebuild that leaves it hidden " +
                "locks the player out of the chat entirely.");
        }

        [Test]
        public void ExpandedPanel_AfterTreeRebuild_StaysExpanded()
        {
            using PanelCtx ctx = NewPanel();
            BindFreshTree(ctx);
            ctx.Panel.SetCollapsed(true, false);
            ctx.Panel.SetCollapsed(false, false);

            VisualElement rebuilt = BindFreshTree(ctx);

            Assert.IsFalse(
                rebuilt.Q<VisualElement>("coreai-chat-root").ClassListContains(CollapsedClass),
                "Re-applying state must apply the CURRENT state, not the last one that happened to be set.");
            Assert.AreEqual(
                DisplayStyle.None,
                rebuilt.Q<Button>("coreai-chat-fab").resolvedStyle.display,
                "An expanded panel must not show the collapsed-mode FAB after a rebuild.");
        }

        [Test]
        public void TypingIndicator_AfterTreeRebuildMidTurn_IsStillVisible()
        {
            using PanelCtx ctx = NewPanel();
            BindFreshTree(ctx);
            ctx.Panel.ShowTypingIndicator();

            VisualElement rebuilt = BindFreshTree(ctx);

            Assert.AreEqual(
                DisplayStyle.Flex,
                rebuilt.Q<VisualElement>("coreai-typing-indicator").resolvedStyle.display,
                "The request keeps running across a UI rebuild, so the 'assistant is answering' row must " +
                "come back with the new tree instead of leaving a silent, frozen-looking chat.");
        }

        [Test]
        public void IdleTypingIndicator_AfterTreeRebuild_StaysHidden()
        {
            using PanelCtx ctx = NewPanel();
            BindFreshTree(ctx);
            ctx.Panel.ShowTypingIndicator();
            ctx.Panel.HideTypingIndicator();

            VisualElement rebuilt = BindFreshTree(ctx);

            Assert.AreEqual(
                DisplayStyle.None,
                rebuilt.Q<VisualElement>("coreai-typing-indicator").resolvedStyle.display,
                "A finished turn must not resurrect the typing row on the next rebuild.");
        }

        // ==================== Helpers ====================

        /// <summary>
        /// Replays what a <c>PanelRenderer</c> UI reload does around the bind: drop every element
        /// reference, then bind the panel to a brand-new tree (same call order as InitializeUiRoot).
        /// Returns the new root so assertions can read the tree the player would be looking at.
        /// </summary>
        private static VisualElement BindFreshTree(PanelCtx ctx)
        {
            InvokePrivate(ctx.Panel, "UnbindUiCallbacks", true);
            InvokePrivate(ctx.Panel, "ResetUiReferences");

            VisualElement root = BuildChatTree();
            ctx.Document.rootVisualElement.Clear();
            ctx.Document.rootVisualElement.Add(root);
            SetPrivateField(ctx.Panel, "Root", root);
            InvokePrivate(ctx.Panel, "BindUI");
            return root;
        }

        /// <summary>Minimal stand-in for CoreAiChat.uxml: the elements BindUI resolves by name.</summary>
        private static VisualElement BuildChatTree()
        {
            VisualElement root = new() { name = "coreai-chat-host" };
            VisualElement container = new() { name = "coreai-chat-root" };
            container.Add(new ScrollView { name = "coreai-chat-scroll" });
            container.Add(new TextField { name = "coreai-chat-input" });
            container.Add(new Button { name = "coreai-chat-send" });
            container.Add(new Button { name = "coreai-chat-clear" });
            container.Add(new Button { name = "coreai-chat-collapse" });

            VisualElement typing = new() { name = "coreai-typing-indicator" };
            typing.Add(new VisualElement { name = "coreai-typing-avatar" });
            typing.Add(new Label { name = "coreai-typing-label" });
            container.Add(typing);

            root.Add(container);

            // WHY: the authored chat tree starts EXPANDED - no collapsed class, FAB hidden. A freshly
            // built tree must therefore be flipped into collapsed mode by the panel, never inherit it.
            Button fab = new() { name = "coreai-chat-fab" };
            fab.style.display = DisplayStyle.None;
            root.Add(fab);
            return root;
        }

        private static VisualElement CurrentContainer(CoreAiChatPanel panel)
        {
            return GetPrivateField<VisualElement>(panel, "ChatContainer");
        }

        private static PanelCtx NewPanel()
        {
            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject panelHost = new("CoreAiChatPanel_StateRebind_PanelHost_Test");
            panelHost.SetActive(false);
            UIDocument document = panelHost.AddComponent<UIDocument>();
            document.panelSettings = panelSettings;
            panelHost.SetActive(true);

            GameObject go = new("CoreAiChatPanel_StateRebind_Test");
            CoreAiChatPanel panel = go.AddComponent<CoreAiChatPanel>();
            panel.SetRuntimeOptions(new CoreAiChatOptions { RoleId = "SmartChat" });

            return new PanelCtx(go, panelHost, panelSettings, document, panel);
        }

        private static object InvokePrivate(object target, string method, params object[] args)
        {
            MethodInfo info = target.GetType().GetMethod(
                method, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.IsNotNull(info, $"Method '{method}' was not found on {target.GetType().Name}.");
            return info.Invoke(target, args);
        }

        private static T GetPrivateField<T>(object target, string field)
        {
            FieldInfo info = FindField(target.GetType(), field);
            Assert.IsNotNull(info, $"Field '{field}' was not found on {target.GetType().Name}.");
            return (T)info.GetValue(target);
        }

        private static void SetPrivateField(object target, string field, object value)
        {
            FieldInfo info = FindField(target.GetType(), field);
            Assert.IsNotNull(info, $"Field '{field}' was not found on {target.GetType().Name}.");
            info.SetValue(target, value);
        }

        private static FieldInfo FindField(Type type, string field)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo info = current.GetField(
                    field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (info != null)
                {
                    return info;
                }
            }

            return null;
        }

        private sealed class PanelCtx : IDisposable
        {
            private readonly GameObject _panelGo;
            private readonly GameObject _hostGo;
            private readonly PanelSettings _panelSettings;

            public PanelCtx(
                GameObject panelGo,
                GameObject hostGo,
                PanelSettings panelSettings,
                UIDocument document,
                CoreAiChatPanel panel)
            {
                _panelGo = panelGo;
                _hostGo = hostGo;
                _panelSettings = panelSettings;
                Document = document;
                Panel = panel;
            }

            public UIDocument Document { get; }

            public CoreAiChatPanel Panel { get; }

            public void Dispose()
            {
                UnityEngine.Object.DestroyImmediate(_panelGo);
                UnityEngine.Object.DestroyImmediate(_hostGo);
                UnityEngine.Object.DestroyImmediate(_panelSettings);
            }
        }
    }
}
