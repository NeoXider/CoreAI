using System.Collections.Generic;
using UnityEngine;
#if COREAI_LUA
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Demos.Shared;
using CoreAI.Sandbox.LuaCs;
using VContainer;
#endif

namespace CoreAI.Demos
{
    /// <summary>
    /// "Unit Forge" — an empty arena that ships with <b>no units and no behaviour</b>. Everything
    /// is added by Lua mods written through chat: a mod calls <c>forge_define(...)</c> to author a
    /// new unit type, <c>forge_spawn(name, x, z)</c> to deploy it, and uses <c>hooks_every</c> to
    /// stream reinforcements. The host only runs a tiny auto-battle: every unit walks toward the
    /// nearest enemy and attacks in range. The whole game emerges from mods.
    /// </summary>
    public sealed class ModdableUnitsDemoController : MonoBehaviour
#if COREAI_LUA
        , IUnitForge, ICoreAiLuaGameplayBindingsProvider
#endif
    {
#if COREAI_LUA
        private const int MaxLogLines = 12;
        private const int MaxUnits = 64;
        private const float ArenaHalfWidth = 7f;
        private const float ArenaHalfDepth = 4.5f;

        [Tooltip("Explicit mods scope supplying this controller's runtime and trusted host identity.")]
        [SerializeField]
        private CoreAiModsLifetimeScope modsScope;

        [Tooltip("Parent for spawned unit visuals. Created automatically when empty.")]
        [SerializeField]
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
        private ActorContext _actorContext;
        private UnitForgeLuaBindings _bindings;
        private CoreAiDemoPanel _panel;
        private string _status = "Starting...";

        private void Start() => InitializePresentation();

        /// <summary>Initializes the runtime view after host injection; repeated calls retain one panel and current units.</summary>
        public void InitializePresentation()
        {
            if (_panel == null)
            {
                _panel = CoreAiDemoPanel.Create(
                    "CoreAI — Unit Forge",
                    "Empty arena. No units and no behaviour ship with it: Lua mods forge and spawn units.");
                _panel.transform.SetParent(transform, false);
            }

            EnsureUnitRoot();
            if (_mods == null && modsScope != null && modsScope.Container != null)
            {
                IObjectResolver luaContainer = modsScope.Container;
                IActorIdentityProvider actorIdentityProvider = luaContainer.Resolve<IActorIdentityProvider>();
                AttachModRuntime(luaContainer.Resolve<ILuaModRuntime>(),
                    actorIdentityProvider.GetActorContext(BuiltInAgentRoleIds.Programmer));
            }

            if (_mods == null)
            {
                _status = "Assign the scene's CoreAiModsLifetimeScope to modsScope.";
                RefreshStatus();
                Debug.LogError($"[ModdableUnitsDemo] {_status}");
                enabled = false;
                return;
            }

            _status = _archetypes.Count == 0 && _units.Count == 0
                ? "Empty arena. Ask chat to write a mod that forges units."
                : "Unit Forge ready. Restored mod content is preserved.";
            enabled = true;
            RefreshStatus();
        }

        /// <summary>Installs lazy callbacks at the VM's effective tier, including before Start.</summary>
        public void RegisterGameplayBindings(LuaCsApiRegistry registry, LuaCapabilities capabilities)
        {
            if (this == null)
            {
                throw new System.ObjectDisposedException(nameof(ModdableUnitsDemoController));
            }

            _bindings ??= new UnitForgeLuaBindings(this);
            _bindings.RegisterGameplayApis(registry, capabilities);
        }

        /// <summary>Attaches a host-supplied runtime and identity, replacing any previous event bridge.</summary>
        public void AttachModRuntime(ILuaModRuntime runtime, ActorContext actorContext)
        {
            if (runtime == null)
            {
                throw new System.ArgumentNullException(nameof(runtime));
            }

            if (!actorContext.IsTrusted || !actorContext.Grants.IsUnrestricted)
            {
                throw new System.ArgumentException("A trusted unrestricted host identity is required.",
                    nameof(actorContext));
            }

            DetachModRuntime();
            runtime.AddModEventEmittedListener(actorContext, OnModEvent);
            try
            {
                runtime.AddModSourceLoadedListener(actorContext, OnModLoaded);
            }
            catch
            {
                runtime.RemoveModEventEmittedListener(actorContext, OnModEvent);
                throw;
            }

            _actorContext = actorContext;
            _mods = runtime;
            RefreshStatus();
        }

        private void DetachModRuntime()
        {
            ILuaModRuntime previous = _mods;
            _mods = null;
            if (previous == null) return;
            try
            {
                previous.RemoveModEventEmittedListener(_actorContext, OnModEvent);
            }
            finally
            {
                previous.RemoveModSourceLoadedListener(_actorContext, OnModLoaded);
            }
        }

        private void OnDestroy()
        {
            DetachModRuntime();
            _bindings = null;
            if (_panel != null)
            {
                if (Application.isPlaying) Destroy(_panel.gameObject);
                else DestroyImmediate(_panel.gameObject);
            }
        }

        private void EnsureUnitRoot()
        {
            if (unitRoot == null)
            {
                GameObject root = new("ForgeUnitRoot");
                root.transform.SetParent(transform, false);
                unitRoot = root.transform;
            }
        }

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

            EnsureUnitRoot();
            GameObject visual = GameObject.CreatePrimitive(
                archetype.Team == "ally" ? PrimitiveType.Capsule : PrimitiveType.Cube);
            Infrastructure.World.CoreAiPrimitiveFactory.EnsureRenderPipelineCompatibleMaterial(visual);
            visual.name = $"{archetype.Name}_{visual.GetEntityId().GetHashCode()}";
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

            _mods?.EmitEvent(_actorContext, "unit_spawned", $"{archetype.Name}:{archetype.Team}");
            RefreshStatus();
            return visual.GetEntityId().GetHashCode();
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
            _mods?.EmitEvent(_actorContext, "unit_died", $"{unit.Archetype.Name}:{team}");

            if (Count(team) == 0)
            {
                _mods?.EmitEvent(_actorContext, "team_wiped", team);
                Log($"Team '{team}' was wiped out.");
            }
            else
            {
                RefreshStatus();
            }
        }

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

