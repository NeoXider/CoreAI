using System;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Look
{
    /// <summary>
    /// The versioned JSON contract of stored graphics options. An absent key means "not overridden", so
    /// a file written today stays valid when a later version adds an option, and an unknown preset or
    /// option name (a value a newer version wrote) reads as absent: an unknown preset becomes the
    /// platform default. A malformed document (not an object, an unsupported schema version, a value of
    /// the wrong type) is reported as an error and the caller falls back to
    /// <see cref="GraphicsOptions.Default"/>.
    /// </summary>
    public static class GraphicsOptionsJson
    {
        /// <summary>The schema version this reader and writer understand.</summary>
        public const int CurrentSchemaVersion = 1;

        /// <summary>JSON key of the schema version.</summary>
        public const string SchemaVersionKey = "schemaVersion";

        private const string PresetKey = "preset";
        private const string PostProcessingKey = "postProcessing";
        private const string ShadowsKey = "shadows";
        private const string AntiAliasingKey = "antiAliasing";
        private const string RenderScaleKey = "renderScale";
        private const string FogKey = "fog";
        private const string SkyKey = "sky";

        /// <summary>Writes <paramref name="options"/> as an indented JSON document.</summary>
        public static string Serialize(GraphicsOptions options)
        {
            GraphicsOptions source = options ?? GraphicsOptions.Default;
            JObject root = new() { [SchemaVersionKey] = CurrentSchemaVersion };
            if (source.Preset.HasValue)
            {
                root[PresetKey] = source.Preset.Value.ToString();
            }

            if (source.PostProcessing.HasValue)
            {
                root[PostProcessingKey] = source.PostProcessing.Value;
            }

            if (source.Shadows.HasValue)
            {
                root[ShadowsKey] = source.Shadows.Value.ToString();
            }

            if (source.AntiAliasing.HasValue)
            {
                root[AntiAliasingKey] = source.AntiAliasing.Value.ToString();
            }

            if (source.RenderScale.HasValue)
            {
                root[RenderScaleKey] = Math.Round(source.RenderScale.Value, 3);
            }

            if (source.Fog.HasValue)
            {
                root[FogKey] = source.Fog.Value;
            }

            if (source.Sky.HasValue)
            {
                root[SkyKey] = source.Sky.Value;
            }

            return root.ToString(Formatting.Indented);
        }

        /// <summary>
        /// Parses a stored document. Returns false with a one-line <paramref name="error"/> when the text
        /// is not a JSON object of a supported schema version or a value has the wrong type; then
        /// <paramref name="options"/> is <see cref="GraphicsOptions.Default"/>.
        /// </summary>
        public static bool TryDeserialize(string json, out GraphicsOptions options, out string error)
        {
            options = GraphicsOptions.Default;
            error = null;
            if (string.IsNullOrWhiteSpace(json))
            {
                error = "the file is empty";
                return false;
            }

            JObject root;
            try
            {
                JToken token = JToken.Parse(json);
                root = token as JObject;
                if (root == null)
                {
                    error = "the document is not a JSON object";
                    return false;
                }
            }
            catch (JsonException ex)
            {
                error = "the document is not valid JSON (" + ex.Message + ")";
                return false;
            }

            JToken version = root[SchemaVersionKey];
            if (version == null || version.Type != JTokenType.Integer)
            {
                error = "'" + SchemaVersionKey + "' is missing or not an integer";
                return false;
            }

            if (version.Value<long>() != CurrentSchemaVersion)
            {
                error = "schema version " + version.Value<long>().ToString(CultureInfo.InvariantCulture) +
                        " is not supported (expected " +
                        CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture) + ")";
                return false;
            }

            if (!TryReadEnum(root, PresetKey, out GraphicsQualityPreset? preset, ref error) ||
                !TryReadBool(root, PostProcessingKey, out bool? postProcessing, ref error) ||
                !TryReadEnum(root, ShadowsKey, out GraphicsShadowQuality? shadows, ref error) ||
                !TryReadEnum(root, AntiAliasingKey, out GraphicsAntiAliasing? antiAliasing, ref error) ||
                !TryReadFloat(root, RenderScaleKey, out float? renderScale, ref error) ||
                !TryReadBool(root, FogKey, out bool? fog, ref error) ||
                !TryReadBool(root, SkyKey, out bool? sky, ref error))
            {
                return false;
            }

            options = new GraphicsOptions(preset, postProcessing, shadows, antiAliasing, renderScale, fog, sky);
            return true;
        }

        private static bool TryReadBool(JObject root, string key, out bool? value, ref string error)
        {
            value = null;
            JToken token = root[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Boolean)
            {
                error = "'" + key + "' must be true or false";
                return false;
            }

            value = token.Value<bool>();
            return true;
        }

        private static bool TryReadFloat(JObject root, string key, out float? value, ref string error)
        {
            value = null;
            JToken token = root[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.Float && token.Type != JTokenType.Integer)
            {
                error = "'" + key + "' must be a number";
                return false;
            }

            double number = token.Value<double>();
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                error = "'" + key + "' must be a finite number";
                return false;
            }

            value = (float)number;
            return true;
        }

        private static bool TryReadEnum<TEnum>(JObject root, string key, out TEnum? value, ref string error)
            where TEnum : struct
        {
            value = null;
            JToken token = root[key];
            if (token == null || token.Type == JTokenType.Null)
            {
                return true;
            }

            if (token.Type != JTokenType.String)
            {
                error = "'" + key + "' must be a name";
                return false;
            }

            string name = token.Value<string>();
            foreach (TEnum candidate in (TEnum[])Enum.GetValues(typeof(TEnum)))
            {
                if (string.Equals(candidate.ToString(), name, StringComparison.Ordinal))
                {
                    value = candidate;
                    return true;
                }
            }

            return true;
        }
    }
}
