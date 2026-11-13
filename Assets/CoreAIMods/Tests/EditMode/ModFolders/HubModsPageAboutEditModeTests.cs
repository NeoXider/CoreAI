#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.ChatMarkdown;
using CoreAI.ChatOverlay;
using CoreAI.Hub;
using CoreAI.Hub.UI;
using CoreAI.Mods.Folders;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode.ModFolders
{
    /// <summary>
    /// The Hub Mods page shows a mod's description again after the move to the folder tree: an About action on a
    /// row with a description opens a panel under the tree whose <see cref="CoreAiMarkdownView"/> renders it, as
    /// plain text when no renderer package is registered.
    /// </summary>
    [TestFixture]
    public sealed class HubModsPageAboutEditModeTests
    {
        private const string Description = "**Castle** builder\n\n- towers\n- walls";

        private ICoreAiMarkdownRenderer _previousRenderer;

        [SetUp]
        public void SetUp()
        {
            _previousRenderer = CoreAiMarkdownRendererRegistry.Current;
            CoreAiMarkdownRendererRegistry.Register(null);
        }

        [TearDown]
        public void TearDown()
        {
            CoreAiMarkdownRendererRegistry.Register(_previousRenderer);
        }

        private static HubModsPage Page(RecordsHubModService service)
        {
            HubModsPage page = new(service, 300, action => action(), null, null, new InMemoryHubModFolderPreferences());
            Assert.IsInstanceOf<VisualElement>(page.CreatePageContent());
            return page;
        }

        private static ModFolderModEntry Entry(HubModsPage page, string id)
        {
            Assert.IsTrue(page.Tree.TryFindMod(id, out int index), id);
            return page.Tree.GetMod(index);
        }

        [Test]
        public void About_ModWithADescription_ShowsItInTheMarkdownView_AsPlainTextWithoutARenderer()
        {
            HubModsPage page = Page(new RecordsHubModService(("castle", Description)));

            page.ToggleAbout("castle");

            Assert.AreEqual("castle", page.AboutModId);
            Assert.AreEqual(DisplayStyle.Flex, page.AboutPanel.style.display.value);
            Assert.AreEqual(Description, page.AboutView.Markdown);
            Assert.IsFalse(page.AboutView.IsRenderedAsMarkdown);
            Label plain = page.AboutView.Q<Label>(className: CoreAiMarkdownView.PlainUssClassName);
            Assert.IsNotNull(plain, "without a renderer the description is a plain label");
            Assert.AreEqual(ChatMarkdownText.ToPlainText(Description), plain.text);
        }

        [Test]
        public void About_WithARegisteredRenderer_RendersTheDescriptionAsMarkdown()
        {
            CountingRenderer renderer = new();
            CoreAiMarkdownRendererRegistry.Register(renderer);
            HubModsPage page = Page(new RecordsHubModService(("castle", Description)));

            page.ToggleAbout("castle");

            Assert.IsTrue(page.AboutView.IsRenderedAsMarkdown);
            Assert.AreEqual(1, renderer.Renders);
            StringAssert.Contains("Castle", renderer.LastMarkdown);
            Assert.IsNull(page.AboutView.Q<Label>(className: CoreAiMarkdownView.PlainUssClassName));
        }

        [Test]
        public void AboutAction_IsOfferedOnlyForAModWithADescription()
        {
            HubModsPage page = Page(new RecordsHubModService(("castle", Description), ("blank", "  ")));

            Assert.IsTrue(HubModsPage.IsModActionShown(Entry(page, "castle"), "About"));
            Assert.IsFalse(HubModsPage.IsModActionShown(Entry(page, "blank"), "About"));
            Assert.IsTrue(HubModsPage.IsModActionShown(Entry(page, "blank"), "Edit"));
            Assert.IsFalse(HubModsPage.IsModActionShown(Entry(page, "blank"), "Apply update"));
        }

        [Test]
        public void About_ModWithoutADescription_KeepsThePanelClosed_TheTwin()
        {
            HubModsPage page = Page(new RecordsHubModService(("blank", "")));

            page.ToggleAbout("blank");

            Assert.IsNull(page.AboutModId);
            Assert.AreEqual(DisplayStyle.None, page.AboutPanel.style.display.value);
        }

        [Test]
        public void About_SameModAgain_ClosesThePanel()
        {
            HubModsPage page = Page(new RecordsHubModService(("castle", Description)));

            page.ToggleAbout("castle");
            page.ToggleAbout("castle");

            Assert.IsNull(page.AboutModId);
            Assert.AreEqual(DisplayStyle.None, page.AboutPanel.style.display.value);
        }

        [Test]
        public void About_FollowsARefresh_NewTextIsShown_AndADeletedModClosesIt()
        {
            RecordsHubModService service = new(("castle", Description));
            HubModsPage page = Page(service);
            page.ToggleAbout("castle");

            service.Records[0].Description = "A bigger castle.";
            service.RaiseModsChanged();

            Assert.AreEqual("A bigger castle.", page.AboutView.Markdown);

            service.Records.Clear();
            service.RaiseModsChanged();

            Assert.IsNull(page.AboutModId);
            Assert.AreEqual(DisplayStyle.None, page.AboutPanel.style.display.value);
        }

        /// <summary>
        /// Pressing the "Mods" sub-tab leaves a mod's views: from the editor and from the About panel, also when the
        /// player came back from the Logs sub-tab, the list is shown again.
        /// </summary>
        [Test]
        public void ModsSubTab_Pressed_LeavesTheEditorAndTheAboutPanel_ForTheList()
        {
            RecordsHubModService service = new(("castle", Description));
            HubModsPage mods = new(service, 300, action => action(), null, null, new InMemoryHubModFolderPreferences());
            HubSubTabPage page = new("coreai.hub.mods", "Mods", 300, mods, new OtherPage());
            VisualElement view = (VisualElement)page.CreatePageContent();
            Button modsTab = view.Q<Button>("coreai-hub-subtab-0");
            Button logsTab = view.Q<Button>("coreai-hub-subtab-1");
            Assert.IsNotNull(modsTab);
            Assert.IsNotNull(logsTab);
            VisualElement list = view.Q<VisualElement>("coreai-hub-mods-list");

            mods.ToggleAbout("castle");
            mods.OpenEditEditorForTests("castle");
            Assert.IsTrue(mods.IsEditorOpen, "precondition: the editor replaces the list");
            Assert.AreEqual("castle", mods.AboutModId, "precondition: the About panel is open");
            Assert.AreEqual(DisplayStyle.None, list.style.display.value);

            InvokeButton(modsTab);

            Assert.IsFalse(mods.IsEditorOpen, "the Mods sub-tab goes back to the list");
            Assert.AreEqual(DisplayStyle.Flex, list.style.display.value);
            Assert.IsNull(mods.AboutModId, "and closes the About panel");

            mods.ToggleAbout("castle");
            InvokeButton(logsTab);
            InvokeButton(modsTab);
            Assert.IsNull(mods.AboutModId, "coming back from Logs shows the list too");

            page.OnDestroyed();
        }

        /// <summary>
        /// Negative twin: a new mod's draft that was never saved keeps the editor open with its discard choice instead
        /// of being lost, as the editor's own Back does.
        /// </summary>
        [Test]
        public void ModsSubTab_Pressed_WithAnUnsavedDraft_KeepsTheEditorAndOffersToDiscard()
        {
            RecordsHubModService service = new(("castle", Description));
            HubModsPage mods = new(service, 300, action => action(), null, null, new InMemoryHubModFolderPreferences());
            HubSubTabPage page = new("coreai.hub.mods", "Mods", 300, mods);
            VisualElement view = (VisualElement)page.CreatePageContent();
            mods.OpenNewEditorForTests();

            InvokeButton(view.Q<Button>("coreai-hub-subtab-0"));

            Assert.IsTrue(mods.IsEditorOpen, "the draft is kept");
            Assert.AreEqual(DisplayStyle.Flex, view.Q<Button>("coreai-hub-mod-discard").style.display.value);
            page.OnDestroyed();
        }

        /// <summary>A second sub-tab that stands in for the Logs page.</summary>
        private sealed class OtherPage : IHubPage
        {
            public string PageId => "other";

            public string DisplayName => "Logs";

            public int Order => 350;

            public Func<object> CreatePageContent => () => new Label("logs");

            public void OnActivated()
            {
            }

            public void OnDeactivated()
            {
            }

            public void OnDestroyed()
            {
            }
        }

        private static void InvokeButton(Button button)
        {
            System.Reflection.MethodInfo invoke = typeof(Clickable).GetMethod(
                "Invoke",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.NonPublic,
                null,
                new[] { typeof(EventBase) },
                null);
            Assert.IsNotNull(invoke, "Unity UI Toolkit Clickable.Invoke(EventBase) must be available.");
            invoke.Invoke(button.clickable, new object[] { null });
        }

        private sealed class CountingRenderer : ICoreAiMarkdownRenderer
        {
            public int Renders { get; private set; }

            public string LastMarkdown { get; private set; } = "";

            public StyleSheet StyleSheet => null;

            public VisualElement Render(string markdown, Action<string> linkActivated)
            {
                Renders++;
                LastMarkdown = markdown ?? "";
                VisualElement element = new();
                element.Add(new Label(markdown));
                return element;
            }
        }

        private sealed class RecordsHubModService : IHubModService
        {
            public RecordsHubModService(params (string Id, string Description)[] mods)
            {
                foreach ((string id, string description) in mods)
                {
                    Records.Add(new HubModRecord
                    {
                        Id = id, Name = id, Category = "Samples", Description = description, IsStored = true
                    });
                }
            }

            public List<HubModRecord> Records { get; } = new();

            public bool IsSupported => true;

            public string NewModTemplate => HubModTemplates.Legacy;

            public event Action ModsChanged;

            public event Action LogsChanged;

            public void RaiseModsChanged()
            {
                ModsChanged?.Invoke();
                LogsChanged?.Invoke();
            }

            public IReadOnlyList<HubModRecord> ListMods()
            {
                return new List<HubModRecord>(Records);
            }

            public bool TryGetSource(string id, out string source)
            {
                source = Records.Exists(record => record.Id == id) ? "return true" : null;
                return source != null;
            }

            public bool IsLoaded(string id)
            {
                return false;
            }

            public void SaveOrReload(string id, string code)
            {
            }

            public HubModSaveResult SaveOrReload(string id, string code, ModReloadMode mode)
            {
                return new HubModSaveResult(id, false, mode, null);
            }

            public void Enable(string id)
            {
            }

            public void Disable(string id)
            {
            }

            public bool Delete(string id)
            {
                return false;
            }

            public IReadOnlyList<LuaScriptRevision> ListModVersions(string id)
            {
                return Array.Empty<LuaScriptRevision>();
            }

            public bool TryRevertMod(string id, int revisionIndex, out string restoredSource)
            {
                restoredSource = null;
                return false;
            }

            public string ExportMod(string id)
            {
                return null;
            }

            public bool ImportMod(string bundleJson)
            {
                return false;
            }

            public bool ApplyBundledUpdate(string id)
            {
                return false;
            }

            public string RecentErrors(string id)
            {
                return "";
            }

            public IReadOnlyList<LuaModHandlerError> RecentErrorEntries(string modId = null)
            {
                return Array.Empty<LuaModHandlerError>();
            }

            public IReadOnlyList<LuaModReport> RecentReports(string modId = null)
            {
                return Array.Empty<LuaModReport>();
            }

            public void ClearReports()
            {
            }

            public void ClearErrors()
            {
            }

            public bool GetReportLoggingEnabled(string modId)
            {
                return false;
            }

            public bool SetReportLoggingEnabled(string modId, bool enabled)
            {
                return false;
            }
        }
    }
}
#endif
