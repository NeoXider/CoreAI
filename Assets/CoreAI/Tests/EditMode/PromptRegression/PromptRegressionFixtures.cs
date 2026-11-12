using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.AgentMemory;
using CoreAI.Ai;
using MEAI = Microsoft.Extensions.AI;

namespace CoreAI.Core.Tests.EditMode
{
    /// <summary>
    /// One prompt-regression case: a built-in role, a player or game input, the request shape the composition must
    /// produce for it, and the tool a model should pick first (none for a prose answer).
    /// </summary>
    public sealed class PromptFixtureCase
    {
        internal PromptFixtureCase(string name, string roleId, string input, string[] offeredTools,
            string[] acceptableFirstTools, bool expectHistory, string systemPromptMarker)
        {
            Name = name;
            RoleId = roleId;
            Input = input;
            OfferedTools = offeredTools;
            AcceptableFirstTools = acceptableFirstTools;
            ExpectHistory = expectHistory;
            SystemPromptMarker = systemPromptMarker;
        }

        public string Name { get; }
        public string RoleId { get; }
        public string Input { get; }

        /// <summary>Exact set of tool names the composed request must offer.</summary>
        public string[] OfferedTools { get; }

        /// <summary>Tools an acceptable first call may name; empty means the role should answer in prose.</summary>
        public string[] AcceptableFirstTools { get; }

        /// <summary>Whether a second turn of the same role carries the first one as chat history.</summary>
        public bool ExpectHistory { get; }

        /// <summary>Text from the role's built-in prompt that the composed system prompt must contain.</summary>
        public string SystemPromptMarker { get; }

        /// <summary>The recorded answer of the scripted model: the first acceptable tool, or prose.</summary>
        public string RecordedFirstTool => AcceptableFirstTools.Length > 0 ? AcceptableFirstTools[0] : null;

        public override string ToString()
        {
            return Name;
        }
    }

    /// <summary>
    /// The prompt-regression fixture set: a handful of inputs per built-in role, composed exactly as a shipped game
    /// composes them (built-in definitions, the shipped tool profiles filled with stand-in tools of the real names,
    /// the built-in prompts and the package universal prefix). The same runner checks the request shape against a
    /// scripted model and, in the explicit run, the tool choice of a real one.
    /// </summary>
    internal static class PromptRegressionFixtures
    {
        private static readonly string[] Prose = Array.Empty<string>();
        private static readonly string[] MemoryOnly = { "memory" };
        private static readonly string[] WorldBuilding = { "memory", "world_command" };

        private static readonly string[] ProgrammerTools =
        {
            "memory", "camera", "execute_lua", "manage_mods", "get_mod_logs", "save_world", "load_world",
            "list_autosaves", "load_autosave", "read_skill"
        };

        private static readonly string[] MerchantTools = { "memory", "get_inventory" };

