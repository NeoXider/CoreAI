using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CoreAI.Composition;
using CoreAI.Editor;
#if COREAI_HAS_LLMUNITY
using CoreAI.WebGl;
#endif
using NUnit.Framework;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using UnityEngine.TestTools;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// EditMode coverage for the WebGL StreamingAssets guard's restore bookkeeping. The guard moves the
    /// user's native LLM binaries into <c>Library/CoreAI/WebGlBuildBackup</c>, so the record of where they
    /// came from must survive an editor restart — SessionState alone does not.
    /// </summary>
    [TestFixture]
    public sealed class CoreAIWebGlStreamingAssetsGuardEditModeTests
    {
        private string _backupRoot;

        [SetUp]
        public void SetUp()
        {
            _backupRoot = Path.Combine(
                Path.GetTempPath(), "CoreAiWebGlGuardTests_" + Guid.NewGuid().ToString("N"));
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_backupRoot))
            {
                Directory.Delete(_backupRoot, true);
            }
        }

        [Test]
        public void Manifest_IsPersistedInsideTheBackupRoot()
        {
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(_backupRoot, NewEntries());

            string manifestPath = CoreAIWebGlStreamingAssetsGuard.GetManifestPath(_backupRoot);

            Assert.AreEqual(_backupRoot, Path.GetDirectoryName(manifestPath),
                "The manifest must live next to the backed-up folders so it survives an editor restart.");
            FileAssert.Exists(manifestPath);
        }

        [Test]
        public void Manifest_RoundTrips_SourceAndBackupPaths()
        {
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(_backupRoot, NewEntries());

            List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry> read =
                CoreAIWebGlStreamingAssetsGuard.ReadManifestFile(_backupRoot);

            Assert.AreEqual(2, read.Count);
            Assert.AreEqual(Path.Combine("Assets", "StreamingAssets", "LlamaLib"), read[0].sourceAbsolutePath);
            Assert.AreEqual(Path.Combine(_backupRoot, "LlamaLib"), read[0].backupAbsolutePath);
            Assert.AreEqual(Path.Combine("Assets", "StreamingAssets", "LLMUnity"), read[1].sourceAbsolutePath);
        }

        [Test]
        public void Manifest_RewrittenAfterEachMove_KeepsOnlyTheLatestSet()
        {
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(_backupRoot, NewEntries());
            CoreAIWebGlStreamingAssetsGuard.WriteManifestFile(
                _backupRoot,
                new List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry>());

            Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ReadManifestFile(_backupRoot).Count);
        }

        [Test]
        public void ReadManifestFile_WhenMissing_ReturnsEmptyInsteadOfThrowing()
        {
            Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ReadManifestFile(_backupRoot).Count);
        }

        [Test]
        public void ParseManifest_MalformedPayload_ReturnsEmptyList()
        {
            LogAssert.ignoreFailingMessages = true;
            try
            {
                Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ParseManifest("not json at all").Count);
                Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ParseManifest("{}").Count);
                Assert.AreEqual(0, CoreAIWebGlStreamingAssetsGuard.ParseManifest("").Count);
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        [TestCase("LlamaLib", true)]
        [TestCase("llamalib-win-cuda", true)]
        [TestCase("LLMUnity", true)]
        [TestCase("LLMUnityBuild", true)]
        [TestCase("MyGameData", false)]
        [TestCase("", false)]
        [TestCase(null, false)]
        public void ShouldGuardFolder_MatchesOnlyLlmFolders(string folderName, bool expected)
        {
            Assert.AreEqual(expected, CoreAIWebGlStreamingAssetsGuard.ShouldGuardFolder(folderName));
        }

        [TestCase(BuildTarget.StandaloneWindows64, true)]
        [TestCase(BuildTarget.StandaloneOSX, true)]
        [TestCase(BuildTarget.StandaloneLinux64, true)]
        [TestCase(BuildTarget.Android, true)]
        [TestCase(BuildTarget.iOS, true)]
        [TestCase(BuildTarget.WebGL, false)]
        public void LocalModelBuildTargetSupport_MatchesNativeLibraryTargets(BuildTarget target, bool expected)
        {
            Assert.AreEqual(expected, CoreAIWebGlStreamingAssetsGuard.IsLocalModelBuildTargetSupported(target));
        }

