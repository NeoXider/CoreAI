#if UNITY_EDITOR && COREAI_HAS_INPUT_SYSTEM && COREAI_HAS_HUB
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Ai.LuaCs;
using CoreAI.ChatOverlay;
using CoreAI.Composition;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure;
using CoreAI.Infrastructure.Llm;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.WorldPackages;
using CoreAI.UiFocus;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using VContainer;
using Object = UnityEngine.Object;

namespace CoreAI.Tests.PlayMode.RbxApi.BlockWorld
{
    /// <summary>
    /// The Hub inside the authored Block World scene, as a first-time player meets it: the chat with no model says to
    /// press F2; F2 opens the Hub on AI Settings while the world takes no keys (W does not walk, T does not open the
    /// chat, E does not talk, a click does not break) and gives them back on close; Test connection against a model
    /// server stub (an in-process HTTP listener) connects and remembers it without the key; and the next chat turn
    /// reaches the stub. Virtual Input System devices, a fresh storage root, the shared settings forced Offline.
    /// </summary>
    [TestFixture]
    [Category("InputDevice")]
    public sealed class BlockWorldHubPlayModeTests : InputTestFixture
    {
        private const string ScenePath = "Assets/CoreAI.Demos/BlockWorld/BlockWorld.unity";
        private const string StubModel = "stub-model";
        private const string StubReply = "Hello from the stub model.";

        private Keyboard _keyboard;
        private Mouse _mouse;
        private CoreAiPlayWorldBootstrap _bootstrap;
        private IDisposable _storageOverride;
        private string _storageRoot;
        private CoreAISettingsAsset _settings;
        private string _settingsSnapshot;
        private Scene _previousActive;
        private Scene _blockWorld;
        private ModelStub _stub;
        private readonly List<string> _record = new();

        public override void Setup()
        {
            base.Setup();
            _record.Clear();
            _storageRoot = Path.Combine(Path.GetTempPath(), "cai-hub", Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_storageRoot);
            _storageOverride = CoreAiStorageRoot.Override(_storageRoot);
            _settings = CoreAISettingsAsset.Instance;
            Assert.IsNotNull(_settings, "the shared Resources/CoreAISettings asset exists");
            _settingsSnapshot = EditorJsonUtility.ToJson(_settings);
            _settings.ConfigureOffline();
            _settings.ClearSessionOverrides();
            EditorUtility.ClearDirty(_settings);
            _stub = new ModelStub();
            _previousActive = SceneManager.GetActiveScene();
        }

        [UnityTearDown]
        public IEnumerator CloseScene()
        {
            if (_blockWorld.IsValid() && _blockWorld.isLoaded)
            {
                foreach (GameObject root in _blockWorld.GetRootGameObjects())
                {
                    root.SetActive(false);
                }

                if (_previousActive.IsValid() && _previousActive.isLoaded)
                {
                    SceneManager.SetActiveScene(_previousActive);
                }

                yield return SceneManager.UnloadSceneAsync(_blockWorld);
            }

            _blockWorld = default;
            yield return null;
            CoreAiBackend.ForgetRememberedBackend();
            _stub?.Dispose();
            _stub = null;
            _storageOverride?.Dispose();
            _storageOverride = null;
            if (_settings != null && _settingsSnapshot != null)
            {
                _settings.ClearSessionOverrides();
                _settings.SetSessionModelSetupHint("");
                EditorJsonUtility.FromJsonOverwrite(_settingsSnapshot, _settings);
                EditorUtility.ClearDirty(_settings);
            }

            try
            {
                if (Directory.Exists(_storageRoot))
                {
                    Directory.Delete(_storageRoot, true);
                }
            }
            catch (IOException)
            {
            }
        }