        internal static IReadOnlyList<PromptFixtureCase> All { get; } = new[]
        {
            new PromptFixtureCase("Creator_BuildsATower", BuiltInAgentRoleIds.Creator,
                "Build a small stone watchtower next to the gate.", WorldBuilding, new[] { "world_command" }, true,
                "You are the Creator agent"),
            new PromptFixtureCase("Creator_SpawnsCubes", BuiltInAgentRoleIds.Creator,
                "Spawn three red cubes in a row.", WorldBuilding, new[] { "world_command" }, true,
                "You are the Creator agent"),
            new PromptFixtureCase("Creator_AnalysisOnly", BuiltInAgentRoleIds.Creator,
                "Analysis only, change nothing: is the arena too crowded?", WorldBuilding, Prose, true,
                "You are the Creator agent"),
            new PromptFixtureCase("Builder_BuildsABridge", BuiltInAgentRoleIds.Builder,
                "Build a wooden bridge across the river.", WorldBuilding, new[] { "world_command" }, true,
                "You are a 3D scene builder"),
            new PromptFixtureCase("Builder_PaintsWalls", BuiltInAgentRoleIds.Builder,
                "Paint the tower walls dark grey.", WorldBuilding, new[] { "world_command" }, true,
                "You are a 3D scene builder"),
            new PromptFixtureCase("Analyzer_ReportsRisks", BuiltInAgentRoleIds.Analyzer,
                "Report the risks in this session.", MemoryOnly, Prose, true, "You are the Analyzer agent"),
            new PromptFixtureCase("Analyzer_Boredom", BuiltInAgentRoleIds.Analyzer,
                "Is the player getting bored?", MemoryOnly, Prose, true, "You are the Analyzer agent"),
            new PromptFixtureCase("Programmer_Greeting", BuiltInAgentRoleIds.Programmer,
                "Hello! What can you do?", ProgrammerTools, Prose, false, "You are the Programmer agent"),
            new PromptFixtureCase("Programmer_BuildsAPart", BuiltInAgentRoleIds.Programmer,
                "Spawn a red part above the spawn point.", ProgrammerTools, new[] { "read_skill", "execute_lua" },
                false, "You are the Programmer agent"),
            new PromptFixtureCase("Programmer_ListsMods", BuiltInAgentRoleIds.Programmer,
                "Which mods are loaded right now?", ProgrammerTools, new[] { "manage_mods" }, false,
                "You are the Programmer agent"),
            new PromptFixtureCase("Programmer_RunsLua", BuiltInAgentRoleIds.Programmer,
                "Run this Lua: report(tostring(2 + 2))", ProgrammerTools, new[] { "execute_lua" }, false,
                "You are the Programmer agent"),
            // WHY these markers: the three turns the tiered Rbx API skill is measured on
            // (RbxApiSkillTieredReadEditModeTests) need the prompt to say how to read one section of it.
            new PromptFixtureCase("Programmer_MovesAPart", BuiltInAgentRoleIds.Programmer,
                "Move the part named Block1 five studs up.", ProgrammerTools, new[] { "read_skill", "execute_lua" },
                false, "read_skill('Rbx API', section='terrain')"),
            new PromptFixtureCase("Programmer_BuildsAWatchtowerAtTheCrosshair", BuiltInAgentRoleIds.Programmer,
                "Build a watchtower at the crosshair.", ProgrammerTools, new[] { "read_skill" }, false,
                "read only the sections the task needs"),
            new PromptFixtureCase("Programmer_MakesAHotbarHud", BuiltInAgentRoleIds.Programmer,
                "Make a HUD with a hotbar of five block slots.", ProgrammerTools, new[] { "read_skill" }, false,
                "It returns the core rules and a section list"),
            new PromptFixtureCase("AiNpc_News", BuiltInAgentRoleIds.AiNpc,
                "Greetings, traveller. Any news from the north?", MemoryOnly, Prose, true,
                "You are an in-world NPC voice"),
            new PromptFixtureCase("AiNpc_Horse", BuiltInAgentRoleIds.AiNpc,
                "Can you lend me a horse?", MemoryOnly, Prose, true, "You are an in-world NPC voice"),
            new PromptFixtureCase("CoreMechanic_Loot", BuiltInAgentRoleIds.CoreMechanic,
                "Roll loot for a level 3 wooden chest and return JSON.", MemoryOnly, Prose, true,
                "You are CoreMechanicAI"),
            new PromptFixtureCase("CoreMechanic_Compatibility", BuiltInAgentRoleIds.CoreMechanic,
                "Can a fire rune socket into an ice sword? Answer with JSON.", MemoryOnly, Prose, true,
                "You are CoreMechanicAI"),
            new PromptFixtureCase("PlainChat_Inventory", BuiltInAgentRoleIds.PlainChat,
                "How do I open my inventory?", Prose, Prose, true, "You are a simple in-game assistant"),
            new PromptFixtureCase("PlainChat_Joke", BuiltInAgentRoleIds.PlainChat,
                "Tell me a short joke.", Prose, Prose, true, "You are a simple in-game assistant"),
            new PromptFixtureCase("SmartChat_Remembers", BuiltInAgentRoleIds.SmartChat,
                "Remember that my favourite colour is green.", MemoryOnly, new[] { "memory" }, true,
                "You are an advanced in-game assistant"),
            new PromptFixtureCase("SmartChat_Arithmetic", BuiltInAgentRoleIds.SmartChat,
                "What is 12 times 7?", MemoryOnly, Prose, true, "You are an advanced in-game assistant"),
            new PromptFixtureCase("Merchant_Browses", BuiltInAgentRoleIds.Merchant,
                "What do you have for sale?", MerchantTools, new[] { "get_inventory" }, true,
                "You are a shopkeeper/merchant NPC"),
            new PromptFixtureCase("Merchant_Farewell", BuiltInAgentRoleIds.Merchant,
                "Farewell, merchant!", MerchantTools, Prose, true, "You are a shopkeeper/merchant NPC")
        };

