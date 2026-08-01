using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;
using UnityEngine;

namespace CoreAI.Editor
{
    /// <summary>
    /// Enable / disable the optional CoreAI modules from the editor:
    /// <list type="bullet">
    /// <item><b>LLM providers</b> — provider-backed HTTP/MEAI/LLMUnity implementations, enabled
    /// explicitly with <c>COREAI_LLM</c>. Portable orchestration, chat, and scripted/stub clients
    /// remain available without the symbol.</item>
    /// <item><b>Lua (Lua-CSharp)</b> — the managed, AOT-safe Lua runtime ships bundled inside the
    /// <c>com.neoxider.coreaimods</c> package (no external package to install). Lua is disabled by
    /// default; the <c>COREAI_LUA</c> scripting define opts a target into the runtime and its security
    /// surface (the runtime guards on <c>COREAI_LUA</c>).</item>
    /// <item><b>LLMUnity</b> — package <c>ai.undream.llm</c>; its presence sets <c>COREAI_HAS_LLMUNITY</c>.</item>
    /// </list>
    /// "Enable + Update" runs <see cref="Client.Add(string)"/> with the canonical git URL, which both
    /// installs the package when missing and re-resolves it to the latest commit of its branch.
    /// </summary>
    public static class CoreAIModuleManager
    {
        private const string LlmUnityId = "ai.undream.llm";
        private const string LlmUnityUrl = "https://github.com/undreamai/LLMUnity.git";

        private const string LlmDefine = "COREAI_LLM";
        private const string LuaDefine = "COREAI_LUA";

        private static bool _busy;

        [MenuItem("CoreAI/Setup/Modules/LLM Providers/Enable Providers", priority = 0)]
        public static void EnableLlm()
        {
            SetDefine(LlmDefine, true);
            EditorUtility.DisplayDialog("CoreAI Modules",
                "LLM providers enabled. Concrete HTTP/MEAI and available LLMUnity implementations now " +
                "compile for the current build target. Portable orchestration, chat, and scripted/stub " +
                "clients are always available. COREAI_LUA remains an independent opt-in.",
                "OK");
        }

        [MenuItem("CoreAI/Setup/Modules/LLM Providers/Disable Providers", priority = 1)]
        public static void DisableLlm()
        {
            if (!EditorUtility.DisplayDialog("CoreAI Modules",
                    "Disable provider-backed LLM implementations by removing COREAI_LLM?\n\nHTTP/MEAI/" +
                    "LLMUnity provider clients and their focused tests compile out. Portable orchestration, " +
                    "chat, scripted/stub clients, and required MEAI contracts remain available. Lua stays " +
                    "independently controlled by COREAI_LUA.",
                    "Disable", "Cancel"))
            {
                return;
            }

            SetDefine(LlmDefine, false);
        }

        // ----------------------------------------------------------------- Lua (Lua-CSharp)

        [MenuItem("CoreAI/Setup/Modules/Lua (Lua-CSharp)/Enable Lua", priority = 0)]
        public static void EnableLua()
        {
            SetDefine(LuaDefine, true);
            EditorUtility.DisplayDialog("CoreAI Modules",
                "Lua enabled. The Lua-CSharp runtime is bundled with the CoreAI Mods package — there is " +
                "no external package to install. Scripts recompile with all Lua features (sandbox, mods, " +
                "logic slots, Full reflection) available.",
                "OK");
        }

        [MenuItem("CoreAI/Setup/Modules/Lua (Lua-CSharp)/Disable Lua", priority = 1)]
        public static void DisableLua()
        {
            if (!EditorUtility.DisplayDialog("CoreAI Modules",
                    "Disable Lua by removing COREAI_LUA?\n\nEvery Lua feature (sandbox, mods, logic slots, Full " +
                    "reflection) compiles out. The bundled Lua-CSharp assembly stays in the project; only " +
                    "the CoreAI Lua surface is removed. Re-enable from the same menu.",
                    "Disable", "Cancel"))
            {
                return;
            }

            SetDefine(LuaDefine, false);
        }

        // ----------------------------------------------------------------- LLMUnity

        [MenuItem("CoreAI/Setup/Modules/LLMUnity/Enable + Update to latest", priority = 20)]
        public static void EnableLlmUnity()
        {
            AddOrUpdatePackage("LLMUnity", LlmUnityUrl);
        }

        [MenuItem("CoreAI/Setup/Modules/LLMUnity/Remove package", priority = 21)]
        public static void RemoveLlmUnity()
        {
            if (!EditorUtility.DisplayDialog("CoreAI Modules",
                    "Remove the LLMUnity package entirely?\n\nCOREAI_HAS_LLMUNITY will unset and the local-LLM " +
                    "pipeline compiles out. HTTP/OpenAI-compatible backends are unaffected.",
                    "Remove", "Cancel"))
            {
                return;
            }

            RemovePackage("LLMUnity", LlmUnityId);
        }

        // ----------------------------------------------------------------- Status

