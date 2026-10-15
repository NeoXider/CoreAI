using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace CoreAI.Benchmarking
{
    /// <summary>Reads benchmark evidence without converting timestamp strings or rewriting source object text.</summary>
    public static class BenchmarkJson
    {
        public static JObject ParseObject(string json)
        {
            using StringReader source = new(json);
            using JsonTextReader reader = new(source) { DateParseHandling = DateParseHandling.None };
            return JObject.Load(reader);
        }

        public static JToken ParseToken(string json)
        {
            using StringReader source = new(json);
            using JsonTextReader reader = new(source) { DateParseHandling = DateParseHandling.None };
            return JToken.Load(reader);
        }

        /// <summary>Returns exact source slices for objects in a named root array, preserving numeric and string spelling.</summary>
        public static IReadOnlyList<string> RawArrayObjects(string json, string propertyName)
        {
            List<int> starts = new() { 0 };
            for (int i = 0; i < json.Length; i++)
                if (json[i] == '\n' || json[i] == '\r' && (i + 1 == json.Length || json[i + 1] != '\n')) starts.Add(i + 1);
            List<string> objects = new();
            using StringReader source = new(json);
            using JsonTextReader reader = new(source) { DateParseHandling = DateParseHandling.None };
            while (reader.Read())
            {
                if (reader.TokenType != JsonToken.PropertyName || reader.Depth != 1 || (string)reader.Value != propertyName) continue;
                if (!reader.Read() || reader.TokenType != JsonToken.StartArray) throw new InvalidDataException($"Expected benchmark array '{propertyName}'.");
                while (reader.Read() && reader.TokenType != JsonToken.EndArray)
                {
                    if (reader.TokenType == JsonToken.Comment) continue;
                    if (reader.TokenType != JsonToken.StartObject) throw new InvalidDataException($"Expected objects in benchmark array '{propertyName}'.");
                    int start = starts[reader.LineNumber - 1] + reader.LinePosition - 1;
                    reader.Skip();
                    int end = starts[reader.LineNumber - 1] + reader.LinePosition;
                    objects.Add(json.Substring(start, end - start));
                }
                return objects;
            }
            throw new InvalidDataException($"Missing benchmark array '{propertyName}'.");
        }
    }
}
