using System;
using CoreAI.Ai;
using CoreAI.Infrastructure.World;
using Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// ILlmTool реализация для WorldTool — позволяет LLM вызывать world commands.
    /// 
    /// Примечание: этот класс находится в CoreAiUnity потому что зависит от Unity и WorldCommand.
    /// Для других движков нужно создать аналогичный инструмент.
    /// </summary>
    public sealed class WorldLlmTool : LlmToolBase
    {
        private readonly ICoreAiWorldCommandExecutor _executor;

        public WorldLlmTool(ICoreAiWorldCommandExecutor executor)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        }

        public override string Name => "world_command";

        public override string Description =>
            "Execute world commands to manipulate the game world. " +
            "Actions: spawn, move, destroy, load_scene, reload_scene, bind_by_name, " +
            "set_active, show_text, apply_force, spawn_particles, list_objects. " +
            "Use 'spawn' to create objects, 'move' to reposition, 'destroy' to remove, " +
            "'load_scene' to change levels, 'list_objects' to get hierarchy (search by name), " +
            "'show_text' to display notifications. " +
            "Objects can be targeted by 'instanceId' or 'targetName'.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "Command: spawn, move, destroy, load_scene, reload_scene, bind_by_name, set_active, show_text, apply_force, spawn_particles, list_objects"),
            ("instanceId", "string", false, "Instance ID of the target object"),
            ("targetName", "string", false, "Object name to target (alternative to instanceId, works with move, destroy, set_active, etc.)"),
            ("x", "number", false, "X coordinate (for spawn, move, apply_force)"),
            ("y", "number", false, "Y coordinate (for spawn, move, apply_force)"),
            ("z", "number", false, "Z coordinate (for spawn, move, apply_force)"),
            ("prefabKey", "string", false, "Prefab key for spawn command"),
            ("stringValue", "string", false, "String value: text for show_text, or search pattern for list_objects"),
            ("volume", "number", false, "Reserved for future use")
        );

        public AIFunction CreateAIFunction()
        {
            var worldTool = new WorldTool(_executor);
            return worldTool.CreateAIFunction();
        }
    }
}
