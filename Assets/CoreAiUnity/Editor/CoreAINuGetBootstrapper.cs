using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using UnityEditor;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// One-click installer for the NuGet half of the CoreAI install chain:
    /// <c>Microsoft.Extensions.AI</c> delivered through NuGetForUnity.
    /// NuGetForUnity is an <b>optional</b> dependency, so this class never references
    /// it at compile time (no asmdef reference, no <c>using</c>): it must keep
    /// compiling in projects where NuGetForUnity is not installed. All interaction
    /// with NuGetForUnity goes through reflection over the assemblies already loaded
    /// in the editor domain, with a manual-restore fallback when the reflected
    /// surface cannot be resolved.
    /// </summary>
    public static class CoreAINuGetBootstrapper
    {
        private const string MenuPath = "CoreAI/Setup/Install LLM Pipeline (NuGet)...";
        private const int MenuPriority = 1;
        private const string DialogTitle = "CoreAI - Install LLM Pipeline (NuGet)";

        private const string MeaiPackageId = "Microsoft.Extensions.AI";

        /// <summary>Version pinned by Assets/packages.config and INSTALL.md section 2.</summary>
        private const string MeaiPackageVersion = "10.7.0";

        /// <summary>A type that only exists once the MEAI DLLs are restored.</summary>
        private const string MeaiProbeTypeName = "Microsoft.Extensions.AI.IChatClient";

        private const string NuGetForUnityAssemblyName = "NugetForUnity";
        private const string NuGetForUnityHomeUrl = "https://github.com/GlitchEnzo/NuGetForUnity";

        private const string NuGetForUnityUpmGitUrl =
            "https://github.com/GlitchEnzo/NuGetForUnity.git?path=/src/NuGetForUnity";

        private const string ManualRestoreMenuPath = "NuGet/Restore Packages";

        [MenuItem(MenuPath, priority = MenuPriority)]
        public static void InstallLlmPipeline()
        {
            try
            {
                InstallLlmPipelineInternal();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CoreAI] NuGet bootstrapper failed: {ex}");
                EditorUtility.DisplayDialog(DialogTitle,
                    $"The bootstrapper hit an unexpected error:\n{ex.Message}\n\nSee the Console for details.",
                    "OK");
            }
        }

        [MenuItem(MenuPath, validate = true)]
        private static bool ValidateInstallLlmPipeline()
        {
            return !EditorApplication.isCompiling && !EditorApplication.isUpdating;
        }

        private static void InstallLlmPipelineInternal()
        {
            // (a) Already installed? The MEAI DLLs are plain precompiled assemblies,
            // so once restored the probe type is resolvable in the editor domain.
            if (IsMeaiResolvable())
            {
                EditorUtility.DisplayDialog(DialogTitle,
                    $"{MeaiPackageId} is already installed and resolvable.\n\nNothing to do.",
                    "OK");
                return;
            }

            // (b) NuGetForUnity itself missing -> explain how to get it.
            Assembly nugetAssembly = FindNuGetForUnityAssembly();
            if (nugetAssembly == null)
            {
                bool copy = EditorUtility.DisplayDialog(DialogTitle,
                    "The LLM pipeline needs the NuGet package \"" + MeaiPackageId + "\", " +
                    "which is delivered through NuGetForUnity - and NuGetForUnity is not installed in this project.\n\n" +
                    "Install NuGetForUnity first (see INSTALL.md section 2):\n" +
                    "  - Package Manager -> Add package from Git URL:\n" +
                    "    " + NuGetForUnityUpmGitUrl + "\n" +
                    "  - or download it from " + NuGetForUnityHomeUrl + "\n\n" +
                    "Then run this menu item again.",
                    "Copy Git URL", "Close");
                if (copy)
                {
                    EditorGUIUtility.systemCopyBuffer = NuGetForUnityUpmGitUrl;
                    CoreAIEditorLog.Log("NuGetForUnity UPM Git URL copied to the clipboard: " + NuGetForUnityUpmGitUrl);
                }

                return;
            }

            // (c) NuGetForUnity present -> ensure the packages.config entry, then restore.
            string packagesConfigPath = Path.Combine(Application.dataPath, "packages.config");
            bool entryAdded;
            try
            {
                entryAdded = EnsurePackagesConfigEntry(packagesConfigPath);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[CoreAI] Failed to update {packagesConfigPath}: {ex}");
                EditorUtility.DisplayDialog(DialogTitle,
                    $"Failed to update Assets/packages.config:\n{ex.Message}\n\n" +
                    $"Add <package id=\"{MeaiPackageId}\" version=\"{MeaiPackageVersion}\" /> manually, " +
                    $"then run {ManualRestoreMenuPath}.",
                    "OK");
                return;
            }

            CoreAIEditorLog.Log(entryAdded
                ? $"Added {MeaiPackageId} {MeaiPackageVersion} to Assets/packages.config."
                : $"{MeaiPackageId} is already declared in Assets/packages.config.");

            // Invoke the reflected restore entry point.
            MethodInfo restoreMethod = FindRestoreMethod(nugetAssembly);
            if (restoreMethod == null)
            {
                // Ambiguous/unknown NuGetForUnity surface: fall back to its own menu item.
                CoreAIEditorLog.LogWarning(
                    "Could not find a restore entry point in the NugetForUnity assembly via reflection; " +
                    $"falling back to the \"{ManualRestoreMenuPath}\" menu item.");
                if (!EditorApplication.ExecuteMenuItem(ManualRestoreMenuPath))
                {
                    EditorUtility.DisplayDialog(DialogTitle,
                        $"{MeaiPackageId} {MeaiPackageVersion} is declared in Assets/packages.config, " +
                        "but the NuGetForUnity restore entry point could not be invoked automatically.\n\n" +
                        $"Run \"{ManualRestoreMenuPath}\" from the main menu to download the DLLs. " +
                        "Unity will recompile once the packages are restored.",
                        "OK");
                    return;
                }
            }
            else
            {
                CoreAIEditorLog.Log(
                    $"Restoring NuGet packages via {restoreMethod.DeclaringType?.FullName}.{restoreMethod.Name}...");
                InvokeRestore(restoreMethod);
            }

            AssetDatabase.Refresh();
            CoreAIEditorLog.Log($"NuGet restore triggered for {MeaiPackageId} {MeaiPackageVersion}.");
            EditorUtility.DisplayDialog(DialogTitle,
                $"{MeaiPackageId} {MeaiPackageVersion} has been declared in Assets/packages.config " +
                "and a NuGet restore was triggered.\n\n" +
                "Unity will now import the DLLs and recompile. When the console settles, the LLM pipeline is ready.",
                "OK");
        }

        /// <summary>True when a Microsoft.Extensions.AI type is resolvable in the current domain.</summary>
        private static bool IsMeaiResolvable()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (assembly.GetType(MeaiProbeTypeName, false) != null)
                    {
                        return true;
                    }
                }
                catch
                {
                    // Some dynamic/reflection-only assemblies throw on GetType; ignore them.
                }
            }

            return false;
        }

        private static Assembly FindNuGetForUnityAssembly()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, NuGetForUnityAssemblyName,
                    StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Discovers a restore entry point on the NugetForUnity assembly at runtime.
        /// Preference order: a public static parameterless "Restore"-named method,
        /// then a public static "Restore"-named method with a single bool parameter
        /// (its slim-restore flag). Types whose name contains "Restorer" win ties.
        /// </summary>
        private static MethodInfo FindRestoreMethod(Assembly nugetAssembly)
        {
            List<MethodInfo> candidates = new();
            foreach (Type type in GetLoadableTypes(nugetAssembly))
            {
                if (type == null || !type.IsClass)
                {
                    continue;
                }

                foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (!method.Name.StartsWith("Restore", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    ParameterInfo[] parameters = method.GetParameters();
                    bool parameterless = parameters.Length == 0;
                    bool singleBool = parameters.Length == 1 && parameters[0].ParameterType == typeof(bool);
                    if (parameterless || singleBool)
                    {
                        candidates.Add(method);
                    }
                }
            }

            return candidates
                .OrderBy(m => m.GetParameters().Length)
                .ThenByDescending(m => m.DeclaringType?.Name.Contains("Restorer") == true)
                .FirstOrDefault();
        }

        private static void InvokeRestore(MethodInfo restoreMethod)
        {
            // Single-bool overload is NugetForUnity's slim-restore flag: true only
            // installs packages that are missing, which is exactly what we want here.
            object[] args = restoreMethod.GetParameters().Length == 1
                ? new object[] { true }
                : Array.Empty<object>();
            restoreMethod.Invoke(null, args);
        }

        private static IEnumerable<Type> GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t != null);
            }
        }

        /// <summary>
        /// Ensures Assets/packages.config declares <see cref="MeaiPackageId"/>.
        /// Creates the file when missing; merges when present. Existing entries are
        /// never removed or rewritten. Returns true when an entry was added.
        /// </summary>
        private static bool EnsurePackagesConfigEntry(string packagesConfigPath)
        {
            XDocument document;
            if (File.Exists(packagesConfigPath))
            {
                document = XDocument.Load(packagesConfigPath);
            }
            else
            {
                document = new XDocument(
                    new XDeclaration("1.0", "utf-8", null),
                    new XElement("packages"));
            }

            XElement root = document.Root;
            if (root == null || !string.Equals(root.Name.LocalName, "packages", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Assets/packages.config does not have the expected <packages> root element.");
            }

            bool alreadyDeclared = root.Elements()
                .Where(e => string.Equals(e.Name.LocalName, "package", StringComparison.OrdinalIgnoreCase))
                .Any(e => string.Equals((string)e.Attribute("id"), MeaiPackageId, StringComparison.OrdinalIgnoreCase));
            if (alreadyDeclared)
            {
                return false;
            }

            XElement entry = new("package",
                new XAttribute("id", MeaiPackageId),
                new XAttribute("version", MeaiPackageVersion),
                new XAttribute("manuallyInstalled", "true"));

            // Keep the file alphabetically sorted by id, matching the repo manifest style.
            XElement insertBefore = root.Elements()
                .Where(e => string.Equals(e.Name.LocalName, "package", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(e => string.Compare((string)e.Attribute("id"), MeaiPackageId,
                    StringComparison.OrdinalIgnoreCase) > 0);
            if (insertBefore != null)
            {
                insertBefore.AddBeforeSelf(entry);
            }
            else
            {
                root.Add(entry);
            }

            document.Save(packagesConfigPath);
            return true;
        }
    }
}