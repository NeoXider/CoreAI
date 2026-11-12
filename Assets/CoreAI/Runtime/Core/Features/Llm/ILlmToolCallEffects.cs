using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;

namespace CoreAI.Ai
{
    /// <summary>
    /// Optional per-call answer to "can this call change state that other calls read?", for a tool whose calls
    /// differ: <c>manage_mods get_source</c> only reads a mod while <c>manage_mods edit</c> changes it.
    /// <para>
    /// The execution policy uses it for echo suppression inside one request. A repeat of a call that already
    /// succeeded is answered "use its earlier result" instead of running again; that answer is wrong for a read
    /// once state has changed since. So after a successful call that changed state, the earlier results of reading
    /// calls stop counting as "the same call" and an identical read runs again. A call that changed state keeps its
    /// own echo protection for the whole request: an echoed mutation is never replayed.
    /// </para>
    /// <para>
    /// A tool without this interface changes state when it is mutating (<see cref="ILlmTool.IsMutating"/>, or one
    /// of the built-in mutating names the policy knows) and only reads otherwise.
    /// </para>
    /// </summary>
    public interface ILlmToolCallEffects
    {
        /// <summary>
        /// True when the call can change state; false when it only reads. <paramref name="functionName"/> is the
        /// function the model called (a tool that publishes several functions tells them apart by it);
        /// <paramref name="arguments"/> are the call's arguments as the model sent them (values may be strings,
        /// <see cref="System.Text.Json.JsonElement"/> or <see cref="JToken"/>; read them with
        /// <see cref="LlmToolCallArguments"/>). When unsure, answer true.
        /// </summary>
        bool ChangesState(string functionName, IDictionary<string, object> arguments);
    }

    /// <summary>Reads call arguments whatever form the transport delivered them in.</summary>
    public static class LlmToolCallArguments
    {
        /// <summary>
        /// The argument <paramref name="name"/> as text, or <c>""</c> when it is absent or null. A JSON string is
        /// unwrapped; any other JSON value is its raw text.
        /// </summary>
        public static string GetString(IDictionary<string, object> arguments, string name)
        {
            if (arguments == null || string.IsNullOrEmpty(name) ||
                !arguments.TryGetValue(name, out object value) || value == null)
            {
                return "";
            }

            switch (value)
            {
                case string text:
                    return text;
                case System.Text.Json.JsonElement element:
                    return element.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.String => element.GetString() ?? "",
                        System.Text.Json.JsonValueKind.Null => "",
                        System.Text.Json.JsonValueKind.Undefined => "",
                        _ => element.GetRawText()
                    };
                case JValue jsonValue:
                    return jsonValue.Value == null ? "" : Convert.ToString(jsonValue.Value,
                        System.Globalization.CultureInfo.InvariantCulture) ?? "";
                case JToken token:
                    return token.ToString(Newtonsoft.Json.Formatting.None);
                default:
                    return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "";
            }
        }

        /// <summary>
        /// The argument <paramref name="name"/> trimmed and in lower case, the form an <c>action</c> argument is
        /// compared in.
        /// </summary>
        public static string GetAction(IDictionary<string, object> arguments, string name = "action")
        {
            return GetString(arguments, name).Trim().ToLowerInvariant();
        }
    }
}
