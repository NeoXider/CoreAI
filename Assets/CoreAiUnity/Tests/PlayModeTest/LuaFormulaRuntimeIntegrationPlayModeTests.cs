using System.Collections;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Tests.PlayMode
{
    /// <summary>
    /// PlayMode интеграционные тесты для Lua формул в реальном времени.
    /// Проверяет что AI может модифицировать формулы боя во время игры.
    /// </summary>
    public sealed class LuaFormulaRuntimeIntegrationPlayModeTests
    {
        private sealed class RuntimeCombatStats
        {
            public float CurrentHealth = 100f;
            public float MaxHealth = 100f;
            public float Armor = 10f;
            public float MagicResist = 5f;
            public float AttackDamage = 25f;
            public float Lifesteal = 0f;
            public readonly List<string> CombatLog = new();
        }

        private sealed class RuntimeCombatBindings : IGameLuaRuntimeBindings
        {
            private readonly RuntimeCombatStats _stats;

            public RuntimeCombatBindings(RuntimeCombatStats stats)
            {
                _stats = stats;
            }

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                registry.Register("get_hp", new System.Func<double>(() => _stats.CurrentHealth));
                registry.Register("get_max_hp", new System.Func<double>(() => _stats.MaxHealth));
                registry.Register("get_armor", new System.Func<double>(() => _stats.Armor));
                registry.Register("get_resist", new System.Func<double>(() => _stats.MagicResist));
                registry.Register("get_damage", new System.Func<double>(() => _stats.AttackDamage));
                registry.Register("get_lifesteal", new System.Func<double>(() => _stats.Lifesteal));

                registry.Register("set_hp", new System.Action<double>(hp =>
                    _stats.CurrentHealth = Mathf.Clamp((float)hp, 0, _stats.MaxHealth)));

                registry.Register("take_damage", new System.Func<double, double>((dmg) =>
                {
                    _stats.CurrentHealth = Mathf.Max(0, _stats.CurrentHealth - (float)dmg);
                    _stats.CombatLog.Add($"took_damage:{dmg:F1}");
                    return _stats.CurrentHealth;
                }));

                registry.Register("heal", new System.Action<double>(amount =>
                {
                    _stats.CurrentHealth = Mathf.Min(_stats.MaxHealth, _stats.CurrentHealth + (float)amount);
                    _stats.CombatLog.Add($"healed:{amount:F1}");
                }));

                registry.Register("calc_phys", new System.Func<double, double, double>((dmg, armor) =>
                    dmg * (100f / (100f + armor))));

                registry.Register("calc_magic", new System.Func<double, double, double>((dmg, resist) =>
                    dmg * (100f / (100f + resist))));

                registry.Register("log", new System.Action<string>(msg => _stats.CombatLog.Add(msg)));
            }
        }

        private LuaEnv CreateEnv(RuntimeCombatStats stats)
        {
            RuntimeCombatBindings bindings = new(stats);
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

        [UnityTest]
        public IEnumerator LuaRuntime_CanModifyDamageFormula_WithArmor()
        {
            yield return null;

            RuntimeCombatStats stats = new() { Armor = 20 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                local base = get_damage()
                local armor = get_armor()
                local final = calc_phys(base, armor)
                return final
            ");

            Assert.IsNotNull(result);
            // 25 * 0.833 = 20.83
            Assert.AreEqual(20.83, (double)result.Number, 0.5);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanApplyDamageAndHeal()
        {
            yield return null;

            RuntimeCombatStats stats = new() { CurrentHealth = 100, Armor = 10 };
            LuaEnv env = CreateEnv(stats);

            // Наносим урон
            env.Run(@"
                local dmg = calc_phys(50, get_armor())
                take_damage(dmg)
            ");

            Assert.Less(stats.CurrentHealth, 100);
            Assert.Greater(stats.CurrentHealth, 50);

            // Исцеляем
            env.Run(@"
                heal(20)
            ");

            Assert.Greater(stats.CurrentHealth, 70);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanChainMultipleFormulas()
        {
            yield return null;

            RuntimeCombatStats stats = new()
            {
                CurrentHealth = 200,
                Armor = 15,
                MagicResist = 20
            };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                -- Комбо: физ + маг урон
                local phys = calc_phys(60, get_armor())
                local magic = calc_magic(40, get_resist())
                local total = phys + magic
                return total
            ");

            Assert.IsNotNull(result);
            // 60 * 0.87 + 40 * 0.833 = 52.17 + 33.33 = 85.5
            Assert.AreEqual(85.5, (double)result.Number, 1);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanDefineCustomFormula()
        {
            yield return null;

            RuntimeCombatStats stats = new() { Armor = 25 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function burst_damage(base, armor, multiplier)
                    local after_armor = base * (100 / (100 + armor))
                    return after_armor * multiplier
                end
                
                return burst_damage(100, get_armor(), 2.0)
            ");

            Assert.IsNotNull(result);
            // 100 * 0.8 * 2 = 160
            Assert.AreEqual(160, (double)result.Number, 1);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanImplementDOT()
        {
            yield return null;

            RuntimeCombatStats stats = new() { CurrentHealth = 100, Armor = 10 };
            LuaEnv env = CreateEnv(stats);

            env.Run(@"
                function apply_dot(tick_dmg, armor, ticks)
                    local total = 0
                    for i = 1, ticks do
                        local tick = calc_phys(tick_dmg, armor)
                        total = total + tick
                    end
                    take_damage(total)
                    return total
                end
                
                apply_dot(10, get_armor(), 5)
            ");

            // 5 тиков * (10 * 0.909) = 45.45
            // 100 - 45.45 = 54.55 HP
            Assert.Greater(stats.CurrentHealth, 53);
            Assert.Less(stats.CurrentHealth, 56);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanModifyStatsDynamically()
        {
            yield return null;

            RuntimeCombatStats stats = new() { CurrentHealth = 50, MaxHealth = 100 };
            LuaEnv env = CreateEnv(stats);

            env.Run(@"
                -- Бафф здоровья
                set_hp(100)
                log('health_buffed')
            ");

            Assert.AreEqual(100, stats.CurrentHealth);
            Assert.IsTrue(stats.CombatLog.Contains("health_buffed"));
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanImplementLifesteal()
        {
            yield return null;

            RuntimeCombatStats stats = new()
            {
                CurrentHealth = 40,
                MaxHealth = 100,
                Lifesteal = 0.2f
            };
            LuaEnv env = CreateEnv(stats);

            env.Run(@"
                local base = get_damage()
                local dmg = calc_phys(base, 10)
                local heal_amount = dmg * get_lifesteal()
                
                take_damage(10) -- Получаем урон от врага
                heal(heal_amount) -- Вампиризм
            ");

            // Должно остаться > 30 HP после вампиризма
            Assert.Greater(stats.CurrentHealth, 30);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanCalculateLevelProgression()
        {
            yield return null;

            RuntimeCombatStats stats = new();
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function xp_to_level(n)
                    return 100 * n * n
                end
                
                return xp_to_level(10)
            ");

            Assert.IsNotNull(result);
            Assert.AreEqual(10000, (double)result.Number, 1);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanImplementScalingFormulas()
        {
            yield return null;

            RuntimeCombatStats stats = new();
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function scale(base, level, per_level)
                    return base + (level * per_level)
                end
                
                return scale(20, 10, 5)
            ");

            Assert.IsNotNull(result);
            Assert.AreEqual(70, (double)result.Number, 0.5);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_SecureEnvironment_BlocksDangerousAPIs()
        {
            yield return null;

            RuntimeCombatStats stats = new();
            LuaEnv env = CreateEnv(stats);

            // Проверяем что опасные API заблокированы
            Assert.Throws<ScriptRuntimeException>(() =>
                env.Run("os.execute('dir')"));

            Assert.Throws<ScriptRuntimeException>(() =>
                env.Run("io.open('test.txt')"));
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanImplementConditionalDamage()
        {
            yield return null;

            RuntimeCombatStats stats = new() { CurrentHealth = 20, MaxHealth = 100, Armor = 10 };
            LuaEnv env = CreateEnv(stats);

            DynValue result = env.Run(@"
                function execute_damage(base, target_hp, target_max_hp)
                    local hp_pct = target_hp / target_max_hp
                    if hp_pct < 0.25 then
                        return base * 3.0  -- Execute!
                    end
                    return base
                end
                
                return execute_damage(50, get_hp(), get_max_hp())
            ");

            Assert.IsNotNull(result);
            // 20/100 = 20% < 25%, получаем 3x: 150
            Assert.AreEqual(150, (double)result.Number, 1);
        }

        [UnityTest]
        public IEnumerator LuaRuntime_CanTrackCombatLog()
        {
            yield return null;

            RuntimeCombatStats stats = new() { CurrentHealth = 100, Armor = 15 };
            LuaEnv env = CreateEnv(stats);

            env.Run(@"
                log('combat_start')
                local dmg = calc_phys(30, get_armor())
                log('damage_calc:' .. dmg)
                take_damage(dmg)
                heal(10)
                log('combat_end')
            ");

            Assert.GreaterOrEqual(stats.CombatLog.Count, 4);
            Assert.IsTrue(stats.CombatLog.Contains("combat_start"));
            Assert.IsTrue(stats.CombatLog.Contains("combat_end"));
        }
    }
}