using System.Collections.Generic;
using UnityEngine;
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
using CoreAI.Ai;
using CoreAI.Composition;
using CoreAI.Infrastructure.Lua;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// "Unit Forge" — an empty arena that ships with <b>no units and no behaviour</b>. Everything
    /// is added by Lua mods written through chat: a mod calls <c>forge_define{...}</c> to author a
    /// new unit type, <c>forge_spawn(name, x, z)</c> to deploy it, and uses <c>hooks_every</c> to
    /// stream reinforcements. The host only runs a tiny auto-battle: every unit walks toward the
    /// nearest enemy and attacks in range. The whole game emerges from mods.
    /// </summary>
    public sealed class ModdableUnitsDemoController : MonoBehaviour
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        , IUnitForge
#endif
    {
#if COREAI_HAS_MOONSHARP && !COREAI_NO_LUA
        private const int MaxLogLines = 12;
        private const int MaxUnits = 64;
        private const float ArenaHalfWidth = 7f;
        private const float ArenaHalfDepth = 4.5f;

        [Tooltip("Scene CoreAI scope. Auto-found when left empty.")] [SerializeField]
        private CoreAILifetimeScope coreAiScope;

        [Tooltip("Parent for spawned unit visuals. Created automatically when empty.")] [SerializeField]
        private Transform unitRoot;

        private sealed class Archetype
        {
            public string Name = "";
            public string Team = "enemy";
            public float Hp = 20f;
            public float Damage = 4f;
            public float Speed = 1.5f;
            public float Range = 1f;
            public Color Color = Color.gray;
        }

        private sealed class Unit
        {
            public GameObject Visual;
            public Archetype Archetype;
            public string Team = "enemy";
            public float Hp;
            public float MaxHp;
            public float AttackCooldown;
        }

        private readonly Dictionary<string, Archetype> _archetypes = new(System.StringComparer.OrdinalIgnoreCase);
        private readonly List<Unit> _units = new();
        private readonly List<string> _log = new();

        private ILuaModRuntime _mods;
        private UnitForgeLuaBindings _bindings;
        private string _status = "Starting...";
        private Vector2 _scroll;

        private void Start()
        {
            if (coreAiScope == null)
            {
                coreAiScope = FindFirstObjectByType<CoreAILifetimeScope>();
            }

            if (coreAiScope == null || coreAiScope.Container == null)
            {
                _status = "CoreAILifetimeScope not found in scene.";
                Debug.LogError($"[ModdableUnitsDemo] {_status}");
                enabled = false;
                return;
            }

            IObjectResolver luaContainer = CoreAiDemoScope.ResolveModsContainer(coreAiScope);

            _mods = luaContainer.Resolve<ILuaModRuntime>();
            _mods.ModEventEmitted += OnModEvent;
            _mods.ModSourceLoaded += OnModLoaded;

            // Expose the forge API to every WorldEdit-capable Lua script/mod created in this scene.
            _bindings = new UnitForgeLuaBindings(this);
            GameLuaBindingsExtensibility.Register(_bindings, LuaCapabilities.WorldEdit);

            EnsureUnitRoot();
            _status = "Empty arena. Ask chat to write a mod that forges units.";
            Log("No content yet - units and waves arrive only from Lua mods.");
        }

        private void OnDestroy()
        {
            if (_mods != null)
            {
                _mods.ModEventEmitted -= OnModEvent;
                _mods.ModSourceLoaded -= OnModLoaded;
            }

            if (_bindings != null)
            {
                GameLuaBindingsExtensibility.Unregister(_bindings);
            }
        }

        private void EnsureUnitRoot()
        {
            if (unitRoot == null)
            {
                GameObject root = new("ForgeUnitRoot");
                root.transform.position = Vector3.zero;
                unitRoot = root.transform;
            }
        }

        // ------------------------------------------------------------------ IUnitForge (called from Lua)

        public bool Define(string name, string team, double hp, double damage, double speed, double range,
            string colorHex)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string normalizedTeam = NormalizeTeam(team);
            Color color = ResolveColor(colorHex, normalizedTeam, name);
            _archetypes[name.Trim()] = new Archetype
            {
                Name = name.Trim(),
                Team = normalizedTeam,
                Hp = Mathf.Max(1f, (float)hp),
                Damage = Mathf.Max(0f, (float)damage),
                Speed = Mathf.Clamp((float)speed, 0f, 12f),
                Range = Mathf.Clamp((float)range, 0.3f, 8f),
                Color = color
            };
            Log(
                $"Defined unit '{name}' [{normalizedTeam}] hp={hp:0.#} dmg={damage:0.#} spd={speed:0.#} rng={range:0.#}.");
            return true;
        }

        public int Spawn(string name, double x, double z)
        {
            if (string.IsNullOrWhiteSpace(name) || !_archetypes.TryGetValue(name.Trim(), out Archetype archetype))
            {
                Log($"Spawn failed: '{name}' is not defined. Call forge_define first.");
                return 0;
            }

            if (_units.Count >= MaxUnits)
            {
                Log($"Spawn rejected: unit cap ({MaxUnits}) reached.");
                return 0;
            }

            Vector3 position = new(
                Mathf.Clamp((float)x, -ArenaHalfWidth, ArenaHalfWidth),
                0.5f,
                Mathf.Clamp((float)z, -ArenaHalfDepth, ArenaHalfDepth));

            GameObject visual = GameObject.CreatePrimitive(
                archetype.Team == "ally" ? PrimitiveType.Capsule : PrimitiveType.Cube);
            Infrastructure.World.CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(visual);
            visual.name = $"{archetype.Name}_{visual.GetInstanceID()}";
            visual.transform.SetParent(unitRoot, false);
            visual.transform.position = position;
            visual.transform.localScale = Vector3.one * 0.8f;
            SetRendererColor(visual, archetype.Color);

            _units.Add(new Unit
            {
                Visual = visual,
                Archetype = archetype,
                Team = archetype.Team,
                Hp = archetype.Hp,
                MaxHp = archetype.Hp
            });

            _mods?.EmitEvent("unit_spawned", $"{archetype.Name}:{archetype.Team}");
            return visual.GetInstanceID();
        }

        public int Count(string team)
        {
            if (string.IsNullOrWhiteSpace(team) || team.Trim().Equals("all", System.StringComparison.OrdinalIgnoreCase))
            {
                return _units.Count;
            }

            string normalized = NormalizeTeam(team);
            int count = 0;
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i].Team == normalized)
                {
                    count++;
                }
            }

            return count;
        }

        public void ClearUnits()
        {
            for (int i = 0; i < _units.Count; i++)
            {
                if (_units[i].Visual != null)
                {
                    Destroy(_units[i].Visual);
                }
            }

            _units.Clear();
            Log("All live units cleared (definitions kept).");
        }

        public void ResetAll()
        {
            ClearUnits();
            _archetypes.Clear();
            Log("Forge reset: all units and definitions removed.");
        }

        // ------------------------------------------------------------------ Auto-battle simulation

        private void Update()
        {
            if (_units.Count == 0)
            {
                return;
            }

            float dt = Time.deltaTime;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit unit = _units[i];
                if (unit.Visual == null)
                {
                    continue;
                }

                Unit target = FindNearestEnemy(unit);
                unit.AttackCooldown -= dt;

                if (target == null)
                {
                    continue;
                }

                Vector3 to = target.Visual.transform.position - unit.Visual.transform.position;
                float distance = to.magnitude;
                if (distance > unit.Archetype.Range)
                {
                    Vector3 step = to.normalized * (unit.Archetype.Speed * dt);
                    unit.Visual.transform.position += step;
                }
                else if (unit.AttackCooldown <= 0f)
                {
                    unit.AttackCooldown = 0.8f;
                    target.Hp -= unit.Archetype.Damage;
                    if (target.Hp <= 0f)
                    {
                        KillUnit(target);
                    }
                }
            }
        }

        private Unit FindNearestEnemy(Unit unit)
        {
            Unit best = null;
            float bestSqr = float.MaxValue;
            Vector3 from = unit.Visual.transform.position;
            for (int i = 0; i < _units.Count; i++)
            {
                Unit other = _units[i];
                if (other == unit || other.Visual == null || other.Team == unit.Team)
                {
                    continue;
                }

                float sqr = (other.Visual.transform.position - from).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = other;
                }
            }

            return best;
        }

        private void KillUnit(Unit unit)
        {
            string team = unit.Team;
            if (unit.Visual != null)
            {
                Destroy(unit.Visual);
            }

            _units.Remove(unit);
            _mods?.EmitEvent("unit_died", $"{unit.Archetype.Name}:{team}");

            if (Count(team) == 0)
            {
                _mods?.EmitEvent("team_wiped", team);
                Log($"Team '{team}' was wiped out.");
            }
        }

        // ------------------------------------------------------------------ Mod event bridge + UI

        private void OnModEvent(string modId, string eventName, string payload)
        {
            Log($"Mod event: {modId} -> {eventName}({payload}).");
        }

        private void OnModLoaded(string modId, string source, LuaCapabilities caps)
        {
            Log($"Mod loaded: {modId} (caps={caps}).");
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
            GUILayout.BeginArea(new Rect(12, 12, 560, 540), GUI.skin.box);
            GUILayout.Label("<b>CoreAI - Unit Forge (mod-driven game)</b>", RichLabel());
            GUILayout.Label(_status, RichLabel());

            GUILayout.Space(4);
            GUILayout.Label(
                $"Allies: <b>{Count("ally")}</b>   Enemies: <b>{Count("enemy")}</b>   Defined types: <b>{_archetypes.Count}</b>",
                RichLabel());

            GUILayout.Space(4);
            GUILayout.Label("<b>Unit types (forged by mods)</b>", RichLabel());
            if (_archetypes.Count == 0)
            {
                GUILayout.Label("None yet - ask chat to write a mod that calls forge_define.");
            }
            else
            {
                foreach (KeyValuePair<string, Archetype> entry in _archetypes)
                {
                    Archetype a = entry.Value;
                    GUILayout.Label(
                        $"* {a.Name} [{a.Team}] hp={a.Hp:0.#} dmg={a.Damage:0.#} spd={a.Speed:0.#} rng={a.Range:0.#}");
                }
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Loaded mods</b>", RichLabel());
            if (_mods != null)
            {
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
                            $"* {mod.Id} handlers={mod.HandlerCount} timers={mod.TimerCount} errors={mod.ErrorCount}");
                    }
                }
            }

            GUILayout.Space(4);
            GUILayout.Label("<b>Event log</b>", RichLabel());
            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.Height(150));
            foreach (string line in _log)
            {
                GUILayout.Label(line);
            }

            GUILayout.EndScrollView();
            GUILayout.EndArea();
        }

        private static string NormalizeTeam(string team)
        {
            if (!string.IsNullOrWhiteSpace(team) &&
                team.Trim().Equals("ally", System.StringComparison.OrdinalIgnoreCase))
            {
                return "ally";
            }

            return "enemy";
        }

        private static Color ResolveColor(string colorHex, string team, string name)
        {
            if (!string.IsNullOrWhiteSpace(colorHex) &&
                ColorUtility.TryParseHtmlString(colorHex.Trim(), out Color parsed))
            {
                return parsed;
            }

            // Stable fallback: tint by team, vary hue by archetype name hash.
            float hueJitter = Mathf.Abs(name.GetHashCode()) % 100 / 100f * 0.12f;
            return team == "ally"
                ? new Color(0.2f + hueJitter, 0.6f, 1f)
                : new Color(1f, 0.35f + hueJitter, 0.25f);
        }

        private static void SetRendererColor(GameObject go, Color color)
        {
            if (go != null && go.TryGetComponent(out Renderer renderer))
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ??
                                Shader.Find("Standard") ??
                                Shader.Find("Unlit/Color");
                Material material = shader != null ? new Material(shader) : new Material(renderer.sharedMaterial);
                material.color = color;
                renderer.sharedMaterial = material;
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
                "[ModdableUnitsDemo] MoonSharp is unavailable or COREAI_NO_LUA is set; demo is inactive.");
            enabled = false;
        }
#endif
    }
}