        [UnityTest]
        [Timeout(900000)]
        public IEnumerator TheHub_OpensOnF2_HoldsTheKeyboard_ConnectsTheStub_AndTheChatTalksToIt()
        {
            yield return OpenScene();
            CoreAiPlayWorldHubModule module = Object.FindFirstObjectByType<CoreAiPlayWorldHubModule>();
            Assert.IsNotNull(module, "the Block World scene carries the Hub module (rebuild the scene with the builder)");
            Assert.IsEmpty(module.Failure, "the Hub module has its prefab");
            Assert.IsFalse(module.IsOpen, "the Hub starts closed");
            Assert.AreEqual(KeyCode.F2, module.OpenKey);
            Assert.AreEqual("F2  Set up the AI model", module.HintText, "the hint names the key while no model is set");
            Label hint = FindHintLabel();
            Assert.IsNotNull(hint, "the hint is drawn");
            Assert.AreEqual(module.HintText, hint.text);

            ChatOverlayController overlay = Core().Resolve<ChatOverlayController>();
            ICoreAiUiFocus focus = Core().Resolve<ICoreAiUiFocus>();
            int start = overlay.Feed.Lines.Count;
            overlay.Submit("build a watchtower here");
            yield return WaitForLine(overlay, start, line => line.Kind != ChatFeedLineKind.Player, 900);
            string noModel = Lines(overlay, start);
            Record("no model: " + noModel);
            StringAssert.Contains("Press F2 to open the AI settings", noModel, "the chat names this scene's key");
            StringAssert.Contains("model", noModel);

            yield return Frames(30);
            RbxVector3 before = RootPosition();
            CursorLockMode lockBefore = UnityEngine.Cursor.lockState;
            yield return Tap(_keyboard.f2Key);
            yield return Frames(3);
            Assert.IsTrue(module.IsOpen, "F2 opens the Hub");
            Assert.IsTrue(module.Window.HoldsKeyboard, "the open Hub holds the keyboard");
            Assert.IsTrue(focus.HoldsKeyboard, "the scope's UI focus says the keyboard is the UI's");
            Assert.AreEqual("", module.HintText, "the hint hides while the Hub is open");
            for (int frame = 0; frame < 60 && module.Window.ActivePageId != HubSettingsPage.DefaultPageId; frame++)
            {
                yield return null;
            }

            Assert.AreEqual(HubSettingsPage.DefaultPageId, module.Window.ActivePageId,
                "with no model the Hub opens on AI Settings");
            yield return Frames(20);
            Assert.IsFalse(focus.HasTextFocus,
                "on AI Settings no field has the keyboard (the Chat tab's input let go when the tab changed): "
                + "the hold is not a text focus");
            Assert.IsTrue(focus.HoldsKeyboard, "and the open Hub still holds the keyboard");
            Record("cursor lock before " + lockBefore + ", while open " + UnityEngine.Cursor.lockState);
            Assert.AreNotEqual(CursorLockMode.Locked, UnityEngine.Cursor.lockState, "the pointer is free");

            // ---- the world takes no input while the Hub is open --------------------------------------------------
            yield return HoldW();
            float walkedOpen = Horizontal(RootPosition() - before);
            float askedOpen = _askedToMove;
            yield return Tap(_keyboard.tKey);
            bool chatOpenedOpen = overlay.IsInputOpen;
            int linesBeforeE = overlay.Feed.Lines.Count;
            yield return Tap(_keyboard.eKey);
            yield return Frames(60);
            int linesAfterE = overlay.Feed.Lines.Count;
            object brokenBefore = Workspace().GetAttribute("BlockWorldBroken");
            yield return Click(_mouse.leftButton);
            yield return Frames(5);
            object brokenAfter = Workspace().GetAttribute("BlockWorldBroken");
            Record("while open: walked " + walkedOpen.ToString("0.00") + " studs (move asked " + askedOpen.ToString("0.00")
                   + "), chat input " + chatOpenedOpen + ", lines after E " + linesBeforeE + " -> " + linesAfterE
                   + ", broken " + brokenBefore + " -> " + brokenAfter);
            Assert.Less(askedOpen, 0.1f, "W asks the character for no move while the Hub is open");
            Assert.Less(walkedOpen, 0.5f, "W does not walk while the Hub is open");
            Assert.IsFalse(chatOpenedOpen, "T does not open the chat while the Hub is open");
            Assert.AreEqual(linesBeforeE, linesAfterE, "E does not talk to a villager while the Hub is open");
            Assert.AreEqual(brokenBefore, brokenAfter, "a click does not break a block while the Hub is open");

            // ---- Test connection against the stub ----------------------------------------------------------------
            VisualElement hubRoot = module.Window.GetComponent<UIDocument>().rootVisualElement;
            List<TextField> fields = hubRoot.Query<TextField>().ToList();
            Assert.GreaterOrEqual(fields.Count, 3, "the AI Settings page shows the server fields");
            fields[0].value = _stub.BaseUrl;
            fields[1].value = "";
            fields[2].value = StubModel;
            Button test = FindButton(hubRoot, HubSettingsPage.TestConnectionLabel);
            Assert.IsNotNull(test, "AI Settings has Test connection");
            Assert.IsTrue(test.enabledInHierarchy, "Test connection can be pressed");
            Assert.AreEqual(DisplayStyle.Flex, test.resolvedStyle.display, "and is shown while no model is set up");
            // WHY focus first: a submit goes to the focused control, as a player's Enter on a tabbed-to button does.
            test.Focus();
            yield return null;
            using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
            {
                test.SendEvent(submit);
            }

            yield return Frames(2);
            Label pressed = hubRoot.Q<Label>(HubSettingsPage.ConnectionStatusName);
            if (pressed != null && pressed.text == HubSettingsPage.NoModelNote)
            {
                // WHY the button's own handler: a batchmode panel can drop a synthetic submit; the press is then
                // made the way the button makes it, through its clicked callback.
                Record("submit did not reach the button; pressing it through its clickable");
                PressButton(test);
            }

            Label status = null;
            for (int frame = 0; frame < 900; frame++)
            {
                yield return null;
                status = hubRoot.Q<Label>(HubSettingsPage.ConnectionStatusName);
                if (status != null && status.text.Length > 0 && status.text != HubSettingsPage.NoModelNote &&
                    !status.text.StartsWith("Testing", StringComparison.Ordinal))
                {
                    break;
                }
            }

            Record("test connection: " + (status != null ? status.text : "(no status line)") + "; stub saw "
                   + _stub.Describe());
            Assert.IsNotNull(status);
            StringAssert.StartsWith("Connected: '" + StubModel + "' answered", status.text);
            Assert.AreEqual(LlmExecutionMode.ClientOwnedApi, _settings.ExecutionMode, "the game uses the server now");
            Assert.AreEqual(_stub.BaseUrl, _settings.ApiBaseUrl);
            Assert.AreEqual(StubModel, _settings.ModelName);
            string remembered = File.ReadAllText(new FileLlmRememberedBackendStore().StoragePath);
            StringAssert.Contains(_stub.BaseUrl, remembered, "this device remembers the address");
            StringAssert.Contains(StubModel, remembered);
            StringAssert.Contains("\"needsKey\": false", remembered);

            // ---- close, and the world has its keys back ---------------------------------------------------------
            yield return Tap(_keyboard.f2Key);
            yield return Frames(3);
            Assert.IsFalse(module.IsOpen, "F2 closes the Hub");
            Assert.IsFalse(focus.HoldsKeyboard, "the closed Hub gives the keyboard back");
            Assert.IsFalse(focus.HasTextFocus, "and leaves no field reported as typing");
            Assert.AreEqual("F2  AI settings", module.HintText, "with a model the hint says AI settings");
            yield return Frames(30);
            RbxVector3 closedFrom = RootPosition();
            yield return HoldW();
            float walkedClosed = Horizontal(RootPosition() - closedFrom);
            float askedClosed = _askedToMove;
            yield return Tap(_keyboard.tKey);
            bool chatOpenedClosed = overlay.IsInputOpen;
            yield return Tap(_keyboard.escapeKey);
            yield return Frames(5);
            Record("after close: walked " + walkedClosed.ToString("0.00") + " studs (move asked "
                   + askedClosed.ToString("0.00") + "), chat input " + chatOpenedClosed);
            Assert.Greater(askedClosed, 0.5f, "twin: W asks the character to move again once the Hub is closed");
            Assert.IsTrue(chatOpenedClosed, "twin: T opens the chat again once the Hub is closed");

            // ---- the chat talks to the stub ----------------------------------------------------------------------
            int chatsBefore = _stub.ChatRequests;
            int turnStart = overlay.Feed.Lines.Count;
            overlay.Submit("Say hello.");
            yield return WaitForLine(overlay, turnStart,
                line => line.Kind != ChatFeedLineKind.Player && (line.Text ?? "").Contains(StubReply), 1800);
            string answer = Lines(overlay, turnStart);
            Record("chat turn: " + answer + "; stub saw " + _stub.Describe());
            Assert.Greater(_stub.ChatRequests, chatsBefore, "the chat turn reached the stub");
            StringAssert.Contains("\"model\":\"" + StubModel + "\"", _stub.LastChatBody.Replace(" ", ""),
                "with the tested model");
            StringAssert.Contains(StubReply, answer, "the stub's answer is in the chat");

            // ---- Escape closes too --------------------------------------------------------------------------------
            yield return Tap(_keyboard.f2Key);
            yield return Frames(3);
            Assert.IsTrue(module.IsOpen);
            yield return Tap(_keyboard.escapeKey);
            yield return Frames(3);
            Assert.IsFalse(module.IsOpen, "Escape closes the Hub");
            Debug.Log("[BlockWorldHubPlayModeTests] " + string.Join(" || ", _record));
        }

