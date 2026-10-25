using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Chat;
using CoreAI.Composition;
using CoreAI.Editor;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;
using VContainer;
using VContainer.Unity;

namespace CoreAI.Tests.EditMode
{
    public sealed class UnityHostRegressionTests
    {
        private readonly List<GameObject> _objects = new List<GameObject>();
        private readonly List<IDisposable> _leases = new List<IDisposable>();
        private readonly List<Scene> _scenes = new List<Scene>();
        private CoreAISettingsAsset _settings;
        private Scene _previousScene;
        private string _sceneAssetFolder;

        [SetUp]
        public void SetUp()
        {
            CoreAi.Invalidate();
            _previousScene = SceneManager.GetActiveScene();
            _settings = ScriptableObject.CreateInstance<CoreAISettingsAsset>();
            _settings.ConfigureOffline();
        }

        [TearDown]
        public void TearDown()
        {
            CoreAi.Invalidate();
            for (int i = _objects.Count - 1; i >= 0; i--)
            {
                if (_objects[i] != null)
                {
                    UnityEngine.Object.DestroyImmediate(_objects[i]);
                }
            }

            foreach (IDisposable lease in _leases)
            {
                lease.Dispose();
            }

            _objects.Clear();
            _leases.Clear();
            if (_previousScene.IsValid() && _previousScene.isLoaded)
            {
                SceneManager.SetActiveScene(_previousScene);
            }

            foreach (Scene scene in _scenes)
            {
                if (scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }

            _scenes.Clear();
            if (_sceneAssetFolder != null)
            {
                Assert.IsTrue(_sceneAssetFolder.StartsWith("Assets/__CoreAIHostRegression_", StringComparison.Ordinal));
                Assert.IsTrue(Guid.TryParseExact(_sceneAssetFolder.Substring("Assets/__CoreAIHostRegression_".Length),
                    "N", out _));
                Assert.IsTrue(AssetDatabase.DeleteAsset(_sceneAssetFolder),
                    "The fixture must remove only its uniquely owned temporary scene assets.");
                _sceneAssetFolder = null;
            }

            UnityEngine.Object.DestroyImmediate(_settings);
            typeof(CoreAi).GetMethod("ResetForSubsystemRegistration", BindingFlags.Static | BindingFlags.NonPublic)
                .Invoke(null, null);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Spawn_MissingParent_RejectsBeforeInstantiation(bool preserveWorld)
        {
            CoreAiWorldCommandExecutor executor = new CoreAiWorldCommandExecutor(new NullGameLogger());
            string name = Unique("spawn");
            CoreAiWorldCommandEnvelope envelope = CoreAiWorldCommandEnvelope.Spawn("empty", name, Vector3.one);
            envelope.stringValue = Unique("missing");
            envelope.worldPositionStays = preserveWorld;

            Assert.IsFalse(Execute(executor, envelope));
            Assert.IsNull(GameObject.Find(name));
            StringAssert.Contains("parent not found", executor.LastErrorMessage);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Change_MissingParent_LeavesEveryTransformFieldUnchanged(bool preserveWorld)
        {
            GameObject parent = Create("parent");
            parent.transform.localScale = new Vector3(2f, 3f, 4f);
            GameObject child = Create("child");
            child.transform.SetParent(parent.transform, false);
            child.transform.localPosition = new Vector3(1f, 2f, 3f);
            child.transform.localRotation = Quaternion.Euler(10f, 20f, 30f);
            child.transform.localScale = new Vector3(0.5f, 0.75f, 1.25f);
            Vector3 position = child.transform.localPosition;
            Quaternion rotation = child.transform.localRotation;
            Vector3 scale = child.transform.localScale;
            CoreAiWorldCommandEnvelope envelope = new CoreAiWorldCommandEnvelope
            {
                action = "change", targetName = child.name, stringValue = Unique("missing"),
                worldPositionStays = preserveWorld, hasPosition = true, hasRotation = true, hasScale = true,
                x = 100f, y = 200f, z = 300f, fx = 50f, fy = 60f, fz = 70f, floatValue = 9f
            };
            CoreAiWorldCommandExecutor executor = new CoreAiWorldCommandExecutor(new NullGameLogger());

            Assert.IsFalse(Execute(executor, envelope));
            Assert.AreSame(parent.transform, child.transform.parent);
            Assert.AreEqual(position, child.transform.localPosition);
            Assert.AreEqual(rotation, child.transform.localRotation);
            Assert.AreEqual(scale, child.transform.localScale);
            StringAssert.Contains("parent not found", executor.LastErrorMessage);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Change_DescendantParent_RejectsBeforeTransformChanges(bool preserveWorld)
        {
            GameObject parent = Create("cycle-parent");
            GameObject child = Create("cycle-child");
            child.transform.SetParent(parent.transform, false);
            CoreAiWorldCommandEnvelope envelope = new CoreAiWorldCommandEnvelope
            {
                action = "change", targetName = parent.name, stringValue = child.name,
                worldPositionStays = preserveWorld, hasPosition = true, x = 100f
            };
            CoreAiWorldCommandExecutor executor = new CoreAiWorldCommandExecutor(new NullGameLogger());

            Assert.IsFalse(Execute(executor, envelope));
            Assert.IsNull(parent.transform.parent);
            Assert.AreEqual(Vector3.zero, parent.transform.position);
            Assert.AreSame(parent.transform, child.transform.parent);
        }

        [Test]
        public void SpawnBatch_MissingParent_CountsFailureWithoutDiscardingValidItem()
        {
            string valid = Unique("valid");
            string invalid = Unique("invalid");
            CoreAiWorldCommandEnvelope envelope = new CoreAiWorldCommandEnvelope
            {
                action = "spawn_batch", prefabKeyOrName = "empty",
                items = new[]
                {
                    new CoreAiSpawnBatchItem { name = valid },
                    new CoreAiSpawnBatchItem { name = invalid, parent = Unique("missing") }
                }
            };
            CoreAiWorldCommandExecutor executor = new CoreAiWorldCommandExecutor(new NullGameLogger());

            Assert.IsTrue(Execute(executor, envelope));
            GameObject created = GameObject.Find(valid);
            if (created != null) _objects.Add(created);
            Assert.IsNotNull(created);
            Assert.IsNull(GameObject.Find(invalid));
            Assert.AreEqual(1, executor.LastSpawnBatchResult.Spawned);
            Assert.AreEqual(1, executor.LastSpawnBatchResult.Failed);
            CollectionAssert.AreEqual(new[] { valid }, executor.LastSpawnBatchResult.Names);
        }

        [Test]
        public void Facade_ExplicitResolverOverridesAnAlreadyResolvedSceneScope()
        {
            if (UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include) != null)
            {
                Assert.Ignore("The resolver fixture requires no external CoreAI lifetime scope.");
            }

            CreateFixtureScene("A");
            RecordingOrchestrator sceneOrchestrator = new RecordingOrchestrator("scene");
            CreateScope(sceneOrchestrator);
            CoreAiChatService sceneService = CoreAi.GetChatService();
            RecordingOrchestrator explicitOrchestrator = new RecordingOrchestrator("explicit");

            CoreAi.SetResolver(() => explicitOrchestrator);

            Assert.AreSame(explicitOrchestrator, CoreAi.GetOrchestrator());
            Assert.IsTrue(CoreAi.IsReady);
            Assert.IsTrue(CoreAi.TryGetOrchestrator(out IAiOrchestrationService resolved));
            Assert.AreSame(explicitOrchestrator, resolved);
            Assert.IsFalse(CoreAi.TryGetChatService(out _),
                "Replacing the resolver must release the automatic chat service from the previous composition.");

            CoreAi.Invalidate();

            Assert.AreNotSame(sceneService, CoreAi.GetChatService());
            Assert.AreNotSame(explicitOrchestrator, CoreAi.GetOrchestrator());
        }

        [Test]
        public async Task Facade_AfterFirstAdditiveSceneIsUnloaded_UsesSecondScopeAndQueue()
        {
            if (UnityEngine.Object.FindAnyObjectByType<CoreAILifetimeScope>(FindObjectsInactive.Include) != null)
            {
                Assert.Ignore("The replacement fixture requires no external CoreAI lifetime scope.");
            }

            RecordingOrchestrator firstInner = new RecordingOrchestrator("first");
            Scene firstScene = CreateFixtureScene("A");
            CoreAILifetimeScope firstScope = CreateScope(firstInner);
            SceneManager.MoveGameObjectToScene(firstScope.gameObject, firstScene);
            CoreAiChatService firstService = CoreAi.GetChatService();
            Assert.AreEqual("first", await CoreAi.AskAsync("one"));
            Scene secondScene = CreateFixtureScene("B");
            CoreAILifetimeScope secondScope = CreateScope(new RecordingOrchestrator("second"));
            SceneManager.MoveGameObjectToScene(secondScope.gameObject, secondScene);
            Assert.IsTrue(EditorSceneManager.CloseScene(firstScene, true));

            Assert.AreEqual("second", await CoreAi.AskAsync("two"));
            Assert.AreNotSame(firstService, CoreAi.GetChatService());
            Assert.AreEqual(1, firstInner.Calls, "No request may enter the retired queue.");
        }

        [Test]
        public void PanelStop_AfterIdentityProviderChanges_UsesTheAdmittedConnection()
        {
            ActorContext admitted = new LocalActorIdentityProvider("old-player", "old-connection", "",
                ActorGrantSet.None, AgentMemoryScope.Empty).GetActorContext("Teacher");
            IActorIdentityProvider replacement = new LocalActorIdentityProvider("new-player", "new-connection", "",
                ActorGrantSet.None, AgentMemoryScope.Empty);
            ActorCancellationRecorder orchestrator = new ActorCancellationRecorder();
            CoreAiChatService service = new CoreAiChatService(orchestrator, actorIdentityProvider: replacement);
            GameObject root = Create("panel");
            root.SetActive(false);
            CoreAiChatPanel panel = root.AddComponent<CoreAiChatPanel>();
            panel.SetActorIdentityProvider(replacement);
            CancellationTokenSource request = new CancellationTokenSource();
            _leases.Add(request);
            SetPanelField(panel, "_chatService", service);
            SetPanelField(panel, "_activeRoleId", "Teacher");
            SetPanelField(panel, "_activeRequestActor", admitted);
            SetPanelField(panel, "_activeRequestCts", request);
            SetPanelField(panel, "_isSending", true);

            panel.StopAgent();

            Assert.AreEqual("old-player", orchestrator.StoppedActor.ActorId);
            Assert.AreEqual("old-connection", orchestrator.StoppedActor.SessionId);
            Assert.AreEqual("Teacher", orchestrator.StoppedRole);
            Assert.IsTrue(request.IsCancellationRequested);
            Assert.AreEqual(0, orchestrator.GlobalCancellationCalls);
        }

        [Test]
        public void NewPlayGeneration_ClearsToolSubscribersHistoryAndBackendSubscribers()
        {
            MethodInfo reset = typeof(CoreAi).GetMethod("ResetForSubsystemRegistration",
                BindingFlags.Static | BindingFlags.NonPublic);
            int records = 0;
            int starts = 0;
            int executed = 0;
            int backends = 0;
            CoreAi.ClearToolCallHistory();
            IDisposable subscription = CoreAi.SubscribeToolCalls(record => records++);
            _leases.Add(subscription);
            CoreAi.OnToolCallStarted += value => starts++;
            CoreAi.OnToolExecuted += (role, tool, args, result) => executed++;
            CoreAiBackend.OnBackendChanged += value => backends++;
            LlmToolCallStarted first = new LlmToolCallStarted("first", "Role", "tool", "{}");
            CoreAi.NotifyToolCallStarted(first);
            CoreAi.NotifyToolExecuted("Role", "tool", null, null);
            InvokeBackendChanged();
            Assert.AreEqual(1, CoreAi.GetToolCallHistorySnapshot().Count);

            CoreAi.Invalidate();
            Assert.AreEqual(1, CoreAi.GetToolCallHistorySnapshot().Count,
                "Manual scene invalidation preserves process subscribers/history.");
            reset.Invoke(null, null);
            Assert.AreEqual(0, CoreAi.GetToolCallHistorySnapshot().Count);
            int fresh = 0;
            _leases.Add(CoreAi.SubscribeToolCalls(record => fresh++));
            CoreAi.NotifyToolCallStarted(new LlmToolCallStarted("second", "Role", "tool", "{}"));
            CoreAi.NotifyToolExecuted("Role", "tool", null, null);
            InvokeBackendChanged();
            Assert.AreEqual(1, records);
            Assert.AreEqual(1, starts);
            Assert.AreEqual(1, executed);
            Assert.AreEqual(1, backends);
            Assert.AreEqual(1, fresh, "The next generation must receive each event exactly once.");
            reset.Invoke(null, null);
        }

        [Test]
        public void EditorLayouts_AreResolvableByStableGuid()
        {
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(
                AssetDatabase.GUIDToAssetPath(CoreAIChatDemoSceneCreator.ChatUxmlGuid)));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<StyleSheet>(
                AssetDatabase.GUIDToAssetPath(CoreAIChatDemoSceneCreator.ChatUssGuid)));
            Assert.IsNotNull(AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(AssetDatabase.GUIDToAssetPath(
                CoreAI.Infrastructure.Llm.Editor.CoreAISettingsAssetEditor.LayoutGuid)));
        }

        [TestCase("change", false)]
        [TestCase("change", true)]
        [TestCase("spawn_batch", false)]
        [TestCase("spawn_batch", true)]
        public async Task WorldTool_CancellationRemainsCancellation(string action, bool cancelFromExecutor)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            CancellingExecutor executor = new CancellingExecutor(cancellation.Token);
            if (!cancelFromExecutor) cancellation.Cancel();
            WorldLlmTool tool = new WorldLlmTool(executor, _settings, new NullGameLogger());
            bool cancelled = false;
            try
            {
                await tool.ExecuteAsync(action, targetName: "target", x: 1f,
                    itemsJson: "[{\"name\":\"item\",\"prefabKey\":\"empty\"}]",
                    cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.IsTrue(cancelled);
            Assert.AreEqual(cancelFromExecutor ? 1 : 0, executor.Calls);
        }

        [TestCase(false)]
        [TestCase(true)]
        public async Task ComponentTool_CancellationRemainsCancellation(bool cancelFromExecutor)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            CancellingExecutor executor = new CancellingExecutor(cancellation.Token);
            if (!cancelFromExecutor) cancellation.Cancel();
            ComponentLlmTool tool = new ComponentLlmTool(executor, _settings, new NullGameLogger());
            bool cancelled = false;
            try
            {
                await tool.ExecuteAsync("set", "target", "linerenderer", "positioncount",
                    floatValue: 1f, cancellationToken: cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.IsTrue(cancelled);
            Assert.AreEqual(cancelFromExecutor ? 1 : 0, executor.Calls);
        }

        private CoreAILifetimeScope CreateScope(RecordingOrchestrator inner)
        {
            GameObject root = Create("scope");
            root.SetActive(false);
            CoreAILifetimeScope scope = root.AddComponent<CoreAILifetimeScope>();
            DefaultAgentMemoryScopeProvider provider = new DefaultAgentMemoryScopeProvider();
            QueuedAiOrchestrator queue = new QueuedAiOrchestrator(inner,
                new AiOrchestrationQueueOptions { MaxConcurrent = 1 }, provider);
            _leases.Add(queue);
            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterInstance<IAiOrchestrationService>(queue);
            builder.RegisterInstance<IActorIdentityProvider>(inner.Identity);
            builder.RegisterInstance<IAgentMemoryScopeProvider>(provider);
            builder.RegisterInstance<ICoreAISettings>(_settings);
            builder.RegisterInstance<IGameLogger>(new NullGameLogger());
            builder.RegisterInstance<IAgentMemoryStore>(new NullAgentMemoryStore());
            IObjectResolver container = builder.Build();
            typeof(LifetimeScope).GetProperty(nameof(LifetimeScope.Container)).SetValue(scope, container);
            return scope;
        }

        private Scene CreateFixtureScene(string name)
        {
            if (name != "A" && name != "B")
            {
                throw new ArgumentOutOfRangeException(nameof(name));
            }

            if (_sceneAssetFolder == null)
            {
                string folderName = "__CoreAIHostRegression_" + Guid.NewGuid().ToString("N");
                _sceneAssetFolder = "Assets/" + folderName;
                Assert.IsFalse(AssetDatabase.IsValidFolder(_sceneAssetFolder));
                Assert.IsNotEmpty(AssetDatabase.CreateFolder("Assets", folderName));
            }

            string path = _sceneAssetFolder + "/" + name + ".unity";
            string absolutePath = Path.Combine(Application.dataPath, Path.GetFileName(_sceneAssetFolder), name + ".unity");
            File.WriteAllText(absolutePath,
                "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n" +
                "--- !u!1660057539 &9223372036854775807\nSceneRoots:\n" +
                "  m_ObjectHideFlags: 0\n  m_Roots: []\n", new UTF8Encoding(false));
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            Scene scene = EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
            _scenes.Add(scene);
            Assert.IsTrue(scene.IsValid() && scene.isLoaded);
            Assert.AreEqual(0, scene.rootCount);
            Assert.IsTrue(SceneManager.SetActiveScene(scene));
            return scene;
        }

        private GameObject Create(string label)
        {
            GameObject created = new GameObject(Unique(label));
            _objects.Add(created);
            return created;
        }

        private static string Unique(string label) => "CoreAiHost_" + label + "_" + Guid.NewGuid().ToString("N");

        private static bool Execute(CoreAiWorldCommandExecutor executor, CoreAiWorldCommandEnvelope envelope)
        {
            return executor.TryExecute(new ApplyAiGameCommand
            {
                CommandTypeId = AiGameCommandTypeIds.WorldCommand,
                JsonPayload = JsonUtility.ToJson(envelope)
            });
        }

        private static void InvokeBackendChanged()
        {
            Action<CoreAiBackendStatus> handler = (Action<CoreAiBackendStatus>)typeof(CoreAiBackend)
                .GetField("OnBackendChanged", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
            handler?.Invoke(default);
        }

        private static void SetPanelField(CoreAiChatPanel panel, string name, object value)
        {
            typeof(CoreAiChatPanel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(panel, value);
        }

        private sealed class ActorCancellationRecorder : IAiOrchestrationService, IActorAiTaskCancellation
        {
            public ActorContext StoppedActor;
            public string StoppedRole;
            public int GlobalCancellationCalls;
            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
                => Task.FromResult("done");
            public void CancelTasks(string scope) { GlobalCancellationCalls++; }
            public void CancelActorTasks(ActorContext actorContext, string roleId = null)
            {
                StoppedActor = actorContext;
                StoppedRole = roleId;
            }
        }

        private sealed class RecordingOrchestrator : IAiOrchestrationService, IAiActorContextResolver
        {
            private readonly string _response;
            public int Calls { get; private set; }
            public IActorIdentityProvider Identity { get; } = new LocalActorIdentityProvider("scope-fixture");
            public RecordingOrchestrator(string response) { _response = response; }
            public ActorContext ResolveActorContext(AiTaskRequest request)
            {
                request.ActorContext ??= Identity.GetActorContext(request.RoleId);
                return request.ActorContext.Value;
            }

            public Task<string> RunTaskAsync(AiTaskRequest request, CancellationToken cancellationToken = default)
            {
                Calls++;
                return Task.FromResult(_response);
            }

            public void CancelTasks(string scope) { }
        }

        private sealed class CancellingExecutor : ICoreAiWorldCommandExecutor, ICoreAiComponentCommandExecutor
        {
            private readonly CancellationToken _token;
            public int Calls { get; private set; }
            public string[] LastListedAnimations => Array.Empty<string>();
            public List<Dictionary<string, object>> LastListedObjects => new List<Dictionary<string, object>>();
            public CancellingExecutor(CancellationToken token) { _token = token; }
            public bool TryExecute(ApplyAiGameCommand command)
            {
                Calls++;
                throw new OperationCanceledException(_token);
            }

            public bool TryExecute(ApplyAiGameCommand command, out List<string> listedComponents)
            {
                listedComponents = new List<string>();
                return TryExecute(command);
            }
        }
    }
}
