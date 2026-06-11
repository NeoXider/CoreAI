using UnityEngine;
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Composition;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// Demo driver for "the LLM rewrites game mechanics live": a tiny auto-battle loop whose
    /// rules go through <c>LuaLogicSlots</c>. The scene also hosts the CoreAI chat panel routed
    /// to the built-in <c>Programmer</c> role, so a real model can call <c>execute_lua</c> and
    /// redefine the declared slots (or load mods / publish world commands) while the battle runs.
    /// </summary>
    public sealed class LiveMechanicsDemoController : MonoBehaviour
    {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        /// <summary>Slot: damage per hero attack, args (atk, def) → number.</summary>
        public const string DamageSlot = "damage_formula";

        /// <summary>Slot: seconds between hero attacks, args () → number.</summary>
        public const string AttackIntervalSlot = "attack_interval";

        /// <summary>Slot: gold per defeated boss, args (bossMaxHp) → number.</summary>
        public const string LootSlot = "loot_formula";

        private const double HeroAttack = 25d;
        private const double BossDefense = 10d;
        private const double BossMaxHp = 200d;
        private const int MaxLogLines = 12;

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")]
        [SerializeField] private CoreAILifetimeScope coreAiScope;

        private LuaLogicSlots _slots;
        private LuaModRuntime _mods;
        private readonly List<string> _battleLog = new();
        private double _bossHp = BossMaxHp;
        private double _gold;
        private int _bossesDefeated;
        private float _attackTimer;
        private string _status = "";

        private void Start()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found in scene.";
                Debug.LogError($"[LiveMechanicsDemo] {_status}");
                enabled = false;
                return;
            }

            _slots = coreAiScope.Container.Resolve<LuaLogicSlots>();
            _mods = coreAiScope.Container.Resolve<LuaModRuntime>();
            _slots.DeclareSlot(DamageSlot);
            _slots.DeclareSlot(AttackIntervalSlot);
            _slots.DeclareSlot(LootSlot);
            _status = "Press C to open the chat and ask the AI to change the rules.";
            Log("Battle started. Default rules: damage = atk - def, attack every 2s, loot = 10 gold.");
        }

        private void Update()
        {
            if (_slots == null)
            {
                return;
            }

            _attackTimer += Time.deltaTime;
            if (_attackTimer < CurrentAttackInterval())
            {
                return;
            }

            _attackTimer = 0f;
            PerformAttack();
        }

        private float CurrentAttackInterval()
        {
            double seconds = _slots.TryInvokeNumber(AttackIntervalSlot, out double v) ? v : 2d;
            return Mathf.Clamp((float)seconds, 0.2f, 10f);
        }

        private void PerformAttack()
        {
            bool overridden = _slots.TryInvokeNumber(DamageSlot, out double dmg, HeroAttack, BossDefense);
            if (!overridden)
            {
                dmg = HeroAttack - BossDefense; // Vanilla C# rule.
            }

            dmg = System.Math.Max(0d, dmg);
            _bossHp -= dmg;
            Log($"Hero hits Boss for {dmg:0.#} ({(overridden ? "Lua rule" : "C# rule")}). Boss HP: {System.Math.Max(0d, _bossHp):0.#}");

            if (_bossHp > 0d)
            {
                return;
            }

            double loot = _slots.TryInvokeNumber(LootSlot, out double l, BossMaxHp) ? l : 10d;
            loot = System.Math.Max(0d, loot);
            _gold += loot;
            _bossesDefeated++;
            _bossHp = BossMaxHp;
            Log($"Boss defeated! +{loot:0.#} gold (total {_gold:0.#}). A new boss appears.");
        }

        private void Log(string line)
        {
            _battleLog.Add($"[{Time.time:0.0}s] {line}");
            if (_battleLog.Count > MaxLogLines)
            {
                _battleLog.RemoveAt(0);
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 470, Screen.height - 24), GUI.skin.box);
            GUILayout.Label("<b>CoreAI — Live Mechanics Demo (LLM edits the rules)</b>", RichLabel());
            GUILayout.Label(_status, RichLabel());

            if (_slots == null)
            {
                GUILayout.EndArea();
                return;
            }

            GUILayout.Space(6);
            GUILayout.Label(
                $"Boss HP: <b>{System.Math.Max(0d, _bossHp):0.#}</b> / {BossMaxHp:0}   " +
                $"Gold: <b>{_gold:0.#}</b>   Defeated: <b>{_bossesDefeated}</b>",
                RichLabel());

            GUILayout.Space(4);
            GUILayout.Label("<b>Rules (Lua logic slots)</b>", RichLabel());
            DrawSlotRow(DamageSlot, "atk=25, def=10");
            DrawSlotRow(AttackIntervalSlot, "()");
            DrawSlotRow(LootSlot, "bossMaxHp=200");
            if (!string.IsNullOrEmpty(_slots.LastError))
            {
                GUILayout.Label($"Last Lua error: {_slots.LastError}", RichLabel());
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Mods</b>", RichLabel());
            IReadOnlyList<LuaModInfo> mods = _mods.ListMods();
            if (mods.Count == 0)
            {
                GUILayout.Label("No mods loaded.");
            }
            else
            {
                foreach (LuaModInfo mod in mods)
                {
                    GUILayout.Label($"• {mod.Id} caps={mod.Capabilities} errors={mod.ErrorCount}");
                }
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Battle log</b>", RichLabel());
            foreach (string line in _battleLog)
            {
                GUILayout.Label(line);
            }

            GUILayout.EndArea();
        }

        private void DrawSlotRow(string slot, string args)
        {
            string state = _slots.IsOverridden(slot) ? "<b>Lua override</b>" : "C# default";
            GUILayout.Label($"• {slot}({args}) — {state}", RichLabel());
        }

        private static GUIStyle RichLabel()
        {
            GUIStyle style = new(GUI.skin.label) { richText = true, wordWrap = true };
            return style;
        }
#else
        private void Start()
        {
            Debug.LogWarning(
                "[LiveMechanicsDemo] MoonSharp is unavailable or COREAI_NO_LUA is set; demo is inactive.");
            enabled = false;
        }
#endif
    }
}
