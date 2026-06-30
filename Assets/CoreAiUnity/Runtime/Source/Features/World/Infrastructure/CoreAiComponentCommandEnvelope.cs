using System;
using UnityEngine;

namespace CoreAI.Infrastructure.World
{
    /// <summary>Serializable envelope for component commands emitted by AI tools.</summary>
    [Serializable]
    public sealed class CoreAiComponentCommandEnvelope
    {
        public string action = "";
        public string targetName = "";
        public string componentType = "";
        public string propertyName = "";
        public string stringValue = "";
        public float floatValue;
        public int boolValue;
        public float x;
        public float y;
        public float z;

        public static CoreAiComponentCommandEnvelope Add(string targetName, string type)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "add",
                targetName = targetName ?? "",
                componentType = type ?? ""
            };
        }

        public static CoreAiComponentCommandEnvelope Remove(string targetName, string type)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "remove",
                targetName = targetName ?? "",
                componentType = type ?? ""
            };
        }

        public static CoreAiComponentCommandEnvelope SetFloat(string targetName, string type, string propertyName,
            float value)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "set",
                targetName = targetName ?? "",
                componentType = type ?? "",
                propertyName = propertyName ?? "",
                floatValue = value
            };
        }

        public static CoreAiComponentCommandEnvelope SetBool(string targetName, string type, string propertyName,
            bool value)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "set",
                targetName = targetName ?? "",
                componentType = type ?? "",
                propertyName = propertyName ?? "",
                boolValue = value ? 1 : 0
            };
        }

        public static CoreAiComponentCommandEnvelope SetString(string targetName, string type, string propertyName,
            string value)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "set",
                targetName = targetName ?? "",
                componentType = type ?? "",
                propertyName = propertyName ?? "",
                stringValue = value ?? ""
            };
        }

        public static CoreAiComponentCommandEnvelope SetVector(string targetName, string type, string propertyName,
            Vector3 value)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "set",
                targetName = targetName ?? "",
                componentType = type ?? "",
                propertyName = propertyName ?? "",
                x = value.x,
                y = value.y,
                z = value.z
            };
        }

        public static CoreAiComponentCommandEnvelope SetColor(string targetName, string type, string propertyName,
            string htmlColor)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "set",
                targetName = targetName ?? "",
                componentType = type ?? "",
                propertyName = propertyName ?? "",
                stringValue = htmlColor ?? ""
            };
        }

        public static CoreAiComponentCommandEnvelope ListComponents(string targetName)
        {
            return new CoreAiComponentCommandEnvelope
            {
                action = "list_components",
                targetName = targetName ?? ""
            };
        }
    }
}