        /// <summary>A policy composed like a shipped game with an inventory provider registered.</summary>
        internal static AgentMemoryPolicy ComposeShippedPolicy()
        {
            AgentMemoryPolicy policy = new();
            AgentMemoryPolicyGoldenSnapshotEditModeTests.ShippedComposition.Compose(policy);
            policy.AddToolToProfile(BuiltInToolProfileIds.Inventory, new InventoryLlmTool(new FixedInventory()));
            return policy;
        }

        /// <summary>Settings with the package universal prefix, as a fresh settings asset has it.</summary>
        internal static CoreAISettingsOptions PackageDefaultSettings()
        {
            return new CoreAISettingsOptions();
        }

        /// <summary>The two requests a role sends for <paramref name="fixture"/>: the input, then a follow-up turn.</summary>
        internal static async Task<(LlmCompletionRequest First, LlmCompletionRequest Second)> ComposeRequestsAsync(
            PromptFixtureCase fixture, CancellationToken cancellationToken)
        {
            AgentMemoryPolicy policy = ComposeShippedPolicy();
            AgentRoleTestHarness.CapturingLlmClient llm = new() { Reply = "Done." };
            AiOrchestrator orchestrator = AgentRoleTestHarness.BuildOrchestrator(llm, policy,
                new InMemoryAgentMemoryStore(), new BuiltInDefaultAgentSystemPromptProvider(), PackageDefaultSettings());
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = fixture.RoleId, Hint = fixture.Input },
                cancellationToken);
            await orchestrator.RunTaskAsync(new AiTaskRequest { RoleId = fixture.RoleId, Hint = "And then?" },
                cancellationToken);
            return (llm.Requests[0], llm.Requests[1]);
        }

        /// <summary>
        /// Sends a composed request to <paramref name="model"/> the way a provider receives it (system prompt, the
        /// request's history and tail messages, the user payload, the offered tools as functions) and returns the
        /// name of the first tool it calls, or null when it answers in prose.
        /// </summary>
        internal static async Task<string> FirstToolCallAsync(MEAI.IChatClient model, LlmCompletionRequest request,
            CancellationToken cancellationToken)
        {
            List<MEAI.ChatMessage> messages = new() { new MEAI.ChatMessage(MEAI.ChatRole.System, request.SystemPrompt) };
            if (request.ChatHistory != null)
            {
                messages.AddRange(request.ChatHistory);
            }

            messages.Add(new MEAI.ChatMessage(MEAI.ChatRole.User, request.UserPayload));
            MEAI.ChatOptions options = new()
            {
                Tools = ToFunctions(request.Tools, request.AgentRoleId).Cast<MEAI.AITool>().ToList(),
                ToolMode = MEAI.ChatToolMode.Auto
            };

            MEAI.ChatResponse response = await model.GetResponseAsync(messages, options, cancellationToken);
            foreach (MEAI.ChatMessage message in response.Messages)
            {
                foreach (MEAI.AIContent content in message.Contents)
                {
                    if (content is MEAI.FunctionCallContent call)
                    {
                        return call.Name;
                    }
                }
            }

            return null;
        }

        /// <summary>The functions a provider would be offered for these tools.</summary>
        internal static List<MEAI.AIFunction> ToFunctions(IReadOnlyList<ILlmTool> tools, string roleId)
        {
            List<MEAI.AIFunction> functions = new();
            foreach (ILlmTool tool in tools ?? Array.Empty<ILlmTool>())
            {
                switch (tool)
                {
                    case MemoryLlmTool _:
                        functions.Add(new MemoryTool(new InMemoryAgentMemoryStore(),
                            string.IsNullOrWhiteSpace(roleId) ? "fixture" : roleId).CreateAIFunction());
                        break;
                    case IAIFunctionsLlmTool many:
                        functions.AddRange(many.CreateAIFunctions());
                        break;
                    case IAIFunctionLlmTool one:
                        functions.Add(one.CreateAIFunction());
                        break;
                }
            }

            return functions;
        }

        private sealed class FixedInventory : InventoryTool.IInventoryProvider
        {
            public Task<List<InventoryTool.InventoryItem>> GetInventoryAsync(CancellationToken cancellationToken)
            {
                return Task.FromResult(new List<InventoryTool.InventoryItem>
                {
                    new() { Name = "Iron Sword", Type = "weapon", Quantity = 2, Price = 50 },
                    new() { Name = "Healing Potion", Type = "consumable", Quantity = 5, Price = 25 }
                });
            }
        }
    }
}
