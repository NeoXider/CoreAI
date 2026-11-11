using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The player-facing backend settings: Test connection on the Hub's AI Settings page (tests the typed server,
    /// switches to it, remembers it without the key), the remembered server at the next start (applied, or offline
    /// with the key asked for again), the file it is kept in, the offline Programmer line that names this scene's
    /// key, and the Hub window holding the keyboard while it is open. Each with its twin; the HTTP is scripted.
    /// </summary>
    public sealed class PlayerBackendSettingsEditModeTests
    {
        private const string Server = "http://127.0.0.1:1234/v1";

        private CoreAISettingsAsset _settings;
        private InMemoryLlmRememberedBackendStore _store;
        private ScriptedHttp _http;
        private IDisposable _storage;
        private string _storageRoot;
        private readonly List<HubSettingsPage> _pages = new();

        private sealed class ScriptedHttp : ILlmConnectionHttp
        {
            public int ModelsStatus = 200;
            public string ModelsBody = "{\"data\":[{\"id\":\"m\"}]}";
            public int ChatStatus = 200;
            public string ChatBody =
                "{\"model\":\"m\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"ok\"}}]}";

            public readonly List<LlmConnectionHttpRequest> Sent = new();

            public Task<LlmConnectionHttpResponse> SendAsync(LlmConnectionHttpRequest request,
                CancellationToken cancellationToken)
            {
                Sent.Add(request);
                bool models = request.Url.EndsWith("/models", StringComparison.Ordinal);
                return Task.FromResult(new LlmConnectionHttpResponse
                {
                    StatusCode = models ? ModelsStatus : ChatStatus,
                    Body = models ? ModelsBody : ChatBody
                });
            }
        }

        [SetUp]
        public void SetUp()
        {
            CoreAiBackend.ResetStaticState();
            CoreAISettingsAsset.BeginNewSession();
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureOffline();
            CoreAISettingsAsset.SetInstance(_settings);
            _store = new InMemoryLlmRememberedBackendStore();
            _http = new ScriptedHttp();
            CoreAiBackend.RememberedBackendStore = _store;
            CoreAiBackend.ConnectionHttp = _http;
            _storageRoot = Path.Combine(Path.GetTempPath(), "cai-hubbw", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_storageRoot);
            _storage = CoreAiStorageRoot.Override(_storageRoot);
        }

        [TearDown]
        public void TearDown()
        {
            foreach (HubSettingsPage page in _pages)
            {
                page.OnDestroyed();
            }

            _pages.Clear();
            _storage?.Dispose();
            if (Directory.Exists(_storageRoot))
            {
                Directory.Delete(_storageRoot, true);
            }

            CoreAiBackend.ResetStaticState();
            CoreAISettingsAsset.ResetInstance();
            CoreAISettingsAsset.BeginNewSession();
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        // ---- connect: test, use, remember -----------------------------------------------------------------------

        [Test]
        public void Connect_AServerThatAnswers_IsUsedAtOnce_AndRememberedWithoutTheKey()
        {
            CoreAiConnectResult result = CoreAiBackend.ConnectHttpApiAsync(Server + "/", "sk-session", "m")
                .GetAwaiter().GetResult();

            Assert.IsTrue(result.Ok, result.Check.Message);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode);
            Assert.AreEqual(Server, _settings.ApiBaseUrl);
            Assert.AreEqual("m", _settings.ModelName);
            Assert.AreEqual("sk-session", _settings.ApiKey, "the key lives in the session override");
            Assert.IsTrue(result.Remembered);
            Assert.IsTrue(_store.TryLoad(out LlmRememberedBackend remembered));
            Assert.AreEqual(Server, remembered.BaseUrl);
            Assert.IsTrue(remembered.NeedsKey);
            StringAssert.DoesNotContain("sk-session", _store.Json, "the key never reaches the device");
        }

        [Test]
        public void Connect_AServerThatFails_ChangesNothing_AndRemembersNothing()
        {
            _http.ChatStatus = 404;
            _http.ChatBody = "{\"error\":{\"message\":\"model 'm' not found\"}}";

            CoreAiConnectResult result = CoreAiBackend.ConnectHttpApiAsync(Server, "", "m").GetAwaiter().GetResult();

            Assert.IsFalse(result.Ok);
            Assert.AreEqual(LlmConnectionProblem.ModelNotFound, result.Check.Problem);
            Assert.AreEqual(LlmExecutionMode.Offline, _settings.ExecutionMode);
            Assert.IsFalse(_store.TryLoad(out _));
        }

        [Test]
        public void Connect_WhenThePageCannotKeepSettings_SaysSo_AndStillUsesTheServer()
        {
            _store.Durable = false;

            CoreAiConnectResult result = CoreAiBackend.ConnectHttpApiAsync(Server, "", "m").GetAwaiter().GetResult();

            Assert.IsTrue(result.Ok);
            Assert.IsFalse(result.Remembered);
            StringAssert.Contains("storage is off", result.RememberProblem);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode);
        }

        // ---- the next start -------------------------------------------------------------------------------------

        [Test]
        public void Restore_AServerWithoutAKey_IsUsed_WithoutAClick()
        {
            _store.Save(new LlmRememberedBackend { BaseUrl = Server, Model = "m", NeedsKey = false });

            CoreAiRememberedBackendRestore outcome = CoreAiBackend.RestoreRememberedBackend();

            Assert.AreEqual(CoreAiRememberedBackendRestore.Applied, outcome);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode);
            Assert.AreEqual(Server, _settings.ApiBaseUrl);
            Assert.AreEqual("m", _settings.ModelName);
            Assert.AreEqual("", _settings.ApiKey);
            Assert.IsNull(_settings.PendingKeyBackend);
        }

        [Test]
        public void Restore_AServerThatNeedsAKey_StaysOffline_AndAsksForTheKey()
        {
            _store.Save(new LlmRememberedBackend { BaseUrl = Server, Model = "m", NeedsKey = true });

            CoreAiRememberedBackendRestore outcome = CoreAiBackend.RestoreRememberedBackend();

            Assert.AreEqual(CoreAiRememberedBackendRestore.NeedsKey, outcome);
            Assert.AreEqual(LlmExecutionMode.Offline, _settings.ExecutionMode,
                "every request would fail with 401; the offline line says what is missing instead");
            Assert.IsNotNull(_settings.PendingKeyBackend);
            Assert.AreEqual(Server, _settings.PendingKeyBackend.BaseUrl);
        }

        [Test]
        public void Restore_NothingRemembered_ChangesNothing()
        {
            CoreAiRememberedBackendRestore outcome = CoreAiBackend.RestoreRememberedBackend();

            Assert.AreEqual(CoreAiRememberedBackendRestore.NothingRemembered, outcome);
            Assert.IsFalse(_settings.HasSessionOverrides);
        }

        [Test]
        public void Forget_ClearsTheRememberedServer_AndTheKeyRequest()
        {
            _store.Save(new LlmRememberedBackend { BaseUrl = Server, Model = "m", NeedsKey = true });
            CoreAiBackend.RestoreRememberedBackend();

            CoreAiBackend.ForgetRememberedBackend();

            Assert.IsFalse(_store.TryLoad(out _));
            Assert.IsNull(_settings.PendingKeyBackend);
        }

        // ---- the file it is kept in -----------------------------------------------------------------------------

        [Test]
        public void TheFileStore_KeepsTheServerUnderTheStorageRoot_WithoutAKey()
        {
            FileLlmRememberedBackendStore store = new();

            Assert.IsFalse(store.TryLoad(out _));
            Assert.IsTrue(store.Save(new LlmRememberedBackend { BaseUrl = Server, Model = "m", NeedsKey = true }));

            StringAssert.StartsWith(Path.GetFullPath(_storageRoot), store.StoragePath);
            Assert.IsTrue(store.TryLoad(out LlmRememberedBackend loaded));
            Assert.AreEqual("m", loaded.Model);
            StringAssert.DoesNotContain("apiKey", File.ReadAllText(store.StoragePath));

            store.Clear();
            Assert.IsFalse(File.Exists(store.StoragePath));
        }

        [Test]
        public void TheFileStore_IgnoresABrokenFile()
        {
            FileLlmRememberedBackendStore store = new();
            Directory.CreateDirectory(Path.GetDirectoryName(store.StoragePath));
            File.WriteAllText(store.StoragePath, "{ broken");

            Assert.IsFalse(store.TryLoad(out LlmRememberedBackend backend));
            Assert.IsNull(backend);
        }

        // ---- the offline line -----------------------------------------------------------------------------------

        [Test]
        public void TheOfflineLine_NamesTheScenesKey_WhenTheSceneSetsAHint()
        {
            _settings.SetSessionModelSetupHint("Press F2 to open the AI settings, then ask again.");
            OfflineLlmClient client = new(_settings);

            LlmCompletionResult result = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.Programmer, UserPayload = "build a tower"
            }).GetAwaiter().GetResult();

            Assert.AreEqual(OfflineLlmClient.OfflineProgrammerReplyStart + " Press F2 to open the AI settings, then ask again.",
                result.Content);
        }

        [Test]
        public void TheOfflineLine_WithoutAHint_DoesNotSendThePlayerToAHubTheSceneMayNotHave()
        {
            OfflineLlmClient client = new(_settings);

            LlmCompletionResult result = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.Programmer, UserPayload = "build a tower"
            }).GetAwaiter().GetResult();

            Assert.AreEqual(OfflineLlmClient.OfflineProgrammerReply, result.Content);
            StringAssert.DoesNotContain("Hub", result.Content);
            StringAssert.Contains("model", result.Content);
        }

        [Test]
        public void TheHint_EndsWithTheSession_AndAnNpcStillFailsInCharacter()
        {
            _settings.SetSessionModelSetupHint("Press F2.");
            OfflineLlmClient client = new(_settings);

            LlmCompletionResult npc = client.CompleteAsync(new LlmCompletionRequest
            {
                AgentRoleId = BuiltInAgentRoleIds.AiNpc, UserPayload = "hello"
            }).GetAwaiter().GetResult();
            CoreAISettingsAsset.BeginNewSession();

            Assert.IsFalse(npc.Ok, "an NPC never speaks the system line");
            Assert.AreEqual("", _settings.ModelSetupHint, "the next session starts without the hint");
        }

        // ---- the AI Settings page -------------------------------------------------------------------------------

        [Test]
        public void ThePage_HasTestConnection_AndSaysTheKeyIsNotSaved()
        {
            ScrollView root = (ScrollView)Own(new HubSettingsPage(_settings)).CreatePageContent();

            Assert.IsNotNull(FindButton(root, HubSettingsPage.TestConnectionLabel));
            Assert.IsNotNull(FindButton(root, "Apply"), "the existing controls stay");
            Assert.IsNotNull(FindButton(root, "Test backend"));
            Assert.IsNotNull(root.Q<Label>(HubSettingsPage.ConnectionStatusName));
            Assert.IsTrue(HasLabel(root, HubSettingsPage.KeyNote));
        }

        [Test]
        public void ThePagesTestConnection_UsesTheTypedServer_AndSaysItIsConnectedAndRemembered()
        {
            HubSettingsPage page = Own(new HubSettingsPage(_settings));
            ScrollView root = (ScrollView)page.CreatePageContent();
            root.Query<TextField>().AtIndex(0).value = Server;
            root.Query<TextField>().AtIndex(1).value = "";
            root.Query<TextField>().AtIndex(2).value = "m";

            Invoke(page, "TestConnection");

            string line = root.Q<Label>(HubSettingsPage.ConnectionStatusName).text;
            StringAssert.StartsWith("Connected: 'm' answered", line);
            StringAssert.Contains("uses it now", line);
            StringAssert.Contains("next start uses them", line);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode);
            Assert.AreEqual(Server, _settings.ApiBaseUrl);
            Assert.AreEqual("", _http.Sent[0].ApiKey, "a local server is tested with the key field empty");
        }

        [Test]
        public void ThePagesTestConnection_ShowsTheReason_AndKeepsTheOldBackend()
        {
            _http.ModelsStatus = 401;
            HubSettingsPage page = Own(new HubSettingsPage(_settings));
            ScrollView root = (ScrollView)page.CreatePageContent();
            root.Query<TextField>().AtIndex(0).value = Server;
            root.Query<TextField>().AtIndex(2).value = "m";

            Invoke(page, "TestConnection");

            StringAssert.Contains("wants an API key", root.Q<Label>(HubSettingsPage.ConnectionStatusName).text);
            Assert.AreEqual(LlmExecutionMode.Offline, _settings.ExecutionMode);
        }

        [Test]
        public void ThePage_FillsARememberedServerThatWaitsForItsKey()
        {
            _store.Save(new LlmRememberedBackend { BaseUrl = Server, Model = "m", NeedsKey = true });
            CoreAiBackend.RestoreRememberedBackend();

            ScrollView root = (ScrollView)Own(new HubSettingsPage(_settings)).CreatePageContent();

            Assert.AreEqual("HTTP API", root.Q<DropdownField>().value);
            Assert.AreEqual(Server, root.Query<TextField>().AtIndex(0).value);
            Assert.AreEqual("m", root.Query<TextField>().AtIndex(2).value);
            StringAssert.Contains("needs its API key again", root.Q<Label>(HubSettingsPage.ConnectionStatusName).text);
        }

        [Test]
        public void TheConnectionKey_IsTheTypedOne_OrTheSessionsForTheSameServerOnly()
        {
            Assert.AreEqual("typed", HubSettingsPage.ResolveConnectionKey(" typed ", Server, Server, "old"));
            Assert.AreEqual("old", HubSettingsPage.ResolveConnectionKey("", Server + "/", Server, "old"));
            Assert.AreEqual("", HubSettingsPage.ResolveConnectionKey("", "http://other:8080/v1", Server, "old"),
                "a key is never sent to a different server");
            Assert.AreEqual("", HubSettingsPage.ResolveConnectionKey("", "", "", "old"));
        }

        [Test]
        public void TheConnectionLine_SaysWhatHappened()
        {
            CoreAiConnectResult connected = new()
            {
                Check = new LlmConnectionCheckResult { Ok = true, Message = "Connected: 'm' answered in 5 ms." },
                Applied = true,
                Remembered = true
            };
            CoreAiConnectResult failed = new()
            {
                Check = new LlmConnectionCheckResult { Ok = false, Message = "Nothing answered." }
            };

            StringAssert.Contains("enter it again after a restart",
                HubSettingsPage.DescribeConnectResult(connected, true, ""));
            StringAssert.Contains("routed to the API profile 'p'",
                HubSettingsPage.DescribeConnectResult(connected, false, "p"));
            Assert.AreEqual("Nothing answered.", HubSettingsPage.DescribeConnectResult(failed, false, ""));
        }

        // ---- the Hub holding the keyboard -----------------------------------------------------------------------

        [Test]
        public void TheHub_HoldsTheKeyboard_OnlyWhenAskedBuiltAndExpanded()
        {
            Assert.IsTrue(CoreAiHubWindow.IsHoldingKeyboard(true, true, false));
            Assert.IsFalse(CoreAiHubWindow.IsHoldingKeyboard(false, true, false), "off by default: a side panel");
            Assert.IsFalse(CoreAiHubWindow.IsHoldingKeyboard(true, false, false), "no UI, no hold");
            Assert.IsFalse(CoreAiHubWindow.IsHoldingKeyboard(true, true, true), "collapsed gives the keyboard back");
        }

        [Test]
        public void AnEmbeddedChat_WhoseHostLeavesThePanel_NoLongerReportsTyping()
        {
            // WHY ignored messages: the embedded panel looks for a chat service the test scene does not have.
            UnityEngine.TestTools.LogAssert.ignoreFailingMessages = true;
            PanelSettings panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            GameObject panelHost = new("PlayerBackendSettings_EmbeddedChat_PanelHost");
            CoreAI.Chat.CoreAiChatPanel chat = null;
            try
            {
                panelHost.SetActive(false);
                UIDocument document = panelHost.AddComponent<UIDocument>();
                document.panelSettings = panelSettings;
                panelHost.SetActive(true);
                VisualElement host = new();
                VisualElement other = new();
                document.rootVisualElement.Add(host);
                document.rootVisualElement.Add(other);
                VisualTreeAsset template = UnityEditor.AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                    "Assets/CoreAiUnity/Runtime/Source/Features/Chat/UI/CoreAiChat.uxml");
                chat = CoreAI.Chat.CoreAiChatPanel.CreateEmbedded(host, template);
                // WHY by hand: OnEnable, which binds the embedded host in Play Mode, does not run in Edit Mode.
                typeof(CoreAI.Chat.CoreAiChatPanel).GetMethod("InitializeEmbeddedHost",
                    BindingFlags.Instance | BindingFlags.NonPublic).Invoke(chat, null);
                CoreAI.UiFocus.CoreAiUiFocus focus = new();
                chat.SetUiFocus(focus);
                MethodInfo setInputFocus = typeof(CoreAI.Chat.CoreAiChatPanel).GetMethod("SetInputFocus",
                    BindingFlags.Instance | BindingFlags.NonPublic);

                setInputFocus.Invoke(chat, new object[] { true });
                other.RemoveFromHierarchy();
                Assert.IsTrue(focus.HasTextFocus, "twin: another element leaving changes nothing");

                host.RemoveFromHierarchy();
                Assert.IsFalse(focus.HasTextFocus, "the detached chat no longer reports typing");
                Assert.IsFalse(focus.HoldsKeyboard);
            }
            finally
            {
                if (chat != null)
                {
                    UnityEngine.Object.DestroyImmediate(chat.gameObject);
                }

                UnityEngine.Object.DestroyImmediate(panelHost);
                UnityEngine.Object.DestroyImmediate(panelSettings);
            }
        }

        [Test]
        public void BlurringInsideAContainer_IsSafeWithoutAPanel()
        {
            Assert.DoesNotThrow(() => CoreAiHubWindow.BlurFocusInside(null));
            Assert.DoesNotThrow(() => CoreAiHubWindow.BlurFocusInside(new VisualElement()));
        }

        private HubSettingsPage Own(HubSettingsPage page)
        {
            _pages.Add(page);
            return page;
        }

        private static Button FindButton(VisualElement root, string text)
        {
            foreach (Button button in root.Query<Button>().ToList())
            {
                if (button.text == text)
                {
                    return button;
                }
            }

            return null;
        }

        private static bool HasLabel(VisualElement root, string text)
        {
            foreach (Label label in root.Query<Label>().ToList())
            {
                if (label.text == text)
                {
                    return true;
                }
            }

            return false;
        }

        private static void Invoke(object target, string method)
        {
            target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(target, null);
        }
    }
}
