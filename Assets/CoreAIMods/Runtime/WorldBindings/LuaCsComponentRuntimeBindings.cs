using System;
using CoreAI.Infrastructure.World;
using CoreAI.Messaging;
using CoreAI.Sandbox.LuaCs;
using UnityEngine;
using static CoreAI.Messaging.AiGameCommandTypeIds;

namespace CoreAI.Ai.LuaCs
{
    /// <summary>
    /// Lua-CSharp counterpart of <see cref="CoreAI.Infrastructure.World.CoreAiComponentLuaRuntimeBindings"/>.
    /// </summary>
    public sealed class LuaCsComponentRuntimeBindings
    {
        private readonly IAiGameCommandSink _sink;

        public LuaCsComponentRuntimeBindings(IAiGameCommandSink sink)
        {
            _sink = sink;
        }

        public void Register(LuaCsApiRegistry registry, LuaCapabilities capabilities)
        {
            if ((capabilities & LuaCapabilities.WorldEdit) == 0)
            {
                return;
            }

            RegisterGameplayApis(registry);
        }

        public void RegisterGameplayApis(LuaCsApiRegistry registry)
        {
            registry.Register("coreai_component_add", new Action<string, string>((targetName, componentType) =>
            {
                if (!TryNormalizeTargetAndType(targetName, componentType, out string name, out string type))
                {
                    return;
                }

                Publish(CoreAiComponentCommandEnvelope.Add(name, type));
            }));

            registry.Register("coreai_component_remove", new Action<string, string>((targetName, componentType) =>
            {
                if (!TryNormalizeTargetAndType(targetName, componentType, out string name, out string type))
                {
                    return;
                }

                Publish(CoreAiComponentCommandEnvelope.Remove(name, type));
            }));

            registry.Register("coreai_component_set_number",
                new Action<string, string, string, double>((targetName, componentType, propertyName, value) =>
                {
                    if (!TryNormalizeTargetTypeAndProperty(
                            targetName,
                            componentType,
                            propertyName,
                            out string name,
                            out string type,
                            out string property))
                    {
                        return;
                    }

                    Publish(CoreAiComponentCommandEnvelope.SetFloat(name, type, property, ValidateFiniteFloat(value)));
                }));

            registry.Register("coreai_component_set_bool",
                new Action<string, string, string, bool>((targetName, componentType, propertyName, value) =>
                {
                    if (!TryNormalizeTargetTypeAndProperty(
                            targetName,
                            componentType,
                            propertyName,
                            out string name,
                            out string type,
                            out string property))
                    {
                        return;
                    }

                    Publish(CoreAiComponentCommandEnvelope.SetBool(name, type, property, value));
                }));

            registry.Register("coreai_component_set_text",
                new Action<string, string, string, string>((targetName, componentType, propertyName, value) =>
                {
                    if (!TryNormalizeTargetTypeAndProperty(
                            targetName,
                            componentType,
                            propertyName,
                            out string name,
                            out string type,
                            out string property))
                    {
                        return;
                    }

                    Publish(CoreAiComponentCommandEnvelope.SetString(name, type, property, value ?? ""));
                }));

            registry.Register("coreai_component_set_vector",
                new Action<string, string, string, double, double, double>((targetName, componentType, propertyName, x,
                    y, z) =>
                {
                    if (!TryNormalizeTargetTypeAndProperty(
                            targetName,
                            componentType,
                            propertyName,
                            out string name,
                            out string type,
                            out string property))
                    {
                        return;
                    }

                    Publish(CoreAiComponentCommandEnvelope.SetVector(
                        name,
                        type,
                        property,
                        new Vector3(
                            ValidateFiniteFloat(x),
                            ValidateFiniteFloat(y),
                            ValidateFiniteFloat(z))));
                }));
        }

        private static bool TryNormalizeTargetAndType(
            string targetName,
            string componentType,
            out string name,
            out string type)
        {
            name = (targetName ?? "").Trim();
            type = (componentType ?? "").Trim();
            return !string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(type);
        }

        private static bool TryNormalizeTargetTypeAndProperty(
            string targetName,
            string componentType,
            string propertyName,
            out string name,
            out string type,
            out string property)
        {
            if (!TryNormalizeTargetAndType(targetName, componentType, out name, out type))
            {
                property = "";
                return false;
            }

            property = (propertyName ?? "").Trim();
            return !string.IsNullOrEmpty(property);
        }

        private static float ValidateFiniteFloat(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentException("component numeric value must be finite.");
            }

            return (float)value;
        }

        private void Publish(CoreAiComponentCommandEnvelope env)
        {
            if (env == null)
            {
                return;
            }

            string json = JsonUtility.ToJson(env, false);
            ApplyAiGameCommand command = new()
            {
                CommandTypeId = ComponentCommand,
                JsonPayload = json,
                SourceRoleId = BuiltInAgentRoleIds.Programmer,
                SourceTaskHint = "component_command",
                SourceTag = "lua:component_command"
            };

            if (_sink == null)
            {
                return;
            }

            _sink.Publish(command);
        }
    }
}