        // ---- opening the scene ------------------------------------------------------------------------------------

        private IEnumerator OpenScene()
        {
            // WHY here and not in Setup: the flag set in a set-up method does not carry into the test's coroutine.
            LogAssert.ignoreFailingMessages = true;
            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _keyboard.MakeCurrent();
            _mouse.MakeCurrent();
            HashSet<GameObject> before = new();
            if (_previousActive.IsValid() && _previousActive.isLoaded)
            {
                before.UnionWith(_previousActive.GetRootGameObjects());
            }

            Scene scene = EditorSceneManager.LoadSceneInPlayMode(ScenePath, new LoadSceneParameters(LoadSceneMode.Additive));
            Assert.IsTrue(scene.IsValid(), "the Block World scene loads: " + ScenePath);
            yield return null;
            // WHY: the play world composes in Awake, before the new scene can be made active, so what it created at the
            // root of the scene active then moves into the new scene.
            if (_previousActive.IsValid() && _previousActive.isLoaded)
            {
                foreach (GameObject root in _previousActive.GetRootGameObjects())
                {
                    if (!before.Contains(root))
                    {
                        SceneManager.MoveGameObjectToScene(root, scene);
                    }
                }
            }

            SceneManager.SetActiveScene(scene);
            _blockWorld = scene;
            yield return null;
            _bootstrap = Object.FindFirstObjectByType<CoreAiPlayWorldBootstrap>();
            Assert.IsNotNull(_bootstrap, "the scene carries the play world bootstrap");
            Assert.IsTrue(_bootstrap.IsComposed, "the play world composes: " + _bootstrap.Failure);
            for (int frame = 0; frame < 1800 && !Equals(WorkspaceOrNull()?.GetAttribute("BlockWorldOpened"), true); frame++)
            {
                yield return null;
            }

            Assert.IsTrue(Equals(Workspace().GetAttribute("BlockWorldOpened"), true), "the world lets the player go");
            Assert.IsNotNull(CoreAiLocalPlayer.FindHumanoid(CurrentWorld()), "the local character spawned");
        }

