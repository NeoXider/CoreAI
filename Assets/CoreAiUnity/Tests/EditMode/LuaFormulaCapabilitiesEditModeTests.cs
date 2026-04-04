using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.LuaFormulas
{
    /// <summary>
    /// Тесты для проверки возможностей модификации формул через Lua.
    /// Проверяет что AI может менять формулы урона, уровней и добавлять новые механики.
    /// </summary>
    public sealed class LuaFormulaCapabilitiesEditModeTests
    {
        private sealed class TestCombatBindings : IGameLuaRuntimeBindings
        {
            public float CurrentHealth = 100f;
            public float Armor = 10f;
            public float MagicResist = 5f;
            public readonly List<string> Reports = new();

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                registry.Register("get_health", new System.Func<double>(() => CurrentHealth));
                registry.Register("get_armor", new System.Func<double>(() => Armor));
                registry.Register("get_resist", new System.Func<double>(() => MagicResist));
                registry.Register("set_health", new System.Action<double>(hp => CurrentHealth = (float)hp));

                // Формула: урон с учетом брони
                registry.Register("calc_damage_armor", new System.Func<double, double, double>((dmg, armor) =>
                    dmg * (100f / (100f + armor))));

                // Формула: урон с учетом резиста
                registry.Register("calc_damage_resist", new System.Func<double, double, double>((dmg, resist) =>
                    dmg * (1f - resist / 100f)));

                registry.Register("apply_damage", new System.Action<double>(dmg =>
                    CurrentHealth = Mathf.Max(0, CurrentHealth - (float)dmg)));

                registry.Register("report", new System.Action<string>(msg => Reports.Add(msg)));
            }
        }

        [Test]
        public void Lua_CanCalculateDamageWithArmor()
        {
            TestCombatBindings bindings = new();
            bindings.Armor = 20;
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                local base_dmg = 100
                local armor = get_armor()
                local final_dmg = calc_damage_armor(base_dmg, armor)
                return final_dmg
            ");

            Assert.IsNotNull(result);
            // 100 * (100/120) = 83.33
            Assert.AreEqual(83.33, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_CanCalculateDamageWithResist()
        {
            TestCombatBindings bindings = new();
            bindings.MagicResist = 30;
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                local base_dmg = 100
                local resist = get_resist()
                local final_dmg = calc_damage_resist(base_dmg, resist)
                return final_dmg
            ");

            Assert.IsNotNull(result);
            // 100 * (1 - 30/100) = 70
            Assert.AreEqual(70, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_CanChainMultipleFormulas()
        {
            TestCombatBindings bindings = new();
            bindings.Armor = 15;
            bindings.MagicResist = 20;
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                local base_dmg = 100
                local armor = get_armor()
                local resist = get_resist()
                
                -- Применяем броню
                local after_armor = calc_damage_armor(base_dmg, armor)
                -- Применяем резист
                local final_dmg = calc_damage_resist(after_armor, resist)
                
                return final_dmg
            ");

            Assert.IsNotNull(result);
            // 100 * 0.8696 * 0.8 = 69.57
            Assert.AreEqual(69.57, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_CanApplyDamageToHealth()
        {
            TestCombatBindings bindings = new();
            bindings.CurrentHealth = 100;
            bindings.Armor = 25;
            LuaScriptEnvironment env = CreateScript(bindings);

            env.RunChunkScript(@"
                local dmg = 50
                local armor = get_armor()
                local final_dmg = calc_damage_armor(dmg, armor)
                apply_damage(final_dmg)
            ");

            // 50 * 0.8 = 40 урона, 100 - 40 = 60 HP
            Assert.AreEqual(60, bindings.CurrentHealth, 1);
        }

        [Test]
        public void Lua_CanDefineCustomDamageFormula()
        {
            TestCombatBindings bindings = new();
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                -- AI создает новую формулу: комбинированный урон
                function combined_damage(base_dmg, armor, resist)
                    local armor_factor = 100 / (100 + armor)
                    local resist_factor = 1 - (resist / 100)
                    return base_dmg * armor_factor * resist_factor
                end
                
                return combined_damage(100, 10, 5)
            ");

            Assert.IsNotNull(result);
            // 100 * 0.909 * 0.95 = 86.36
            Assert.AreEqual(86.36, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_CanCalculateLevelProgression()
        {
            TestCombatBindings bindings = new();
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                -- Формула: XP для уровня N = 100 * N^2
                function xp_for_level(n)
                    return 100 * n * n
                end
                
                return xp_for_level(5)
            ");

            Assert.IsNotNull(result);
            // 100 * 25 = 2500
            Assert.AreEqual(2500, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_CanModifyStatsDynamically()
        {
            TestCombatBindings bindings = new();
            bindings.CurrentHealth = 100;
            LuaScriptEnvironment env = CreateScript(bindings);

            env.RunChunkScript(@"
                set_health(150)
            ");

            Assert.AreEqual(150, bindings.CurrentHealth);
        }

        [Test]
        public void Lua_CanImplementDamageOverTime()
        {
            TestCombatBindings bindings = new();
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                -- DOT формула
                function calculate_dot(base_tick, duration, interval)
                    local ticks = math.floor(duration / interval)
                    return base_tick * ticks
                end
                
                return calculate_dot(10, 5.0, 1.0)
            ");

            Assert.IsNotNull(result);
            // 10 * 5 = 50
            Assert.AreEqual(50, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_CanImplementScalingDamage()
        {
            TestCombatBindings bindings = new();
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                -- Скалирование от уровня
                function scale_damage(base, level, scaling)
                    return base + (level * scaling)
                end
                
                return scale_damage(20, 10, 5)
            ");

            Assert.IsNotNull(result);
            // 20 + 50 = 70
            Assert.AreEqual(70, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_CanImplementConditionalDamage()
        {
            TestCombatBindings bindings = new();
            bindings.Armor = 25;
            LuaScriptEnvironment env = CreateScript(bindings);

            DynValue result = env.RunChunkScript(@"
                function smart_damage(base_dmg, armor)
                    if armor > 20 then
                        -- Высокая броня: больше магического урона
                        return base_dmg * 0.4
                    else
                        -- Низкая броня: больше физического
                        return base_dmg * 0.7
                    end
                end
                
                return smart_damage(100, get_armor())
            ");

            Assert.IsNotNull(result);
            // Броня 25 > 20, используем 40%: 40
            Assert.AreEqual(40, (double)result.Number, 0.5);
        }

        private LuaScriptEnvironment CreateScript(TestCombatBindings bindings)
        {
            LuaApiRegistry reg = new();
            bindings.RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            env.CreateScript(reg);
            return new LuaScriptEnvironment(env, reg);
        }

        private sealed class LuaScriptEnvironment
        {
            private readonly SecureLuaEnvironment _env;
            private readonly LuaApiRegistry _reg;

            public LuaScriptEnvironment(SecureLuaEnvironment env, LuaApiRegistry reg)
            {
                _env = env;
                _reg = reg;
            }

            public DynValue RunChunkScript(string lua)
            {
                Script script = _env.CreateScript(_reg);
                return _env.RunChunk(script, lua);
            }
        }
    }
}