#if UNITY_EDITOR
using System;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;

namespace CoreAI.Editor
{
    /// <summary>
    /// Build-time guard that FAILS a WebGL build whose web template does not pass
    /// <c>config.autoSyncPersistentDataPath = true</c> to <c>createUnityInstance()</c>, unless the
    /// project explicitly opted out with the <see cref="OptOutDefineSymbol"/> scripting define symbol.
    /// <para>
    /// That flag is the only durable-storage channel CoreAI has left in the browser. Unity 6.3
    /// deprecated the manual <c>JS_FileSystem_Sync()</c> path and its <c>FS.syncfs</c> callback never
    /// fires, so <see cref="CoreAI.Infrastructure.CoreAiWebGlPersistence"/> stopped driving it. With
    /// the flag off, agent memory, skills, Lua mods, Lua version history and world packages are written
    /// into the tab's in-memory filesystem and are gone on reload.
    /// </para>
    /// <para>
    /// This is a build-time gate, not the feature: the runtime detects the same misconfiguration and
    /// reports a visible error. The gate exists so a shipped player never reaches a player's browser
    /// with silent data loss - which is exactly what happened with Unity's stock Default template,
    /// where the line is present but commented out.
    /// </para>
    /// <para>
    /// The fix travels with the gate: <see cref="CoreAIWebGlTemplateInstaller"/> installs a ready
    /// template from inside this package into the consuming project, and every failure message names
    /// the project-local action instead of a path that only exists in CoreAI's own repository.
    /// </para>
    /// </summary>
    public sealed class CoreAIWebGlPersistentDataSyncBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>
        /// Scripting define symbol (Project Settings &gt; Player &gt; Web &gt; Other Settings &gt;
        /// Scripting Define Symbols) that turns this gate off for projects that deliberately ship
        /// without CoreAI's persistent storage. The refusal is logged on every build - it is a stated
        /// decision, not a silent one.
        /// </summary>
        public const string OptOutDefineSymbol = "COREAI_WEBGL_NO_PERSISTENCE";

        /// <summary>The single line a web template must contain for CoreAI's storage to survive a reload.</summary>
        public const string RequiredTemplateLine = "config.autoSyncPersistentDataPath = true;";

        private readonly Func<string> _readWebGlDefineSymbols;
        private readonly Action<string> _logWarning;

        /// <summary>Constructor used by Unity's build pipeline.</summary>
        public CoreAIWebGlPersistentDataSyncBuildGuard()
            : this(ReadWebGlDefineSymbols, CoreAIEditorLog.LogWarning)
        {
        }

        /// <summary>
        /// Test seam: the define symbols and the warning sink are injected so a fixture never has to
        /// write real scripting define symbols, which would recompile the editor mid-run.
        /// </summary>
        /// <param name="readWebGlDefineSymbols">Reads the Web platform's scripting define symbols.</param>
        /// <param name="logWarning">Receives the opt-out notice.</param>
        internal CoreAIWebGlPersistentDataSyncBuildGuard(
            Func<string> readWebGlDefineSymbols,
            Action<string> logWarning)
        {
            _readWebGlDefineSymbols = readWebGlDefineSymbols ?? ReadWebGlDefineSymbols;
            _logWarning = logWarning ?? CoreAIEditorLog.LogWarning;
        }

