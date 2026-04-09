using System.Collections.Generic;
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
    /// Интеграционные тесты: проверяют СОВМЕСТНУЮ работу компонентов.
    /// Пайплайн: AI генеррует Lua → выполняется → результат передается другому AI → улучшает скрипт.
    /// </summary>
    public sealed class AiLuaPipelineIntegrationEditModeTests
    {
        #region Test Infrastructure

        private sealed class MockLlmClient : ILlmClient
        {
            private readonly Queue<string> _responses;
            public List<LlmCompletionRequest> ReceivedRequests { get; } = new();

            public MockLlmClient(params string[] responses)
            {
                _responses = new Queue<string>(responses);
            }

            public Task<LlmCompletionResult> CompleteAsync(LlmCompletionRequest request, CancellationToken ct = default)
            {
                ReceivedRequests.Add(request);
                string response = _responses.Count > 0 ? _responses.Dequeue() : "{\"ok\":true}";
                return Task.FromResult(new LlmCompletionResult { Ok = true, Content = response });
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

        private sealed class WorldState
        {
            public readonly Dictionary<string, Vector3> SpawnedEntities = new();
            public readonly Dictionary<string, bool> EntityStates = new();
            public readonly List<string> Events = new();
            public float Time = 0f;
        }

        private sealed class WorldBindings : IGameLuaRuntimeBindings
        {
            private readonly WorldState _world;

            public WorldBindings(WorldState world)
            {
                _world = world;
            }

            public void RegisterGameplayApis(LuaApiRegistry registry)
            {
                // Spawn entity
                registry.Register("spawn_entity",
                    new System.Func<string, double, double, double, string>((name, x, y, z) =>
                    {
                        Vector3 pos = new((float)x, (float)y, (float)z);
                        string id = $"{name}_{_world.SpawnedEntities.Count}";
                        _world.SpawnedEntities[id] = pos;
                        _world.EntityStates[id] = true;
                        _world.Events.Add($"spawn:{id}@{pos}");
                        return id;
                    }));

                // Move entity
                registry.Register("move_entity", new System.Action<string, double, double, double>((id, x, y, z) =>
                {
                    if (_world.SpawnedEntities.ContainsKey(id))
                    {
                        _world.SpawnedEntities[id] = new Vector3((float)x, (float)y, (float)z);
                        _world.Events.Add($"move:{id}");
                    }
                }));

                // Set entity active/inactive
                registry.Register("set_entity_active", new System.Action<string, bool>((id, active) =>
                {
                    if (_world.EntityStates.ContainsKey(id))
                    {
                        _world.EntityStates[id] = active;
                        _world.Events.Add(active ? $"activate:{id}" : $"deactivate:{id}");
                    }
                }));

                // Get entity count
                registry.Register("get_entity_count", new System.Func<double>(() =>
                    _world.SpawnedEntities.Count));

                // Get entities at position (within radius)
                registry.Register("get_entities_near",
                    new System.Func<double, double, double, double, string>((x, y, z, radius) =>
                    {
                        Vector3 pos = new((float)x, (float)y, (float)z);
                        float r = (float)radius;
                        List<string> near = new();
                        foreach (KeyValuePair<string, Vector3> kv in _world.SpawnedEntities)
                        {
                            if (Vector3.Distance(kv.Value, pos) <= r && _world.EntityStates[kv.Key])
                            {
                                near.Add(kv.Key);
                            }
                        }

                        return string.Join(",", near);
                    }));

                // Get world time
                registry.Register("get_time", new System.Func<double>(() => _world.Time));

                // Report
                registry.Register("report", new System.Action<string>(msg =>
                    _world.Events.Add($"report:{msg}")));
            }
        }

        private sealed class LuaProcessor
        {
            private readonly SecureLuaEnvironment _env;
            private readonly WorldBindings _bindings;
            private readonly LuaApiRegistry _reg;

            public LuaProcessor()
            {
                _env = new SecureLuaEnvironment();
                _reg = new LuaApiRegistry();
            }

            public DynValue ExecuteLua(string lua, WorldBindings bindings)
            {
                // Strip markdown code blocks
                lua = StripMarkdown(lua);
                bindings.RegisterGameplayApis(_reg);
                Script script = _env.CreateScript(_reg);
                return _env.RunChunk(script, lua);
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
        }

        #endregion

        #region Test 1: AI Spawns Entities → Programmer Improves

        [Test]
        public async Task Pipeline_SpawnEntities_ProgrammerOptimizes()
        {
            WorldState world = new();
            CommandSink sink = new();
            LuaProcessor luaProc = new();

            // Шаг 1: AI Programmer создает базовый скрипт спавна
            MockLlmClient llm1 = new(
                "```lua\n" +
                "-- Spawn 3 enemies at different positions\n" +
                "spawn_entity('goblin', 0, 0, 0)\n" +
                "spawn_entity('goblin', 5, 0, 0)\n" +
                "spawn_entity('goblin', -5, 0, 0)\n" +
                "report('spawned 3 goblins')\n" +
                "```"
            );

            AiOrchestrator orch1 = CreateOrchestrator(llm1, sink);
            await orch1.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Spawn 3 goblin enemies at positions (0,0,0), (5,0,0), (-5,0,0)"
            });

            // Шаг 2: Извлекаем Lua из конверта и выполняем
            ApplyAiGameCommand envelope = sink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(envelope, "Должен быть создан Lua конверт");

            DynValue result1 = luaProc.ExecuteLua(envelope.JsonPayload, new WorldBindings(world));

            // Проверяем что entities заспавнились
            Assert.AreEqual(3, world.SpawnedEntities.Count, "Должно быть 3 entity");
            Assert.AreEqual(3, world.SpawnedEntities.Count);
            Assert.IsTrue(world.Events.Exists(e => e.Contains("report:spawned 3 goblins")));

            // Шаг 3: AI Programmer ОПТИМИЗИРУЕТ скрипт (добавляет функцию)
            MockLlmClient llm2 = new(
                "```lua\n" +
                "-- Optimized: spawn in circle pattern\n" +
                "function spawn_circle(name, count, radius)\n" +
                "    for i = 0, count - 1 do\n" +
                "        local angle = (i / count) * math.pi * 2\n" +
                "        local x = math.cos(angle) * radius\n" +
                "        local z = math.sin(angle) * radius\n" +
                "        spawn_entity(name, x, 0, z)\n" +
                "    end\n" +
                "    report('spawned ' .. count .. ' in circle')\n" +
                "end\n" +
                "\n" +
                "spawn_circle('orc', 4, 10)\n" +
                "```\n"
            );

            CommandSink sink2 = new();
            AiOrchestrator orch2 = CreateOrchestrator(llm2, sink2);
            await orch2.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Optimize: spawn 4 orcs in a circle pattern with radius 10"
            });

            ApplyAiGameCommand envelope2 = sink2.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(envelope2, "Оптимизированный скрипт должен быть создан");

            WorldState world2 = new();
            DynValue result2 = luaProc.ExecuteLua(envelope2.JsonPayload, new WorldBindings(world2));

            Assert.AreEqual(4, world2.SpawnedEntities.Count, "Должно быть 4 орка в круге");
            Assert.IsTrue(world2.Events.Exists(e => e.Contains("report:spawned 4 in circle")));
        }

        #endregion

        #region Test 2: Creator Analyzes → Programmer Creates Better Script

        [Test]
        public async Task Pipeline_CreatorAnalyzes_ProgrammerCreatesBetterScript()
        {
            WorldState world = new();
            LuaProcessor luaProc = new();

            // Шаг 1: Создаем начальное состояние
            luaProc.ExecuteLua(@"
                spawn_entity('warrior', 0, 0, 0)
                spawn_entity('warrior', 10, 0, 0)
                spawn_entity('warrior', 20, 0, 0)
            ", new WorldBindings(world));

            Assert.AreEqual(3, world.SpawnedEntities.Count);

            // Шаг 2: Creator АНАЛИЗИРУЕТ ситуацию и рекомендует улучшение
            MockLlmClient creatorLlm = new(
                "{\"analysis\": \"Entities are in a line, should be in triangle formation\", " +
                "\"recommendation\": \"Move to triangle: (0,0,0), (10,0,8.66), (20,0,0)\"}"
            );

            CommandSink creatorSink = new();
            AiOrchestrator creatorOrch = CreateOrchestrator(creatorLlm, creatorSink);
            await creatorOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Analyze current entity positions and suggest better formation"
            });

            Assert.AreEqual(1, creatorSink.Commands.Count, "Creator должен дать ответ");
            StringAssert.Contains("triangle", creatorSink.Commands[0].JsonPayload.ToLower());

            // Шаг 3: Programmer СОЗДАЕТ скрипт для треугольной формации
            MockLlmClient programmerLlm = new(
                "```lua\n" +
                "-- Triangle formation script\n" +
                "function spawn_triangle(name, size)\n" +
                "    local h = size * 0.866  -- sin(60)\n" +
                "    spawn_entity(name, 0, 0, 0)\n" +
                "    spawn_entity(name, size, 0, 0)\n" +
                "    spawn_entity(name, size/2, 0, h)\n" +
                "    report('triangle formed with size ' .. size)\n" +
                "end\n" +
                "\n" +
                "spawn_triangle('knight', 15)\n" +
                "```"
            );

            CommandSink progSink = new();
            AiOrchestrator progOrch = CreateOrchestrator(programmerLlm, progSink);
            await progOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Create script to spawn 3 knights in triangle formation with size 15"
            });

            ApplyAiGameCommand progEnvelope =
                progSink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(progEnvelope, "Programmer должен создать Lua скрипт");

            // Выполняем улучшенный скрипт
            WorldState world2 = new();
            luaProc.ExecuteLua(progEnvelope.JsonPayload, new WorldBindings(world2));

            Assert.AreEqual(3, world2.SpawnedEntities.Count, "Треугольная формация из 3 рыцарей");
            Assert.IsTrue(world2.Events.Exists(e => e.Contains("report:triangle formed")));
        }

        #endregion

        #region Test 3: Multi-Step AI Pipeline

        [Test]
        public async Task Pipeline_MultiStep_AiCollaboration()
        {
            // Пайплайн: Creator → Programmer → Execution → Verification
            WorldState world = new();
            LuaProcessor luaProc = new();

            // Шаг 1: Programmer создает базовый скрипт
            MockLlmClient progLlm = new(
                "```lua\n" +
                "-- Basic wave spawn\n" +
                "for i = 1, 5 do\n" +
                "    spawn_entity('enemy_' .. i, i * 3, 0, 0)\n" +
                "end\n" +
                "report('wave 1: 5 enemies')\n" +
                "```"
            );

            CommandSink progSink = new();
            AiOrchestrator progOrch = CreateOrchestrator(progLlm, progSink);
            await progOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Spawn wave 1: 5 enemies in a line"
            });

            // Выполняем скрипт
            ApplyAiGameCommand env1 = progSink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(env1);
            luaProc.ExecuteLua(env1.JsonPayload, new WorldBindings(world));

            Assert.AreEqual(5, world.SpawnedEntities.Count);
            Assert.IsTrue(world.Events.Exists(e => e.Contains("report:wave 1")));

            // Шаг 2: Creator анализирует и предлагает улучшение
            MockLlmClient creatorLlm = new(
                "{\"suggestion\": \"Add a boss enemy and make formation circular\"}"
            );

            CommandSink creatorSink = new();
            AiOrchestrator creatorOrch = CreateOrchestrator(creatorLlm, creatorSink);
            await creatorOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Creator,
                Hint = "Suggest improvement to current wave spawn"
            });

            Assert.AreEqual(1, creatorSink.Commands.Count);
            StringAssert.Contains("boss", creatorSink.Commands[0].JsonPayload.ToLower());

            // Шаг 3: Programmer создает улучшенный скрипт
            MockLlmClient progLlm2 = new(
                "```lua\n" +
                "-- Wave 2: Circle + Boss in center\n" +
                "function spawn_wave2(count, radius)\n" +
                "    -- Regular enemies in circle\n" +
                "    for i = 0, count - 1 do\n" +
                "        local angle = (i / count) * math.pi * 2\n" +
                "        local x = math.cos(angle) * radius\n" +
                "        local z = math.sin(angle) * radius\n" +
                "        spawn_entity('enemy_' .. (i+1), x, 0, z)\n" +
                "    end\n" +
                "    -- Boss in center\n" +
                "    spawn_entity('BOSS', 0, 0, 0)\n" +
                "    report('wave 2: ' .. count .. ' enemies + boss')\n" +
                "end\n" +
                "\n" +
                "spawn_wave2(6, 8)\n" +
                "```"
            );

            CommandSink progSink2 = new();
            AiOrchestrator progOrch2 = CreateOrchestrator(progLlm2, progSink2);
            await progOrch2.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Create wave 2: 6 enemies in circle + boss in center"
            });

            ApplyAiGameCommand env2 = progSink2.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(env2);

            WorldState world2 = new();
            luaProc.ExecuteLua(env2.JsonPayload, new WorldBindings(world2));

            // 6 врагов + 1 босс = 7
            Assert.AreEqual(7, world2.SpawnedEntities.Count);
            Assert.IsTrue(world2.SpawnedEntities.ContainsKey("BOSS_6"), "Босс должен быть заспавнен");
            Assert.IsTrue(world2.Events.Exists(e => e.Contains("report:wave 2")));
        }

        #endregion

        #region Test 4: Error Handling and Recovery

        [Test]
        public async Task Pipeline_BadLua_Fails_ProgrammerFixes()
        {
            WorldState world = new();
            LuaProcessor luaProc = new();

            // Шаг 1: Programmer создает ПЛОХОЙ скрипт (с ошибкой)
            MockLlmClient badLlm = new(
                "```lua\n" +
                "-- Bad script: missing function call\n" +
                "spawn_entity('goblin', 0, 0, 0)\n" +
                "non_existent_function()  -- Это вызовет ошибку\n" +
                "```"
            );

            CommandSink badSink = new();
            AiOrchestrator badOrch = CreateOrchestrator(badLlm, badSink);
            await badOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Spawn a goblin"
            });

            ApplyAiGameCommand badEnv = badSink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(badEnv);

            // Выполняем - должна быть ошибка
            Assert.Throws<ScriptRuntimeException>(() =>
                luaProc.ExecuteLua(badEnv.JsonPayload, new WorldBindings(world)));

            // Шаг 2: Programmer создает ИСПРАВЛЕННЫЙ скрипт
            MockLlmClient fixedLlm = new(
                "```lua\n" +
                "-- Fixed script\n" +
                "spawn_entity('goblin', 0, 0, 0)\n" +
                "report('goblin spawned successfully')\n" +
                "```"
            );

            CommandSink fixedSink = new();
            AiOrchestrator fixedOrch = CreateOrchestrator(fixedLlm, fixedSink);
            await fixedOrch.RunTaskAsync(new AiTaskRequest
            {
                RoleId = BuiltInAgentRoleIds.Programmer,
                Hint = "Spawn a goblin correctly"
            });

            ApplyAiGameCommand fixedEnv =
                fixedSink.Commands.Find(c => c.CommandTypeId == AiGameCommandTypeIds.Envelope);
            Assert.IsNotNull(fixedEnv);

            // Выполняем исправленный скрипт
            WorldState world2 = new();
            luaProc.ExecuteLua(fixedEnv.JsonPayload, new WorldBindings(world2));

            Assert.AreEqual(1, world2.SpawnedEntities.Count);
            Assert.IsTrue(world2.Events.Exists(e => e.Contains("report:goblin spawned")));
        }

        #endregion

        #region Helper Methods

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
    }
}