        private IObjectResolver Core()
        {
            return _bootstrap.Composition.CoreScope.Container;
        }

        private LuaCsRbxApiBindings CurrentWorld()
        {
            IObjectResolver container = _bootstrap?.Composition?.ModsScope?.Container;
            return container?.Resolve<RbxWorldRuntimeSessionController>().CurrentRbxApi;
        }

        private RbxInstance WorkspaceOrNull()
        {
            return CurrentWorld()?.Game?.FindFirstChildOfClass("Workspace");
        }

        private RbxInstance Workspace()
        {
            return CurrentWorld().Game.FindFirstChildOfClass("Workspace");
        }

        private RbxVector3 RootPosition()
        {
            RbxHumanoid humanoid = CoreAiLocalPlayer.FindHumanoid(CurrentWorld());
            return CurrentWorld().PartSink.GetLivePositionStuds(humanoid.RootPart.Id);
        }

        private static float Horizontal(RbxVector3 delta)
        {
            return Mathf.Sqrt(delta.X * delta.X + delta.Z * delta.Z);
        }

        private static Label FindHintLabel()
        {
            foreach (UIDocument document in Object.FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                Label label = document.rootVisualElement?.Q<Label>(CoreAiPlayWorldHubModule.HintElementName);
                if (label != null)
                {
                    return label;
                }
            }

            return null;
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

        private static void PressButton(Button button)
        {
            const System.Reflection.BindingFlags Flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            foreach (string name in new[] { "Invoke", "SimulateSingleClick" })
            {
                foreach (System.Reflection.MethodInfo method in typeof(Clickable).GetMethods(Flags))
                {
                    System.Reflection.ParameterInfo[] parameters = method.GetParameters();
                    if (method.Name != name || parameters.Length == 0 || parameters[0].ParameterType != typeof(EventBase))
                    {
                        continue;
                    }

                    object[] arguments = new object[parameters.Length];
                    for (int index = 1; index < parameters.Length; index++)
                    {
                        arguments[index] = parameters[index].HasDefaultValue ? parameters[index].DefaultValue : null;
                    }

                    using (NavigationSubmitEvent submit = NavigationSubmitEvent.GetPooled())
                    {
                        arguments[0] = submit;
                        method.Invoke(button.clickable, arguments);
                    }

                    return;
                }
            }

            Assert.Fail("the button's clickable has no press method to call");
        }

        private IEnumerator Tap(ButtonControl button)
        {
            yield return null;
            Press(button);
            yield return Frames(2);
            Release(button);
            yield return Frames(2);
        }

        private IEnumerator Click(ButtonControl button)
        {
            // WHY the right half: the world breaks at the crosshair whatever the pointer's place, and the Hub's panel
            // sits on the left, so the click lands on no Hub control.
            Set(_mouse.position, new Vector2(Screen.width * 0.8f, Screen.height * 0.5f));
            yield return Tap(button);
        }

        private float _askedToMove;

        /// <summary>Holds W for 45 frames; <see cref="_askedToMove"/> is the largest move the controls asked for.</summary>
        private IEnumerator HoldW()
        {
            _askedToMove = 0f;
            Press(_keyboard.wKey);
            for (int frame = 0; frame < 45; frame++)
            {
                yield return null;
                RbxHumanoid humanoid = CoreAiLocalPlayer.FindHumanoid(CurrentWorld());
                _askedToMove = Mathf.Max(_askedToMove, humanoid != null ? humanoid.RequestedMoveDirection.Magnitude : 0f);
            }

            Release(_keyboard.wKey);
            yield return Frames(10);
        }

        private static IEnumerator Frames(int count)
        {
            for (int frame = 0; frame < count; frame++)
            {
                yield return null;
            }
        }

        private static IEnumerator WaitForLine(ChatOverlayController overlay, int start, Func<ChatFeedLine, bool> match,
            int maxFrames)
        {
            for (int frame = 0; frame < maxFrames; frame++)
            {
                for (int index = start; index < overlay.Feed.Lines.Count; index++)
                {
                    if (match(overlay.Feed.Lines[index]))
                    {
                        yield break;
                    }
                }

                yield return null;
            }
        }

        private static string Lines(ChatOverlayController overlay, int start)
        {
            StringBuilder text = new();
            for (int index = start; index < overlay.Feed.Lines.Count; index++)
            {
                ChatFeedLine line = overlay.Feed.Lines[index];
                if (line.Kind == ChatFeedLineKind.Player)
                {
                    continue;
                }

                text.Append(line.Speaker).Append(": ").Append(line.Text).Append(" | ");
            }

            return text.ToString();
        }

        private void Record(string line)
        {
            _record.Add(line);
        }

        /// <summary>
        /// An OpenAI-compatible model server on a loopback port: <c>GET /v1/models</c> lists <see cref="StubModel"/>,
        /// <c>POST /v1/chat/completions</c> answers <see cref="StubReply"/> as JSON or, for a streaming request, as
        /// server-sent events. Every request is counted.
        /// </summary>
        private sealed class ModelStub : IDisposable
        {
            private readonly TcpListener _listener;
            private readonly CancellationTokenSource _stop = new();
            private readonly ConcurrentQueue<string> _seen = new();
            private int _chatRequests;
            private int _modelRequests;
            private string _lastChatBody = "";

            public ModelStub()
            {
                _listener = new TcpListener(IPAddress.Loopback, 0);
                _listener.Start();
                BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + "/v1";
                Task.Run(AcceptLoopAsync);
            }

            public string BaseUrl { get; }

            public int ChatRequests => Volatile.Read(ref _chatRequests);

            public string LastChatBody => Volatile.Read(ref _lastChatBody) ?? "";

            public string Describe()
            {
                return Volatile.Read(ref _modelRequests) + " model list(s), " + ChatRequests + " chat request(s): "
                       + string.Join(", ", _seen.ToArray());
            }

            public void Dispose()
            {
                _stop.Cancel();
                try
                {
                    _listener.Stop();
                }
                catch (SocketException)
                {
                }
            }

            private async Task AcceptLoopAsync()
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await _listener.AcceptTcpClientAsync();
                    }
                    catch (Exception)
                    {
                        return;
                    }

                    _ = Task.Run(() => ServeAsync(client));
                }
            }

