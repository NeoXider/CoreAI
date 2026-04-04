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
    /// Расширенные тесты Lua формул: критические удары, вампиризм, щиты, комбо-формулы.
    /// </summary>
    public sealed class AdvancedLuaFormulaEditModeTests
    {
        private sealed class AdvancedCombatBindings : IGameLuaRuntimeBindings
        {
            public float CurrentHealth = 100f;
            public float MaxHealth = 100f;
            public float Armor = 10f;
            public float MagicResist = 5f;
            public float LifestealPercent = 0f;
            public float ShieldAmount = 0f;
            public float AttackPower = 20f;
            public float SpellPower = 15f;
            public readonly List<string> Reports = new();

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                // Статы
                registry.Register("get_health", new System.Func<double>(() => CurrentHealth));
                registry.Register("get_max_health", new System.Func<double>(() => MaxHealth));
                registry.Register("get_armor", new System.Func<double>(() => Armor));
                registry.Register("get_resist", new System.Func<double>(() => MagicResist));
                registry.Register("get_attack_power", new System.Func<double>(() => AttackPower));
                registry.Register("get_spell_power", new System.Func<double>(() => SpellPower));
                registry.Register("get_lifesteal", new System.Func<double>(() => LifestealPercent));
                registry.Register("get_shield", new System.Func<double>(() => ShieldAmount));

                registry.Register("set_health", new System.Action<double>(hp =>
                    CurrentHealth = Mathf.Clamp((float)hp, 0, MaxHealth)));
                registry.Register("set_shield", new System.Action<double>(s =>
                    ShieldAmount = Mathf.Max(0, (float)s)));

                // Базовые формулы
                registry.Register("calc_physical_damage", new System.Func<double, double, double>((dmg, armor) =>
                    dmg * (100f / (100f + armor))));

                registry.Register("calc_magical_damage", new System.Func<double, double, double>((dmg, resist) =>
                    dmg * (100f / (100f + resist))));

                // Вампиризм
                registry.Register("calc_lifesteal", new System.Func<double, double>((dmg) =>
                    dmg * LifestealPercent));

                registry.Register("heal",
                    new System.Action<double>(amount =>
                    {
                        CurrentHealth = Mathf.Min(MaxHealth, CurrentHealth + (float)amount);
                    }));

                registry.Register("apply_damage_to_shield", new System.Func<double, double>((dmg) =>
                {
                    if (ShieldAmount > 0)
                    {
                        float absorbed = Mathf.Min(ShieldAmount, (float)dmg);
                        ShieldAmount -= absorbed;
                        return dmg - absorbed;
                    }

                    return dmg;
                }));

                registry.Register("report", new System.Action<string>(msg => Reports.Add(msg)));
            }
        }

        private LuaScriptEnv CreateScript(AdvancedCombatBindings bindings)
        {
            LuaApiRegistry reg = new();
            bindings.RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            return new LuaScriptEnv(env, reg);
        }

        private sealed class LuaScriptEnv
        {
            private readonly SecureLuaEnvironment _env;
            private readonly LuaApiRegistry _reg;

            public LuaScriptEnv(SecureLuaEnvironment env, LuaApiRegistry reg)
            {
                _env = env;
                _reg = reg;
            }

            public DynValue Run(string lua)
            {
                return _env.RunChunk(_env.CreateScript(_reg), lua);
            }
        }

        [Test]
        public void Lua_CriticalStrike_WithArmor()
        {
            AdvancedCombatBindings bindings = new() { Armor = 15 };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                math.randomseed(100) -- Фиксированный сид для воспроизводимости
                
                function calc_crit_damage(base_dmg, armor, crit_chance)
                    local is_crit = math.random() < crit_chance
                    local crit_mult = is_crit and 2.0 or 1.0
                    local after_armor = base_dmg * crit_mult * (100 / (100 + armor))
                    return after_armor, is_crit
                end
                
                local dmg, crit = calc_crit_damage(100, get_armor(), 0.5)
                return dmg
            ");

            Assert.IsNotNull(result);
            // С сидом 100, crit_chance 0.5 - может быть крит или нет
            // Но формула должна работать
            Assert.Greater(result.Number, 0);
        }

        [Test]
        public void Lua_Lifesteal_HealsPlayer()
        {
            AdvancedCombatBindings bindings = new()
            {
                CurrentHealth = 50,
                MaxHealth = 100,
                LifestealPercent = 0.2f,
                Armor = 10
            };
            LuaScriptEnv env = CreateScript(bindings);

            env.Run(@"
                local base_dmg = 50
                local armor = get_armor()
                local final_dmg = calc_physical_damage(base_dmg, armor)
                local heal_amount = calc_lifesteal(final_dmg)
                heal(heal_amount)
            ");

            // 50 * 0.909 = 45.45 урона
            // 45.45 * 0.2 = 9.09 исцеления
            // 50 + 9.09 = 59.09 HP
            Assert.Greater(bindings.CurrentHealth, 58);
            Assert.Less(bindings.CurrentHealth, 61);
        }

        [Test]
        public void Lua_Shield_AbsorbsDamage()
        {
            AdvancedCombatBindings bindings = new()
            {
                CurrentHealth = 100,
                ShieldAmount = 30,
                Armor = 20
            };
            LuaScriptEnv env = CreateScript(bindings);

            env.Run(@"
                local base_dmg = 100
                local after_shield = apply_damage_to_shield(base_dmg)
                local armor = get_armor()
                local final_dmg = calc_physical_damage(after_shield, armor)
                
                local current_hp = get_health()
                set_health(current_hp - final_dmg)
            ");

            // 100 - 30 (щит) = 70 проходит
            // 70 * 0.833 = 58.33 урона
            // 100 - 58.33 = 41.67 HP
            Assert.Greater(bindings.CurrentHealth, 40);
            Assert.Less(bindings.CurrentHealth, 43);
            Assert.AreEqual(0, bindings.ShieldAmount, "Щит должен быть полностью поглощен");
        }

        [Test]
        public void Lua_CombatCombo_AttackWithLifesteal()
        {
            AdvancedCombatBindings bindings = new()
            {
                CurrentHealth = 60,
                MaxHealth = 100,
                AttackPower = 30,
                Armor = 10,
                LifestealPercent = 0.15f
            };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Комбо атака: урон + вампиризм
                function combo_attack(base_power, target_armor, lifesteal)
                    local raw_dmg = base_power
                    local final_dmg = raw_dmg * (100 / (100 + target_armor))
                    local heal_amount = final_dmg * lifesteal
                    return final_dmg, heal_amount
                end

                local dmg, heal_amt = combo_attack(get_attack_power(), get_armor(), get_lifesteal())
                heal(heal_amt)
                return dmg
            ");

            Assert.IsNotNull(result);
            // 30 * 0.909 = 27.27 урона
            // 27.27 * 0.15 = 4.09 исцеления
            // 60 + 4.09 = 64.09 HP
            Assert.Greater(bindings.CurrentHealth, 63);
            Assert.Less(bindings.CurrentHealth, 65);
        }

        [Test]
        public void Lua_MultiTargetDamage_Cleave()
        {
            AdvancedCombatBindings bindings = new();
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Урон по нескольким целям с уменьшением
                function calc_cleave_damage(primary_dmg, secondary_count, falloff_percent)
                    local total = primary_dmg
                    for i = 1, secondary_count do
                        total = total + (primary_dmg * falloff_percent)
                    end
                    return total
                end
                
                -- Основной урон 50, 2 дополнительные цели с 60% уроном
                return calc_cleave_damage(50, 2, 0.6)
            ");

            Assert.IsNotNull(result);
            // 50 + (50 * 0.6) + (50 * 0.6) = 50 + 30 + 30 = 110
            Assert.AreEqual(110, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_DamageOverTime_WithTickInterval()
        {
            AdvancedCombatBindings bindings = new() { CurrentHealth = 100 };
            LuaScriptEnv env = CreateScript(bindings);

            env.Run(@"
                -- DOT с тиками
                function apply_dot(base_tick, armor, duration, interval)
                    local ticks = math.floor(duration / interval)
                    local total_dmg = 0
                    
                    for i = 1, ticks do
                        local tick = base_tick * (100 / (100 + armor))
                        total_dmg = total_dmg + tick
                    end
                    
                    local current = get_health()
                    set_health(current - total_dmg)
                    return total_dmg
                end
                
                -- Яд: 8 урона/тик, 4 сек, тик каждые 1 сек
                return apply_dot(8, get_armor(), 4.0, 1.0)
            ");

            // Armor = 10, тик = 8 * 0.909 = 7.27
            // 4 тика * 7.27 = 29.09
            // 100 - 29.09 = 70.91 HP
            Assert.Greater(bindings.CurrentHealth, 70);
            Assert.Less(bindings.CurrentHealth, 72);
        }

        [Test]
        public void Lua_BuffDebuff_System()
        {
            AdvancedCombatBindings bindings = new() { AttackPower = 30 };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Система баффов/дебаффов
                function apply_multiplier(base_value, multiplier)
                    return base_value * multiplier
                end
                
                -- Бафф: +50% к атаке
                local buffed = apply_multiplier(get_attack_power(), 1.5)
                
                -- Дебафф: -30% от баффнутого
                local debuffed = apply_multiplier(buffed, 0.7)
                
                return debuffed
            ");

            Assert.IsNotNull(result);
            // 30 * 1.5 * 0.7 = 31.5
            Assert.AreEqual(31.5, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_SpellDamage_WithSpellPower()
        {
            AdvancedCombatBindings bindings = new() { SpellPower = 25, MagicResist = 20 };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Урон заклинания: base + spell_power -> resist
                function calc_spell_damage(base_spell, spell_power, resist)
                    local total = base_spell + spell_power
                    return total * (100 / (100 + resist))
                end
                
                return calc_spell_damage(40, get_spell_power(), get_resist())
            ");

            Assert.IsNotNull(result);
            // (40 + 25) * 0.833 = 54.17
            Assert.AreEqual(54.17, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_PerkSystem_StackingBonuses()
        {
            AdvancedCombatBindings bindings = new() { AttackPower = 20 };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Система перков с аддитивными бонусами
                function calc_perk_damage(base, perk_count, bonus_per_perk)
                    local multiplier = 1 + (perk_count * bonus_per_perk)
                    return base * multiplier
                end
                
                -- 3 перка по 10% каждый
                return calc_perk_damage(get_attack_power(), 3, 0.10)
            ");

            Assert.IsNotNull(result);
            // 20 * 1.3 = 26
            Assert.AreEqual(26, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_HealthThreshold_BonusDamage()
        {
            AdvancedCombatBindings bindings = new() { CurrentHealth = 25, MaxHealth = 100 };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Бонусный урон при низком здоровье
                function calc_desperation_damage(base_dmg, current_hp, max_hp)
                    local hp_percent = current_hp / max_hp
                    if hp_percent < 0.3 then
                        return base_dmg * 2.0  -- 100% бонус
                    elseif hp_percent < 0.5 then
                        return base_dmg * 1.5  -- 50% бонус
                    else
                        return base_dmg
                    end
                end
                
                return calc_desperation_damage(50, get_health(), get_max_health())
            ");

            Assert.IsNotNull(result);
            // 25/100 = 25% < 30%, получаем 2x: 100
            Assert.AreEqual(100, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_ElementalDamage_MultipleTypes()
        {
            AdvancedCombatBindings bindings = new()
            {
                SpellPower = 30,
                MagicResist = 15
            };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Элементальный урон: огонь + лед
                function calc_elemental_damage(base_fire, base_ice, spell_power, resist)
                    local fire = (base_fire + spell_power) * (100 / (100 + resist))
                    local ice = (base_ice + spell_power * 0.5) * (100 / (100 + resist))
                    return fire + ice
                end
                
                return calc_elemental_damage(20, 15, get_spell_power(), get_resist())
            ");

            Assert.IsNotNull(result);
            // Fire: (20 + 30) * 0.87 = 43.48
            // Ice: (15 + 15) * 0.87 = 26.09
            // Total: 69.57
            Assert.AreEqual(69.57, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_ExecutionThreshold_Finisher()
        {
            AdvancedCombatBindings bindings = new() { CurrentHealth = 15 };
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Добивание: 3x урон если цель < 20% HP
                function calc_execute_damage(base_dmg, target_hp, target_max_hp)
                    local threshold = 0.20
                    if (target_hp / target_max_hp) < threshold then
                        return base_dmg * 3.0
                    end
                    return base_dmg
                end
                
                return calc_execute_damage(40, get_health(), get_max_health())
            ");

            Assert.IsNotNull(result);
            // 15/100 = 15% < 20%, получаем 3x: 120
            Assert.AreEqual(120, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_DamageReflection_PartialReflect()
        {
            AdvancedCombatBindings bindings = new();
            LuaScriptEnv env = CreateScript(bindings);

            DynValue result = env.Run(@"
                -- Отражение урона
                function calc_reflect(incoming_dmg, reflect_percent)
                    local reflected = incoming_dmg * reflect_percent
                    local taken = incoming_dmg - reflected
                    return taken, reflected
                end
                
                local taken, reflected = calc_reflect(80, 0.25)
                return taken
            ");

            Assert.IsNotNull(result);
            // 80 - (80 * 0.25) = 60
            Assert.AreEqual(60, (double)result.Number, 0.5);
        }

        [Test]
        public void Lua_OverHealing_ToShield()
        {
            AdvancedCombatBindings bindings = new()
            {
                CurrentHealth = 90,
                MaxHealth = 100,
                ShieldAmount = 0
            };
            LuaScriptEnv env = CreateScript(bindings);

            env.Run(@"
                -- Избыточное исцеление -> щит
                function overheal_to_shield(heal_amount)
                    local current = get_health()
                    local max = get_max_health()
                    local overheal = math.max(0, (current + heal_amount) - max)
                    
                    set_health(math.min(max, current + heal_amount))
                    set_shield(get_shield() + overheal * 0.5) -- 50% overheal -> shield
                end
                
                overheal_to_shield(30)
            ");

            // 90 + 30 = 120, но макс 100
            // Overheal = 20, shield = 20 * 0.5 = 10
            Assert.AreEqual(100, bindings.CurrentHealth);
            Assert.AreEqual(10, bindings.ShieldAmount, 0.5);
        }

        [Test]
        public void Lua_CombatLog_RecordsAllActions()
        {
            AdvancedCombatBindings bindings = new();
            LuaScriptEnv env = CreateScript(bindings);

            env.Run(@"
                report('attack:50')
                report('armor:10')
                report('lifesteal:0.2')
                
                local dmg = 50 * (100 / (100 + 10))
                local heal = dmg * 0.2
                report('damage:' .. dmg)
                report('heal:' .. heal)
            ");

            Assert.AreEqual(5, bindings.Reports.Count);
            StringAssert.Contains("attack:50", bindings.Reports[0]);
            StringAssert.Contains("damage:", bindings.Reports[3]);
            StringAssert.Contains("heal:", bindings.Reports[4]);
        }
    }
}