            RefreshStatus();
        }

        /// <summary>Recomputes the status block (counts, unit types, loaded mods, event log) shown in the panel.</summary>
        private void RefreshStatus()
        {
            if (_panel == null) return;
            _panel.SetLog(
                $"{_status}\n\n" +
                $"Allies: {Count("ally")}   Enemies: {Count("enemy")}   Defined types: {_archetypes.Count}\n\n" +
                $"Unit types (forged by mods):\n{BuildArchetypeList()}\n\n" +
                $"Loaded mods:\n{BuildModList()}\n\n" +
                $"Event log:\n{string.Join("\n", _log)}");
        }

        private string BuildArchetypeList()
        {
            if (_archetypes.Count == 0)
            {
                return "None yet - ask chat to write a mod that calls forge_define.";
            }

            List<string> lines = new();
            foreach (KeyValuePair<string, Archetype> entry in _archetypes)
            {
                Archetype a = entry.Value;
                lines.Add($"* {a.Name} [{a.Team}] hp={a.Hp:0.#} dmg={a.Damage:0.#} spd={a.Speed:0.#} rng={a.Range:0.#}");
            }

            return string.Join("\n", lines);
        }

        private string BuildModList()
        {
            if (_mods == null)
            {
                return "No mods loaded.";
            }

            IReadOnlyList<LuaModInfo> mods = _mods.ListMods(_actorContext);
            if (mods.Count == 0)
            {
                return "No mods loaded.";
            }

            List<string> lines = new();
            foreach (LuaModInfo mod in mods)
            {
                lines.Add($"* {mod.Id} handlers={mod.HandlerCount} timers={mod.TimerCount} errors={mod.ErrorCount}");
            }

            return string.Join("\n", lines);
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

            // WHY: each archetype retains a stable fallback tint within its team.
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
#else
        private void Start()
        {
            Debug.LogWarning(
                "[ModdableUnitsDemo] COREAI_LUA is not set; demo is inactive.");
            enabled = false;
        }
#endif
    }
}
