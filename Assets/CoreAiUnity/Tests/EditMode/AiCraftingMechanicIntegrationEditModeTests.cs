using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Infrastructure.Lua;
using CoreAI.Messaging;
using CoreAI.Sandbox;
using CoreAI.Session;
using MoonSharp.Interpreter;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.Integration
{
    /// <summary>
    /// Интеграционные тесты AI механики крафта.
    /// AI придумывает УНИКАЛЬНЫЕ свойства предметов на основе характеристик ингредиентов.
    /// Пайплайн: Ингредиенты → AI Analyzer (анализ) → AI Programmer (создает формулу) → Execution → Result.
    /// </summary>
    public sealed class AiCraftingMechanicIntegrationEditModeTests
    {
        #region Crafting Domain

        /// <summary>
        /// Ингредиент с характеристиками
        /// </summary>
        private sealed class CraftingIngredient
        {
            public string Name;
            public string Type; // "metal", "herb", "crystal", "leather", "wood", "magic"
            public float Hardness; // Твердость (0-100)
            public float Flexibility; // Гибкость (0-100)
            public float MagicPower; // Магическая сила (0-100)
            public float Weight; // Вес (0-100)
            public float Rarity; // Редкость (1-5)
            public Dictionary<string, float> SpecialProperties = new();

            public override string ToString()
            {
                return $"{Name}({Type})";
            }
        }

        /// <summary>
        /// Результат крафта - предмет с УНИКАЛЬНЫМИ свойствами
        /// </summary>
        private sealed class CraftedItem
        {
            public string Name;
            public string ItemType; // "weapon", "armor", "potion", "amulet", "staff"
            public readonly Dictionary<string, float> Stats = new();
            public readonly List<string> SpecialEffects = new();
            public float Quality; // 0-100
            public string Description;

            public override string ToString()
            {
                return $"{Name} [{ItemType}, Quality:{Quality:F0}]";
            }
        }

        /// <summary>
        /// Состояние системы крафта
        /// </summary>
        private sealed class CraftingSystemState
        {
            public readonly List<CraftedItem> CraftedItems = new();
            public readonly List<string> CraftingLog = new();
            public int TotalCrafts = 0;
            public float TotalQuality = 0;

            public void AddCraft(CraftedItem item)
            {
                CraftedItems.Add(item);
                TotalCrafts++;
                TotalQuality += item.Quality;
                CraftingLog.Add($"crafted:{item.Name} (quality:{item.Quality:F0})");
            }
        }

        #endregion

        #region Crafting Bindings

        private sealed class CraftingBindings : IGameLuaRuntimeBindings
        {
            private readonly CraftingSystemState _state;
            private readonly List<CraftingIngredient> _ingredients;

            public CraftingBindings(CraftingSystemState state, List<CraftingIngredient> ingredients)
            {
                _state = state;
                _ingredients = ingredients;
            }

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                // Получить количество ингредиентов
                registry.Register("get_ingredient_count", new System.Func<double>(() =>
                    _ingredients.Count));

                // Получить свойство ингредиента по индексу
                registry.Register("get_ingredient_name", new System.Func<double, string>(idx =>
                    _ingredients[(int)idx].Name));

                registry.Register("get_ingredient_type", new System.Func<double, string>(idx =>
                    _ingredients[(int)idx].Type));

                registry.Register("get_hardness", new System.Func<double, double>(idx =>
                    _ingredients[(int)idx].Hardness));

                registry.Register("get_flexibility", new System.Func<double, double>(idx =>
                    _ingredients[(int)idx].Flexibility));

                registry.Register("get_magic_power", new System.Func<double, double>(idx =>
                    _ingredients[(int)idx].MagicPower));

                registry.Register("get_weight", new System.Func<double, double>(idx =>
                    _ingredients[(int)idx].Weight));

                registry.Register("get_rarity", new System.Func<double, double>(idx =>
                    _ingredients[(int)idx].Rarity));

                // Рассчитать средние характеристики
                registry.Register("calc_avg_hardness", new System.Func<double>(() =>
                    _ingredients.Average(i => i.Hardness)));

                registry.Register("calc_avg_flexibility", new System.Func<double>(() =>
                    _ingredients.Average(i => i.Flexibility)));

                registry.Register("calc_avg_magic", new System.Func<double>(() =>
                    _ingredients.Average(i => i.MagicPower)));

                registry.Register("calc_avg_rarity", new System.Func<double>(() =>
                    _ingredients.Average(i => i.Rarity)));

                // Формулы для определения типа предмета
                registry.Register("determine_item_type", new System.Func<string>(() =>
                {
                    float avgMagic = _ingredients.Average(i => i.MagicPower);
                    float avgHardness = _ingredients.Average(i => i.Hardness);
                    float avgFlexibility = _ingredients.Average(i => i.Flexibility);

                    if (avgMagic > 60)
                    {
                        return "amulet";
                    }

                    if (avgHardness > 60)
                    {
                        return "weapon";
                    }

                    if (avgFlexibility > 60)
                    {
                        return "armor";
                    }

                    if (_ingredients.Any(i => i.Type == "herb"))
                    {
                        return "potion";
                    }

                    return "staff";
                }));

                // Рассчитать качество предмета
                registry.Register("calc_quality", new System.Func<double>(() =>
                {
                    float avgRarity = _ingredients.Average(i => i.Rarity);
                    float avgStats = (_ingredients.Average(i => i.Hardness) +
                                      _ingredients.Average(i => i.Flexibility) +
                                      _ingredients.Average(i => i.MagicPower)) / 3f;
                    float synergy = CalculateSynergyBonus();
                    return Mathf.Min(100, avgRarity / 5f * 40 + avgStats / 100f * 40 + synergy);
                }));

                // Рассчитать бонус синергии ингредиентов
                registry.Register("calc_synergy_bonus", new System.Func<double>(() =>
                    CalculateSynergyBonus()));

                // Рассчитать стат на основе формулы
                registry.Register("calc_stat",
                    new System.Func<string, double, double, double>((statName, weight1, weight2) =>
                    {
                        float total = 0;
                        foreach (CraftingIngredient ing in _ingredients)
                        {
                            float value = statName switch
                            {
                                "hardness" => ing.Hardness,
                                "flexibility" => ing.Flexibility,
                                "magic" => ing.MagicPower,
                                "weight" => ing.Weight,
                                _ => 0
                            };
                            total += value;
                        }

                        return total / _ingredients.Count * weight1 + weight2;
                    }));

                // Добавить спецэффект
                registry.Register("add_special_effect", new System.Action<string>(effect =>
                    _specialEffects.Add(effect)));

                // Создать предмет
                registry.Register("create_item",
                    new System.Func<string, string, double, string>((name, itemType, quality) =>
                    {
                        CraftedItem item = new()
                        {
                            Name = name,
                            ItemType = itemType,
                            Quality = (float)quality,
                            Description = $"Crafted from {_ingredients.Count} ingredients"
                        };

                        // Добавляем статы
                        item.Stats["damage"] = CalculateStat("hardness", 0.5f, 10f);
                        item.Stats["defense"] = CalculateStat("flexibility", 0.5f, 5f);
                        item.Stats["magic_power"] = CalculateStat("magic", 0.7f, 15f);

                        // Добавляем спецэффекты
                        foreach (string effect in _specialEffects)
                        {
                            item.SpecialEffects.Add(effect);
                        }

                        _state.AddCraft(item);
                        return item.Name;
                    }));

                registry.Register("report", new System.Action<string>(msg =>
                    _state.CraftingLog.Add($"report:{msg}")));
            }

            private readonly List<string> _specialEffects = new();

            private float CalculateStat(string statName, float weight1, float weight2)
            {
                float total = 0;
                foreach (CraftingIngredient ing in _ingredients)
                {
                    float value = statName switch
                    {
                        "hardness" => ing.Hardness,
                        "flexibility" => ing.Flexibility,
                        "magic" => ing.MagicPower,
                        "weight" => ing.Weight,
                        _ => 0
                    };
                    total += value;
                }

                return total / _ingredients.Count * weight1 + weight2;
            }

            private float CalculateSynergyBonus()
            {
                float bonus = 0;

                // Бонус за разные типы
                int uniqueTypes = _ingredients.Select(i => i.Type).Distinct().Count();
                bonus += uniqueTypes * 5;

                // Бонус за высокую редкость
                int highRarityCount = _ingredients.Count(i => i.Rarity >= 4);
                bonus += highRarityCount * 10;

                // Бонус за баланс характеристик
                float avgHardness = _ingredients.Average(i => i.Hardness);
                float avgFlexibility = _ingredients.Average(i => i.Flexibility);
                float balance = Mathf.Abs(avgHardness - avgFlexibility);
                if (balance < 20)
                {
                    bonus += 10;
                }

                return bonus;
            }
        }

        #endregion

        #region Helper

        private DynValue RunLua(CraftingSystemState state, List<CraftingIngredient> ingredients, string lua)
        {
            // Strip markdown code blocks
            lua = StripMarkdown(lua);
            CraftingBindings bindings = new(state, ingredients);
            LuaApiRegistry reg = new();
            bindings.RegisterGameplayApis(reg);
            SecureLuaEnvironment env = new();
            Script script = env.CreateScript(reg);
            return env.RunChunk(script, lua);
        }

        private static string StripMarkdown(string lua)
        {
            if (string.IsNullOrEmpty(lua))
            {
                return lua;
            }

            // Remove ```lua ... ``` or ``` ... ```
            int start = lua.IndexOf("```");
            if (start >= 0)
            {
                int end = lua.LastIndexOf("```");
                if (end > start)
                {
                    lua = lua.Substring(start + 3, end - start - 3);
                    // Remove "lua" prefix if present
                    if (lua.StartsWith("lua"))
                    {
                        lua = lua.Substring(3);
                    }
                }
            }

            return lua.Trim();
        }

        private AiOrchestrator CreateOrchestrator(ILlmClient llm, IAiGameCommandSink sink)
        {
            return new AiOrchestrator(
                new SoloAuthorityHost(),
                llm,
                sink,
                new SessionTelemetryCollector(),
                new AiPromptComposer(
                    new BuiltInDefaultAgentSystemPromptProvider(),
                    new NoAgentUserPromptTemplateProvider(),
                    new NullLuaScriptVersionStore()),
                new NullAgentMemoryStore(),
                new AgentMemoryPolicy(),
                new NoOpRoleStructuredResponsePolicy(),
                new NullAiOrchestrationMetrics(), UnityEngine.ScriptableObject.CreateInstance<CoreAI.Infrastructure.Llm.CoreAISettingsAsset>());
        }

        #endregion

        #region Test 1: Basic Craft - Weapon from Metal + Crystal

        [Test]
        public async Task Craft_WeaponFromMetalAndCrystal_AiCreatesUniqueProperties()
        {
            CraftingSystemState state = new();
            List<CraftingIngredient> ingredients = new()
            {
                new CraftingIngredient
                {
                    Name = "Steel Ingot",
                    Type = "metal",
                    Hardness = 80,
                    Flexibility = 20,
                    MagicPower = 10,
                    Weight = 70,
                    Rarity = 2
                },
                new CraftingIngredient
                {
                    Name = "Fire Crystal",
                    Type = "crystal",
                    Hardness = 30,
                    Flexibility = 10,
                    MagicPower = 85,
                    Weight = 15,
                    Rarity = 4,
                    SpecialProperties = { ["fire_damage"] = 25 }
                }
            };

            // AI Programmer создает формулу крафта
            MockLlmClient llm = new(
                "```lua\n" +
                "-- Craft: Steel Fireblade\n" +
                "-- Analysis: High hardness metal + high magic crystal = Magic Weapon\n" +
                "local item_type = determine_item_type()\n" +
                "local quality = calc_quality()\n" +
                "local synergy = calc_synergy_bonus()\n" +
                "\n" +
                "-- Add special effect from fire crystal\n" +
                "add_special_effect('fire_damage: +25%')\n" +
                "add_special_effect('crystal_resonance: magic_power * 1.2')\n" +
                "\n" +
                "create_item('Steel Fireblade', item_type, quality)\n" +
                "report('crafted Steel Fireblade with fire damage')\n" +
                "```"
            );

            CommandSink sink = new();
            AiOrchestrator orch = CreateOrchestrator(llm, sink);
            await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Craft a weapon from Steel Ingot (hardness:80, magic:10) and Fire Crystal (magic:85, rarity:4)"
            });

            ApplyAiGameCommand envelope = sink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(envelope, "AI должен создать Lua скрипт крафта");

            // Выполняем скрипт
            RunLua(state, ingredients, envelope.JsonPayload);

            // Проверяем результат
            Assert.AreEqual(1, state.TotalCrafts, "Должен быть создан 1 предмет");
            CraftedItem item = state.CraftedItems[0];
            Assert.AreEqual("Steel Fireblade", item.Name);
            Assert.IsTrue(item.Quality > 40, "Качество должно быть > 40");
            Assert.IsTrue(item.SpecialEffects.Contains("fire_damage: +25%"), "Должен быть эффект огня");
            Assert.IsTrue(state.CraftingLog.Exists(e => e.Contains("report:crafted Steel Fireblade")));
        }

        #endregion

        #region Test 2: Craft Potion from Multiple Herbs - AI Invents Recipe

        [Test]
        public async Task Craft_PotionFromHerbs_AiInventsNewRecipe()
        {
            CraftingSystemState state = new();
            List<CraftingIngredient> ingredients = new()
            {
                new CraftingIngredient
                {
                    Name = "Healing Herb",
                    Type = "herb",
                    Hardness = 5,
                    Flexibility = 30,
                    MagicPower = 60,
                    Weight = 10,
                    Rarity = 2,
                    SpecialProperties = { ["healing"] = 30 }
                },
                new CraftingIngredient
                {
                    Name = "Stamina Leaf",
                    Type = "herb",
                    Hardness = 3,
                    Flexibility = 40,
                    MagicPower = 45,
                    Weight = 8,
                    Rarity = 2,
                    SpecialProperties = { ["stamina"] = 20 }
                },
                new CraftingIngredient
                {
                    Name = "Moonpetal",
                    Type = "herb",
                    Hardness = 2,
                    Flexibility = 25,
                    MagicPower = 75,
                    Weight = 5,
                    Rarity = 4,
                    SpecialProperties = { ["mana_regen"] = 15 }
                }
            };

            // AI Programmer создает рецепт зелья
            MockLlmClient llm = new(
                "```lua\n" +
                "-- Craft: Elixir of Vitality\n" +
                "-- 3 herbs with healing + stamina + mana properties\n" +
                "local avg_magic = calc_avg_magic()\n" +
                "local synergy = calc_synergy_bonus()\n" +
                "\n" +
                "-- Special effects from herb combination\n" +
                "add_special_effect('healing: 30 HP per 5 sec')\n" +
                "add_special_effect('stamina_boost: +20% movement speed')\n" +
                "add_special_effect('mana_regen: +15 mana per sec')\n" +
                "add_special_effect('synergy_bonus: duration * ' .. (1 + synergy/100))\n" +
                "\n" +
                "create_item('Elixir of Vitality', 'potion', calc_quality())\n" +
                "report('invented Elixir of Vitality')\n" +
                "```"
            );

            CommandSink sink = new();
            AiOrchestrator orch = CreateOrchestrator(llm, sink);
            await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Craft a potion from 3 herbs: Healing Herb, Stamina Leaf, Moonpetal (rarity 4)"
            });

            ApplyAiGameCommand envelope = sink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(envelope);

            RunLua(state, ingredients, envelope.JsonPayload);

            Assert.AreEqual(1, state.TotalCrafts);
            CraftedItem potion = state.CraftedItems[0];
            Assert.AreEqual("Elixir of Vitality", potion.Name);
            Assert.GreaterOrEqual(potion.SpecialEffects.Count, 3, "Должно быть 3+ спецэффекта");
            Assert.IsTrue(potion.SpecialEffects.Exists(e => e.Contains("healing")));
            Assert.IsTrue(potion.SpecialEffects.Exists(e => e.Contains("stamina")));
        }

        #endregion

        #region Test 3: Craft Armor with Unique Properties - AI Analyzes Synergies

        [Test]
        public async Task Craft_Armor_AiAnalyzesSynergies()
        {
            CraftingSystemState state = new();
            List<CraftingIngredient> ingredients = new()
            {
                new CraftingIngredient
                {
                    Name = "Dragon Leather",
                    Type = "leather",
                    Hardness = 50,
                    Flexibility = 70,
                    MagicPower = 40,
                    Weight = 40,
                    Rarity = 5,
                    SpecialProperties = { ["fire_resist"] = 30 }
                },
                new CraftingIngredient
                {
                    Name = "Ironbark Wood",
                    Type = "wood",
                    Hardness = 65,
                    Flexibility = 45,
                    MagicPower = 20,
                    Weight = 50,
                    Rarity = 3,
                    SpecialProperties = { ["nature_resist"] = 15 }
                },
                new CraftingIngredient
                {
                    Name = "Shadow Essence",
                    Type = "magic",
                    Hardness = 10,
                    Flexibility = 80,
                    MagicPower = 90,
                    Weight = 5,
                    Rarity = 5,
                    SpecialProperties = { ["stealth"] = 25 }
                }
            };

            // AI Programmer создает уникальную броню
            MockLlmClient llm = new(
                "```lua\n" +
                "-- Craft: Shadow Dragon Guard\n" +
                "-- Synergy: Dragon Leather + Ironbark + Shadow = Stealth Armor\n" +
                "local synergy = calc_synergy_bonus()\n" +
                "local quality = calc_quality()\n" +
                "\n" +
                "-- Unique properties from synergy analysis\n" +
                "add_special_effect('fire_resistance: 30%')\n" +
                "add_special_effect('nature_resistance: 15%')\n" +
                "add_special_effect('stealth_field: +25% invisibility')\n" +
                "add_special_effect('synergy_armor: defense * ' .. (1 + synergy/200))\n" +
                "\n" +
                "local item_name = 'Shadow Dragon Guard'\n" +
                "create_item(item_name, 'armor', quality)\n" +
                "report('crafted ' .. item_name .. ' with synergy bonus ' .. synergy)\n" +
                "```"
            );

            CommandSink sink = new();
            AiOrchestrator orch = CreateOrchestrator(llm, sink);
            await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Craft armor from Dragon Leather (rarity 5), Ironbark Wood, Shadow Essence (rarity 5)"
            });

            ApplyAiGameCommand envelope = sink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(envelope);

            RunLua(state, ingredients, envelope.JsonPayload);

            Assert.AreEqual(1, state.TotalCrafts);
            CraftedItem armor = state.CraftedItems[0];
            Assert.AreEqual("Shadow Dragon Guard", armor.Name);
            Assert.GreaterOrEqual(armor.SpecialEffects.Count, 4);
            Assert.IsTrue(armor.SpecialEffects.Exists(e => e.Contains("fire_resistance")));
            Assert.IsTrue(armor.SpecialEffects.Exists(e => e.Contains("stealth")));
        }

        #endregion

        #region Test 4: AI Analyzer Suggests Improvements

        [Test]
        public async Task Craft_AnalyzerSuggestsImprovements_ProgrammerImplements()
        {
            CraftingSystemState state = new();
            List<CraftingIngredient> ingredients = new()
            {
                new CraftingIngredient
                {
                    Name = "Mithril Ore",
                    Type = "metal",
                    Hardness = 70,
                    Flexibility = 50,
                    MagicPower = 60,
                    Weight = 30,
                    Rarity = 4
                },
                new CraftingIngredient
                {
                    Name = "Phoenix Feather",
                    Type = "magic",
                    Hardness = 5,
                    Flexibility = 90,
                    MagicPower = 95,
                    Weight = 2,
                    Rarity = 5,
                    SpecialProperties = { ["resurrection"] = 1 }
                }
            };

            // Шаг 1: AI Analyzer анализирует ингредиенты
            MockLlmClient analyzerLlm = new(
                "{\"analysis\": \"Mithril + Phoenix = Legendary weapon with resurrection property. " +
                "Suggestion: Add rebirth mechanic and fire damage scaling with magic power.\"}"
            );

            CommandSink analyzerSink = new();
            AiOrchestrator analyzerOrch = CreateOrchestrator(analyzerLlm, analyzerSink);
            await analyzerOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Analyzer,
                Hint =
                    "Analyze: Mithril Ore (hardness:70, magic:60) + Phoenix Feather (magic:95, rarity:5, resurrection)"
            });

            Assert.AreEqual(1, analyzerSink.Commands.Count);
            StringAssert.Contains("resurrection", analyzerSink.Commands[0].JsonPayload.ToLower());
            StringAssert.Contains("fire damage", analyzerSink.Commands[0].JsonPayload.ToLower());

            // Шаг 2: AI Programmer создает улучшенный предмет
            MockLlmClient programmerLlm = new(
                "```lua\n" +
                "-- Craft: Phoenix Mithril Blade (Legendary)\n" +
                "-- Based on Analyzer suggestion: resurrection + fire scaling\n" +
                "local avg_magic = calc_avg_magic()\n" +
                "local quality = calc_quality()\n" +
                "\n" +
                "-- Legendary properties\n" +
                "add_special_effect('resurrection: 1 time per day')\n" +
                "add_special_effect('fire_damage: ' .. (avg_magic * 0.5) .. ' per hit')\n" +
                "add_special_effect('phoenix_blessing: +50% healing received')\n" +
                "add_special_effect('mithril_core: weapon durability x2')\n" +
                "\n" +
                "create_item('Phoenix Mithril Blade', 'weapon', quality)\n" +
                "report('crafted legendary Phoenix Mithril Blade')\n" +
                "```"
            );

            CommandSink progSink = new();
            AiOrchestrator progOrch = CreateOrchestrator(programmerLlm, progSink);
            await progOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Create legendary weapon from Mithril + Phoenix Feather with resurrection"
            });

            ApplyAiGameCommand envelope = progSink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(envelope);

            RunLua(state, ingredients, envelope.JsonPayload);

            Assert.AreEqual(1, state.TotalCrafts);
            CraftedItem weapon = state.CraftedItems[0];
            Assert.AreEqual("Phoenix Mithril Blade", weapon.Name);
            Assert.GreaterOrEqual(weapon.SpecialEffects.Count, 4);
            Assert.IsTrue(weapon.SpecialEffects.Exists(e => e.Contains("resurrection")));
            Assert.IsTrue(weapon.SpecialEffects.Exists(e => e.Contains("fire_damage")));
        }

        #endregion

        #region Test 5: AI Creates Completely New Item Type

        [Test]
        public async Task Craft_CreatesNewItemFromUnusualCombination()
        {
            CraftingSystemState state = new();
            List<CraftingIngredient> ingredients = new()
            {
                new CraftingIngredient
                {
                    Name = "Time Crystal",
                    Type = "crystal",
                    Hardness = 40,
                    Flexibility = 30,
                    MagicPower = 100,
                    Weight = 10,
                    Rarity = 5,
                    SpecialProperties = { ["time_manipulation"] = 50 }
                },
                new CraftingIngredient
                {
                    Name = "Void Essence",
                    Type = "magic",
                    Hardness = 0,
                    Flexibility = 100,
                    MagicPower = 95,
                    Weight = 0,
                    Rarity = 5,
                    SpecialProperties = { ["void_damage"] = 40 }
                },
                new CraftingIngredient
                {
                    Name = "Ancient Rune Stone",
                    Type = "crystal",
                    Hardness = 90,
                    Flexibility = 5,
                    MagicPower = 70,
                    Weight = 60,
                    Rarity = 4,
                    SpecialProperties = { ["rune_inscription"] = 1 }
                }
            };

            // AI Programmer создает НЕОБЫЧНЫЙ предмет
            MockLlmClient llm = new(
                "```lua\n" +
                "-- Craft: Chrono Void Amulet (Artifact)\n" +
                "-- Unusual combo: Time + Void + Runes = Time Manipulation Artifact\n" +
                "local synergy = calc_synergy_bonus()\n" +
                "local avg_magic = calc_avg_magic()\n" +
                "\n" +
                "-- Unique artifact properties\n" +
                "add_special_effect('time_slow: enemies -30% speed in 10m radius')\n" +
                "add_special_effect('void_strike: ' .. (avg_magic * 0.8) .. ' void damage')\n" +
                "add_special_effect('rune_ward: absorb ' .. (synergy * 2) .. ' damage per minute')\n" +
                "add_special_effect('chrono_shift: blink 5 sec back in time (1 use per day)')\n" +
                "\n" +
                "create_item('Chrono Void Amulet', 'amulet', calc_quality())\n" +
                "report('created artifact: Chrono Void Amulet')\n" +
                "```"
            );

            CommandSink sink = new();
            AiOrchestrator orch = CreateOrchestrator(llm, sink);
            await orch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Craft artifact from Time Crystal (rarity 5), Void Essence (rarity 5), Ancient Rune Stone"
            });

            ApplyAiGameCommand envelope = sink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(envelope);

            RunLua(state, ingredients, envelope.JsonPayload);

            Assert.AreEqual(1, state.TotalCrafts);
            CraftedItem artifact = state.CraftedItems[0];
            Assert.AreEqual("Chrono Void Amulet", artifact.Name);
            Assert.AreEqual("amulet", artifact.ItemType);
            Assert.GreaterOrEqual(artifact.SpecialEffects.Count, 4);
            Assert.IsTrue(artifact.SpecialEffects.Exists(e => e.Contains("time_slow")));
            Assert.IsTrue(artifact.SpecialEffects.Exists(e => e.Contains("chrono_shift")));
        }

        #endregion

        #region Test 6: Multiple Crafts - AI Learns Pattern

        [Test]
        public async Task Craft_MultipleCrafts_AiLearnsPatterns()
        {
            CraftingSystemState state = new();

            // Craft 1: Simple Weapon
            List<CraftingIngredient> ingredients1 = new()
            {
                new CraftingIngredient
                {
                    Name = "Iron", Type = "metal", Hardness = 60, Flexibility = 30, MagicPower = 5, Weight = 70,
                    Rarity = 1
                },
                new CraftingIngredient
                {
                    Name = "Wood", Type = "wood", Hardness = 40, Flexibility = 50, MagicPower = 10, Weight = 40,
                    Rarity = 1
                }
            };

            MockLlmClient llm1 = new(
                "```lua\n" +
                "add_special_effect('basic_damage: +15')\n" +
                "create_item('Iron Sword', determine_item_type(), calc_quality())\n" +
                "report('crafted basic weapon')\n" +
                "```"
            );

            CommandSink sink1 = new();
            await CreateOrchestrator(llm1, sink1).RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Craft simple weapon from Iron and Wood"
            });

            RunLua(state, ingredients1, sink1.Commands[0].JsonPayload);

            // Craft 2: Better Weapon
            List<CraftingIngredient> ingredients2 = new()
            {
                new CraftingIngredient
                {
                    Name = "Steel", Type = "metal", Hardness = 75, Flexibility = 35, MagicPower = 10, Weight = 60,
                    Rarity = 2
                },
                new CraftingIngredient
                {
                    Name = "Hardwood", Type = "wood", Hardness = 50, Flexibility = 45, MagicPower = 15, Weight = 35,
                    Rarity = 2
                }
            };

            MockLlmClient llm2 = new(
                "```lua\n" +
                "add_special_effect('improved_damage: +25')\n" +
                "add_special_effect('steel_edge: +10% critical chance')\n" +
                "create_item('Steel Longsword', determine_item_type(), calc_quality())\n" +
                "report('crafted better weapon')\n" +
                "```"
            );

            CommandSink sink2 = new();
            await CreateOrchestrator(llm2, sink2).RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Craft better weapon from Steel and Hardwood"
            });

            RunLua(state, ingredients2, sink2.Commands[0].JsonPayload);

            // Craft 3: Magic Weapon
            List<CraftingIngredient> ingredients3 = new()
            {
                new CraftingIngredient
                {
                    Name = "Mithril", Type = "metal", Hardness = 70, Flexibility = 50, MagicPower = 60, Weight = 30,
                    Rarity = 4
                },
                new CraftingIngredient
                {
                    Name = "Enchanted Wood", Type = "wood", Hardness = 45, Flexibility = 55, MagicPower = 70,
                    Weight = 25, Rarity = 3
                }
            };

            MockLlmClient llm3 = new(
                "```lua\n" +
                "add_special_effect('magic_damage: +40')\n" +
                "add_special_effect('mana_drain: 5 mana per hit')\n" +
                "add_special_effect('enchantment: +20% spell damage')\n" +
                "create_item('Mithril Enchanted Blade', determine_item_type(), calc_quality())\n" +
                "report('crafted magic weapon')\n" +
                "```"
            );

            CommandSink sink3 = new();
            await CreateOrchestrator(llm3, sink3).RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Craft magic weapon from Mithril and Enchanted Wood"
            });

            RunLua(state, ingredients3, sink3.Commands[0].JsonPayload);

            // Проверяем эволюцию крафта
            Assert.AreEqual(3, state.TotalCrafts);
            Assert.AreEqual("Iron Sword", state.CraftedItems[0].Name);
            Assert.AreEqual("Steel Longsword", state.CraftedItems[1].Name);
            Assert.AreEqual("Mithril Enchanted Blade", state.CraftedItems[2].Name);

            // Эволюция: 1 → 2 → 3 спецэффекта
            Assert.AreEqual(1, state.CraftedItems[0].SpecialEffects.Count);
            Assert.AreEqual(2, state.CraftedItems[1].SpecialEffects.Count);
            Assert.AreEqual(3, state.CraftedItems[2].SpecialEffects.Count);

            // Качество растет
            Assert.Less(state.CraftedItems[0].Quality, state.CraftedItems[2].Quality);
        }

        #endregion

        #region Test 7: Deterministic Crafting - Same Ingredients = Same Result

        [Test]
        public void Craft_Deterministic_SameIngredients_SameResult()
        {
            // Этот тест проверяет что ОДНИ И ТЕ ЖЕ ингредиенты дают ОДИН И ТОТ ЖЕ результат
            // Важно для повторяемости крафта в игре

            string recipeName = "Fire Crystal Amulet";
            List<CraftingIngredient> ingredients = new()
            {
                new CraftingIngredient
                {
                    Name = "Fire Crystal",
                    Type = "crystal",
                    Hardness = 30,
                    Flexibility = 10,
                    MagicPower = 85,
                    Weight = 15,
                    Rarity = 4,
                    SpecialProperties = { ["fire_damage"] = 25 }
                },
                new CraftingIngredient
                {
                    Name = "Silver Chain",
                    Type = "metal",
                    Hardness = 40,
                    Flexibility = 80,
                    MagicPower = 20,
                    Weight = 25,
                    Rarity = 2,
                    SpecialProperties = { ["magic_conduit"] = 10 }
                },
                new CraftingIngredient
                {
                    Name = "Phoenix Ash",
                    Type = "magic",
                    Hardness = 5,
                    Flexibility = 50,
                    MagicPower = 90,
                    Weight = 3,
                    Rarity = 5,
                    SpecialProperties = { ["rebirth"] = 1 }
                }
            };

            // Lua скрипт крафта - ОДИНАКОВЫЙ для всех итераций
            string craftingScript =
                "local avg_magic = calc_avg_magic()\n" +
                "local avg_rarity = calc_avg_rarity()\n" +
                "local synergy = calc_synergy_bonus()\n" +
                "local quality = calc_quality()\n" +
                "\n" +
                "-- Unique properties derived from ingredients\n" +
                "add_special_effect('fire_damage: ' .. string.format('%.0f', avg_magic * 0.4))\n" +
                "add_special_effect('magic_conduit: spell_cost -' .. string.format('%.0f', synergy * 0.5) .. '%')\n" +
                "add_special_effect('phoenix_blessing: revive with ' .. string.format('%.0f', avg_rarity * 10) .. ' HP')\n" +
                "add_special_effect('synergy_multiplier: ' .. string.format('%.2f', 1 + synergy/100))\n" +
                "\n" +
                "create_item('Fire Crystal Amulet', 'amulet', quality)\n" +
                "report('crafted Fire Crystal Amulet')\n";

            // ===== ИТЕРАЦИЯ 1: Первый крафт =====
            CraftingSystemState state1 = new();
            RunLua(state1, ingredients, craftingScript);

            Assert.AreEqual(1, state1.TotalCrafts);
            CraftedItem item1 = state1.CraftedItems[0];
            Assert.AreEqual("Fire Crystal Amulet", item1.Name);
            Assert.AreEqual("amulet", item1.ItemType);
            Assert.AreEqual(4, item1.SpecialEffects.Count, "Должно быть 4 спецэффекта");

            // Запоминаем ВСЕ свойства первого крафта
            float firstQuality = item1.Quality;
            Dictionary<string, float> firstStats = new(item1.Stats);
            List<string> firstEffects = new(item1.SpecialEffects);

            // ===== ИТЕРАЦИЯ 2: Те же ингредиенты = тот же результат =====
            CraftingSystemState state2 = new();
            RunLua(state2, ingredients, craftingScript);

            Assert.AreEqual(1, state2.TotalCrafts);
            CraftedItem item2 = state2.CraftedItems[0];

            // Проверяем ИДЕНТИЧНОСТЬ
            Assert.AreEqual(item1.Name, item2.Name, "Имя должно совпадать");
            Assert.AreEqual(item1.ItemType, item2.ItemType, "Тип должен совпадать");
            Assert.AreEqual(item1.Quality, item2.Quality, 0.1f, "Качество должно совпадать");
            Assert.AreEqual(item1.SpecialEffects.Count, item2.SpecialEffects.Count, "Кол-во эффектов должно совпадать");

            // Проверяем каждый спецэффект
            for (int i = 0; i < firstEffects.Count; i++)
            {
                Assert.AreEqual(firstEffects[i], item2.SpecialEffects[i],
                    $"Спецэффект [{i}] должен совпадать");
            }

            // Проверяем каждый стат
            foreach (KeyValuePair<string, float> kvp in firstStats)
            {
                Assert.IsTrue(item2.Stats.ContainsKey(kvp.Key), $"Стат '{kvp.Key}' должен существовать");
                Assert.AreEqual(kvp.Value, item2.Stats[kvp.Key], 0.1f,
                    $"Значение стата '{kvp.Key}' должно совпадать");
            }

            // ===== ИТЕРАЦИЯ 3: Еще раз для уверенности =====
            CraftingSystemState state3 = new();
            RunLua(state3, ingredients, craftingScript);

            CraftedItem item3 = state3.CraftedItems[0];

            // Снова проверяем идентичность
            Assert.AreEqual(item1.Name, item3.Name);
            Assert.AreEqual(item1.Quality, item3.Quality, 0.1f);
            Assert.AreEqual(firstEffects.Count, item3.SpecialEffects.Count);

            for (int i = 0; i < firstEffects.Count; i++)
            {
                Assert.AreEqual(firstEffects[i], item3.SpecialEffects[i],
                    $"Итерация 3: Спецэффект [{i}] должен совпадать");
            }

            // ===== ЛОГИРОВАНИЕ ДЛЯ ПРОВЕРКИ =====
            Debug.Log("=== DETERMINISTIC CRAFTING TEST ===");
            Debug.Log($"Recipe: {recipeName}");
            Debug.Log($"Ingredients used:");
            foreach (CraftingIngredient ing in ingredients)
            {
                Debug.Log(
                    $"  - {ing.Name} ({ing.Type}): rarity={ing.Rarity}, magic={ing.MagicPower}, hardness={ing.Hardness}");
            }

            Debug.Log($"Result (3 identical iterations):");
            Debug.Log($"  Item: {item1.Name} [{item1.ItemType}]");
            Debug.Log($"  Quality: {item1.Quality:F1}");
            Debug.Log($"  Stats:");
            foreach (KeyValuePair<string, float> kvp in firstStats)
            {
                Debug.Log($"    {kvp.Key}: {kvp.Value:F1}");
            }

            Debug.Log($"  Special Effects:");
            foreach (string effect in firstEffects)
            {
                Debug.Log($"    • {effect}");
            }

            Debug.Log("===================================");
        }

        #endregion

        #region Test 8: Different Ingredients = Different Results (Uniqueness)

        [Test]
        public void Craft_DifferentIngredients_DifferentUniqueResults()
        {
            // Проверяем что РАЗНЫЕ ингредиенты дают ДЕЙСТВИТЕЛЬНО разные результаты

            string craftingScript =
                "local avg_magic = calc_avg_magic()\n" +
                "local synergy = calc_synergy_bonus()\n" +
                "local quality = calc_quality()\n" +
                "add_special_effect('magic_power: ' .. string.format('%.0f', avg_magic * 0.5))\n" +
                "add_special_effect('synergy_bonus: ' .. string.format('%.0f', synergy))\n" +
                "create_item('Test Item', determine_item_type(), quality)\n";

            // Рецепт A: Высокая магия
            List<CraftingIngredient> ingredientsA = new()
            {
                new CraftingIngredient
                {
                    Name = "Crystal A", Type = "crystal", Hardness = 20, Flexibility = 20, MagicPower = 90, Weight = 10,
                    Rarity = 5
                },
                new CraftingIngredient
                {
                    Name = "Herb A", Type = "herb", Hardness = 5, Flexibility = 30, MagicPower = 80, Weight = 5,
                    Rarity = 4
                }
            };

            CraftingSystemState stateA = new();
            RunLua(stateA, ingredientsA, craftingScript);
            CraftedItem itemA = stateA.CraftedItems[0];

            // Рецепт B: Низкая магия, высокая твердость
            List<CraftingIngredient> ingredientsB = new()
            {
                new CraftingIngredient
                {
                    Name = "Iron B", Type = "metal", Hardness = 90, Flexibility = 10, MagicPower = 5, Weight = 80,
                    Rarity = 2
                },
                new CraftingIngredient
                {
                    Name = "Wood B", Type = "wood", Hardness = 70, Flexibility = 40, MagicPower = 10, Weight = 50,
                    Rarity = 1
                }
            };

            CraftingSystemState stateB = new();
            RunLua(stateB, ingredientsB, craftingScript);
            CraftedItem itemB = stateB.CraftedItems[0];

            // Проверяем что результаты РАЗНЫЕ
            Assert.IsTrue(Mathf.Abs(itemA.Quality - itemB.Quality) > 1f);
            Assert.AreNotEqual(itemA.ItemType, itemB.ItemType);

            // Спецэффекты должны быть разными (значения зависят от ингредиентов)
            Assert.AreEqual(itemA.SpecialEffects.Count, itemB.SpecialEffects.Count, "Кол-во эффектов одинаковое");
            Assert.AreNotEqual(itemA.SpecialEffects[0], itemB.SpecialEffects[0], "Значения эффектов должны отличаться");

            Debug.Log("=== UNIQUENESS TEST ===");
            Debug.Log($"Recipe A: {itemA.Name} [{itemA.ItemType}], Quality: {itemA.Quality.ToString("F1")}");
            Debug.Log($"  Effects: {string.Join(", ", itemA.SpecialEffects)}");
            Debug.Log($"Recipe B: {itemB.Name} [{itemB.ItemType}], Quality: {itemB.Quality.ToString("F1")}");
            Debug.Log($"  Effects: {string.Join(", ", itemB.SpecialEffects)}");
            Debug.Log("Results are UNIQUE - different ingredients produce different items!");
            Debug.Log("=========================");
        }

        #endregion

        #region Helpers

        private sealed class MockLlmClient : ILlmClient
        {
            private readonly string _response;

            public MockLlmClient(string response)
            {
                _response = response;
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest req, CancellationToken ct = default)
            {
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = _response });
            }
        }

        private sealed class CommandSink : IAiGameCommandSink
        {
            public readonly List<ApplyAiGameCommand> Commands = new();

            public void Publish(ApplyAiGameCommand cmd)
            {
                Commands.Add(cmd);
            }
        }

        #endregion
    }
}
