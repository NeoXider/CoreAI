#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace CoreAI.Editor
{
    /// <summary>
    /// Installs CoreAI's web template into the project being built.
    /// <para>
    /// The template ships inside this package under <see cref="PackagedTemplatesFolderName"/>. Unity
    /// only lists web templates found in <c>Assets/WebGLTemplates</c> and in the installed editor
    /// (<c>WebGLTemplateManager.customTemplatesFolder</c> is <c>Application.dataPath/WebGLTemplates</c>,
    /// and the build resolves <c>PROJECT:Name</c> against the same folder), so a package cannot publish
    /// a selectable template directly. Copying it into the project on request is therefore the only
    /// honest way for <see cref="CoreAIWebGlPersistentDataSyncBuildGuard"/> to ship together with the
    /// fix it demands.
    /// </para>
    /// </summary>
    public static class CoreAIWebGlTemplateInstaller
    {
        /// <summary>Menu item that installs the template and selects it.</summary>
        public const string MenuPath = "CoreAI/Setup/Install WebGL Template";

        /// <summary>Folder name of the template, both inside the package and after installation.</summary>
        public const string TemplateName = "CoreAI";

        /// <summary>
        /// Package-relative folder holding the shipped templates. The trailing <c>~</c> keeps Unity from
        /// importing it, so it needs no <c>.meta</c> files and can never be mistaken for a project
        /// template; UPM still ships it, exactly like <c>Samples~</c>.
        /// </summary>
        public const string PackagedTemplatesFolderName = "WebGLTemplates~";

        /// <summary>Project-relative folder the template is installed into.</summary>
        public const string InstalledTemplateFolder = "Assets/WebGLTemplates/" + TemplateName;

        /// <summary>Project-relative path of the installed template's <c>index.html</c>.</summary>
        public const string InstalledTemplateIndexPath = InstalledTemplateFolder + "/index.html";

        /// <summary>Value written to <c>PlayerSettings.WebGL.template</c> once the template is installed.</summary>
        public const string SelectedTemplateValue = "PROJECT:" + TemplateName;

        private const string IndexFileName = "index.html";

        /// <summary>
        /// Copies the packaged template into <see cref="InstalledTemplateFolder"/> and selects it in
        /// player settings, asking before replacing an existing folder of the same name.
        /// </summary>
        [MenuItem(MenuPath, priority = 12)]
        public static void InstallAndSelect()
        {
            if (!TryResolvePackagedTemplateFolder(out string source, out string failure))
            {
                EditorUtility.DisplayDialog("CoreAI web template", failure, "OK");
                CoreAIEditorLog.LogError(failure);
                return;
            }

            string destination = GetInstalledTemplateAbsolutePath();
            if (Directory.Exists(destination) && !EditorUtility.DisplayDialog(
                    "CoreAI web template",
                    $"'{InstalledTemplateFolder}' already exists. Replace it with the template shipped " +
                    "in the CoreAI package? Any local edits in that folder are lost.",
                    "Replace",
                    "Cancel"))
            {
                return;
            }

            CopyTemplateFolder(source, destination);
            AssetDatabase.Refresh();
            PlayerSettings.WebGL.template = SelectedTemplateValue;
            AssetDatabase.SaveAssets();
            CoreAIEditorLog.Log(
                $"Installed the web template into '{InstalledTemplateFolder}' and selected it " +
                $"(PlayerSettings.WebGL.template = '{SelectedTemplateValue}'). It sets " +
                $"'{CoreAIWebGlPersistentDataSyncBuildGuard.RequiredTemplateLine}', which is what keeps " +
                "agent memory, skills, Lua mods, Lua version history and world packages alive across a " +
                "page reload.");
        }

        /// <summary>
        /// Locates the template shipped inside this package, whether the package is resolved by UPM or
        /// embedded in <c>Assets</c>.
        /// </summary>
        /// <param name="absoluteFolder">Absolute path of the packaged template folder.</param>
        /// <param name="failure">Human-readable reason when the folder cannot be located.</param>
        public static bool TryResolvePackagedTemplateFolder(out string absoluteFolder, out string failure)
        {
            List<string> searched = new();
            foreach (string root in EnumeratePackageRoots())
            {
                string candidate = Path.Combine(root, PackagedTemplatesFolderName, TemplateName);
                searched.Add(candidate);
                if (File.Exists(Path.Combine(candidate, IndexFileName)))
                {
                    absoluteFolder = candidate;
                    failure = "";
                    return true;
                }
            }

            absoluteFolder = "";
            failure =
                "CoreAI cannot find the web template shipped with the package. Looked in: " +
                (searched.Count == 0 ? "<no package root resolved>" : string.Join(", ", searched)) +
                $". Add '{CoreAIWebGlPersistentDataSyncBuildGuard.RequiredTemplateLine}' to your own " +
                "web template instead, or reinstall com.neoxider.coreaiunity.";
            return false;
        }

        /// <summary>Absolute path of <see cref="InstalledTemplateFolder"/> in the current project.</summary>
        public static string GetInstalledTemplateAbsolutePath()
        {
            return Path.Combine(Application.dataPath, "WebGLTemplates", TemplateName);
        }

        /// <summary>
        /// Replaces <paramref name="destinationFolder"/> with a copy of <paramref name="sourceFolder"/>.
        /// </summary>
        /// <remarks>
        /// WHY: a merge would leave files from an older template version behind, and the installed copy
        /// is meant to be exactly what the package ships - that is the invariant the drift test asserts.
        /// </remarks>
        /// <param name="sourceFolder">Absolute path of the packaged template.</param>
        /// <param name="destinationFolder">Absolute path of the installed template.</param>
        internal static void CopyTemplateFolder(string sourceFolder, string destinationFolder)
        {
            if (!Directory.Exists(sourceFolder))
            {
                throw new DirectoryNotFoundException(sourceFolder);
            }

            if (Directory.Exists(destinationFolder))
            {
                Directory.Delete(destinationFolder, true);
            }

            foreach (string relative in EnumerateTemplateFiles(sourceFolder))
            {
                string target = Path.Combine(destinationFolder, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? destinationFolder);
                File.Copy(Path.Combine(sourceFolder, relative.Replace('/', Path.DirectorySeparatorChar)), target, true);
            }
        }

        /// <summary>
        /// Template-relative paths of every content file under <paramref name="folder"/>, ignoring the
        /// <c>.meta</c> files Unity generates for the installed copy but never for a <c>~</c> folder.
        /// </summary>
        /// <param name="folder">Absolute path of a template folder.</param>
        internal static IReadOnlyList<string> EnumerateTemplateFiles(string folder)
        {
            List<string> relative = new();
            if (!Directory.Exists(folder))
            {
                return relative;
            }

            string root = Path.GetFullPath(folder);
            foreach (string file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                relative.Add(Path.GetFullPath(file)
                    .Substring(root.Length)
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace('\\', '/'));
            }

            relative.Sort(StringComparer.Ordinal);
            return relative;
        }

        private static IEnumerable<string> EnumeratePackageRoots()
        {
            PackageInfo package = PackageInfo.FindForAssembly(typeof(CoreAIWebGlTemplateInstaller).Assembly);
            if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
            {
                yield return package.resolvedPath;
            }

            // WHY: FindForAssembly returns null while the package is developed inside Assets (this
            // repository) - the asmdef is then the only reliable anchor for the package root.
            string assemblyDefinition = FindAssemblyDefinitionPath();
            if (string.IsNullOrEmpty(assemblyDefinition))
            {
                yield break;
            }

            string editorFolder = Path.GetDirectoryName(Path.GetFullPath(assemblyDefinition));
            string packageRoot = string.IsNullOrEmpty(editorFolder) ? "" : Path.GetDirectoryName(editorFolder);
            if (!string.IsNullOrEmpty(packageRoot))
            {
                yield return packageRoot;
            }
        }

        private static string FindAssemblyDefinitionPath()
        {
            foreach (string guid in AssetDatabase.FindAssets("CoreAI.Editor t:AssemblyDefinitionAsset"))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileName(path), "CoreAI.Editor.asmdef", StringComparison.Ordinal))
                {
                    return path;
                }
            }

            return "";
        }
    }
}
#endif