            private async Task ServeAsync(TcpClient client)
            {
                using (client)
                {
                    try
                    {
                        NetworkStream stream = client.GetStream();
                        (string requestLine, Dictionary<string, string> headers, byte[] body) = await ReadRequestAsync(stream);
                        string text = Encoding.UTF8.GetString(body);
                        _seen.Enqueue(requestLine);
                        if (requestLine.StartsWith("GET ", StringComparison.Ordinal) && requestLine.Contains("/v1/models"))
                        {
                            Interlocked.Increment(ref _modelRequests);
                            await WriteAsync(stream, "200 OK", "application/json",
                                "{\"object\":\"list\",\"data\":[{\"id\":\"" + StubModel + "\",\"object\":\"model\"}]}");
                            return;
                        }

                        if (requestLine.StartsWith("POST ", StringComparison.Ordinal) &&
                            requestLine.Contains("/v1/chat/completions"))
                        {
                            Interlocked.Increment(ref _chatRequests);
                            Volatile.Write(ref _lastChatBody, text);
                            if (Regex.IsMatch(text, "\"stream\"\\s*:\\s*true"))
                            {
                                await WriteStreamAsync(stream);
                            }
                            else
                            {
                                await WriteAsync(stream, "200 OK", "application/json",
                                    "{\"id\":\"stub\",\"object\":\"chat.completion\",\"created\":1,\"model\":\"" + StubModel
                                    + "\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\""
                                    + StubReply + "\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":1,"
                                    + "\"completion_tokens\":5,\"total_tokens\":6}}");
                            }

                            return;
                        }

                        await WriteAsync(stream, "404 Not Found", "application/json", "{\"error\":\"not found\"}");
                    }
                    catch (Exception ex)
                    {
                        _seen.Enqueue("stub error: " + ex.Message);
                    }
                }
            }

