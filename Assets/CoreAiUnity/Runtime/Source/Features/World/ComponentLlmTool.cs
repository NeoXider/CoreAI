using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using Cysharp.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// LLM tool that converts model requests into component commands.
    /// </summary>
    public sealed class ComponentLlmTool : LlmToolBase, IAIFunctionLlmTool
    {
        private readonly ICoreAiComponentCommandExecutor _executor;
        private readonly ICoreAISettings _settings;
        private readonly IGameLogger _logger;

        public ComponentLlmTool(ICoreAiComponentCommandExecutor executor, ICoreAISettings settings,
            IGameLogger logger)
        {
            _executor = executor ?? throw new ArgumentNullException(nameof(executor));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override string Name => "component_command";
        public override bool AllowDuplicates => false;

        public override string Description =>
            "Execute component commands to add, remove, configure, or list Unity components on existing GameObjects. " +
            "Actions: add, remove, set, list_components. " +
            "componentType must be one of: " + CoreAiComponentCatalog.SupportedTypes + ". " +
            "Use 'add' to add a supported component if missing, 'remove' to remove a supported component, " +
            "'set' to configure a supported property, and 'list_components' to list component type names on targetName. " +
            "For 'set', pass propertyName and the matching value field: floatValue for numbers, " +
            "boolValue 0 or 1 for booleans, stringValue for text, colors, and enums, and x/y/z for vectors.";

        public override string ParametersSchema => JsonParams(
            ("action", "string", true, "Command: add, remove, set, list_components"),
            ("targetName", "string", true, "Existing GameObject name to target"),
            ("componentType", "string", false,
                "Component type. Supported: " + CoreAiComponentCatalog.SupportedTypes),
            ("propertyName", "string", false, "Property to set for the set action"),
            ("stringValue", "string", false, "String value for text, HTML color, or enum properties"),
            ("floatValue", "number", false, "Numeric value for float or integer-like component properties"),
            ("boolValue", "number", false, "Boolean value encoded as 0 or 1"),
            ("x", "number", false, "Vector X value"),
            ("y", "number", false, "Vector Y value"),
            ("z", "number", false, "Vector Z value")
        );

        public AIFunction CreateAIFunction()
        {
            Func<string, string, string?, string?, string?, float, int, float, float, float, CancellationToken,
                Task<string>> func = ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = Name,
                Description = Description
            };
            return AIFunctionFactory.Create(func, options);
        }

        public async Task<string> ExecuteAsync(
            [Description("Command: add, remove, set, list_components")]
            string action,
            [Description("Existing GameObject name to target")]
            string targetName,
            [Description(
                "Component type. Supported: " + CoreAiComponentCatalog.SupportedTypes)]
            string? componentType = null,
            [Description("Property to set for the set action")]
            string? propertyName = null,
            [Description("String value for text, HTML color, or enum properties")]
            string? stringValue = null,
            [Description("Numeric value for float or integer-like component properties")]
            float floatValue = 0f,
            [Description("Boolean value encoded as 0 or 1")]
            int boolValue = 0,
            [Description("Vector X value")]
            float x = 0f,
            [Description("Vector Y value")]
            float y = 0f,
            [Description("Vector Z value")]
            float z = 0f,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(action))
            {
                return SerializeResult(false,
                    $"Action is required. Valid actions: {ValidActionsText}");
            }

            if (string.IsNullOrEmpty(targetName))
            {
                return SerializeResult(false, "targetName is required.", action);
            }

            if (_settings.LogToolCalls)
            {
                _logger.LogInfo(GameLogFeature.MessagePipe, $"[Tool Call] component_command: action={action}");
            }

            if (_settings.LogToolCallArguments)
            {
                StringBuilder args = new();
                args.Append($" targetName={targetName}");

                if (!string.IsNullOrEmpty(componentType))
                {
                    args.Append($" componentType={componentType}");
                }

                if (!string.IsNullOrEmpty(propertyName))
                {
                    args.Append($" propertyName={propertyName}");
                }

                if (!string.IsNullOrEmpty(stringValue))
                {
                    args.Append($" stringValue={stringValue}");
                }

                if (floatValue != 0f)
                {
                    args.Append($" floatValue={floatValue}");
                }

                if (boolValue != 0)
                {
                    args.Append($" boolValue={boolValue}");
                }

                if (x != 0f || y != 0f || z != 0f)
                {
                    args.Append($" vector=({x},{y},{z})");
                }

                _logger.LogInfo(GameLogFeature.MessagePipe, $"  args:{args}");
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                CoreAiComponentCommandEnvelope envelope = action switch
                {
                    "add" => CreateAddCommand(targetName, componentType),
                    "remove" => CreateRemoveCommand(targetName, componentType),
                    "set" => CreateSetCommand(targetName, componentType, propertyName, stringValue, floatValue,
                        boolValue, x, y, z),
                    "list_components" => CoreAiComponentCommandEnvelope.ListComponents(targetName),
                    _ => null
                };

                if (envelope == null)
                {
                    if (!IsKnownComponentAction(action))
                    {
                        throw new ArgumentException(
                            $"Unknown action: '{action}'. Valid actions: {ValidActionsText}");
                    }

                    return SerializeResult(false, MissingRequiredParametersMessage(action), action);
                }

                string json = Newtonsoft.Json.JsonConvert.SerializeObject(envelope);
                cancellationToken.ThrowIfCancellationRequested();
                await UniTask.SwitchToMainThread(cancellationToken);
                bool success = _executor.TryExecute(new ApplyAiGameCommand
                {
                    CommandTypeId = AiGameCommandTypeIds.ComponentCommand,
                    JsonPayload = json
                });

                if (_settings.LogToolCallResults)
                {
                    _logger.LogInfo(GameLogFeature.MessagePipe,
                        $"[Tool Call] component_command: {(success ? "SUCCESS" : "FAILED")} - {action}");
                }

                if (success && action == "list_components")
                {
                    List<string> components = _executor.LastListedComponents ?? new List<string>();
                    return SerializeResult(true,
                        $"Found {components.Count} components: {string.Join(", ", components)}", action);
                }

                return SerializeResult(success,
                    success
                        ? $"Component command '{action}' executed successfully"
                        : $"Failed to execute component command '{action}'",
                    action);
            }
            catch (Exception ex)
            {
                if (_settings.LogToolCallResults)
                {
                    _logger.LogError(GameLogFeature.MessagePipe,
                        $"[Tool Call] component_command: FAILED - {ex.Message}");
                }

                return SerializeResult(false, $"Component command failed: {ex.Message}", action);
            }
        }

        private static CoreAiComponentCommandEnvelope CreateAddCommand(string targetName, string? componentType)
        {
            if (string.IsNullOrEmpty(componentType))
            {
                return null;
            }

            return CoreAiComponentCommandEnvelope.Add(targetName, componentType);
        }

        private static CoreAiComponentCommandEnvelope CreateRemoveCommand(string targetName, string? componentType)
        {
            if (string.IsNullOrEmpty(componentType))
            {
                return null;
            }

            return CoreAiComponentCommandEnvelope.Remove(targetName, componentType);
        }

        private static CoreAiComponentCommandEnvelope CreateSetCommand(
            string targetName,
            string? componentType,
            string? propertyName,
            string? stringValue,
            float floatValue,
            int boolValue,
            float x,
            float y,
            float z)
        {
            if (string.IsNullOrEmpty(componentType) || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            return new CoreAiComponentCommandEnvelope
            {
                action = "set",
                targetName = targetName ?? "",
                componentType = componentType ?? "",
                propertyName = propertyName ?? "",
                stringValue = stringValue ?? "",
                floatValue = floatValue,
                boolValue = boolValue,
                x = x,
                y = y,
                z = z
            };
        }

        private const string ValidActionsText = "add, remove, set, list_components";

        private static bool IsKnownComponentAction(string action)
        {
            return action switch
            {
                "add" or "remove" or "set" or "list_components" => true,
                _ => false
            };
        }

        private static string MissingRequiredParametersMessage(string action)
        {
            return action switch
            {
                "add" => "Missing required parameters for action 'add': targetName and componentType are required.",
                "remove" =>
                    "Missing required parameters for action 'remove': targetName and componentType are required.",
                "set" =>
                    "Missing required parameters for action 'set': targetName, componentType, and propertyName are required.",
                "list_components" =>
                    "Missing required parameters for action 'list_components': targetName is required.",
                _ => $"Missing required parameters for action '{action}'."
            };
        }

        private static string SerializeResult(bool success, string message, string? action = null)
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(new ComponentResult
            {
                Success = success,
                Message = message,
                Action = action ?? ""
            });
        }

        /// <summary>
        /// Component Result used by CoreAI.
        /// </summary>
        public sealed class ComponentResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Action { get; set; }
        }
    }
}
