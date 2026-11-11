using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CoreAI.Mods.Performance
{
    /// <summary>
    /// Writes a <see cref="PerfRunReport"/> as JSON with no serializer dependency.
    /// </summary>
    /// <remarks>
    /// WHY hand-written: the same report must come out of the Editor, a Mono or IL2CPP player and a WebGL
    /// page, and JsonUtility cannot write the nested lists while a serializer package adds a stripping and
    /// AOT question to every player build the harness exists to measure. Numbers use the invariant culture
    /// and round-trip formatting; a series that was not measured is written as <c>null</c>, never as zeros.
    /// </remarks>
    public static class PerfReportJson
    {
        /// <summary>Line printed before the report when it goes to a log instead of a file.</summary>
        public const string LogBeginMarker = "COREAI_PERF_JSON_BEGIN";

        /// <summary>Line printed after the report when it goes to a log instead of a file.</summary>
        public const string LogEndMarker = "COREAI_PERF_JSON_END";

        /// <summary>The report as one indented JSON document.</summary>
        public static string Write(PerfRunReport report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            StringBuilder json = new();
            json.Append("{\n");
            AppendProperty(json, 1, "schema", PerfRunReport.Schema, true);
            Indent(json, 1).Append("\"environment\": {");
            for (int index = 0; index < report.Environment.Count; index++)
            {
                json.Append(index == 0 ? "\n" : ",\n");
                Indent(json, 2);
                AppendString(json, report.Environment[index].Key);
                json.Append(": ");
                AppendString(json, report.Environment[index].Value);
            }

            json.Append(report.Environment.Count == 0 ? "},\n" : "\n  },\n");
            Indent(json, 1).Append("\"scenarios\": [");
            for (int index = 0; index < report.Scenarios.Count; index++)
            {
                json.Append(index == 0 ? "\n" : ",\n");
                AppendScenario(json, report.Scenarios[index]);
            }

            json.Append(report.Scenarios.Count == 0 ? "]\n" : "\n  ]\n");
            json.Append("}\n");
            return json.ToString();
        }

        /// <summary>The report wrapped in the log markers, for a player that cannot write a file (WebGL).</summary>
        public static string WriteForLog(PerfRunReport report)
        {
            return LogBeginMarker + "\n" + Write(report) + LogEndMarker;
        }

        private static void AppendScenario(StringBuilder json, PerfScenarioResult result)
        {
            Indent(json, 2).Append("{\n");
            AppendProperty(json, 3, "id", result.Scenario.Id, true);
            AppendProperty(json, 3, "description", result.Scenario.Description, true);
            AppendProperty(json, 3, "harness", result.Harness, true);
            AppendProperty(json, 3, "failure", result.Failure, true);
            AppendNumber(json, 3, "staticParts", result.Scenario.StaticPartCount);
            AppendNumber(json, 3, "movers", result.Scenario.MoverCount);
            AppendNumber(json, 3, "moverHandlers", result.Scenario.MoverHandlerCount);
            AppendNumber(json, 3, "warmupFrames", result.WarmupFrames);
            AppendNumber(json, 3, "measuredFrames", result.MeasuredFrames);
            AppendDouble(json, 3, "measuredSeconds", result.MeasuredSeconds);
            AppendDistribution(json, 3, "frameMs", result.FrameMilliseconds);
            AppendDistribution(json, 3, "schedulerMs", result.SchedulerMilliseconds);
            AppendDistribution(json, 3, "mainThreadMs", result.MainThreadMilliseconds);
            AppendDistribution(json, 3, "gcAllocatedBytesPerFrame", result.GcAllocatedBytesPerFrame);
            AppendProperty(json, 3, "gcAllocatedSource", result.GcAllocatedSource, true);
            AppendNumber(json, 3, "gcCollections", result.GcCollections);
            AppendDistribution(json, 3, "batches", result.Batches);
            AppendDistribution(json, 3, "drawCalls", result.DrawCalls);
            AppendDistribution(json, 3, "setPassCalls", result.SetPassCalls);
            AppendNumber(json, 3, "managedHeapBytes", result.ManagedHeapBytes);
            AppendNumber(json, 3, "errorLogs", result.ErrorLogs);
            Indent(json, 3).Append("\"memoryBytes\": {");
            for (int index = 0; index < result.Memory.Count; index++)
            {
                json.Append(index == 0 ? "\n" : ",\n");
                Indent(json, 4);
                AppendString(json, result.Memory[index].Key);
                json.Append(": ").Append(result.Memory[index].Value.ToString(CultureInfo.InvariantCulture));
            }

            json.Append(result.Memory.Count == 0 ? "},\n" : "\n      },\n");
            Indent(json, 3).Append("\"lua\": {\n");
            AppendNumber(json, 4, "guardedSteps", result.Lua.GuardedSteps);
            AppendNumber(json, 4, "threadResumes", result.Lua.ThreadResumes);
            AppendNumber(json, 4, "eventsDelivered", result.Lua.EventsDelivered);
            AppendNumber(json, 4, "completedOperations", result.Lua.CompletedOperations);
            AppendDouble(json, 4, "guardedStepsPerSecond", result.GuardedStepsPerSecond);
            AppendDouble(json, 4, "resumesPerSecond", result.ResumesPerSecond, false);
            Indent(json, 3).Append("},\n");
            AppendStringArray(json, 3, "loadedMods", result.LoadedMods, true);
            AppendStringArray(json, 3, "failedMods", result.FailedMods, true);
            AppendStringArray(json, 3, "unavailable", result.Unavailable, true);
            AppendStringArray(json, 3, "notes", result.Notes, false);
            Indent(json, 2).Append('}');
        }

        private static void AppendDistribution(StringBuilder json, int depth, string name, PerfDistribution value)
        {
            Indent(json, depth);
            AppendString(json, name);
            if (value.Count == 0)
            {
                json.Append(": null,\n");
                return;
            }

            json.Append(": { \"count\": ").Append(value.Count.ToString(CultureInfo.InvariantCulture))
                .Append(", \"mean\": ").Append(FormatDouble(value.Mean))
                .Append(", \"min\": ").Append(FormatDouble(value.Min))
                .Append(", \"p50\": ").Append(FormatDouble(value.P50))
                .Append(", \"p95\": ").Append(FormatDouble(value.P95))
                .Append(", \"p99\": ").Append(FormatDouble(value.P99))
                .Append(", \"max\": ").Append(FormatDouble(value.Max))
                .Append(" },\n");
        }

        private static void AppendStringArray(StringBuilder json, int depth, string name, List<string> values,
            bool trailingComma)
        {
            Indent(json, depth);
            AppendString(json, name);
            json.Append(": [");
            for (int index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    json.Append(", ");
                }

                AppendString(json, values[index]);
            }

            json.Append(trailingComma ? "],\n" : "]\n");
        }

        private static void AppendProperty(StringBuilder json, int depth, string name, string value, bool trailingComma)
        {
            Indent(json, depth);
            AppendString(json, name);
            json.Append(": ");
            AppendString(json, value);
            json.Append(trailingComma ? ",\n" : "\n");
        }

        private static void AppendNumber(StringBuilder json, int depth, string name, long value)
        {
            Indent(json, depth);
            AppendString(json, name);
            json.Append(": ").Append(value.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        }

        private static void AppendDouble(StringBuilder json, int depth, string name, double value,
            bool trailingComma = true)
        {
            Indent(json, depth);
            AppendString(json, name);
            json.Append(": ").Append(FormatDouble(value)).Append(trailingComma ? ",\n" : "\n");
        }

        /// <summary>A finite number in round-trip invariant form; NaN and infinities become <c>null</c>.</summary>
        public static string FormatDouble(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "null";
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>Appends <paramref name="value"/> as a JSON string literal.</summary>
        public static void AppendString(StringBuilder json, string value)
        {
            json.Append('"');
            string text = value ?? "";
            for (int index = 0; index < text.Length; index++)
            {
                char c = text[index];
                switch (c)
                {
                    case '"':
                        json.Append("\\\"");
                        break;
                    case '\\':
                        json.Append("\\\\");
                        break;
                    case '\n':
                        json.Append("\\n");
                        break;
                    case '\r':
                        json.Append("\\r");
                        break;
                    case '\t':
                        json.Append("\\t");
                        break;
                    default:
                        if (c < ' ')
                        {
                            json.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            json.Append(c);
                        }

                        break;
                }
            }

            json.Append('"');
        }

        private static StringBuilder Indent(StringBuilder json, int depth)
        {
            return json.Append(' ', depth * 2);
        }
    }
}
