using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace CoreAI.Editor
{
    /// <summary>
    /// Temporarily excludes heavy LLMUnity-related StreamingAssets folders from WebGL builds,
    /// then restores them after build completion.
    /// </summary>
    /// <remarks>
    /// <see cref="callbackOrder"/> must run <b>after</b> LLMUnity's <c>LLMBuildProcessor</c> (default ~0),
    /// which calls <c>Directory.GetDirectories</c> on <c>StreamingAssets/LlamaLib*</c>. If we move those
    /// folders first, WebGL preprocess fails with <see cref="DirectoryNotFoundException"/>.
    /// </remarks>
    internal sealed class CoreAIWebGlStreamingAssetsGuard : IPreprocessBuildWithReport, IPostprocessBuildWithReport,
        IProcessSceneWithReport, IPostBuildPlayerScriptDLLs
    {
        private const string SessionStateKey = "CoreAI.WebGlStreamingAssetsGuard.Manifest";
        private const string LlmUnityAssemblyName = "undream.llmunity.Runtime";
        private const string LlmUnityAssemblyFileName = LlmUnityAssemblyName + ".dll";
        private const string LlmUnitySetupTypeName = "LLMUnity.LLMUnitySetup";
        private const string LlmUnityInitializerMethodName = "InitializeOnLoad";

        /// <summary>File name of the on-disk restore manifest, stored inside the backup root itself.</summary>
        internal const string ManifestFileName = "manifest.json";

        /// <summary>
        /// Late preprocess: after undream/LLMUnity (and similar) have consumed StreamingAssets.
        /// </summary>
        private const int LateBuildCallbackOrder = 100_000;

        // Common folder prefixes produced by local LLM/LLMUnity setups.
        private static readonly string[] GuardedFolderPrefixes =
        {
            "LlamaLib",
            "LLMUnity",
            "LLMUnityBuild"
        };

        public int callbackOrder => LateBuildCallbackOrder;

        [InitializeOnLoadMethod]
        private static void Initialize()
        {
            // WHY: a build that crashed or was cancelled never reaches OnPostprocessBuild, so the moved
            // folders survive only as a manifest. Restore on domain load and, as a second net, when the
            // editor is closing — otherwise the next Library wipe deletes the user's native binaries.
            EditorApplication.quitting -= RestoreOnEditorQuitting;
            EditorApplication.quitting += RestoreOnEditorQuitting;

            if (BuildPipeline.isBuildingPlayer)
            {
                return;
            }

            RestoreMovedFoldersIfAny(true);
        }

        private static void RestoreOnEditorQuitting()
        {
            // WHY: no AssetDatabase.Refresh during shutdown — the folders only need to be back on disk.
            RestoreMovedFoldersIfAny(false);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
            {
                return;
            }

            string streamingAssetsAbsolute = Path.Combine(Application.dataPath, "StreamingAssets");
            string backupRoot = GetBackupRoot();

            // WHY: entries left over from an interrupted build are still parked in the backup root; carry
            // them into the new manifest instead of overwriting (and thereby orphaning) them.
            List<MovedFolderEntry> moved = LoadPendingEntries(backupRoot);
            int carriedOver = moved.Count;

            if (!Directory.Exists(streamingAssetsAbsolute))
            {
                if (carriedOver == 0)
                {
                    EraseManifest(backupRoot);
                }

                return;
            }

            Directory.CreateDirectory(backupRoot);

            try
            {
                string[] subDirectories =
                    Directory.GetDirectories(streamingAssetsAbsolute, "*", SearchOption.TopDirectoryOnly);
                foreach (string sourceAbs in subDirectories)
                {
                    string folderName = Path.GetFileName(sourceAbs);
                    if (!ShouldGuardFolder(folderName))
                    {
                        continue;
                    }

                    string backupAbs = Path.Combine(backupRoot, folderName);
                    if (Directory.Exists(backupAbs))
                    {
                        Directory.Delete(backupAbs, true);
                    }

                    Directory.Move(sourceAbs, backupAbs);
                    MoveMetaIfExists(sourceAbs, backupAbs);

                    moved.RemoveAll(e =>
                        string.Equals(e.backupAbsolutePath, backupAbs, StringComparison.OrdinalIgnoreCase));
                    moved.Add(new MovedFolderEntry
                    {
                        sourceAbsolutePath = sourceAbs,
                        backupAbsolutePath = backupAbs
                    });

                    // WHY: persist after EVERY move. A manifest written only after the loop means an
                    // IOException halfway through leaves already-moved folders with no record of where
                    // they went, and Library/ is a directory users delete freely.
                    PersistManifest(backupRoot, moved);
                }
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning(
                    $"StreamingAssets guard: excluding folders failed ({ex.Message}); restoring what was moved.");
                RestoreMovedFoldersIfAny(true);
                throw;
            }

            if (moved.Count == 0)
            {
                EraseManifest(backupRoot);
                return;
            }

            int newlyMoved = moved.Count - carriedOver;
            if (newlyMoved <= 0)
            {
                return;
            }

            AssetDatabase.Refresh();

            CoreAIEditorLog.Log(
                $"WebGL build: temporarily excluded {newlyMoved} StreamingAssets folder(s) from build output.");
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.WebGL)
            {
                return;
            }

            if (report.summary.result != BuildResult.Succeeded)
            {
                CoreAIEditorLog.LogWarning(
                    $"WebGL build finished with result '{report.summary.result}'; restoring excluded StreamingAssets folders.");
            }

            RestoreMovedFoldersIfAny(true);
        }

        public void OnProcessScene(Scene scene, BuildReport report)
        {
            if (report == null || IsLocalModelBuildTargetSupported(report.summary.platform))
            {
                return;
            }

            int removed = RemoveLlmUnityComponentsForTarget(
                report.summary.platform,
                scene.GetRootGameObjects());
            if (removed > 0)
            {
                CoreAIEditorLog.Log(
                    $"{report.summary.platform} build: excluded {removed} LLMUnity scene component(s).");
            }
        }

        public void OnPostBuildPlayerScriptDLLs(BuildReport report)
        {
            if (report == null || IsLocalModelBuildTargetSupported(report.summary.platform))
            {
                return;
            }

#if COREAI_HAS_LLMUNITY
            List<string> reportedPaths = new();
            foreach (BuildFile file in report.GetFiles())
            {
                if (!string.IsNullOrWhiteSpace(file.path))
                {
                    reportedPaths.Add(file.path);
                }
            }

            try
            {
                string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
                int verifiedAssemblies = DisableLlmUnityRuntimeForUnsupportedBuild(
                    report.summary.platform,
                    reportedPaths,
                    projectRoot);
                CoreAIEditorLog.Log(
                    $"{report.summary.platform} build: disabled LLMUnity native runtime initialization " +
                    $"in {verifiedAssemblies} staged player assembly file(s).");
            }
            catch (BuildFailedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new BuildFailedException(
                    $"CoreAI could not disable LLMUnity for unsupported target {report.summary.platform}: " +
                    ex.Message);
            }
#endif
        }

        internal static bool IsLocalModelBuildTargetSupported(BuildTarget target)
        {
            switch (target)
            {
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                case BuildTarget.StandaloneOSX:
                case BuildTarget.StandaloneLinux64:
                case BuildTarget.Android:
                case BuildTarget.iOS:
                case BuildTarget.VisionOS:
                    return true;
                default:
                    return false;
            }
        }

        internal static int RemoveLlmUnityComponentsForTarget(BuildTarget target, GameObject[] roots)
        {
            return IsLocalModelBuildTargetSupported(target) ? 0 : RemoveLlmUnityComponents(roots);
        }

        internal static int RemoveLlmUnityComponents(GameObject[] roots)
        {
            if (roots == null || roots.Length == 0)
            {
                return 0;
            }

            List<MonoBehaviour> remove = new();
            foreach (GameObject root in roots)
            {
                if (root == null)
                {
                    continue;
                }

                MonoBehaviour[] components = root.GetComponentsInChildren<MonoBehaviour>(true);
                foreach (MonoBehaviour component in components)
                {
                    if (component != null && string.Equals(
                            component.GetType().Assembly.GetName().Name,
                            LlmUnityAssemblyName,
                            StringComparison.Ordinal))
                    {
                        remove.Add(component);
                    }
                }
            }

            foreach (MonoBehaviour component in remove)
            {
                UnityEngine.Object.DestroyImmediate(component);
            }

            return remove.Count;
        }

        internal static int DisableLlmUnityRuntimeForUnsupportedBuild(
            BuildTarget target,
            IEnumerable<string> reportedPaths,
            string projectRoot)
        {
            if (IsLocalModelBuildTargetSupported(target))
            {
                return 0;
            }

            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                throw new BuildFailedException("CoreAI could not resolve the project root for LLMUnity staging.");
            }

            string fullProjectRoot = Path.GetFullPath(projectRoot);
            HashSet<string> visited = new(GetPathComparer());
            List<StagedAssembly> stagedAssemblies = new();
            if (reportedPaths != null)
            {
                foreach (string reportedPath in reportedPaths)
                {
                    if (string.IsNullOrWhiteSpace(reportedPath) ||
                        !string.Equals(Path.GetFileName(reportedPath), LlmUnityAssemblyFileName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    string fullPath = Path.GetFullPath(
                        Path.IsPathRooted(reportedPath)
                            ? reportedPath
                            : Path.Combine(fullProjectRoot, reportedPath));
                    if (!TryGetVerifiedBuildStagingRoot(fullPath, fullProjectRoot, target, out string stagingRoot) ||
                        !File.Exists(fullPath) ||
                        !visited.Add(fullPath))
                    {
                        continue;
                    }

                    stagedAssemblies.Add(new StagedAssembly(fullPath, stagingRoot));
                }
            }

            if (stagedAssemblies.Count == 0)
            {
                throw new BuildFailedException(
                    $"CoreAI found no staged {LlmUnityAssemblyFileName} for unsupported target {target}. " +
                    "The build is stopped because patching an editor, package, source, or cache assembly is unsafe.");
            }

            foreach (StagedAssembly stagedAssembly in stagedAssemblies)
            {
                PatchLlmUnityRuntimeInitializer(stagedAssembly.AssemblyPath, stagedAssembly.StagingRoot);
                if (!IsLlmUnityRuntimeInitializerDisabled(stagedAssembly.AssemblyPath))
                {
                    throw new BuildFailedException(
                        $"CoreAI patched '{stagedAssembly.AssemblyPath}', but the staged initializer did not " +
                        "re-read as a return-only method.");
                }
            }

            return stagedAssemblies.Count;
        }

        private static bool PatchLlmUnityRuntimeInitializer(string assemblyPath, string verifiedBuildStagingRoot)
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
            {
                throw new ArgumentException("A staged LLMUnity assembly path is required.", nameof(assemblyPath));
            }

            string fullPath = Path.GetFullPath(assemblyPath);
            string fullStagingRoot = string.IsNullOrWhiteSpace(verifiedBuildStagingRoot)
                ? string.Empty
                : Path.GetFullPath(verifiedBuildStagingRoot);
            if (!string.Equals(Path.GetFileName(fullPath), LlmUnityAssemblyFileName,
                    StringComparison.OrdinalIgnoreCase) ||
                !IsPathInside(fullPath, fullStagingRoot) ||
                !File.Exists(fullPath))
            {
                throw new FileNotFoundException(
                    "The verified staged LLMUnity player assembly was not found inside the build staging root.",
                    fullPath);
            }

            Assembly cecilAssembly = ResolveCecilAssembly();
            Type assemblyDefinitionType = cecilAssembly.GetType("Mono.Cecil.AssemblyDefinition", true);
            MethodInfo readAssembly = assemblyDefinitionType.GetMethod(
                "ReadAssembly",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string) },
                null) ?? throw new MissingMethodException(assemblyDefinitionType.FullName, "ReadAssembly(string)");
            object definition = readAssembly.Invoke(null, new object[] { fullPath });
            string temporaryPath = fullPath + ".coreai.tmp";
            bool changed = false;

            try
            {
                object method = FindCecilMethod(
                    definition,
                    LlmUnitySetupTypeName,
                    LlmUnityInitializerMethodName);
                if (method == null)
                {
                    throw new MissingMethodException(
                        LlmUnitySetupTypeName,
                        LlmUnityInitializerMethodName);
                }

                changed = ReplaceCecilMethodBodyWithReturn(cecilAssembly, method);
                if (changed)
                {
                    MethodInfo write = assemblyDefinitionType.GetMethod(
                        "Write",
                        BindingFlags.Public | BindingFlags.Instance,
                        null,
                        new[] { typeof(string) },
                        null) ?? throw new MissingMethodException(assemblyDefinitionType.FullName, "Write(string)");
                    write.Invoke(definition, new object[] { temporaryPath });
                }
            }
            catch
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }

                throw;
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
                if (changed)
                {
                    File.Copy(temporaryPath, fullPath, true);
                }
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }

            if (!IsLlmUnityRuntimeInitializerDisabled(fullPath))
            {
                throw new InvalidDataException(
                    "The staged LLMUnity initializer did not re-read as a return-only method after patching.");
            }

            return changed;
        }

        private static bool TryGetVerifiedBuildStagingRoot(
            string assemblyPath,
            string projectRoot,
            BuildTarget target,
            out string stagingRoot)
        {
            string[] allowedRoots =
            {
                Path.Combine(projectRoot, "Temp", "StagingArea", "Data", "Managed")
            };

            foreach (string allowedRoot in allowedRoots)
            {
                string fullAllowedRoot = Path.GetFullPath(allowedRoot);
                if (IsPathInside(assemblyPath, fullAllowedRoot))
                {
                    stagingRoot = fullAllowedRoot;
                    return true;
                }
            }

            stagingRoot = string.Empty;
            return false;
        }

        private static bool IsPathInside(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
            {
                return false;
            }

            string fullPath = Path.GetFullPath(path);
            string fullRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string prefix = fullRoot + Path.DirectorySeparatorChar;
            return fullPath.StartsWith(prefix, GetPathComparison());
        }

        private static StringComparer GetPathComparer()
        {
            return Path.DirectorySeparatorChar == '\\'
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        private static StringComparison GetPathComparison()
        {
            return Path.DirectorySeparatorChar == '\\'
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
        }

        internal static bool IsLlmUnityRuntimeInitializerDisabled(string assemblyPath)
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
            try
            {
                object method = FindCecilMethod(
                    definition,
                    LlmUnitySetupTypeName,
                    LlmUnityInitializerMethodName);
                return method != null && CecilMethodBodyIsReturnOnly(method);
            }
            finally
            {
                if (definition is IDisposable disposable)
                {
                    disposable.Dispose();
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

        private static object FindCecilMethod(object definition, string typeFullName, string methodName)
        {
            object module = GetRequiredProperty(definition, "MainModule");
            IEnumerable types = (IEnumerable)GetRequiredProperty(module, "Types");
            foreach (object type in types)
            {
                string fullName = GetRequiredProperty(type, "FullName") as string;
                if (!string.Equals(fullName, typeFullName, StringComparison.Ordinal))
                {
                    continue;
                }

                IEnumerable methods = (IEnumerable)GetRequiredProperty(type, "Methods");
                foreach (object method in methods)
                {
                    string name = GetRequiredProperty(method, "Name") as string;
                    if (!string.Equals(name, methodName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    IList parameters = (IList)GetRequiredProperty(method, "Parameters");
                    object returnType = GetRequiredProperty(method, "ReturnType");
                    string returnTypeName = GetRequiredProperty(returnType, "FullName") as string;
                    if (parameters.Count == 0 && string.Equals(returnTypeName, "System.Void", StringComparison.Ordinal))
                    {
                        return method;
                    }
                }
            }

            return null;
        }

        private static bool ReplaceCecilMethodBodyWithReturn(Assembly cecilAssembly, object method)
        {
            if (CecilMethodBodyIsReturnOnly(method))
            {
                return false;
            }

            object body = GetRequiredProperty(method, "Body");
            IList instructions = (IList)GetRequiredProperty(body, "Instructions");
            IList variables = (IList)GetRequiredProperty(body, "Variables");
            IList exceptionHandlers = (IList)GetRequiredProperty(body, "ExceptionHandlers");
            instructions.Clear();
            variables.Clear();
            exceptionHandlers.Clear();

            PropertyInfo initLocals = body.GetType().GetProperty("InitLocals", BindingFlags.Public | BindingFlags.Instance)
                                      ?? throw new MissingMemberException(body.GetType().FullName, "InitLocals");
            initLocals.SetValue(body, false);

            MethodInfo getProcessor = body.GetType().GetMethod(
                "GetILProcessor",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                Type.EmptyTypes,
                null) ?? throw new MissingMethodException(body.GetType().FullName, "GetILProcessor()");
            object processor = getProcessor.Invoke(body, null);
            Type opCodeType = cecilAssembly.GetType("Mono.Cecil.Cil.OpCode", true);
            Type opCodesType = cecilAssembly.GetType("Mono.Cecil.Cil.OpCodes", true);
            object returnOpCode = opCodesType.GetField("Ret", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                                  ?? throw new MissingFieldException(opCodesType.FullName, "Ret");
            MethodInfo create = processor.GetType().GetMethod(
                "Create",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { opCodeType },
                null) ?? throw new MissingMethodException(processor.GetType().FullName, "Create(OpCode)");
            object returnInstruction = create.Invoke(processor, new[] { returnOpCode });
            Type instructionType = cecilAssembly.GetType("Mono.Cecil.Cil.Instruction", true);
            MethodInfo append = processor.GetType().GetMethod(
                "Append",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { instructionType },
                null) ?? throw new MissingMethodException(processor.GetType().FullName, "Append(Instruction)");
            append.Invoke(processor, new[] { returnInstruction });
            return true;
        }

        private static bool CecilMethodBodyIsReturnOnly(object method)
        {
            object body = GetRequiredProperty(method, "Body");
            IList instructions = (IList)GetRequiredProperty(body, "Instructions");
            if (instructions.Count != 1)
            {
                return false;
            }

            object opCode = GetRequiredProperty(instructions[0], "OpCode");
            string name = GetRequiredProperty(opCode, "Name") as string;
            return string.Equals(name, "ret", StringComparison.Ordinal);
        }

        private static object GetRequiredProperty(object instance, string propertyName)
        {
            PropertyInfo property = instance.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance)
                                    ?? throw new MissingMemberException(instance.GetType().FullName, propertyName);
            return property.GetValue(instance);
        }

        private static void RestoreMovedFoldersIfAny(bool refreshAssetDatabase)
        {
            string backupRoot = GetBackupRoot();
            List<MovedFolderEntry> entries = LoadManifest(backupRoot);
            if (entries.Count == 0)
            {
                EraseManifest(backupRoot);
                return;
            }

            List<MovedFolderEntry> remaining = new();
            int restored = 0;
            foreach (MovedFolderEntry entry in entries)
            {
                if (string.IsNullOrWhiteSpace(entry.sourceAbsolutePath) ||
                    string.IsNullOrWhiteSpace(entry.backupAbsolutePath))
                {
                    continue;
                }

                if (!Directory.Exists(entry.backupAbsolutePath))
                {
                    continue;
                }

                if (Directory.Exists(entry.sourceAbsolutePath))
                {
                    CoreAIEditorLog.LogWarning(
                        $"StreamingAssets guard restore skipped: destination already exists ({entry.sourceAbsolutePath}).");
                    remaining.Add(entry);
                    continue;
                }

                try
                {
                    Directory.Move(entry.backupAbsolutePath, entry.sourceAbsolutePath);
                    MoveMetaIfExists(entry.backupAbsolutePath, entry.sourceAbsolutePath);
                    restored++;
                }
                catch (Exception ex)
                {
                    // WHY: keep the entry in the manifest so the next domain load / editor quit retries it.
                    CoreAIEditorLog.LogWarning(
                        $"StreamingAssets guard restore failed for '{entry.backupAbsolutePath}': {ex.Message}");
                    remaining.Add(entry);
                }
            }

            if (remaining.Count == 0)
            {
                EraseManifest(backupRoot);
            }
            else
            {
                PersistManifest(backupRoot, remaining);
            }

            if (restored == 0)
            {
                return;
            }

            if (refreshAssetDatabase)
            {
                AssetDatabase.Refresh();
            }

            CoreAIEditorLog.Log($"WebGL build: restored {restored} excluded StreamingAssets folder(s).");
        }

        /// <summary>Manifest entries whose backup folder is still parked in the backup root.</summary>
        private static List<MovedFolderEntry> LoadPendingEntries(string backupRoot)
        {
            List<MovedFolderEntry> pending = new();
            foreach (MovedFolderEntry entry in LoadManifest(backupRoot))
            {
                if (!string.IsNullOrWhiteSpace(entry.backupAbsolutePath) &&
                    Directory.Exists(entry.backupAbsolutePath))
                {
                    pending.Add(entry);
                }
            }

            return pending;
        }

        private static string GetBackupRoot()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName ?? string.Empty;
            return Path.Combine(projectRoot, "Library", "CoreAI", "WebGlBuildBackup");
        }

        /// <summary>Absolute path of the restore manifest for the given backup root.</summary>
        internal static string GetManifestPath(string backupRoot)
        {
            return Path.Combine(backupRoot, ManifestFileName);
        }

        /// <summary>
        /// Writes the restore manifest next to the backed-up folders.
        /// </summary>
        /// <remarks>
        /// WHY: <see cref="SessionState"/> alone is not durable — it is wiped when the editor closes, so a
        /// build that failed before <see cref="OnPostprocessBuild"/> used to leave the moved folders
        /// unrecoverable once Unity restarted.
        /// </remarks>
        internal static void WriteManifestFile(string backupRoot, List<MovedFolderEntry> entries)
        {
            Directory.CreateDirectory(backupRoot);
            MovedFoldersManifest manifest = new() { entries = entries?.ToArray() ?? Array.Empty<MovedFolderEntry>() };
            File.WriteAllText(GetManifestPath(backupRoot), JsonUtility.ToJson(manifest));
        }

        /// <summary>Reads the restore manifest, returning an empty list when it is missing or unreadable.</summary>
        internal static List<MovedFolderEntry> ReadManifestFile(string backupRoot)
        {
            string path = GetManifestPath(backupRoot);
            if (!File.Exists(path))
            {
                return new List<MovedFolderEntry>();
            }

            try
            {
                return ParseManifest(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot read restore manifest: {ex.Message}");
                return new List<MovedFolderEntry>();
            }
        }

        /// <summary>Deserializes a manifest payload, returning an empty list for unusable json.</summary>
        internal static List<MovedFolderEntry> ParseManifest(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new List<MovedFolderEntry>();
            }

            MovedFoldersManifest manifest;
            try
            {
                manifest = JsonUtility.FromJson<MovedFoldersManifest>(json);
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot parse restore manifest: {ex.Message}");
                return new List<MovedFolderEntry>();
            }

            return manifest?.entries == null
                ? new List<MovedFolderEntry>()
                : new List<MovedFolderEntry>(manifest.entries);
        }

        private static void PersistManifest(string backupRoot, List<MovedFolderEntry> entries)
        {
            try
            {
                WriteManifestFile(backupRoot, entries);
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot write restore manifest: {ex.Message}");
            }

            // WHY: SessionState stays as a fast in-session cache; the file is the source of truth.
            MovedFoldersManifest manifest = new() { entries = entries?.ToArray() ?? Array.Empty<MovedFolderEntry>() };
            SessionState.SetString(SessionStateKey, JsonUtility.ToJson(manifest));
        }

        private static List<MovedFolderEntry> LoadManifest(string backupRoot)
        {
            List<MovedFolderEntry> fromFile = ReadManifestFile(backupRoot);
            if (fromFile.Count > 0)
            {
                return fromFile;
            }

            return ParseManifest(SessionState.GetString(SessionStateKey, string.Empty));
        }

        private static void EraseManifest(string backupRoot)
        {
            SessionState.EraseString(SessionStateKey);
            try
            {
                string path = GetManifestPath(backupRoot);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                CoreAIEditorLog.LogWarning($"StreamingAssets guard: cannot delete restore manifest: {ex.Message}");
            }
        }

        private static void MoveMetaIfExists(string fromPathWithoutMeta, string toPathWithoutMeta)
        {
            string fromMeta = fromPathWithoutMeta + ".meta";
            string toMeta = toPathWithoutMeta + ".meta";
            if (!File.Exists(fromMeta))
            {
                return;
            }

            if (File.Exists(toMeta))
            {
                File.Delete(toMeta);
            }

            File.Move(fromMeta, toMeta);
        }

        internal static bool ShouldGuardFolder(string folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            foreach (string prefix in GuardedFolderPrefixes)
            {
                if (folderName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        [Serializable]
        internal sealed class MovedFoldersManifest
        {
            public MovedFolderEntry[] entries;
        }

        [Serializable]
        internal sealed class MovedFolderEntry
        {
            public string sourceAbsolutePath;
            public string backupAbsolutePath;
        }

        private readonly struct StagedAssembly
        {
            internal StagedAssembly(string assemblyPath, string stagingRoot)
            {
                AssemblyPath = assemblyPath;
                StagingRoot = stagingRoot;
            }

            internal string AssemblyPath { get; }
            internal string StagingRoot { get; }
        }
    }
}
