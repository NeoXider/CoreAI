using System.Collections.Generic;
using UnityEngine;
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Composition;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// A compact auto-battler scene where chat-created Lua mods can change real combat rules:
    /// wave size, enemy scaling, hero damage, rewards, regen, hooks and timed effects.
    /// </summary>
    public sealed class WaveAutoBattlerModsDemoController : MonoBehaviour
    {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        public const string HeroDamageSlot = "hero_damage";
        public const string HeroAttackIntervalSlot = "hero_attack_interval";
        public const string HeroRegenSlot = "hero_regen";
        public const string EnemyCountSlot = "enemy_count";
        public const string EnemyHpSlot = "enemy_hp";
        public const string EnemyDamageSlot = "enemy_damage";
        public const string WaveRewardSlot = "wave_reward";

        private const int MaxLogLines = 14;
        private const float EnemySpacing = 1.35f;

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [SerializeField] private Transform heroAnchor;

        [SerializeField] private Transform enemyRoot;

        private readonly List<EnemyState> _enemies = new();
        private readonly List<string> _log = new();
        private ILuaModRuntime _mods;
        private LuaCsLogicSlots _slots;
        private GameObject _heroVisual;
        private float _heroHp;
        private float _heroMaxHp = 120f;
        private float _heroAttack = 16f;
        private int _heroLevel = 1;
        private float _xp;
        private int _gold;
        private int _wave;
        private float _attackTimer;
        private float _enemyAttackTimer;
        private string _status = "Starting...";

        private sealed class EnemyState
        {
            public GameObject Visual;
            public float Hp;
            public float MaxHp;
            public float Damage;
        }

        private void Start()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found.";
                Debug.LogError($"[WaveAutoBattlerModsDemo] {_status}");
                enabled = false;
                return;
            }

            var modsScope = FindFirstObjectByType<CoreAI.Composition.CoreAiModsLifetimeScope>();
            IObjectResolver luaContainer =
                (modsScope != null && modsScope.Container != null) ? modsScope.Container : coreAiScope.Container;

            _mods = luaContainer.Resolve<ILuaModRuntime>();
            _slots = luaContainer.Resolve<LuaCsLogicSlots>();
            DeclareSlots();
            EnsureAnchorsAndVisuals();
            _heroHp = _heroMaxHp;
            _mods.ModEventEmitted += OnModEvent;
            _status = "Ask chat to create or edit mods. Prompt buttons insert ready requests.";
            Log("Battle started. Lua mods can alter wave scaling, damage, regen and rewards.");
            StartNextWave();
        }

        private void OnDestroy()
        {
            if (_mods != null)
            {
                _mods.ModEventEmitted -= OnModEvent;
            }
        }

        private void DeclareSlots()
        {
            _slots.DeclareSlot(HeroDamageSlot);
            _slots.DeclareSlot(HeroAttackIntervalSlot);
            _slots.DeclareSlot(HeroRegenSlot);
            _slots.DeclareSlot(EnemyCountSlot);
            _slots.DeclareSlot(EnemyHpSlot);
            _slots.DeclareSlot(EnemyDamageSlot);
            _slots.DeclareSlot(WaveRewardSlot);
        }

        private void EnsureAnchorsAndVisuals()
        {
            if (heroAnchor == null)
            {
                GameObject anchor = new("HeroAnchor");
                anchor.transform.SetPositionAndRotation(new Vector3(-3f, 0.8f, 0f), Quaternion.identity);
                heroAnchor = anchor.transform;
            }

            if (enemyRoot == null)
            {
                GameObject root = new("EnemyRoot");
                root.transform.position = Vector3.zero;
                enemyRoot = root.transform;
            }

            if (_heroVisual == null)
            {
                _heroVisual = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Infrastructure.World.CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(_heroVisual);
                _heroVisual.name = "AutoBattlerHero";
                _heroVisual.transform.SetParent(heroAnchor, false);
                _heroVisual.transform.localPosition = Vector3.zero;
                _heroVisual.transform.localScale = new Vector3(0.85f, 1.05f, 0.85f);
                SetRendererColor(_heroVisual, new Color(0.15f, 0.72f, 1f));
            }
        }

        private void Update()
        {
            if (_slots == null || _enemies.Count == 0)
            {
                return;
            }

            float dt = Time.deltaTime;
            _mods.EmitEvent("battle_tick", $"{_wave}:{_heroLevel}:{_enemies.Count}");
            RegenerateHero(dt);
            RunHeroAttack(dt);
            RunEnemyAttack(dt);
            UpdateEnemyLayout();
        }

        private void RegenerateHero(float dt)
        {
            double regen = _slots.TryInvokeNumber(HeroRegenSlot, out double value, _heroLevel, _wave)
                ? value
                : 0.8d + _heroLevel * 0.15d;
            _heroHp = Mathf.Min(_heroMaxHp, _heroHp + (float)regen * dt);
        }

        private void RunHeroAttack(float dt)
        {
            _attackTimer += dt;
            float interval = CurrentHeroAttackInterval();
            if (_attackTimer < interval)
            {
                return;
            }

            _attackTimer = 0f;
            if (_enemies.Count == 0)
            {
                return;
            }

            EnemyState target = _enemies[0];
            double damage = _slots.TryInvokeNumber(HeroDamageSlot, out double value, _heroAttack, _heroLevel, _wave)
                ? value
                : _heroAttack + _heroLevel * 2f;
            damage = System.Math.Max(1d, damage);
            target.Hp -= (float)damage;
            Log($"Hero hits enemy for {damage:0.#}. Enemy HP {Mathf.Max(0f, target.Hp):0.#}/{target.MaxHp:0.#}.");

            if (target.Hp > 0f)
            {
                return;
            }

            DefeatEnemy(target);
        }

        private void RunEnemyAttack(float dt)
        {
            _enemyAttackTimer += dt;
            if (_enemyAttackTimer < 1.25f)
            {
                return;
            }

            _enemyAttackTimer = 0f;
            float totalDamage = 0f;
            foreach (EnemyState enemy in _enemies)
            {
                totalDamage += enemy.Damage;
            }

            if (totalDamage <= 0f)
            {
                return;
            }

            _heroHp -= totalDamage;
            Log($"Enemies hit hero for {totalDamage:0.#}. Hero HP {Mathf.Max(0f, _heroHp):0.#}/{_heroMaxHp:0.#}.");
            if (_heroHp > 0f)
            {
                return;
            }

            _mods.EmitEvent("hero_died", _wave.ToString());
            _heroHp = _heroMaxHp;
            _gold = Mathf.Max(0, _gold - 5);
            Log("Hero was knocked out. He recovers, loses 5 gold, and retries the wave.");
            RestartWave();
        }

        private float CurrentHeroAttackInterval()
        {
            double interval = _slots.TryInvokeNumber(HeroAttackIntervalSlot, out double value, _heroLevel, _wave)
                ? value
                : 0.95d;
            return Mathf.Clamp((float)interval, 0.2f, 4f);
        }

        private void DefeatEnemy(EnemyState enemy)
        {
            _enemies.Remove(enemy);
            if (enemy.Visual != null)
            {
                Destroy(enemy.Visual);
            }

            _xp += 7f + _wave;
            _mods.EmitEvent("enemy_defeated", $"{_wave}:{_enemies.Count}");
            Log($"Enemy defeated. Remaining: {_enemies.Count}.");
            TryLevelUp();

            if (_enemies.Count == 0)
            {
                CompleteWave();
            }
        }

        private void TryLevelUp()
        {
            float required = 20f + _heroLevel * 10f;
            if (_xp < required)
            {
                return;
            }

            _xp -= required;
            _heroLevel++;
            _heroMaxHp += 12f;
            _heroAttack += 3.5f;
            _heroHp = _heroMaxHp;
            _mods.EmitEvent("hero_level_up", _heroLevel.ToString());
            Log($"Hero leveled up to {_heroLevel}! Stats increased.");
        }

        private void CompleteWave()
        {
            int reward = Mathf.RoundToInt((float)(_slots.TryInvokeNumber(WaveRewardSlot, out double value, _wave)
                ? value
                : 12d + _wave * 3d));
            reward = Mathf.Max(0, reward);
            _gold += reward;
            _mods.EmitEvent("wave_cleared", $"{_wave}:{reward}:{_heroLevel}");
            Log($"Wave {_wave} cleared. +{reward} gold.");
            StartNextWave();
        }

        private void RestartWave()
        {
            ClearEnemies();
            SpawnWave(_wave);
        }

        private void StartNextWave()
        {
            _wave++;
            ClearEnemies();
            SpawnWave(_wave);
        }

        private void SpawnWave(int wave)
        {
            int count = Mathf.RoundToInt((float)(_slots.TryInvokeNumber(EnemyCountSlot, out double countValue, wave)
                ? countValue
                : 2d + System.Math.Floor(wave / 2d)));
            count = Mathf.Clamp(count, 1, 8);

            float hp = Mathf.Max(5f, (float)(_slots.TryInvokeNumber(EnemyHpSlot, out double hpValue, wave)
                ? hpValue
                : 26d + wave * 7d));
            float damage = Mathf.Max(0f, (float)(_slots.TryInvokeNumber(EnemyDamageSlot, out double damageValue, wave)
                ? damageValue
                : 2.5d + wave * 0.8d));

            for (int i = 0; i < count; i++)
            {
                GameObject visual = GameObject.CreatePrimitive(PrimitiveType.Cube);
                Infrastructure.World.CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(visual);
                visual.name = $"WaveEnemy_{wave}_{i + 1}";
                visual.transform.SetParent(enemyRoot, false);
                visual.transform.localScale = Vector3.one * 0.8f;
                SetRendererColor(visual,
                    Color.Lerp(new Color(1f, 0.37f, 0.22f), Color.magenta, Mathf.Clamp01(wave / 18f)));
                _enemies.Add(new EnemyState { Visual = visual, Hp = hp, MaxHp = hp, Damage = damage });
            }

            UpdateEnemyLayout();
            _mods.EmitEvent("wave_started", $"{wave}:{count}:{hp:0.#}:{damage:0.#}");
            Log($"Wave {wave} started: {count} enemies, HP {hp:0.#}, damage {damage:0.#}.");
        }

        private void ClearEnemies()
        {
            foreach (EnemyState enemy in _enemies)
            {
                if (enemy.Visual != null)
                {
                    Destroy(enemy.Visual);
                }
            }

            _enemies.Clear();
        }

        private void UpdateEnemyLayout()
        {
            for (int i = 0; i < _enemies.Count; i++)
            {
                EnemyState enemy = _enemies[i];
                if (enemy.Visual == null)
                {
                    continue;
                }

                float z = (i - (_enemies.Count - 1) * 0.5f) * EnemySpacing;
                enemy.Visual.transform.position = new Vector3(2.5f + i * 0.12f, 0.45f, z);
                float pulse = 1f + Mathf.Sin(Time.time * 5f + i) * 0.04f;
                enemy.Visual.transform.localScale = Vector3.one * (0.75f * pulse);
            }
        }

        private void OnModEvent(string modId, string eventName, string payload)
        {
            Log($"Mod event: {modId} -> {eventName}({payload}).");
        }

        private void Log(string line)
        {
            _log.Add($"[{Time.time:0.0}s] {line}");
            if (_log.Count > MaxLogLines)
            {
                _log.RemoveAt(0);
            }
        }

        private void OnGUI()
        {
            GUILayout.BeginArea(new Rect(12, 12, 560, 390), GUI.skin.box);
            GUILayout.Label("<b>CoreAI - Wave Auto-Battler Mods Demo</b>", RichLabel());
            GUILayout.Label(_status, RichLabel());
            if (_slots == null || _mods == null)
            {
                GUILayout.EndArea();
                return;
            }

            GUILayout.Space(4);
            GUILayout.Label(
                $"Wave <b>{_wave}</b>   Hero Lvl <b>{_heroLevel}</b>   HP <b>{Mathf.Max(0f, _heroHp):0.#}</b>/{_heroMaxHp:0.#}   Gold <b>{_gold}</b>   XP <b>{_xp:0.#}</b>",
                RichLabel());
            GUILayout.Label(
                $"Enemies alive: <b>{_enemies.Count}</b>   Hero attack interval: {CurrentHeroAttackInterval():0.##}s",
                RichLabel());

            GUILayout.Space(4);
            GUILayout.Label("<b>Lua mod slots</b>", RichLabel());
            DrawSlot(HeroDamageSlot, "(heroAttack, heroLevel, wave)");
            DrawSlot(HeroAttackIntervalSlot, "(heroLevel, wave)");
            DrawSlot(HeroRegenSlot, "(heroLevel, wave)");
            DrawSlot(EnemyCountSlot, "(wave)");
            DrawSlot(EnemyHpSlot, "(wave)");
            DrawSlot(EnemyDamageSlot, "(wave)");
            DrawSlot(WaveRewardSlot, "(wave)");

            GUILayout.Space(4);
            GUILayout.Label("<b>Loaded mods</b>", RichLabel());
            IReadOnlyList<LuaModInfo> mods = _mods.ListMods();
            if (mods.Count == 0)
            {
                GUILayout.Label("No mods loaded.");
            }
            else
            {
                foreach (LuaModInfo mod in mods)
                {
                    GUILayout.Label(
                        $"* {mod.Id} caps={mod.Capabilities} handlers={mod.HandlerCount} timers={mod.TimerCount} errors={mod.ErrorCount}");
                }
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Battle log</b>", RichLabel());
            foreach (string line in _log)
            {
                GUILayout.Label(line);
            }

            GUILayout.EndArea();
        }

        private void DrawSlot(string slot, string args)
        {
            GUILayout.Label($"* {slot}{args} - {(_slots.IsOverridden(slot) ? "Lua override" : "C# default")}");
        }

        private static void SetRendererColor(GameObject go, Color color)
        {
            if (go != null && go.TryGetComponent(out Renderer renderer))
            {
                renderer.sharedMaterial = new Material(Shader.Find("Standard")) { color = color };
            }
        }

        private static GUIStyle RichLabel()
        {
            return new GUIStyle(GUI.skin.label) { richText = true, wordWrap = true };
        }
#else
        private void Start()
        {
            Debug.LogWarning(
                "[WaveAutoBattlerModsDemo] MoonSharp is unavailable or COREAI_NO_LUA is set; demo is inactive.");
            enabled = false;
        }
#endif
    }
}