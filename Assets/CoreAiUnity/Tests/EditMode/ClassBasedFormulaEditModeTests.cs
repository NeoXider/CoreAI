using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;
using System.Collections.Generic;

namespace CoreAI.Tests.EditMode.LuaFormulas
{
    /// <summary>
    /// Тесты формул урона для разных классов/ролей (воин, маг, лучник и т.д.).
    /// Проверяет что AI может создавать уникальные формулы для каждого класса.
    /// </summary>
    public sealed class ClassBasedFormulaEditModeTests
    {
        private sealed class CharacterStats
        {
            public float Strength = 10f;
            public float Agility = 10f;
            public float Intelligence = 10f;
            public float Vitality = 10f;
            public float CurrentHealth = 100f;
            public readonly Dictionary<string, float> Buffs = new();
        }

        private sealed class ClassBindings : IGameLuaRuntimeBindings
        {
            private readonly CharacterStats _stats;

            public ClassBindings(CharacterStats stats)
            {
                _stats = stats;
            }

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                registry.Register("get_str", new System.Func<double>(() => _stats.Strength));
                registry.Register("get_agi", new System.Func<double>(() => _stats.Agility));
                registry.Register("get_int", new System.Func<double>(() => _stats.Intelligence));
                registry.Register("get_vit", new System.Func<double>(() => _stats.Vitality));
                registry.Register("get_hp", new System.Func<double>(() => _stats.CurrentHealth));

                registry.Register("set_hp", new System.Action<double>(hp =>
                    _stats.CurrentHealth = Mathf.Max(0, (float)hp)));

                registry.Register("add_buff", new System.Action<string, double>((name, value) =>
                    _stats.Buffs[name] = (float)value));

                registry.Register("get_buff", new System.Func<string, double>((name) =>
                    _stats.Buffs.TryGetValue(name, out float v) ? v : 0));

                // Формулы для разных классов
                registry.Register("warrior_damage", new System.Func<double, double, double>((str, weapon_dmg) =>
                    weapon_dmg + str * 2.5));

                registry.Register("mage_damage", new System.Func<double, double, double>((intel, spell_base) =>
                    spell_base + intel * 3.0));

                registry.Register("ranger_damage", new System.Func<double, double, double>((agi, bow_dmg) =>
                    bow_dmg + agi * 2.0));

                registry.Register("calc_health", new System.Func<double, double>((vit) =>
                    50 + vit * 10));

                registry.Register("calc_crit", new System.Func<double, double>((agi) =>
                    Mathf.Min(50f, (float)(agi * 0.5))));

                registry.Register("report", new System.Action<string>(msg =>
                    LastReport = msg));
            }

            public string LastReport { get; private set; }
        }

        private LuaEnv CreateEnv(CharacterStats stats)
        {
            ClassBindings bindings = new(stats);
            LuaApiRegistry reg = new();
            bindings.RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            return new LuaEnv(env, reg);
        }

        private sealed class LuaEnv
        {
            private readonly SecureLuaEnvironment _env;
            private readonly LuaApiRegistry _reg;

            public LuaEnv(SecureLuaEnvironment e, LuaApiRegistry r)
            {
                _env = e;
                _reg = r;
            }

            public DynValue Run(string lua)
            {
                return _env.RunChunk(_env.CreateScript(_reg), lua);
            }
        }

