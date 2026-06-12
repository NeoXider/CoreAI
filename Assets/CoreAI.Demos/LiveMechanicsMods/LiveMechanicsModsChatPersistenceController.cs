using System.Collections;
using UnityEngine;
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Composition;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// Scene-level host policy for the LiveMechanics mods-chat demo.
    /// LuaModRuntime intentionally does not autoload arbitrary source by itself; this component
    /// saves mods loaded by the chat scene and reloads them on the next scene start.
    /// </summary>
    public sealed class LiveMechanicsModsChatPersistenceController : MonoBehaviour
    {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        private const string ModKeyPrefix = "demo.live_mechanics.mods_chat.mod.";

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [SerializeField]
        private bool showStatusPanel = true;

        private LuaModRuntime _mods;
        private ILuaScriptVersionStore _versions;
        private string _status = "Waiting for CoreAI scope.";
        private int _autoloadedCount;
        private bool _isAutoloading;

        private IEnumerator Start()
        {
            // LiveMechanicsDemoController declares its logic slots in Start. Wait one frame so
            // saved mods that call logic_define can bind to those slots reliably.
            yield return null;

            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found; mod autoload disabled.";
                Debug.LogError($"[LiveMechanicsModsChatDemo] {_status}");
                enabled = false;
                yield break;
            }

            _mods = coreAiScope.Container.Resolve<LuaModRuntime>();
            _versions = coreAiScope.Container.Resolve<ILuaScriptVersionStore>();
            _mods.ModSourceLoaded += OnModSourceLoaded;
            _mods.ModSourceUnloaded += OnModSourceUnloaded;

            AutoloadSavedMods();
            _status = _autoloadedCount == 0
                ? "Mods-chat persistence ready. Load mods with manage_mods from the chat."
                : $"Mods-chat persistence ready. Autoloaded {_autoloadedCount} saved mod(s).";
        }

        private void OnDestroy()
        {
            if (_mods == null)
            {
                return;
            }

            _mods.ModSourceLoaded -= OnModSourceLoaded;
            _mods.ModSourceUnloaded -= OnModSourceUnloaded;
        }

        private void AutoloadSavedMods()
        {
            if (_versions == null || _mods == null)
            {
                return;
            }

            _isAutoloading = true;
            try
            {
                foreach (string key in _versions.GetKnownKeys())
                {
                    if (string.IsNullOrEmpty(key) || !key.StartsWith(ModKeyPrefix, System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string modId = key.Substring(ModKeyPrefix.Length);
                    if (modId.Length == 0 ||
                        !_versions.TryGetSnapshot(key, out LuaScriptVersionRecord record) ||
                        string.IsNullOrWhiteSpace(record.CurrentLua))
                    {
                        continue;
                    }

                    try
                    {
                        if (_mods.IsLoaded(modId))
                        {
                            _mods.ReloadMod(modId, record.CurrentLua);
                        }
                        else
                        {
                            _mods.LoadMod(modId, record.CurrentLua, LuaCapabilities.All);
                        }

                        _autoloadedCount++;
                        Debug.Log($"[LiveMechanicsModsChatDemo] Autoloaded saved Lua mod '{modId}'.");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning(
                            $"[LiveMechanicsModsChatDemo] Saved Lua mod '{modId}' failed to autoload: {ex.Message}");
                    }
                }
            }
            finally
            {
                _isAutoloading = false;
            }
        }

        private void OnModSourceLoaded(string modId, string source, LuaCapabilities capabilities)
        {
            if (_isAutoloading || _versions == null || string.IsNullOrWhiteSpace(modId))
            {
                return;
            }

            string key = MakeModKey(modId);
            _versions.SeedOriginal(key, "", false);
            _versions.RecordSuccessfulExecution(key, source ?? "");
            _status = $"Saved mod '{modId}' for next scene start.";
        }

        private void OnModSourceUnloaded(string modId)
        {
            if (_isAutoloading || _versions == null || string.IsNullOrWhiteSpace(modId))
            {
                return;
            }

            string key = MakeModKey(modId);
            _versions.SeedOriginal(key, "", false);
            _versions.RecordSuccessfulExecution(key, "");
            _status = $"Removed mod '{modId}' from scene autoload.";
        }

        private static string MakeModKey(string modId)
        {
            return ModKeyPrefix + (modId ?? "").Trim();
        }

        private void OnGUI()
        {
            if (!showStatusPanel)
            {
                return;
            }

            GUILayout.BeginArea(new Rect(Screen.width - 490, 12, 470, 88), GUI.skin.box);
            GUILayout.Label("<b>LiveMechanics Mods Chat</b>", RichLabel());
            GUILayout.Label(_status, RichLabel());
            GUILayout.Label("Use chat: load/reload/unload via manage_mods. Saved loaded mods autoload here.", RichLabel());
            GUILayout.EndArea();
        }

        private static GUIStyle RichLabel()
        {
            return new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        }
#else
        private void Start()
        {
            Debug.LogWarning(
                "[LiveMechanicsModsChatDemo] MoonSharp is unavailable or COREAI_NO_LUA is set; demo persistence is inactive.");
            enabled = false;
        }
#endif
    }
}
