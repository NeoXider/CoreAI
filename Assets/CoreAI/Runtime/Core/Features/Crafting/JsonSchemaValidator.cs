using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CoreAI.Crafting
{
    /// <summary>
    /// Легковесный валидатор JSON-ответов от LLM.
    /// Проверяет обязательные поля, типы, числовые диапазоны и enum-значения.
    /// Не требует внешних зависимостей (использует Newtonsoft.Json.Linq).
    /// 
    /// Пример использования для CoreMechanicAI:
    /// <code>
    /// var schema = new JsonSchemaValidator("CraftResult");
    /// schema.AddField("itemName", "string", required: true);
    /// schema.AddField("quality", "number", required: true, min: 0, max: 100);
    /// schema.AddField("rarity", "string", required: true, allowedValues: new[]{"common","rare","epic","legendary"});
    /// var result = schema.Validate(llmResponseJson);
    /// </code>
    /// </summary>
    public sealed class JsonSchemaValidator
    {
        private readonly string _schemaName;
        private readonly List<JsonFieldSchema> _fields = new();

        /// <summary>
        /// Создаёт валидатор с заданным именем схемы (для отчётов об ошибках).
        /// </summary>
        public JsonSchemaValidator(string schemaName)
        {
            _schemaName = schemaName ?? throw new ArgumentNullException(nameof(schemaName));
        }

        /// <summary>
        /// Добавляет определение поля в схему.
        /// </summary>
        public JsonSchemaValidator AddField(JsonFieldSchema field)
        {
            _fields.Add(field ?? throw new ArgumentNullException(nameof(field)));
            return this;
        }

        /// <summary>
        /// Добавляет определение поля (shortcut).
        /// </summary>
        public JsonSchemaValidator AddField(
            string name,
            string type,
            bool required = false,
            double? min = null,
            double? max = null,
            string[] allowedValues = null,
            string description = null)
        {
            _fields.Add(new JsonFieldSchema
            {
                Name = name,
                Type = type,
                Required = required,
                Min = min,
                Max = max,
                AllowedValues = allowedValues,
                Description = description
            });
            return this;
        }

        /// <summary>
        /// Валидирует JSON-строку против схемы.
        /// </summary>
        public JsonValidationResult Validate(string json)
        {
            JsonValidationResult result = new();

            if (string.IsNullOrWhiteSpace(json))
            {
                result.Errors.Add($"[{_schemaName}] Empty or null JSON response");
                return result;
            }

            // Trim potential markdown fences
            json = json.Trim();
            if (json.StartsWith("```"))
            {
                int firstNewline = json.IndexOf('\n');
                if (firstNewline >= 0) json = json.Substring(firstNewline + 1);
                if (json.EndsWith("```")) json = json.Substring(0, json.Length - 3);
                json = json.Trim();
            }

            JObject obj;
            try
            {
                obj = JObject.Parse(json);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"[{_schemaName}] Invalid JSON: {ex.Message}");
                return result;
            }

            result.ParsedObject = obj;

            foreach (JsonFieldSchema field in _fields)
            {
                JToken token = obj[field.Name];

                // Required check
                if (field.Required && (token == null || token.Type == JTokenType.Null))
                {
                    result.Errors.Add($"[{_schemaName}] Missing required field '{field.Name}'");
                    continue;
                }

                if (token == null || token.Type == JTokenType.Null) continue;

                // Type check
                if (!CheckType(token, field.Type, out string typeError))
                {
                    result.Errors.Add($"[{_schemaName}] Field '{field.Name}': {typeError}");
                    continue;
                }

                // Range check (number/integer)
                if ((field.Type == "number" || field.Type == "integer") &&
                    token.Type != JTokenType.Null)
                {
                    double val = token.Value<double>();

                    if (field.Min.HasValue && val < field.Min.Value)
                    {
                        result.Errors.Add(
                            $"[{_schemaName}] Field '{field.Name}': value {val} is below minimum {field.Min.Value}");
                    }

                    if (field.Max.HasValue && val > field.Max.Value)
                    {
                        result.Errors.Add(
                            $"[{_schemaName}] Field '{field.Name}': value {val} exceeds maximum {field.Max.Value}");
                    }
                }

                // Enum check
                if (field.AllowedValues != null && field.AllowedValues.Length > 0 &&
                    token.Type == JTokenType.String)
                {
                    string val = token.Value<string>();
                    bool found = false;
                    foreach (string allowed in field.AllowedValues)
                    {
                        if (string.Equals(val, allowed, StringComparison.OrdinalIgnoreCase))
                        {
                            found = true;
                            break;
                        }
                    }

                    if (!found)
                    {
                        result.Errors.Add(
                            $"[{_schemaName}] Field '{field.Name}': value '{val}' is not in allowed values [{string.Join(", ", field.AllowedValues)}]");
                    }
                }
            }

            result.IsValid = result.Errors.Count == 0;
            return result;
        }

        /// <summary>
        /// Генерирует описание схемы как строку для вставки в system prompt.
        /// </summary>
        public string ToPromptDescription()
        {
            List<string> lines = new();
            lines.Add($"JSON schema '{_schemaName}':");
            lines.Add("{");
            foreach (JsonFieldSchema f in _fields)
            {
                string req = f.Required ? " (REQUIRED)" : "";
                string range = "";
                if (f.Min.HasValue || f.Max.HasValue)
                {
                    range = $" range:[{f.Min?.ToString() ?? "..."},{f.Max?.ToString() ?? "..."}]";
                }
                string allowed = f.AllowedValues != null ? $" values:[{string.Join(",", f.AllowedValues)}]" : "";
                string desc = !string.IsNullOrEmpty(f.Description) ? $" — {f.Description}" : "";
                lines.Add($"  \"{f.Name}\": {f.Type}{req}{range}{allowed}{desc}");
            }
            lines.Add("}");
            return string.Join("\n", lines);
        }

        /// <summary>Количество полей в схеме.</summary>
        public int FieldCount => _fields.Count;

        /// <summary>Имя схемы.</summary>
        public string SchemaName => _schemaName;

        private static bool CheckType(JToken token, string expectedType, out string error)
        {
            error = null;
            switch (expectedType?.ToLowerInvariant())
            {
                case "string":
                    if (token.Type != JTokenType.String)
                    {
                        error = $"expected string but got {token.Type}";
                        return false;
                    }
                    return true;

                case "number":
                    if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
                    {
                        error = $"expected number but got {token.Type}";
                        return false;
                    }
                    return true;

                case "integer":
                    if (token.Type != JTokenType.Integer)
                    {
                        // Allow float that is whole number
                        if (token.Type == JTokenType.Float)
                        {
                            double d = token.Value<double>();
                            if (Math.Abs(d - Math.Floor(d)) > 0.0001)
                            {
                                error = $"expected integer but got float {d}";
                                return false;
                            }
                            return true;
                        }
                        error = $"expected integer but got {token.Type}";
                        return false;
                    }
                    return true;

                case "boolean":
                    if (token.Type != JTokenType.Boolean)
                    {
                        error = $"expected boolean but got {token.Type}";
                        return false;
                    }
                    return true;

                case "array":
                    if (token.Type != JTokenType.Array)
                    {
                        error = $"expected array but got {token.Type}";
                        return false;
                    }
                    return true;

                case "object":
                    if (token.Type != JTokenType.Object)
                    {
                        error = $"expected object but got {token.Type}";
                        return false;
                    }
                    return true;

                default:
                    return true; // unknown type = pass
            }
        }
    }
}
