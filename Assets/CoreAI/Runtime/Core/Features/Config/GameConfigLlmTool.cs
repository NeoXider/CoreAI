using System;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using Microsoft.Extensions.AI;

namespace CoreAI.Config
{
    /// <summary>
    /// Обёртка GameConfigTool для ILlmTool интерфейса.
    /// </summary>
    public sealed class GameConfigLlmTool : ILlmTool
    {
        private readonly GameConfigTool _tool;

        public GameConfigLlmTool(IGameConfigStore store, GameConfigPolicy policy, string roleId)
        {
            _tool = new GameConfigTool(store, policy, roleId);
        }

        /// <inheritdoc />
        public string Name => "game_config";

        public bool AllowDuplicates => false;

        /// <inheritdoc />
        public string Description =>
            "Read or modify game configuration. Use 'read' to get current config as JSON, or 'update' with modified JSON to apply changes.";

        /// <inheritdoc />
        public string ParametersSchema =>
            "{" +
            "  \"type\": \"object\"," +
            "  \"properties\": {" +
            "    \"action\": { \"type\": \"string\", \"enum\": [\"read\", \"update\"], \"description\": \"Action to perform.\" }," +
            "    \"content\": { \"type\": \"string\", \"description\": \"For 'update': modified JSON config. For 'read': ignored.\" }" +
            "  }," +
            "  \"required\": [\"action\"]" +
            "}";

        /// <summary>
        /// Создаёт AIFunction для MEAI function calling.
        /// </summary>
        public AIFunction CreateAIFunction()
        {
            return _tool.CreateAIFunction();
        }
    }
}