            private static async Task<(string, Dictionary<string, string>, byte[])> ReadRequestAsync(NetworkStream stream)
            {
                List<byte> head = new();
                byte[] one = new byte[1];
                while (head.Count < 65536)
                {
                    int read = await stream.ReadAsync(one, 0, 1);
                    if (read == 0)
                    {
                        break;
                    }

                    head.Add(one[0]);
                    int n = head.Count;
                    if (n >= 4 && head[n - 4] == '\r' && head[n - 3] == '\n' && head[n - 2] == '\r' && head[n - 1] == '\n')
                    {
                        break;
                    }
                }

                string[] lines = Encoding.ASCII.GetString(head.ToArray()).Split(new[] { "\r\n" }, StringSplitOptions.None);
                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                for (int index = 1; index < lines.Length; index++)
                {
                    int colon = lines[index].IndexOf(':');
                    if (colon > 0)
                    {
                        headers[lines[index].Substring(0, colon).Trim()] = lines[index].Substring(colon + 1).Trim();
                    }
                }

                byte[] body = Array.Empty<byte>();
                if (headers.TryGetValue("Content-Length", out string lengthText) && int.TryParse(lengthText, out int length))
                {
                    body = await ReadExactlyAsync(stream, length);
                }
                else if (headers.TryGetValue("Transfer-Encoding", out string encoding) &&
                         encoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    body = await ReadChunkedAsync(stream);
                }

                return (lines.Length > 0 ? lines[0] : "", headers, body);
            }

