#if !COREAI_NO_LUA
using System;
using CoreAI.Ai.LuaCs;
using CoreAI.Sandbox.LuaCs;

namespace CoreAI.Demos
{
    /// <summary>
    /// Host contract driven by <see cref="UnitForgeLuaBindings"/>. The demo scene controller
    /// implements it so Lua mods can author and spawn entirely new unit types at runtime — the
    /// game ships empty and every unit/wave comes from a mod.
    /// </summary>
    public interface IUnitForge
    {
        /// <summary>Registers (or overwrites) a unit archetype. Returns true when accepted.</summary>
        bool Define(string name, string team, double hp, double damage, double speed, double range, string colorHex);

        /// <summary>Spawns a live instance of a defined archetype. Returns its instance id, or 0 on failure.</summary>
        int Spawn(string name, double x, double z);

        /// <summary>Counts live units on a team: <c>"ally"</c>, <c>"enemy"</c>, or <c>"all"</c>.</summary>
        int Count(string team);

        /// <summary>Removes all live units but keeps archetype definitions.</summary>
        void ClearUnits();

        /// <summary>Removes all live units and all archetype definitions.</summary>
        void ResetAll();
    }

    /// <summary>
    /// Lua API that lets mods extend the game with brand-new content. Implemented as a Lua-CSharp
    /// gameplay binding set (<see cref="ILuaCsGameRuntimeBindings"/>) that a host wires into the mod
    /// runtime at the WorldEdit tier, so only world-editing mods can forge units.
    /// Exposed functions:
    /// <list type="bullet">
    /// <item><c>forge_define(name, team, hp, dmg, speed, range, color)</c> → bool</item>
    /// <item><c>forge_spawn(name, x, z)</c> → instance id (0 on failure)</item>
    /// <item><c>forge_count(team)</c> → int (<c>"ally"</c> | <c>"enemy"</c> | <c>"all"</c>)</item>
    /// <item><c>forge_clear()</c> — remove live units, keep definitions</item>
    /// <item><c>forge_reset()</c> — remove units and definitions</item>
    /// </list>
    /// Deliberately uses only plain CLR types (no direct Lua VM types) in its host-facing surface, so
    /// this demo assembly stays decoupled from the concrete Lua runtime.
    /// </summary>
    public sealed class UnitForgeLuaBindings : ILuaCsGameRuntimeBindings
    {
        private readonly IUnitForge _forge;

        public UnitForgeLuaBindings(IUnitForge forge)
        {
            _forge = forge ?? throw new ArgumentNullException(nameof(forge));
        }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("forge_define",
                new Func<string, string, double, double, double, double, string, bool>(Define));
            registry.Register("forge_spawn",
                new Func<string, double, double, int>((name, x, z) => _forge.Spawn(name, x, z)));
            registry.Register("forge_count", new Func<string, int>(team => _forge.Count(team)));
            registry.Register("forge_clear", new Action(() => _forge.ClearUnits()));
            registry.Register("forge_reset", new Action(() => _forge.ResetAll()));
        }

        // Missing Lua arguments arrive as default(T) (null/0), so apply friendly defaults here.
        private bool Define(string name, string team, double hp, double damage, double speed, double range,
            string color)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("forge_define: a non-empty unit name is required.");
            }

            return _forge.Define(
                name,
                string.IsNullOrWhiteSpace(team) ? "enemy" : team,
                hp <= 0d ? 20d : hp,
                damage < 0d ? 4d : damage,
                speed <= 0d ? 1.5d : speed,
                range <= 0d ? 1d : range,
                color);
        }
    }
}
#endif