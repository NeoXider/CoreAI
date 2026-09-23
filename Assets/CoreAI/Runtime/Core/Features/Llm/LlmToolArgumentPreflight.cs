using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// The structural argument preflight shared by the direct tool path (<c>ToolExecutionPolicy</c>) and
    /// the skill proxy (<c>call_skill_tool</c>): the SAME coercion MEAI's binder performs, run standalone
    /// before any invocation, so a failure here is a proof that the tool body was never entered.
    /// </summary>
    internal static class LlmToolArgumentPreflight
    {
        /// <summary>
        /// Proves an argument-binding failure STRUCTURALLY: round-trips each declared parameter's raw
        /// value through the SAME <see cref="JsonSerializerOptions"/> MEAI itself binds
        /// with (<see cref="AIFunction.JsonSerializerOptions"/>), reflecting the target CLR type from
        /// <see cref="AIFunction.UnderlyingMethod"/> — entirely BEFORE <c>function.InvokeAsync</c> is
        /// called, so a failure here can never have entered the tool body.
        /// <para>
        /// WHY not classify by the invocation exception's type/message instead: a tool body can legitimately
        /// throw <see cref="ArgumentException"/> (or any exception whose text happens to contain "convert")
        /// AFTER already mutating state, and that is indistinguishable from a true binding failure once it
        /// has already crossed the invocation boundary. Running the SAME coercion the binder performs,
        /// standalone, first, is the only way to prove "never entered the body" instead of guessing it.
        /// </para>
        /// <para>
        /// Only possible when <see cref="AIFunction.UnderlyingMethod"/> is non-null (reflection-based
        /// functions, e.g. via <see cref="AIFunctionFactory"/>). A hand-written <see cref="AIFunction"/>
        /// subclass with no underlying method cannot be proven this way; it is invoked normally and left to
        /// the conservative "native" default at the invocation boundary for whatever it throws.
        /// </para>
        /// <para>
        /// WHY the <see cref="Type.IsInstanceOfType"/> shortcut: MEAI's own binder accepts a value already
        /// assignable to the parameter type without going through JSON at all (e.g. a boxed <c>string</c>
        /// for a <c>string</c> parameter, or ANY value for an <c>object</c> parameter). Forcing every value
        /// through <c>SerializeToElement</c>/<c>Deserialize</c> regardless rejected shapes MEAI itself would
        /// have accepted — this preflight must never be stricter than the binder it is proving.
        /// </para>
        /// <para>
        /// WHY skip entirely when <see cref="AIFunction.JsonSerializerOptions"/> is null: inventing a
        /// default <see cref="JsonSerializerOptions"/> diverges from whatever options MEAI
        /// actually binds with, and on IL2CPP with reflection metadata trimmed a fresh
        /// <c>JsonSerializerOptions</c> throws <see cref="NotSupportedException"/> for every argument —
        /// which would reject every tool call as "arg-conversion" before MEAI ever got a chance to run.
        /// Absent real options this preflight cannot prove anything; the conservative answer is to let
        /// MEAI decide, exactly as the code did before this preflight existed.
        /// </para>
        /// </summary>
        internal static bool TryBindArgumentsStructurally(AIFunction function,
            IDictionary<string, object> normalized, out string bindingError)
        {
            bindingError = null;
            MethodInfo method = function?.UnderlyingMethod;
            JsonSerializerOptions options = function?.JsonSerializerOptions;
            if (method == null || options == null || normalized == null || normalized.Count == 0)
            {
                return true;
            }

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                string name = parameter.Name;
                if (string.IsNullOrEmpty(name) || parameter.ParameterType == typeof(CancellationToken) ||
                    !normalized.TryGetValue(name, out object raw) || raw == null)
                {
                    continue;
                }

                if (parameter.ParameterType.IsInstanceOfType(raw))
                {
                    continue;
                }

                try
                {
                    // WHY two routes, in THIS order: it is the binder's own order (AIFunctionFactory,
                    // MarshallViaJsonRoundtrip). A string bound to a non-string parameter is read as JSON
                    // CONTENT first (an object argument the normalizer handed over as compact JSON) and,
                    // only when it is not JSON at all, as a JSON STRING VALUE - the route an enum name, a
                    // Guid or a DateTime bind through. 7.41.2 kept the first route alone, so every bare
                    // enum name was rejected with "'G' is an invalid start of a value" while MEAI bound it.
                    // This is a second route, not a looser check: the value must still deserialize into
                    // the parameter type by one of the binder's two exact readings, so an unknown enum
                    // member fails both here exactly as it fails MEAI. The binder's "looks like JSON"
                    // gate is not mirrored: a non-JSON string fails the content read at its first token
                    // with the very JsonException the binder falls through on.
                    bool boundAsJsonContent = raw is string rawJson && parameter.ParameterType != typeof(string) &&
                                              BindsAsJsonContent(rawJson, parameter.ParameterType, options);
                    if (!boundAsJsonContent)
                    {
                        JsonElement element = JsonSerializer.SerializeToElement(raw, raw.GetType(), options);
                        JsonSerializer.Deserialize(element, parameter.ParameterType, options);
                    }
                }
                catch (Exception ex) when (ex is JsonException || ex is FormatException ||
                                            ex is InvalidCastException || ex is NotSupportedException)
                {
                    bindingError =
                        $"Argument '{name}' does not match the expected type for tool '{function.Name}': {ex.Message}";
                    return false;
                }
                catch (ArgumentException)
                {
                    // WHY: an ArgumentException/ArgumentNullException raised INSIDE the serializer is an
                    // infrastructure failure (the observed one is ArgumentNullException("format")), not a
                    // proof that the model's arguments are wrong. It must not reject a call MEAI may still
                    // accept, and it must not leak past this preflight where it would be misread as a
                    // conversion error. Leave the decision to MEAI.
                    continue;
                }
            }

            return true;
        }

        /// <summary>
        /// The binder's first reading of a string bound to a non-string parameter: the string as JSON
        /// CONTENT. <c>false</c> means "not JSON", the one failure the binder falls through on
        /// (<c>catch (JsonException)</c> around its content read); any other exception propagates so the
        /// caller classifies it exactly as it would coming from the string-value route.
        /// </summary>
        private static bool BindsAsJsonContent(string rawJson, Type parameterType, JsonSerializerOptions options)
        {
            try
            {
                JsonSerializer.Deserialize(rawJson, parameterType, options);
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }
    }
}