#if COREAI_HAS_LLMUNITY
        [Test]
        public void UnsupportedComposition_RemovesLlmUnityComponentsFromStagedSceneRoots()
        {
            GameObject root = new("CoreAI_LLMUnity_Strip_Test");
            try
            {
                root.AddComponent<LLMUnity.LLM>();
                root.AddComponent<LLMUnity.LLMAgent>();
                CoreAiWebGlLlmUnitySceneGuard nonLlmComponent =
                    root.AddComponent<CoreAiWebGlLlmUnitySceneGuard>();

                int removed = CoreAIWebGlStreamingAssetsGuard.RemoveLlmUnityComponentsForTarget(
                    BuildTarget.WebGL,
                    new[] { root });

                Assert.AreEqual(2, removed);
                Assert.IsNull(root.GetComponent<LLMUnity.LLM>());
                Assert.IsNull(root.GetComponent<LLMUnity.LLMAgent>());
                Assert.AreSame(nonLlmComponent, root.GetComponent<CoreAiWebGlLlmUnitySceneGuard>(),
                    "Scene staging must preserve non-LLMUnity components on the same GameObject.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SupportedComposition_DoesNotRemoveLlmUnityComponents()
        {
            GameObject root = new("CoreAI_LLMUnity_Supported_Target_Test");
            try
            {
                LLMUnity.LLM llm = root.AddComponent<LLMUnity.LLM>();
                LLMUnity.LLMAgent agent = root.AddComponent<LLMUnity.LLMAgent>();

                int removed = CoreAIWebGlStreamingAssetsGuard.RemoveLlmUnityComponentsForTarget(
                    BuildTarget.StandaloneWindows64,
                    new[] { root });

                Assert.AreEqual(0, removed);
                Assert.AreSame(llm, root.GetComponent<LLMUnity.LLM>());
                Assert.AreSame(agent, root.GetComponent<LLMUnity.LLMAgent>());
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void UnsupportedRuntimeGuard_DisablesOnlyLlmUnityBehaviours()
        {
            GameObject root = new("CoreAI_LLMUnity_Runtime_Guard_Test");
            root.SetActive(false);
            try
            {
                LLMUnity.LLM llm = root.AddComponent<LLMUnity.LLM>();
                LLMUnity.LLMAgent agent = root.AddComponent<LLMUnity.LLMAgent>();
                CoreAILifetimeScope nonLlmComponent = root.AddComponent<CoreAILifetimeScope>();

                int disabled = CoreAiWebGlLlmUnitySceneGuard.DisableLlmUnityBehaviours(
                    new MonoBehaviour[] { llm, agent, nonLlmComponent });

                Assert.AreEqual(2, disabled);
                Assert.IsFalse(llm.enabled);
                Assert.IsFalse(agent.enabled);
                Assert.IsTrue(nonLlmComponent.enabled,
                    "Runtime containment must not disable unrelated behaviours on the same host.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void UnsupportedBuild_DisablesAndVerifiesInitializerOnlyInReportedStagingRoot()
        {
            string sourcePath = typeof(LLMUnity.LLM).Assembly.Location;
            string stagingRoot = Path.Combine(_backupRoot, "Temp", "StagingArea", "Data", "Managed");
            Directory.CreateDirectory(stagingRoot);
            string stagedPath = Path.Combine(stagingRoot, "undream.llmunity.Runtime.dll");
            File.Copy(sourcePath, stagedPath, true);
            ShapeEditorAssemblyAsPlayerInitializer(stagedPath);

            int patched = CoreAIWebGlStreamingAssetsGuard.DisableLlmUnityRuntimeForUnsupportedBuild(
                BuildTarget.WebGL,
                new[] { stagedPath },
                _backupRoot);

            Assert.AreEqual(1, patched);
            Assert.IsTrue(CoreAIWebGlStreamingAssetsGuard.IsLlmUnityRuntimeInitializerDisabled(stagedPath));
            Assert.AreEqual(1, CoreAIWebGlStreamingAssetsGuard.DisableLlmUnityRuntimeForUnsupportedBuild(
                BuildTarget.WebGL,
                new[] { stagedPath },
                _backupRoot));
            Assert.IsTrue(CoreAIWebGlStreamingAssetsGuard.IsLlmUnityRuntimeInitializerDisabled(stagedPath));
        }

        [Test]
        public void UnsupportedBuild_EditorInitializerShapeFailsClosedWithoutMutation()
        {
            string sourcePath = typeof(LLMUnity.LLM).Assembly.Location;
            string stagingRoot = Path.Combine(_backupRoot, "Temp", "StagingArea", "Data", "Managed");
            Directory.CreateDirectory(stagingRoot);
            string stagedPath = Path.Combine(stagingRoot, "undream.llmunity.Runtime.dll");
            File.Copy(sourcePath, stagedPath, true);
            byte[] before = File.ReadAllBytes(stagedPath);

            Assert.Throws<MissingMethodException>(() =>
                CoreAIWebGlStreamingAssetsGuard.DisableLlmUnityRuntimeForUnsupportedBuild(
                    BuildTarget.WebGL,
                    new[] { stagedPath },
                    _backupRoot));

            CollectionAssert.AreEqual(before, File.ReadAllBytes(stagedPath),
                "The async Editor initializer must not be rewritten as a void player initializer.");
        }

        [Test]
        public void UnsupportedBuild_WhenReportedStagedAssemblyIsMissing_FailsClosed()
        {
            BuildFailedException error = Assert.Throws<BuildFailedException>(() =>
                CoreAIWebGlStreamingAssetsGuard.DisableLlmUnityRuntimeForUnsupportedBuild(
                    BuildTarget.WebGL,
                    Array.Empty<string>(),
                    _backupRoot));

            StringAssert.Contains("no staged undream.llmunity.Runtime.dll", error.Message);
        }

        [TestCase("Assets/ThirdParty")]
        [TestCase("Packages/ai.undream.llm/Runtime")]
        [TestCase("Library/PackageCache/ai.undream.llm@source/Runtime")]
        [TestCase("Library/ScriptAssemblies")]
        [TestCase("Library/Bee/PlayerScriptAssemblies")]
        [TestCase("Library/Bee/artifacts/2000b0aE.dag")]
        [TestCase("Library/Bee/artifacts/WebGL/ManagedStripped")]
        public void UnsupportedBuild_RejectsReportedSourceEditorPackageAndCacheAssemblies(string relativeDirectory)
        {
            string sourcePath = typeof(LLMUnity.LLM).Assembly.Location;
            string rejectedDirectory = Path.Combine(
                _backupRoot,
                relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(rejectedDirectory);
            string rejectedPath = Path.Combine(rejectedDirectory, "undream.llmunity.Runtime.dll");
            File.Copy(sourcePath, rejectedPath, true);
            byte[] before = File.ReadAllBytes(rejectedPath);

            Assert.Throws<BuildFailedException>(() =>
                CoreAIWebGlStreamingAssetsGuard.DisableLlmUnityRuntimeForUnsupportedBuild(
                    BuildTarget.WebGL,
                    new[] { rejectedPath },
                    _backupRoot));

            CollectionAssert.AreEqual(before, File.ReadAllBytes(rejectedPath),
                "A reported source/editor/package/cache assembly must never be rewritten.");
        }

        [Test]
        public void SupportedBuild_DoesNotPatchReportedStagedAssembly()
        {
            string sourcePath = typeof(LLMUnity.LLM).Assembly.Location;
            string stagingRoot = Path.Combine(_backupRoot, "Temp", "StagingArea", "Data", "Managed");
            Directory.CreateDirectory(stagingRoot);
            string stagedPath = Path.Combine(stagingRoot, "undream.llmunity.Runtime.dll");
            File.Copy(sourcePath, stagedPath, true);
            byte[] before = File.ReadAllBytes(stagedPath);

            int patched = CoreAIWebGlStreamingAssetsGuard.DisableLlmUnityRuntimeForUnsupportedBuild(
                BuildTarget.StandaloneWindows64,
                new[] { stagedPath },
                _backupRoot);

            Assert.AreEqual(0, patched);
            CollectionAssert.AreEqual(before, File.ReadAllBytes(stagedPath));
        }
#endif

#if COREAI_HAS_LLMUNITY
        private static void ShapeEditorAssemblyAsPlayerInitializer(string assemblyPath)
        {
            Assembly cecilAssembly = ResolveCecilAssembly();
            Type assemblyDefinitionType = cecilAssembly.GetType("Mono.Cecil.AssemblyDefinition", true);
            MethodInfo readAssembly = assemblyDefinitionType.GetMethod(
                "ReadAssembly",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null) ?? throw new MissingMethodException(assemblyDefinitionType.FullName, "ReadAssembly(string)");
            object definition = readAssembly.Invoke(null, new object[] { Path.GetFullPath(assemblyPath) });
            string temporaryPath = assemblyPath + ".player-fixture";
            try
            {
                object module = GetRequiredProperty(definition, "MainModule");
                IEnumerable types = (IEnumerable)GetRequiredProperty(module, "Types");
                object editorInitializer = null;
                object commonInitializer = null;
                foreach (object type in types)
                {
                    string fullName = GetRequiredProperty(type, "FullName") as string;
                    if (!string.Equals(fullName, "LLMUnity.LLMUnitySetup", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    IEnumerable methods = (IEnumerable)GetRequiredProperty(type, "Methods");
                    foreach (object method in methods)
                    {
                        string name = GetRequiredProperty(method, "Name") as string;
                        object returnType = GetRequiredProperty(method, "ReturnType");
                        string returnTypeName = GetRequiredProperty(returnType, "FullName") as string;
                        if (string.Equals(name, "InitializeOnLoad", StringComparison.Ordinal)
                            && string.Equals(returnTypeName, "System.Threading.Tasks.Task", StringComparison.Ordinal))
                        {
                            editorInitializer = method;
                        }
                        else if (string.Equals(name, "InitializeOnLoadCommon", StringComparison.Ordinal)
                                 && string.Equals(returnTypeName, "System.Void", StringComparison.Ordinal))
                        {
                            commonInitializer = method;
                        }
                    }
                }

                Assert.IsNotNull(editorInitializer, "The source fixture must expose the current async Editor initializer.");
                Assert.IsNotNull(commonInitializer, "The source fixture must expose a void player-shaped initializer body.");
                SetRequiredProperty(editorInitializer, "Name", "InitializeOnLoadEditorOnly");
                SetRequiredProperty(commonInitializer, "Name", "InitializeOnLoad");

                MethodInfo write = assemblyDefinitionType.GetMethod(
                    "Write",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { typeof(string) },
                    null) ?? throw new MissingMethodException(assemblyDefinitionType.FullName, "Write(string)");
                write.Invoke(definition, new object[] { temporaryPath });
            }
            finally
            {
                if (definition is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            try
            {
                File.Copy(temporaryPath, assemblyPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }

        private static Assembly ResolveCecilAssembly()
        {
            Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();
            foreach (Assembly assembly in loaded)
            {
                if (string.Equals(assembly.GetName().Name, "Unity.Cecil", StringComparison.Ordinal))
                {
                    return assembly;
                }
            }

            return Assembly.Load("Unity.Cecil");
        }

        private static object GetRequiredProperty(object target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
            return property.GetValue(target);
        }

        private static void SetRequiredProperty(object target, string propertyName, object value)
        {
            PropertyInfo property = target.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance)
                ?? throw new MissingMemberException(target.GetType().FullName, propertyName);
            property.SetValue(target, value);
        }
#endif

        private List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry> NewEntries()
        {
            return new List<CoreAIWebGlStreamingAssetsGuard.MovedFolderEntry>
            {
                new()
                {
                    sourceAbsolutePath = Path.Combine("Assets", "StreamingAssets", "LlamaLib"),
                    backupAbsolutePath = Path.Combine(_backupRoot, "LlamaLib")
                },
                new()
                {
                    sourceAbsolutePath = Path.Combine("Assets", "StreamingAssets", "LLMUnity"),
                    backupAbsolutePath = Path.Combine(_backupRoot, "LLMUnity")
                }
            };
        }
    }

    /// <summary>
    /// EditMode coverage for the deterministic G11 WebGL build request and its guarded output cleanup.
    /// </summary>
    [TestFixture]
    public sealed class CoreAIG11WebGlBuildEditModeTests
    {
        private string _projectRoot;

        [SetUp]
        public void SetUp()
        {
            _projectRoot = Path.Combine(
                Path.GetTempPath(), "CoreAiG11WebGlBuildTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_projectRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_projectRoot))
            {
                Directory.Delete(_projectRoot, true);
            }
        }

        [Test]
        public void FrozenScenes_HaveTheRequiredOrderAndCannotBeMutatedByCaller()
        {
            string[] expected =
            {
                "Assets/CoreAI.Demos/FullAccess/FullAccessDemo.unity",
                "Assets/CoreAI.Demos/Hub/CoreAiHubDemo.unity",
                "Assets/CoreAI.Demos/LiveMechanics/LiveMechanicsDemo.unity",
                "Assets/CoreAI.Demos/LiveMechanicsMods/LiveMechanicsModsChatDemo.unity",
                "Assets/CoreAI.Demos/LiveMechanicsMods/WaveAutoBattlerModsDemo.unity",
                "Assets/CoreAI.Demos/LuaMods/LuaModsDemo.unity",
                "Assets/CoreAI.Demos/MiniRpg/MiniRpgModsDemo.unity",
                "Assets/CoreAI.Demos/ModdableUnits/ModdableUnitsDemo.unity",
                "Assets/CoreAI.Demos/MultiplayerFoundation/MultiplayerFoundationDemo.unity",
                "Assets/CoreAI.Demos/ProceduralMaterials/ProceduralMaterialsShowcase.unity",
                "Assets/CoreAI.Demos/QwenDemo/QwenGenieDemo.unity",
                "Assets/CoreAI.Demos/QwenDemo/QwenSpellcraftDemo.unity",
                "Assets/CoreAI.Demos/Skills/SkillsDemo.unity",
                "Assets/CoreAI.Demos/WorldCommands/WorldCommandsDemo.unity",
                "Assets/CoreAiUnity/Scenes/CoreAiChatDemo.unity"
            };
            string[] firstRead = CoreAIG11WebGlBuild.GetFrozenScenePaths();

            CollectionAssert.AreEqual(expected, firstRead);
            firstRead[0] = "mutated";
            CollectionAssert.AreEqual(expected, CoreAIG11WebGlBuild.GetFrozenScenePaths());
        }

        [Test]
        public void BuildOptions_AreExplicitReleaseWebGlSettings()
        {
            string outputPath = CoreAIG11WebGlBuild.GetOutputPath(_projectRoot);
            BuildPlayerOptions options = CoreAIG11WebGlBuild.CreateBuildPlayerOptions(outputPath);

            Assert.AreEqual(outputPath, options.locationPathName);
            Assert.AreEqual(BuildTarget.WebGL, options.target);
            Assert.AreEqual(BuildTargetGroup.WebGL, options.targetGroup);
            Assert.AreEqual(BuildOptions.CleanBuildCache | BuildOptions.StrictMode, options.options);
            CollectionAssert.AreEqual(CoreAIG11WebGlBuild.GetFrozenScenePaths(), options.scenes);
        }

        [Test]
        public void PrepareOutputDirectory_RemovesStaleFilesAndRejectsOtherPaths()
        {
            string outputPath = CoreAIG11WebGlBuild.GetOutputPath(_projectRoot);
            Directory.CreateDirectory(outputPath);
            string stalePath = Path.Combine(outputPath, "stale.txt");
            File.WriteAllText(stalePath, "stale");

            CoreAIG11WebGlBuild.PrepareOutputDirectory(_projectRoot, outputPath);

            DirectoryAssert.Exists(outputPath);
            FileAssert.DoesNotExist(stalePath);
            Assert.Throws<BuildFailedException>(() => CoreAIG11WebGlBuild.PrepareOutputDirectory(
                _projectRoot,
                Path.Combine(_projectRoot, "outside")));
        }
    }
}
