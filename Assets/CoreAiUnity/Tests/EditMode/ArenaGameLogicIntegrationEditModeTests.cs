using System.Collections.Generic;
using System.Linq;
using CoreAI.Ai;
using CoreAI.Infrastructure.Lua;
using CoreAI.Sandbox;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.Integration
{
    /// <summary>
    /// Тесты интеграции AI с игровой логикой Arena: бой, прогрессия, волны.
    /// Проверяют что AI может управлять реальной игровой механикой через Lua.
    /// </summary>
    public sealed class ArenaGameLogicIntegrationEditModeTests
    {
        #region Game State

        private sealed class ArenaGameState
        {
            public int Wave = 1;
            public int PlayerGold = 100;
            public int PlayerLevel = 1;
            public int PlayerXp = 0;
            public float PlayerHealth = 100f;
            public float PlayerMaxHealth = 100f;
            public float PlayerDamage = 25f;
            public float PlayerArmor = 10f;

            public readonly List<Enemy> Enemies = new();
            public readonly List<Item> Items = new();
            public readonly List<string> Events = new();

            public class Enemy
            {
                public string Id;
                public float Health;
                public float Damage;
                public Vector3 Position;
                public bool IsAlive = true;
            }

            public class Item
            {
                public string Name;
                public string Type; // "weapon", "armor", "potion"
                public int Value;
                public Vector3 Position;
            }
        }

        #endregion

        #region Arena Bindings

        private sealed class ArenaBindings : IGameLuaRuntimeBindings
        {
            private readonly ArenaGameState _game;

            public ArenaBindings(ArenaGameState game)
            {
                _game = game;
            }

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                // Player stats
                registry.Register("get_player_hp", new System.Func<double>(() => _game.PlayerHealth));
                registry.Register("get_player_max_hp", new System.Func<double>(() => _game.PlayerMaxHealth));
                registry.Register("get_player_damage", new System.Func<double>(() => _game.PlayerDamage));
                registry.Register("get_player_armor", new System.Func<double>(() => _game.PlayerArmor));
                registry.Register("get_player_gold", new System.Func<double>(() => _game.PlayerGold));
                registry.Register("get_player_level", new System.Func<double>(() => _game.PlayerLevel));
                registry.Register("get_player_xp", new System.Func<double>(() => _game.PlayerXp));
                registry.Register("get_wave", new System.Func<double>(() => _game.Wave));

                registry.Register("set_player_hp", new System.Action<double>(hp =>
                    _game.PlayerHealth = Mathf.Clamp((float)hp, 0, _game.PlayerMaxHealth)));

                registry.Register("set_player_damage", new System.Action<double>(dmg =>
                    _game.PlayerDamage = (float)dmg));

                registry.Register("set_player_armor", new System.Action<double>(armor =>
                    _game.PlayerArmor = (float)armor));

                registry.Register("add_gold", new System.Action<double>(gold =>
                {
                    _game.PlayerGold += Mathf.Max(0, (int)gold);
                    _game.Events.Add($"gold:{_game.PlayerGold}");
                }));

                registry.Register("add_xp", new System.Action<double>(xp =>
                {
                    _game.PlayerXp += Mathf.Max(0, (int)xp);
                    _game.Events.Add($"xp:{_game.PlayerXp}");
                }));

                // Enemy management
                registry.Register("spawn_enemy",
                    new System.Func<string, double, double, double, double, double, string>((type, hp, dmg, x, y, z) =>
                    {
                        ArenaGameState.Enemy enemy = new()
                        {
                            Id = $"{type}_{_game.Enemies.Count}",
                            Health = (float)hp,
                            Damage = (float)dmg,
                            Position = new Vector3((float)x, (float)y, (float)z)
                        };
                        _game.Enemies.Add(enemy);
                        _game.Events.Add($"spawn_enemy:{enemy.Id}");
                        return enemy.Id;
                    }));

                registry.Register("damage_enemy", new System.Action<string, double>((id, dmg) =>
                {
                    ArenaGameState.Enemy enemy = _game.Enemies.Find(e => e.Id == id && e.IsAlive);
                    if (enemy != null)
                    {
                        enemy.Health -= (float)dmg;
                        if (enemy.Health <= 0)
                        {
                            enemy.IsAlive = false;
                            _game.Events.Add($"kill:{id}");
                        }
                    }
                }));

                registry.Register("get_enemy_hp", new System.Func<string, double>((id) =>
                {
                    ArenaGameState.Enemy enemy = _game.Enemies.Find(e => e.Id == id);
                    return enemy != null ? enemy.Health : -1;
                }));

                registry.Register("get_alive_enemy_count", new System.Func<double>(() =>
                    _game.Enemies.FindAll(e => e.IsAlive).Count));

                // Combat
                registry.Register("calc_damage", new System.Func<double, double, double>((dmg, armor) =>
                    dmg * (100f / (100f + armor))));

                registry.Register("player_attack", new System.Action<string>((enemyId) =>
                {
                    float dmg = calcDamageWithArmor(_game.PlayerDamage, _game.PlayerArmor);
                    damageEnemy(enemyId, dmg);
                    _game.Events.Add($"player_attack:{enemyId}:{dmg:F1}");
                }));

                // Items
                registry.Register("spawn_item",
                    new System.Action<string, string, double, double, double, double>((name, type, value, x, y, z) =>
                    {
                        ArenaGameState.Item item = new()
                        {
                            Name = name,
                            Type = type,
                            Value = (int)value,
                            Position = new Vector3((float)x, (float)y, (float)z)
                        };
                        _game.Items.Add(item);
                        _game.Events.Add($"spawn_item:{name}");
                    }));

                registry.Register("collect_item", new System.Action<string>((itemName) =>
                {
                    ArenaGameState.Item item = _game.Items.Find(i => i.Name == itemName);
                    if (item != null)
                    {
                        switch (item.Type)
                        {
                            case "weapon":
                                _game.PlayerDamage += item.Value;
                                break;
                            case "armor":
                                _game.PlayerArmor += item.Value;
                                break;
                            case "potion":
                                _game.PlayerHealth = Mathf.Min(_game.PlayerMaxHealth,
                                    _game.PlayerHealth + item.Value);
                                break;
                        }

                        _game.Items.Remove(item);
                        _game.Events.Add($"collect:{itemName}");
                    }
                }));

                // Wave management
                registry.Register("next_wave", new System.Action(() =>
                {
                    _game.Wave++;
                    _game.Events.Add($"wave:{_game.Wave}");
                }));

                // Level up
                registry.Register("check_level_up", new System.Func<bool>(() =>
                {
                    int xpNeeded = _game.PlayerLevel * 100;
                    if (_game.PlayerXp >= xpNeeded)
                    {
                        _game.PlayerLevel++;
                        _game.PlayerXp -= xpNeeded;
                        _game.PlayerMaxHealth += 10;
                        _game.PlayerHealth = _game.PlayerMaxHealth;
                        _game.PlayerDamage += 2;
                        _game.Events.Add($"level_up:{_game.PlayerLevel}");
                        return true;
                    }

                    return false;
                }));

                registry.Register("report", new System.Action<string>(msg =>
                    _game.Events.Add($"report:{msg}")));
            }

            private float calcDamageWithArmor(float dmg, float armor)
            {
                return dmg * (100f / (100f + armor));
            }

            private void damageEnemy(string id, float dmg)
            {
                ArenaGameState.Enemy enemy = _game.Enemies.Find(e => e.Id == id && e.IsAlive);
                if (enemy != null)
                {
                    enemy.Health -= dmg;
                    if (enemy.Health <= 0)
                    {
                        enemy.IsAlive = false;
                        _game.Events.Add($"kill:{id}");
                    }
                }
            }
        }

        #endregion

        #region Helper

        private DynValue RunLua(ArenaGameState game, string lua)
        {
            ArenaBindings bindings = new(game);
            LuaApiRegistry reg = new();
            bindings.RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);
            return env.RunChunk(script, lua);
        }

        #endregion

        #region Tests

        [Test]
        public void ArenaLua_SpawnWaveAndDefeat()
        {
            ArenaGameState game = new();

            RunLua(game, @"
                -- Spawn wave 1: 3 weak enemies
                spawn_enemy('goblin', 30, 5, 0, 0, 0)
                spawn_enemy('goblin', 30, 5, 5, 0, 0)
                spawn_enemy('goblin', 30, 5, -5, 0, 0)
                
                report('wave spawned')
                
                -- Player attacks all enemies
                player_attack('goblin_0')
                player_attack('goblin_1')
                player_attack('goblin_2')
                
                report('attacked all')
            ");

            Assert.AreEqual(3, game.Enemies.Count);
            // Урон игрока 25, брони 10: 25 * 0.909 = 22.73
            // 30 - 22.73 = 7.27 HP осталось у каждого
            Assert.Less(game.Enemies[0].Health, 10);
            Assert.IsTrue(game.Events.Exists(e => e.Contains("report:wave spawned")));
        }

        [Test]
        public void ArenaLua_BossFight_WithPhaseLogic()
        {
            ArenaGameState game = new() { PlayerDamage = 50 };

            RunLua(game, @"
                -- Spawn boss
                spawn_enemy('BOSS', 200, 20, 0, 0, 0)
                
                -- Phase 1: Attack boss 5 times
                for i = 1, 5 do
                    player_attack('BOSS_0')
                end
                
                local boss_hp = get_enemy_hp('BOSS_0')
                report('boss hp after phase 1: ' .. boss_hp)
                
                -- Check if boss is dead
                local alive = get_alive_enemy_count()
                if alive == 0 then
                    report('boss defeated')
                else
                    report('boss still alive')
                end
            ");

            // 5 атак * (50 * 0.909) = 5 * 45.45 = 227.25 урона
            // 200 - 227.25 = -27.25 (мертв)
            Assert.AreEqual(0, game.Enemies.FindAll(e => e.IsAlive).Count, "Босс должен быть мертв");
            Assert.IsTrue(game.Events.Exists(e => e.Contains("report:boss defeated")));
        }

        [Test]
        public void ArenaLua_ItemCollection_Buffs()
        {
            ArenaGameState game = new() { PlayerHealth = 50, PlayerMaxHealth = 100 };

            RunLua(game, @"
                -- Spawn items
                spawn_item('sword', 'weapon', 15, 0, 0, 0)
                spawn_item('shield', 'armor', 10, 2, 0, 0)
                spawn_item('health_potion', 'potion', 30, -2, 0, 0)
                
                -- Collect all
                collect_item('sword')
                collect_item('shield')
                collect_item('health_potion')
                
                report('all collected')
            ");

            // Sword: +15 damage
            Assert.AreEqual(40, game.PlayerDamage);
            // Shield: +10 armor
            Assert.AreEqual(20, game.PlayerArmor);
            // Potion: heal 30, но не больше макс
            Assert.AreEqual(80, game.PlayerHealth);
            Assert.AreEqual(0, game.Items.Count, "Все предметы собраны");
        }

        [Test]
        public void ArenaLua_LevelUp_System()
        {
            ArenaGameState game = new() { PlayerXp = 80, PlayerLevel = 1 };

            RunLua(game, @"
                -- Gain XP
                add_xp(30)
                
                -- Check level up
                local leveled = check_level_up()
                
                if leveled then
                    report('leveled up to ' .. get_player_level())
                end
            ");

            // 80 + 30 = 110 XP, нужно 100 для уровня 1
            Assert.AreEqual(2, game.PlayerLevel);
            Assert.AreEqual(10, game.PlayerXp); // 110 - 100 = 10 осталось
            Assert.AreEqual(110, game.PlayerMaxHealth); // +10 HP
            Assert.AreEqual(27, game.PlayerDamage); // +2 damage
            Assert.IsTrue(game.Events.Exists(e => e.Contains("report:leveled up to 2")));
        }

        [Test]
        public void ArenaLua_ComplexCombatRotation()
        {
            ArenaGameState game = new() { PlayerDamage = 40 };

            RunLua(game, @"
                -- Complex fight: spawn, attack, check, repeat
                spawn_enemy('warrior', 100, 15, 0, 0, 0)
                spawn_enemy('mage', 60, 25, 5, 0, 0)
                
                local attacks = 0
                while get_alive_enemy_count() > 0 do
                    -- Attack first alive enemy
                    for i = 0, 9 do
                        local id = 'warrior_' .. i
                        if get_enemy_hp(id) > 0 then
                            player_attack(id)
                            attacks = attacks + 1
                            break
                        end
                        
                        id = 'mage_' .. i
                        if get_enemy_hp(id) > 0 then
                            player_attack(id)
                            attacks = attacks + 1
                            break
                        end
                    end
                    
                    -- Safety break
                    if attacks > 20 then
                        break
                    end
                end
                
                report('defeated all in ' .. attacks .. ' attacks')
            ");

            Assert.AreEqual(0, game.Enemies.FindAll(e => e.IsAlive).Count);
            Assert.IsTrue(game.Events.Exists(e => e.Contains("report:defeated all")));
        }

        [Test]
        public void ArenaLua_WaveProgression()
        {
            ArenaGameState game = new();

            RunLua(game, @"
                -- Wave 1
                spawn_enemy('goblin', 30, 5, 0, 0, 0)
                player_attack('goblin_0')
                
                -- Next wave
                next_wave()
                
                -- Wave 2: harder enemies
                spawn_enemy('orc', 60, 10, 0, 0, 0)
                spawn_enemy('orc', 60, 10, 3, 0, 0)
                
                report('wave 2 ready')
            ");

            Assert.AreEqual(2, game.Wave);
            Assert.AreEqual(3, game.Enemies.Count);
            Assert.IsTrue(game.Events.Exists(e => e.Contains("report:wave 2 ready")));
        }

        [Test]
        public void ArenaLua_DynamicDifficultyScaling()
        {
            ArenaGameState game = new() { PlayerLevel = 5 };

            RunLua(game, @"
                -- Enemy scales with player level
                function scale_enemy(base_hp, base_dmg, player_level)
                    local hp = base_hp + (player_level * 20)
                    local dmg = base_dmg + (player_level * 3)
                    return hp, dmg
                end
                
                local hp, dmg = scale_enemy(50, 10, get_player_level())
                spawn_enemy('scaled_enemy', hp, dmg, 0, 0, 0)
                
                report('spawned enemy with hp=' .. hp .. ' dmg=' .. dmg)
            ");

            // HP: 50 + (5 * 20) = 150
            // DMG: 10 + (5 * 3) = 25
            Assert.AreEqual(150, game.Enemies[0].Health);
            Assert.AreEqual(25, game.Enemies[0].Damage);
        }

        [Test]
        public void ArenaLua_MultiEnemyAOE()
        {
            ArenaGameState game = new() { PlayerDamage = 30 };

            RunLua(game, @"
                -- Spawn 5 enemies close together
                spawn_enemy('minion', 40, 5, 0, 0, 0)
                spawn_enemy('minion', 40, 5, 1, 0, 0)
                spawn_enemy('minion', 40, 5, -1, 0, 0)
                spawn_enemy('minion', 40, 5, 0, 0, 1)
                spawn_enemy('minion', 40, 5, 0, 0, -1)
                
                -- AOE attack: damage all enemies
                local total_dmg = 0
                for i = 0, 4 do
                    local id = 'minion_' .. i
                    local hp_before = get_enemy_hp(id)
                    player_attack(id)
                    local hp_after = get_enemy_hp(id)
                    total_dmg = total_dmg + (hp_before - hp_after)
                end
                
                report('AOE dealt ' .. total_dmg .. ' damage')
            ");

            // 5 * (30 * 0.909) = 136.35 total damage
            float totalDealt = game.Enemies.Sum(e => 40 - e.Health);
            Assert.Greater(totalDealt, 130);
        }

        [Test]
        public void ArenaLua_CombatHealingStrategy()
        {
            ArenaGameState game = new() { PlayerHealth = 40, PlayerMaxHealth = 100 };

            RunLua(game, @"
                -- Healing strategy: heal when low
                local hp = get_player_hp()
                local max_hp = get_player_max_hp()
                local hp_percent = hp / max_hp
                
                if hp_percent < 0.5 then
                    -- Use potion
                    spawn_item('emergency_potion', 'potion', 50, 0, 0, 0)
                    collect_item('emergency_potion')
                    report('emergency heal')
                else
                    report('hp ok')
                end
            ");

            // 40/100 = 40% < 50%, должен использовать зелье
            Assert.AreEqual(90, game.PlayerHealth); // 40 + 50 = 90
            Assert.IsTrue(game.Events.Exists(e => e.Contains("report:emergency heal")));
        }

        [Test]
        public void ArenaLua_GoldEconomy()
        {
            ArenaGameState game = new() { PlayerGold = 50 };

            RunLua(game, @"
                -- Earn gold from kills
                spawn_enemy('bandit', 30, 5, 0, 0, 0)
                player_attack('bandit_0')
                
                -- Gold reward
                add_xp(50)
                add_gold(25)
                
                -- Check wealth
                local gold = get_player_gold()
                report('gold: ' .. gold)
            ");

            Assert.AreEqual(75, game.PlayerGold);
            Assert.IsTrue(game.Events.Exists(e => e.Contains("gold:75")));
        }

        #endregion
    }
}