            private static async Task<byte[]> ReadExactlyAsync(NetworkStream stream, int length)
            {
                byte[] buffer = new byte[length];
                int received = 0;
                while (received < length)
                {
                    int read = await stream.ReadAsync(buffer, received, length - received);
                    if (read == 0)
                    {
                        throw new IOException("the request body ended early");
                    }

                    received += read;
                }

                return buffer;
            }

            private static async Task<byte[]> ReadChunkedAsync(NetworkStream stream)
            {
                List<byte> body = new();
                while (true)
                {
                    string sizeLine = await ReadLineAsync(stream);
                    int size = Convert.ToInt32(sizeLine.Split(';')[0].Trim(), 16);
                    if (size == 0)
                    {
                        await ReadLineAsync(stream);
                        return body.ToArray();
                    }

                    body.AddRange(await ReadExactlyAsync(stream, size));
                    await ReadLineAsync(stream);
                }
            }

            private static async Task<string> ReadLineAsync(NetworkStream stream)
            {
                List<byte> line = new();
                byte[] one = new byte[1];
                while (await stream.ReadAsync(one, 0, 1) == 1)
                {
                    if (one[0] == '\n')
                    {
                        break;
                    }

                    if (one[0] != '\r')
                    {
                        line.Add(one[0]);
                    }
                }

                return Encoding.ASCII.GetString(line.ToArray());
            }

            private static async Task WriteAsync(NetworkStream stream, string status, string contentType, string body)
            {
                byte[] payload = Encoding.UTF8.GetBytes(body);
                byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: " + contentType
                                                         + "\r\nContent-Length: " + payload.Length
                                                         + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, 0, headers.Length);
                await stream.WriteAsync(payload, 0, payload.Length);
                await stream.FlushAsync();
            }

            private static async Task WriteStreamAsync(NetworkStream stream)
            {
                StringBuilder events = new();
                events.Append("data: {\"id\":\"stub\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"")
                    .Append(StubModel).Append("\",\"choices\":[{\"index\":0,\"delta\":{\"role\":\"assistant\",\"content\":\"")
                    .Append(StubReply).Append("\"},\"finish_reason\":null}]}\n\n");
                events.Append("data: {\"id\":\"stub\",\"object\":\"chat.completion.chunk\",\"created\":1,\"model\":\"")
                    .Append(StubModel).Append("\",\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"stop\"}]}\n\n");
                events.Append("data: [DONE]\n\n");
                byte[] payload = Encoding.UTF8.GetBytes(events.ToString());
                byte[] headers = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\n"
                                                         + "Cache-Control: no-cache\r\nContent-Length: " + payload.Length
                                                         + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(headers, 0, headers.Length);
                await stream.WriteAsync(payload, 0, payload.Length);
                await stream.FlushAsync();
            }
        }
    }
}
#endif