        [MenuItem("CoreAI/Setup/Modules/Report module status", priority = 40)]
        public static void ReportStatus()
        {
            if (!BeginRequest())
            {
                return;
            }

            ListRequest list = Client.List(false, false);
            Pump(list, () =>
            {
                string llmunity = DescribePackage(list, LlmUnityId);
                bool llmEnabled = HasDefine(LlmDefine);
                bool luaEnabled = HasDefine(LuaDefine);

                string message =
                    $"COREAI_LLM define: {(llmEnabled ? "SET (providers enabled)" : "not set")}\n" +
                    $"-> Provider-backed LLM implementations: {(llmEnabled ? "ENABLED" : "disabled")}\n" +
                    "-> Portable orchestration/chat + scripted/stub clients: ENABLED\n\n" +
                    $"Lua runtime: Lua-CSharp (bundled with CoreAI Mods)\n" +
                    $"COREAI_LUA define: {(luaEnabled ? "SET (Lua enabled)" : "not set")}\n" +
                    $"-> Lua effective: {(luaEnabled ? "ENABLED" : "disabled")}\n\n" +
                    $"LLMUnity package: {llmunity}\n" +
                    $"-> Local LLM effective: {(llmEnabled && llmunity != "not installed" ? "ENABLED" : "disabled")}";

                EditorUtility.DisplayDialog("CoreAI - Module status", message, "OK");
            });
        }

        // ----------------------------------------------------------------- Helpers

        private static void AddOrUpdatePackage(string label, string identifier)
        {
            if (!BeginRequest())
            {
                return;
            }

            AddRequest request = Client.Add(identifier);
            Pump(request, () =>
            {
                if (request.Status == StatusCode.Success)
                {
                    CoreAIEditorLog.Log($"{label}: installed/updated to {request.Result.packageId}.");
                }
                else
                {
                    EditorUtility.DisplayDialog("CoreAI Modules",
                        $"{label}: install/update failed.\n\n{request.Error?.message}", "OK");
                }
            });
        }

        private static void RemovePackage(string label, string packageName)
        {
            if (!BeginRequest())
            {
                return;
            }

            RemoveRequest request = Client.Remove(packageName);
            Pump(request, () =>
            {
                if (request.Status == StatusCode.Success)
                {
                    CoreAIEditorLog.Log($"{label}: package '{packageName}' removed.");
                }
                else
                {
                    EditorUtility.DisplayDialog("CoreAI Modules",
                        $"{label}: removal failed.\n\n{request.Error?.message}", "OK");
                }
            });
        }

        private static string DescribePackage(ListRequest list, string packageName)
        {
            if (list.Status != StatusCode.Success || list.Result == null)
            {
                return "unknown (list failed)";
            }

            foreach (UnityEditor.PackageManager.PackageInfo info in list.Result)
            {
                if (string.Equals(info.name, packageName, StringComparison.Ordinal))
                {
                    return $"{info.version} ({info.source})";
                }
            }

            return "not installed";
        }

        private static bool BeginRequest()
        {
            if (_busy)
            {
                EditorUtility.DisplayDialog("CoreAI Modules",
                    "A package operation is already running. Wait for it to finish.", "OK");
                return false;
            }

            if (EditorApplication.isCompiling || EditorApplication.isUpdating)
            {
                EditorUtility.DisplayDialog("CoreAI Modules",
                    "Editor is busy compiling/updating. Try again in a moment.", "OK");
                return false;
            }

            _busy = true;
            return true;
        }

        private static void EndRequest()
        {
            _busy = false;
        }

        // UPM requests resolve on the editor's main loop, so we must poll via EditorApplication.update
        // (a busy-wait would starve the resolver and deadlock).
        private static void Pump(Request request, Action onCompleted)
        {
            EditorUtility.DisplayProgressBar("CoreAI Modules", "Resolving package via UPM...", 0.5f);

            void Tick()
            {
                if (request == null || !request.IsCompleted)
                {
                    return;
                }

                EditorApplication.update -= Tick;
                EditorUtility.ClearProgressBar();

                try
                {
                    onCompleted();
                }
                finally
                {
                    EndRequest();
                }
            }

            EditorApplication.update += Tick;
        }

        private static bool HasDefine(string symbol)
        {
            return GetDefines().Contains(symbol);
        }

        private static void SetDefine(string symbol, bool enabled)
        {
            List<string> defines = GetDefines();
            bool has = defines.Contains(symbol);
            if (enabled == has)
            {
                return;
            }

            if (enabled)
            {
                defines.Add(symbol);
            }
            else
            {
                defines.RemoveAll(d => d == symbol);
            }

            PlayerSettings.SetScriptingDefineSymbols(CurrentNamedBuildTarget(), string.Join(";", defines));
            CoreAIEditorLog.Log($"Scripting define '{symbol}' {(enabled ? "added" : "removed")}.");
        }

        private static List<string> GetDefines()
        {
            string raw = PlayerSettings.GetScriptingDefineSymbols(CurrentNamedBuildTarget());
            return raw.Split(';').Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        }

        private static NamedBuildTarget CurrentNamedBuildTarget()
        {
            BuildTargetGroup group = EditorUserBuildSettings.selectedBuildTargetGroup;
            if (group == BuildTargetGroup.Unknown)
            {
                group = BuildTargetGroup.Standalone;
            }

            return NamedBuildTarget.FromBuildTargetGroup(group);
        }
    }
}