        [Test]
        public void Warrior_Formula_StrengthBased()
        {
            CharacterStats stats = new() { Strength = 20 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                return warrior_damage(get_str(), 50)
            ");

            Assert.IsNotNull(result);
            // 50 + (20 * 2.5) = 100
            Assert.AreEqual(100, (double)result.Number, 0.5);
        }

        [Test]
        public void Mage_Formula_IntelligenceBased()
        {
            CharacterStats stats = new() { Intelligence = 25 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                return mage_damage(get_int(), 40)
            ");

            Assert.IsNotNull(result);
            // 40 + (25 * 3.0) = 115
            Assert.AreEqual(115, (double)result.Number, 0.5);
        }

        [Test]
        public void Ranger_Formula_AgilityBased()
        {
            CharacterStats stats = new() { Agility = 30 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                return ranger_damage(get_agi(), 35)
            ");

            Assert.IsNotNull(result);
            // 35 + (30 * 2.0) = 95
            Assert.AreEqual(95, (double)result.Number, 0.5);
        }

        [Test]
        public void Health_Calculation_FromVitality()
        {
            CharacterStats stats = new() { Vitality = 15 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                return calc_health(get_vit())
            ");

            Assert.IsNotNull(result);
            // 50 + (15 * 10) = 200
            Assert.AreEqual(200, (double)result.Number, 0.5);
        }

        [Test]
        public void CriticalChance_FromAgility()
        {
            CharacterStats stats = new() { Agility = 40 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                return calc_crit(get_agi())
            ");

            Assert.IsNotNull(result);
            // min(50, 40 * 0.5) = 20%
            Assert.AreEqual(20, (double)result.Number, 0.5);
        }

        [Test]
        public void Hybrid_Class_WarriorMage()
        {
            CharacterStats stats = new() { Strength = 15, Intelligence = 15 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                -- Гибридный класс: 60% воина, 40% мага
                local warrior = warrior_damage(get_str(), 40) * 0.6
                local mage = mage_damage(get_int(), 30) * 0.4
                return warrior + mage
            ");

            Assert.IsNotNull(result);
            // Warrior: (40 + 37.5) * 0.6 = 46.5
            // Mage: (30 + 45) * 0.4 = 30
            // Total: 76.5
            Assert.AreEqual(76.5, (double)result.Number, 0.5);
        }

        [Test]
        public void BuffSystem_AddAndGetBuffs()
        {
            CharacterStats stats = new();
            LuaEnv env = CreateEnv(stats);

            env.Run(@"
                add_buff('attack_power', 25.0)
                add_buff('crit_chance', 15.0)
            ");

            Assert.IsTrue(stats.Buffs.ContainsKey("attack_power"));
            Assert.IsTrue(stats.Buffs.ContainsKey("crit_chance"));
            Assert.AreEqual(25, stats.Buffs["attack_power"], 0.5);
            Assert.AreEqual(15, stats.Buffs["crit_chance"], 0.5);
        }

        [Test]
        public void BuffedDamage_Calculation()
        {
            CharacterStats stats = new() { Strength = 20 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                add_buff('damage_mult', 1.5)
                local base = warrior_damage(get_str(), 50)
                return base * get_buff('damage_mult')
            ");

            Assert.IsNotNull(result);
            // (50 + 50) * 1.5 = 150
            Assert.AreEqual(150, (double)result.Number, 0.5);
        }

        [Test]
        public void LevelScaling_ClassFormulas()
        {
            CharacterStats stats = new() { Strength = 10 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function scale_warrior(level, str, base_weapon)
                    local scaling = str * 2.5 * level
                    return base_weapon + scaling
                end
                
                return scale_warrior(5, get_str(), 30)
            ");

            Assert.IsNotNull(result);
            // 30 + (10 * 2.5 * 5) = 155
            Assert.AreEqual(155, (double)result.Number, 0.5);
        }

        [Test]
        public void DefensiveStance_DamageReduction()
        {
            CharacterStats stats = new() { CurrentHealth = 100 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function defensive_stance(incoming_dmg, reduction_percent)
                    return incoming_dmg * (1 - reduction_percent)
                end
                
                local dmg = defensive_stance(80, 0.4)
                set_hp(get_hp() - dmg)
                return dmg
            ");

            Assert.IsNotNull(result);
            // 80 * 0.6 = 48 урона
            // 100 - 48 = 52 HP
            Assert.AreEqual(48, (double)result.Number, 0.5);
            Assert.AreEqual(52, stats.CurrentHealth, 0.5);
        }

        [Test]
        public void Berserker_Mode_LowHealthDamageBonus()
        {
            CharacterStats stats = new() { CurrentHealth = 25, Strength = 20 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function berserker_damage(base, str, current_hp, max_hp)
                    local hp_percent = current_hp / max_hp
                    local bonus = 1.0 + (1.0 - hp_percent) * 2.0  -- до 3x при 0% HP
                    return (base + str * 2.5) * bonus
                end
                
                return berserker_damage(30, get_str(), get_hp(), 100)
            ");

            Assert.IsNotNull(result);
            // HP% = 25%, bonus = 1 + 0.75 * 2 = 2.5
            // (30 + 50) * 2.5 = 200
            Assert.AreEqual(200, (double)result.Number, 1);
        }

        [Test]
        public void MultiClass_ComboAttack()
        {
            CharacterStats stats = new() { Strength = 15, Agility = 15, Intelligence = 15 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                -- Комбо атака: воин -> маг -> лучник
                local w = warrior_damage(get_str(), 20)
                local m = mage_damage(get_int(), 15)
                local r = ranger_damage(get_agi(), 10)
                return w + m + r
            ");

            Assert.IsNotNull(result);
            // W: 20 + 37.5 = 57.5
            // M: 15 + 45 = 60
            // R: 10 + 30 = 40
            // Total: 157.5
            Assert.AreEqual(157.5, (double)result.Number, 0.5);
        }

        [Test]
        public void ClassProgression_UnlockSkills()
        {
            CharacterStats stats = new();
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function unlock_skill(level, skill_name)
                    if level >= 10 then
                        add_buff(skill_name, 1.0)
                        return true
                    end
                    return false
                end
                
                local unlocked = unlock_skill(12, 'whirlwind')
                return unlocked and 1 or 0
            ");

            Assert.IsNotNull(result);
            Assert.AreEqual(1, (double)result.Number, 0.5);
            Assert.IsTrue(stats.Buffs.ContainsKey("whirlwind"));
        }

        [Test]
        public void UltimateAbility_CooldownAndDamage()
        {
            CharacterStats stats = new() { Strength = 30, Intelligence = 20 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function ultimate_ability(str, intel, cooldown_remaining)
                    if cooldown_remaining > 0 then
                        return 0
                    end
                    
                    -- Ульт: стр + инт * 5
                    return (str * 3) + (intel * 5)
                end
                
                return ultimate_ability(get_str(), get_int(), 0)
            ");

            Assert.IsNotNull(result);
            // (30 * 3) + (20 * 5) = 90 + 100 = 190
            Assert.AreEqual(190, (double)result.Number, 0.5);
        }
    }
}