        /// <inheritdoc />
        public int callbackOrder => 0;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report)
        {
            if (report != null && report.summary.platform != BuildTarget.WebGL)
            {
                return;
            }

            VerifySelectedTemplate(
                PlayerSettings.WebGL.template ?? "",
                _readWebGlDefineSymbols() ?? "",
                _logWarning);
        }

        /// <summary>
        /// Runs the gate for one selected template: throws <see cref="BuildFailedException"/> when the
        /// template cannot be read or does not arm browser storage, and returns quietly when it does.
        /// An opt-out never throws; it logs what the player gives up.
        /// </summary>
        /// <param name="template">Raw <c>PlayerSettings.WebGL.template</c> value.</param>
        /// <param name="webGlDefineSymbols">Scripting define symbols of the Web platform.</param>
        /// <param name="logWarning">Receives the opt-out notice.</param>
        internal static void VerifySelectedTemplate(
            string template,
            string webGlDefineSymbols,
            Action<string> logWarning)
        {
            string indexPath = ResolveTemplateIndexPath(template);
            bool readable = !string.IsNullOrEmpty(indexPath) && File.Exists(indexPath);
            bool armed = readable && TemplateEnablesAutoSync(File.ReadAllText(indexPath));

            if (IsOptedOut(webGlDefineSymbols))
            {
                (logWarning ?? CoreAIEditorLog.LogWarning)(DescribeOptOut(template, armed));
                return;
            }

            if (armed)
            {
                return;
            }

            throw new BuildFailedException(readable
                ? DescribeMissingFlag(template, indexPath)
                : DescribeUnreadableTemplate(template, indexPath));
        }

        /// <summary>
        /// True when <paramref name="defineSymbols"/> - a raw <c>;</c>, <c>,</c> or whitespace separated
        /// scripting define list - contains <see cref="OptOutDefineSymbol"/>.
        /// </summary>
        /// <param name="defineSymbols">Raw scripting define symbols of the Web platform.</param>
        public static bool IsOptedOut(string defineSymbols)
        {
            if (string.IsNullOrWhiteSpace(defineSymbols))
            {
                return false;
            }

            foreach (string symbol in defineSymbols.Split(
                         new[] { ';', ',', ' ', '\t', '\n', '\r' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (string.Equals(symbol.Trim(), OptOutDefineSymbol, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// True when <paramref name="indexHtml"/> actually enables Unity's automatic
        /// <c>persistentDataPath</c> synchronization: an assignment or an object-literal entry setting
        /// <c>autoSyncPersistentDataPath</c> to <c>true</c> that is not commented out.
        /// </summary>
        /// <param name="indexHtml">Full text of a web template's <c>index.html</c>.</param>
        public static bool TemplateEnablesAutoSync(string indexHtml)
        {
            if (string.IsNullOrEmpty(indexHtml))
            {
                return false;
            }

            foreach (string rawLine in StripBlockComments(indexHtml)
                         .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                int flag = line.IndexOf("autoSyncPersistentDataPath", StringComparison.Ordinal);
                if (flag < 0)
                {
                    continue;
                }

                int comment = line.IndexOf("//", StringComparison.Ordinal);
                if (comment >= 0 && comment < flag)
                {
                    continue;
                }

                string tail = line.Substring(flag + "autoSyncPersistentDataPath".Length);
                int assignment = tail.IndexOfAny(new[] { '=', ':' });
                if (assignment < 0)
                {
                    continue;
                }

                if (tail.Substring(assignment + 1).TrimStart().StartsWith("true", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Resolves the <c>index.html</c> of the template named by <c>PlayerSettings.WebGL.template</c>
        /// (<c>PROJECT:Name</c> lives under <c>Assets/WebGLTemplates</c>, <c>APPLICATION:Name</c> under
        /// the installed editor). Returns an empty string when the name cannot be resolved.
        /// </summary>
        /// <param name="template">Raw <c>PlayerSettings.WebGL.template</c> value.</param>
        public static string ResolveTemplateIndexPath(string template)
        {
            if (string.IsNullOrWhiteSpace(template))
            {
                return "";
            }

            int separator = template.IndexOf(':');
            string kind = separator < 0 ? "PROJECT" : template.Substring(0, separator);
            string name = separator < 0 ? template : template.Substring(separator + 1);
            if (string.IsNullOrWhiteSpace(name))
            {
                return "";
            }

            if (string.Equals(kind, "PROJECT", StringComparison.OrdinalIgnoreCase))
            {
                return Path.Combine("Assets", "WebGLTemplates", name, "index.html")
                    .Replace('\\', '/');
            }

            string buildTools = Path.Combine(
                EditorApplication.applicationContentsPath,
                "PlaybackEngines", "WebGLSupport", "BuildTools", "WebGLTemplates");

            // WHY: Unity 6.x nests the shipped templates under a "Base" folder; older layouts do not.
            string nested = Path.Combine(buildTools, "Base", name, "index.html");
            return (File.Exists(nested) ? nested : Path.Combine(buildTools, name, "index.html"))
                .Replace('\\', '/');
        }

        /// <summary>Message for a template that is readable but never arms browser storage.</summary>
        /// <param name="template">Raw <c>PlayerSettings.WebGL.template</c> value.</param>
        /// <param name="indexPath">Resolved path of the template's <c>index.html</c>.</param>
        internal static string DescribeMissingFlag(string template, string indexPath)
        {
            return
                $"[CoreAI] WebGL build aborted: the selected web template '{template}' ({indexPath}) " +
                $"never sets '{RequiredTemplateLine}' before createUnityInstance(). That flag is the " +
                "only channel that carries Application.persistentDataPath into IndexedDB, so without " +
                "it agent memory, skills, Lua mods, Lua version history and world packages are written " +
                "to the tab's in-memory filesystem and are gone on reload - silently, because every " +
                "write reports success. Unity's stock templates ship the line COMMENTED OUT." +
                DescribeRemedies(template);
        }

        /// <summary>Message for a template whose <c>index.html</c> cannot be read at all.</summary>
        /// <param name="template">Raw <c>PlayerSettings.WebGL.template</c> value.</param>
        /// <param name="indexPath">Resolved path of the template's <c>index.html</c>, possibly empty.</param>
        internal static string DescribeUnreadableTemplate(string template, string indexPath)
        {
            string where = string.IsNullOrEmpty(indexPath)
                ? "its name does not resolve to a template folder"
                : $"'{indexPath}' does not exist";
            return
                $"[CoreAI] WebGL build aborted: the selected web template '{template}' has no readable " +
                $"index.html ({where}), so CoreAI cannot verify that browser storage is armed. An " +
                "unverifiable template is not an armed one, and a player that ships with storage " +
                "disarmed loses agent memory, skills, Lua mods, Lua version history and world packages " +
                "on every reload." +
                DescribeRemedies(template);
        }

        /// <summary>Warning text for a build that explicitly refused the gate.</summary>
        /// <param name="template">Raw <c>PlayerSettings.WebGL.template</c> value.</param>
        /// <param name="templateArmsStorage">Whether the selected template turned out to arm storage anyway.</param>
        internal static string DescribeOptOut(string template, bool templateArmsStorage)
        {
            string state = templateArmsStorage
                ? $"The selected template '{template}' arms storage anyway, so the define currently " +
                  "changes nothing and can be removed."
                : $"The selected template '{template}' does not arm storage: this player will lose " +
                  "agent memory, skills, Lua mods, Lua version history and world packages on every " +
                  "page reload, and CoreAI's stores will report that failure at runtime.";
            return
                $"WebGL build: the persistent-storage gate is disabled by the scripting define symbol " +
                $"'{OptOutDefineSymbol}'. {state} Remove the symbol from Project Settings > Player > " +
                "Web > Other Settings > Scripting Define Symbols to restore the gate.";
        }

        private static string DescribeRemedies(string template)
        {
            return
                "\nFix it in the project you are building, in one of three ways:\n" +
                $"  1) add the line '{RequiredTemplateLine}' to your own template next to the other " +
                "'config.' assignments, before createUnityInstance(canvas, config, ...);\n" +
                $"  2) run the menu item '{CoreAIWebGlTemplateInstaller.MenuPath}' - it copies CoreAI's " +
                $"ready template out of the package into '{CoreAIWebGlTemplateInstaller.InstalledTemplateFolder}' " +
                "in THIS project and selects it in Project Settings > Player > Web > Resolution and " +
                "Presentation > WebGL Template;\n" +
                "  3) if this player deliberately ships without CoreAI's persistent storage, add the " +
                $"scripting define symbol '{OptOutDefineSymbol}' to Project Settings > Player > Web > " +
                "Other Settings > Scripting Define Symbols. The build then proceeds and CoreAI logs one " +
                "warning per build naming what is lost.\n" +
                $"Currently selected: '{template}'.";
        }

        private static string ReadWebGlDefineSymbols()
        {
            return PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.WebGL);
        }

        private static string StripBlockComments(string source)
        {
            int open = source.IndexOf("/*", StringComparison.Ordinal);
            if (open < 0)
            {
                return source;
            }

            StringBuilder builder = new(source.Length);
            int cursor = 0;
            while (open >= 0)
            {
                builder.Append(source, cursor, open - cursor);
                int close = source.IndexOf("*/", open + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    return builder.ToString();
                }

                cursor = close + 2;
                open = source.IndexOf("/*", cursor, StringComparison.Ordinal);
            }

            builder.Append(source, cursor, source.Length - cursor);
            return builder.ToString();
        }
    }
}
#endif
