using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using CoreAI.Benchmarking;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace CoreAI.Tests.EditMode
{
    /// <summary>Publishes a report's optional editor prefabs without changing their asset GUIDs.</summary>
    public static class GameCreationBenchmarkPrefabPublisher
    {
        public const string ArgRun = "-coreAiBenchmarkPublishRun";
        public const string ArgDate = "-coreAiBenchmarkPublishDate";

        /// <summary>CLI entry: -executeMethod CoreAI.Tests.EditMode.GameCreationBenchmarkPrefabPublisher.RunFromCli.</summary>
        public static void RunFromCli()
        {
            int exitCode = RunFromArguments(Environment.GetCommandLineArgs());
#if UNITY_EDITOR
            if (UnityEngine.Application.isBatchMode && exitCode != 0) EditorApplication.Exit(exitCode);
#endif
            if (exitCode != 0) throw new InvalidOperationException("Castle prefab publication failed. See the preceding error.");
        }

        /// <summary>Returns a process-compatible failure code for invalid arguments or a failed publication.</summary>
        public static int RunFromArguments(string[] args)
        {
            try
            {
                PublishCastlePrefabs(ReadArgument(args, ArgRun), ReadArgument(args, ArgDate));
                return 0;
            }
            catch (Exception exception)
            {
                Log("Castle prefab publication failed: " + exception.Message, true);
                return 1;
            }
        }

        /// <summary>Moves a run's optional editor assets and repairs its report links using a durable move journal.</summary>
        public static IReadOnlyDictionary<string, string> PublishCastlePrefabs(string runId, string dateFolder)
        {
            return PublishCastlePrefabs(runId, dateFolder, "TestResults/CoreAI/Benchmarks", "Assets/Benchmark", true);
        }

        /// <summary>Explicit roots support isolated verification and file-only publication outside the editor.</summary>
        public static IReadOnlyDictionary<string, string> PublishCastlePrefabs(
            string runId, string dateFolder, string reportDirectory, string scratchDirectory, bool useAssetDatabase)
        {
            ValidateComponent(runId, nameof(runId));
            ValidateComponent(dateFolder, nameof(dateFolder));
            string scratch = scratchDirectory.Replace('\\', '/').TrimEnd('/');
            string published = scratch + "/Published/" + dateFolder;
            string[] reports = Directory.GetFiles(reportDirectory, "BENCHMARK_" + runId + "_*.json")
                .Where(path => !path.EndsWith(".tools.json", StringComparison.OrdinalIgnoreCase)).ToArray();
            if (reports.Length == 0) throw new InvalidOperationException("No benchmark reports were found for run " + runId + ".");
            string journalPath = Path.Combine(reportDirectory, ".runs", runId, "publish-" + dateFolder + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(journalPath));
            JObject journal = File.Exists(journalPath) ? BenchmarkJson.ParseObject(File.ReadAllText(journalPath)) :
                new JObject { ["completed"] = false, ["moves"] = new JArray() };
            JArray moves = (JArray)journal["moves"];
            Dictionary<string, string> moved = moves.Cast<JObject>().ToDictionary(
                move => (string)move["source"], move => (string)move["destination"], StringComparer.Ordinal);
            List<(string path, string text)> parsed = new();
            bool hasPrefab = false;
            foreach (string reportPath in reports)
            {
                string originalReport = File.ReadAllText(reportPath);
                JObject report = BenchmarkJson.ParseObject(originalReport);
                parsed.Add((reportPath, originalReport));
                foreach (JToken result in report["results"] ?? new JArray())
                {
                    string source = ((string)result["castlePrefabPath"])?.Replace('\\', '/');
                    if (string.IsNullOrEmpty(source)) continue;
                    hasPrefab = true;
                    if (source.StartsWith(scratch + "/Published/", StringComparison.Ordinal) || moved.ContainsKey(source)) continue;
                    if (!source.StartsWith(scratch + "/", StringComparison.Ordinal))
                        throw new InvalidOperationException("Prefab is outside the benchmark scratch folder: " + source);
                    string relative = source.Substring(scratch.Length + 1);
                    string[] parts = relative.Split('/');
                    if (parts.Length != 2 || !source.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Prefab is outside a benchmark target folder: " + source);
                    ValidateComponent(parts[0], "target folder");
                    string targetDirectory = scratch + "/" + parts[0];
#if UNITY_EDITOR
                    string[] dependencies = useAssetDatabase ? AssetDatabase.GetDependencies(source, true) : FindLocalDependencies(source, targetDirectory);
#else
                    string[] dependencies = FindLocalDependencies(source, targetDirectory);
#endif
                    foreach (string dependency in dependencies.Where(path => path != source &&
                                 path.StartsWith(targetDirectory + "/", StringComparison.Ordinal)))
                    {
                        PlanMove(dependency, published + "/" + parts[0] + "/" +
                            dependency.Substring(targetDirectory.Length + 1), moved, moves);
                    }

                    PlanMove(source, published + "/" + relative, moved, moves);
                }
            }

            if (!hasPrefab) throw new InvalidOperationException("No castle prefab paths are recorded for this run. " +
                "Legacy reports without prefab paths cannot identify their run's scratch prefabs safely.");
            WriteAtomic(journalPath, journal.ToString(Newtonsoft.Json.Formatting.Indented));
            bool didWork = false;
            foreach (JObject move in moves.Cast<JObject>())
            {
                string source = (string)move["source"];
                string destination = (string)move["destination"];
                string guid = (string)move["guid"];
                EnsureDestinationFolder(Path.GetDirectoryName(destination), useAssetDatabase);
                if (!File.Exists(source) && File.Exists(destination) && !File.Exists(destination + ".meta") && File.Exists(source + ".meta"))
                {
                    File.Move(source + ".meta", destination + ".meta");
                    didWork = true;
                }
                if (!File.Exists(source) && File.Exists(destination) && ReadGuid(destination) == guid) continue;
                if (!File.Exists(source) || ReadGuid(source) != guid || File.Exists(destination) || File.Exists(destination + ".meta"))
                    throw new IOException("Publication cannot safely complete recorded move: " + source + " -> " + destination);
                string actual = MovePreservingGuid(source, destination, useAssetDatabase, true);
                if (actual != destination) throw new IOException("Publication destination changed after planning: " + destination);
                didWork = true;
            }

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fffffff", System.Globalization.CultureInfo.InvariantCulture);
            foreach ((string reportPath, string originalReport) in parsed)
            {
                string rewritten = RewriteRecordedPaths(originalReport, moved);
                if (rewritten != originalReport)
                {
                    File.Copy(reportPath, reportPath + ".bak-" + stamp, false);
                    WriteAtomic(reportPath, rewritten);
                    didWork = true;
                }

                string markdown = Path.ChangeExtension(reportPath, ".md");
                if (File.Exists(markdown))
                {
                    string original = File.ReadAllText(markdown);
                    string text = original;
                    foreach (KeyValuePair<string, string> move in moved) text = text.Replace(move.Key, move.Value);
                    if (text != original)
                    {
                        File.Copy(markdown, markdown + ".bak-" + stamp, false);
                        WriteAtomic(markdown, text);
                        didWork = true;
                    }
                }
            }

            string runState = Path.Combine(reportDirectory, ".runs", runId);
            foreach (string statePath in Directory.GetFiles(runState, "*.state.json"))
            {
                string originalState = File.ReadAllText(statePath);
                string rewritten = RewriteRecordedPaths(originalState, moved);
                if (rewritten != originalState) WriteAtomic(statePath, rewritten);
            }

            journal["completed"] = true;
            WriteAtomic(journalPath, journal.ToString(Newtonsoft.Json.Formatting.Indented));
            Log(didWork ? "Castle prefab publication completed for run " + runId + "." :
                "Castle prefabs are already published for run " + runId + "; no changes were needed.", false);
            return moved;
        }

        private static void PlanMove(string source, string destination, Dictionary<string, string> moved, JArray moves)
        {
            if (moved.ContainsKey(source)) return;
            string guid = ReadGuid(source);
            if (!File.Exists(source) || string.IsNullOrEmpty(guid))
                throw new FileNotFoundException("Publishing requires both the asset and its .meta sidecar.", source);
            string candidate = destination;
            int suffix = 1;
            while (File.Exists(candidate) || File.Exists(candidate + ".meta") || moved.Values.Contains(candidate))
                candidate = Path.Combine(Path.GetDirectoryName(destination), Path.GetFileNameWithoutExtension(destination) + "_" +
                    suffix++ + Path.GetExtension(destination)).Replace('\\', '/');
            moved.Add(source, candidate);
            moves.Add(new JObject { ["source"] = source, ["destination"] = candidate, ["guid"] = guid });
        }

        private static string ReadGuid(string path)
        {
            if (!File.Exists(path + ".meta")) return null;
            Match match = Regex.Match(File.ReadAllText(path + ".meta"), @"guid: ([a-fA-F0-9]{32})");
            return match.Success ? match.Groups[1].Value : null;
        }

        private static void EnsureDestinationFolder(string directory, bool useAssetDatabase)
        {
#if UNITY_EDITOR
            string normalized = directory.Replace('\\', '/');
            if (useAssetDatabase && normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                string current = "Assets";
                foreach (string component in normalized.Substring(7).Split('/'))
                {
                    string next = current + "/" + component;
                    if (!AssetDatabase.IsValidFolder(next))
                    {
                        if (Directory.Exists(next)) AssetDatabase.ImportAsset(next, ImportAssetOptions.ForceSynchronousImport);
                        else AssetDatabase.CreateFolder(current, component);
                        if (!AssetDatabase.IsValidFolder(next)) throw new IOException("Cannot create asset folder " + next + ".");
                    }

                    current = next;
                }

                return;
            }
#endif
            Directory.CreateDirectory(directory);
        }

        private static void Log(string message, bool error)
        {
#if UNITY_EDITOR
            if (error) UnityEngine.Debug.LogError(message);
            else UnityEngine.Debug.Log(message);
#else
            if (error) Console.Error.WriteLine(message);
            else Console.WriteLine(message);
#endif
        }

        /// <summary>Rewrites checkpoint paths, including the frozen report row embedded as ImportedJson.</summary>
        public static bool RewriteRecordedPaths(JObject state, IReadOnlyDictionary<string, string> moved)
        {
            bool changed = false;
            foreach (JValue value in state.Descendants().OfType<JValue>())
            {
                if (value.Type != JTokenType.String) continue;
                if (moved.TryGetValue((string)value, out string destination))
                {
                    value.Value = destination;
                    changed = true;
                }
                else if (value.Parent is JProperty property &&
                         string.Equals(property.Name, "ImportedJson", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrEmpty((string)value))
                {
                    string original = (string)value;
                    string rewritten = RewriteRecordedPaths(original, moved);
                    if (rewritten != original)
                    {
                        value.Value = rewritten;
                        changed = true;
                    }
                }
                else if (value.Parent is JProperty markdownProperty &&
                         markdownProperty.Name.StartsWith("Imported", StringComparison.Ordinal) &&
                         markdownProperty.Name.EndsWith("Markdown", StringComparison.Ordinal))
                {
                    string original = (string)value;
                    string rewritten = original;
                    foreach (KeyValuePair<string, string> move in moved) rewritten = rewritten.Replace(move.Key, move.Value);
                    if (rewritten != original)
                    {
                        value.Value = rewritten;
                        changed = true;
                    }
                }
            }

            return changed;
        }

        /// <summary>Patches only recorded path spans, retaining all other JSON bytes and embedded evidence formatting.</summary>
        public static string RewriteRecordedPaths(string json, IReadOnlyDictionary<string, string> moved)
        {
            BenchmarkJson.ParseToken(json);
            return ApplyPathEdits(json, CollectPathEdits(json, moved));
        }

        private static List<(int start, int length, string replacement)> CollectPathEdits(
            string json, IReadOnlyDictionary<string, string> moved)
        {
            List<(int start, int length, string replacement)> edits = new();
            string property = "";
            foreach (Match match in Regex.Matches(json, "\"(?:\\\\.|[^\"\\\\])*\""))
            {
                string decoded = JsonConvert.DeserializeObject<string>(match.Value);
                int next = match.Index + match.Length;
                while (next < json.Length && char.IsWhiteSpace(json[next])) next++;
                if (next < json.Length && json[next] == ':')
                {
                    property = decoded;
                    continue;
                }

                List<(int start, int length, string replacement)> nested = new();
                if (moved.TryGetValue(decoded, out string destination)) nested.Add((0, decoded.Length, destination));
                else if (string.Equals(property, "ImportedJson", StringComparison.OrdinalIgnoreCase) && decoded.Length > 0)
                {
                    BenchmarkJson.ParseToken(decoded);
                    nested = CollectPathEdits(decoded, moved);
                }
                else if (property.StartsWith("Imported", StringComparison.Ordinal) &&
                         property.EndsWith("Markdown", StringComparison.Ordinal))
                {
                    foreach (KeyValuePair<string, string> move in moved)
                    {
                        int index = 0;
                        while ((index = decoded.IndexOf(move.Key, index, StringComparison.Ordinal)) >= 0)
                        {
                            nested.Add((index, move.Key.Length, move.Value));
                            index += move.Key.Length;
                        }
                    }
                }

                int[] offsets = JsonStringOffsets(match.Value, decoded.Length);
                foreach ((int start, int length, string replacement) in nested)
                {
                    string encoded = JsonConvert.ToString(replacement);
                    edits.Add((match.Index + offsets[start], offsets[start + length] - offsets[start],
                        encoded.Substring(1, encoded.Length - 2)));
                }
                property = "";
            }
            return edits;
        }

        private static int[] JsonStringOffsets(string token, int decodedLength)
        {
            int[] offsets = new int[decodedLength + 1];
            int index = 1;
            for (int decoded = 0; decoded < decodedLength; decoded++)
            {
                offsets[decoded] = index;
                index += token[index] == '\\' ? token[index + 1] == 'u' ? 6 : 2 : 1;
            }
            offsets[decodedLength] = index;
            return offsets;
        }

        private static string ApplyPathEdits(string json, List<(int start, int length, string replacement)> edits)
        {
            StringBuilder text = new(json);
            foreach ((int start, int length, string replacement) in edits.OrderByDescending(edit => edit.start))
            {
                text.Remove(start, length);
                text.Insert(start, replacement);
            }
            return text.ToString();
        }

        /// <summary>Moves the asset and sidecar together when an AssetDatabase is unavailable.</summary>
        public static string MovePreservingGuid(string source, string destination, bool useAssetDatabase = true, bool requireExactDestination = false)
        {
            if (!File.Exists(source) || !File.Exists(source + ".meta"))
            {
                throw new FileNotFoundException("Publishing requires both the asset and its .meta sidecar.", source);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            string candidate = destination;
            int suffix = 1;
            while (File.Exists(candidate) || File.Exists(candidate + ".meta"))
            {
                if (requireExactDestination) throw new IOException("Publication destination is occupied: " + candidate);
                candidate = Path.Combine(Path.GetDirectoryName(destination),
                    Path.GetFileNameWithoutExtension(destination) + "_" + suffix++ + Path.GetExtension(destination)).Replace('\\', '/');
            }

#if UNITY_EDITOR
            if (useAssetDatabase)
            {
                string error = AssetDatabase.MoveAsset(source, candidate);
                if (!string.IsNullOrEmpty(error))
                {
                    throw new IOException("Cannot publish " + source + ": " + error);
                }

                return candidate;
            }
#endif
            File.Move(source, candidate);
            try
            {
                File.Move(source + ".meta", candidate + ".meta");
            }
            catch
            {
                File.Move(candidate, source);
                throw;
            }

            return candidate;
        }

        private static string[] FindLocalDependencies(string prefab, string targetDirectory)
        {
            Dictionary<string, string> local = Directory.GetFiles(targetDirectory, "*.meta", SearchOption.AllDirectories)
                .Select(path => path.Substring(0, path.Length - 5)).Where(File.Exists)
                .Where(path => !string.IsNullOrEmpty(ReadGuid(path))).ToDictionary(ReadGuid, path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase);
            HashSet<string> found = new(StringComparer.Ordinal);
            Queue<string> pending = new();
            pending.Enqueue(prefab);
            while (pending.Count > 0)
            {
                string asset = pending.Dequeue();
                foreach (Match match in Regex.Matches(File.ReadAllText(asset), @"guid: ([a-fA-F0-9]{32})"))
                {
                    if (local.TryGetValue(match.Groups[1].Value, out string dependency) && dependency != prefab && found.Add(dependency))
                        pending.Enqueue(dependency);
                }
            }

            return found.ToArray();
        }

        private static void WriteAtomic(string path, string content)
        {
            string temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temporary, content, new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }

        private static void ValidateComponent(string value, string name)
        {
            if (string.IsNullOrWhiteSpace(value) || value == "." || value == ".." ||
                value.Any(character => !char.IsLetterOrDigit(character) && character != '_' && character != '-' && character != '.'))
            {
                throw new ArgumentException("Expected a single folder or run identifier.", name);
            }
        }

        private static string ReadArgument(string[] args, string name)
        {
            int index = Array.IndexOf(args, name);
            if (index < 0 || index + 1 >= args.Length)
            {
                throw new ArgumentException("Missing required argument " + name + ".");
            }

            return args[index + 1];
        }
    }
}
