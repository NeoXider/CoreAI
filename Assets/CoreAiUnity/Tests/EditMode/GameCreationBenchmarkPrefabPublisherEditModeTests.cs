using System;
using System.IO;
using System.Collections.Generic;
using CoreAI.Benchmarking;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    public sealed class GameCreationBenchmarkPrefabPublisherEditModeTests
    {
        [Test]
        public void Publication_RewritesCheckpointAndEmbeddedImportedReportPaths()
        {
            const string source = "Assets/Benchmark/model/castle.prefab";
            const string destination = "Assets/Benchmark/Published/2026-10-16/model/castle.prefab";
            JObject row = new() { ["castlePrefabPath"] = source, ["notes"] = "Preserved evidence" };
            JObject state = new()
            {
                ["Results"] = new JArray(new JObject
                {
                    ["CastlePrefabPath"] = source,
                    ["ImportedSessionMarkdown"] = "Kept evidence [prefab](" + source + ")",
                    ["ImportedJson"] = row.ToString(Newtonsoft.Json.Formatting.None)
                })
            };
            Dictionary<string, string> moved = new() { [source] = destination };
            Assert.IsTrue(GameCreationBenchmarkPrefabPublisher.RewriteRecordedPaths(state, moved));
            Assert.AreEqual(destination, (string)state["Results"][0]["CastlePrefabPath"]);
            Assert.AreEqual("Kept evidence [prefab](" + destination + ")", (string)state["Results"][0]["ImportedSessionMarkdown"]);
            JObject imported = BenchmarkJson.ParseObject((string)state["Results"][0]["ImportedJson"]);
            Assert.AreEqual(destination, (string)imported["castlePrefabPath"]);
            Assert.AreEqual("Preserved evidence", (string)imported["notes"]);
            Assert.IsFalse(GameCreationBenchmarkPrefabPublisher.RewriteRecordedPaths(state, moved));
        }

        [Test]
        public void Publication_TextPatchPreservesEveryUnrelatedByteIncludingEmbeddedEvidence()
        {
            const string source = "Assets/Benchmark/target/castle.prefab";
            const string destination = "Assets/Benchmark/Published/2026-10-16/target/castle.prefab";
            const string row = "{  \"timestampUtc\" : \"2026-10-15T17:48:50.1764570Z\", \"latency\":7.1137000000000006, " +
                "\"notes\":\"escaped \\u0065vidence and a\\/b\", \"castlePrefabPath\":\"" + source + "\" }";
            string state = "{\n  \"Results\" : [ { \"ImportedJson\" : " + Newtonsoft.Json.JsonConvert.ToString(row) +
                ", \"ImportedSessionMarkdown\" : " + Newtonsoft.Json.JsonConvert.ToString("\n### G6\n[Castle](" + source + ")\n\n") +
                ", \"CastlePrefabPath\":\"" + source + "\" } ], \"unchanged\":7.1137000000000006 }\n";
            Dictionary<string, string> moved = new() { [source] = destination };
            Assert.AreEqual(row.Replace(source, destination), GameCreationBenchmarkPrefabPublisher.RewriteRecordedPaths(row, moved));
            string rewritten = GameCreationBenchmarkPrefabPublisher.RewriteRecordedPaths(state, moved);
            Assert.AreEqual(state.Replace(source, destination), rewritten);
            Assert.AreEqual(rewritten, GameCreationBenchmarkPrefabPublisher.RewriteRecordedPaths(rewritten, moved));
        }

        [Test]
        public void Publication_TextPatchUnderstandsEscapedPathWithoutNormalizingOtherEscapes()
        {
            const string source = "Assets/Benchmark/target/castle.prefab";
            const string destination = "Assets/Benchmark/Published/date/target/castle.prefab";
            string original = "{\"notes\":\"a\\/b\\u0020c\",\"castlePrefabPath\":\"" + source.Replace("/", "\\/") + "\"}";
            string expected = "{\"notes\":\"a\\/b\\u0020c\",\"castlePrefabPath\":\"" + destination + "\"}";
            Assert.AreEqual(expected, GameCreationBenchmarkPrefabPublisher.RewriteRecordedPaths(original,
                new Dictionary<string, string> { [source] = destination }));
        }

        [Test]
        public void Publication_RecoversInterruptedMovesRepairsBothReportsAndIsIdempotent()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CoreAiPublishRecovery_" + Guid.NewGuid().ToString("N"));
            string reports = Path.Combine(directory, "reports");
            string scratch = Path.Combine(directory, "scratch").Replace('\\', '/');
            string source = scratch + "/target/castle.prefab";
            string material = scratch + "/target/Materials/m_castle.mat";
            string texture = scratch + "/target/Materials/Textures/t_castle.asset";
            string destination = scratch + "/Published/2026-10-16/target/castle.prefab";
            string destinationMaterial = scratch + "/Published/2026-10-16/target/Materials/m_castle.mat";
            string destinationTexture = scratch + "/Published/2026-10-16/target/Materials/Textures/t_castle.asset";
            const string prefabGuid = "1a3ecdbfc2db4cdcb17e7dc4a1c1342d";
            const string materialGuid = "2a3ecdbfc2db4cdcb17e7dc4a1c1342d";
            const string textureGuid = "3a3ecdbfc2db4cdcb17e7dc4a1c1342d";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(texture));
                Directory.CreateDirectory(reports);
                File.WriteAllText(source, "material: {guid: " + materialGuid + "}\n");
                File.WriteAllText(source + ".meta", "guid: " + prefabGuid + "\n");
                File.WriteAllText(material, "texture: {guid: " + textureGuid + "}\n");
                File.WriteAllText(material + ".meta", "guid: " + materialGuid + "\n");
                File.WriteAllText(texture, "texture contents");
                File.WriteAllText(texture + ".meta", "guid: " + textureGuid + "\n");
                string json = Path.Combine(reports, "BENCHMARK_run_model__target.json");
                string markdown = Path.ChangeExtension(json, ".md");
                string originalJson = "{\n  \"metadata\":{\"timestampUtc\":\"2026-10-15T17:48:50.1764570Z\"}, " +
                    "\"precise\":7.1137000000000006,\n  \"results\":[{\"castlePrefabPath\":\"" + source + "\"}]\n}\n";
                string originalMarkdown = "[Castle](" + source + ")\n";
                File.WriteAllText(json, originalJson);
                File.WriteAllText(markdown, originalMarkdown);
                string journal = Path.Combine(reports, ".runs", "run", "publish-2026-10-16.json");
                Directory.CreateDirectory(Path.GetDirectoryName(journal));
                File.WriteAllText(journal, new JObject
                {
                    ["completed"] = false,
                    ["moves"] = new JArray(
                        new JObject { ["source"] = texture, ["destination"] = destinationTexture, ["guid"] = textureGuid },
                        new JObject { ["source"] = material, ["destination"] = destinationMaterial, ["guid"] = materialGuid },
                        new JObject { ["source"] = source, ["destination"] = destination, ["guid"] = prefabGuid })
                }.ToString());
                string statePath = Path.Combine(Path.GetDirectoryName(journal), "target.state.json");
                string originalState = "{ \"Results\" : [{ \"CastlePrefabPath\":\"" + source +
                    "\", \"ImportedJson\":" + Newtonsoft.Json.JsonConvert.ToString(originalJson) + "}], \"precise\":7.1137000000000006 }\n";
                File.WriteAllText(statePath, originalState);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationTexture));
                File.Move(texture, destinationTexture);
                File.Move(texture + ".meta", destinationTexture + ".meta");
                File.Move(material, destinationMaterial);
                IReadOnlyDictionary<string, string> moved = GameCreationBenchmarkPrefabPublisher.PublishCastlePrefabs(
                    "run", "2026-10-16", reports, scratch, false);
                Assert.AreEqual(destination, moved[source]);
                Assert.AreEqual(originalJson.Replace(source, destination), File.ReadAllText(json));
                Assert.AreEqual(originalState.Replace(source, destination), File.ReadAllText(statePath));
                Assert.AreEqual("guid: " + materialGuid + "\n", File.ReadAllText(destinationMaterial + ".meta"));
                Assert.AreEqual(destination, (string)BenchmarkJson.ParseObject(File.ReadAllText(json))["results"][0]["castlePrefabPath"]);
                Assert.AreEqual("[Castle](" + destination + ")\n", File.ReadAllText(markdown));
                Assert.AreEqual(originalJson, File.ReadAllText(Directory.GetFiles(reports, "*.json.bak-*")[0]));
                Assert.AreEqual(originalMarkdown, File.ReadAllText(Directory.GetFiles(reports, "*.md.bak-*")[0]));
                Assert.IsTrue((bool)BenchmarkJson.ParseObject(File.ReadAllText(journal))["completed"]);
                string repairedJson = File.ReadAllText(json);
                string repairedMarkdown = File.ReadAllText(markdown);
                GameCreationBenchmarkPrefabPublisher.PublishCastlePrefabs("run", "2026-10-16", reports, scratch, false);
                Assert.AreEqual(repairedJson, File.ReadAllText(json));
                Assert.AreEqual(repairedMarkdown, File.ReadAllText(markdown));
                Assert.AreEqual(1, Directory.GetFiles(reports, "*.json.bak-*").Length);
                Assert.AreEqual(1, Directory.GetFiles(reports, "*.md.bak-*").Length);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void Publication_DiscoversMaterialTextureDependenciesBeforeMovingAndRepairsMarkdownAfterJson()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CoreAiPublishPlan_" + Guid.NewGuid().ToString("N"));
            string reports = Path.Combine(directory, "reports");
            string scratch = Path.Combine(directory, "scratch").Replace('\\', '/');
            string source = scratch + "/target/castle.prefab";
            string material = scratch + "/target/Materials/m_castle.mat";
            string texture = scratch + "/target/Materials/Textures/t_castle.asset";
            const string materialGuid = "2a3ecdbfc2db4cdcb17e7dc4a1c1342d";
            const string textureGuid = "3a3ecdbfc2db4cdcb17e7dc4a1c1342d";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(texture));
                Directory.CreateDirectory(reports);
                File.WriteAllText(source, "material: {guid: " + materialGuid + "}");
                File.WriteAllText(source + ".meta", "guid: 1a3ecdbfc2db4cdcb17e7dc4a1c1342d");
                File.WriteAllText(material, "texture: {guid: " + textureGuid + "}");
                File.WriteAllText(material + ".meta", "guid: " + materialGuid);
                File.WriteAllText(texture, "texture contents");
                File.WriteAllText(texture + ".meta", "guid: " + textureGuid);
                string json = Path.Combine(reports, "BENCHMARK_run_model__target.json");
                string markdown = Path.ChangeExtension(json, ".md");
                File.WriteAllText(json, new JObject { ["results"] = new JArray(new JObject { ["castlePrefabPath"] = source }) }.ToString());
                File.WriteAllText(markdown, source);
                IReadOnlyDictionary<string, string> moved = GameCreationBenchmarkPrefabPublisher.PublishCastlePrefabs(
                    "run", "2026-10-16", reports, scratch, false);
                Assert.AreEqual(3, moved.Count);
                Assert.IsTrue(File.Exists(moved[texture]));
                Assert.IsTrue(File.Exists(moved[material]));
                File.WriteAllText(markdown, source);
                GameCreationBenchmarkPrefabPublisher.PublishCastlePrefabs("run", "2026-10-16", reports, scratch, false);
                Assert.AreEqual(moved[source], File.ReadAllText(markdown));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void PublicationCli_InvalidArgumentsReturnNonzeroExitCode()
        {
#if UNITY_EDITOR
            UnityEngine.TestTools.LogAssert.Expect(UnityEngine.LogType.Error,
                new System.Text.RegularExpressions.Regex("Castle prefab publication failed: Missing required argument"));
#endif
            Assert.AreNotEqual(0, GameCreationBenchmarkPrefabPublisher.RunFromArguments(Array.Empty<string>()));
        }

        [Test]
        public void FileFallback_MovesAssetAndMetaWithIdenticalGuidAndPreservesCollisions()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CoreAiPublish_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string source = Path.Combine(directory, "source.prefab");
                string destination = Path.Combine(directory, "published.prefab");
                const string meta = "fileFormatVersion: 2\nguid: 3a3ecdbfc2db4cdcb17e7dc4a1c1342d\n";
                File.WriteAllText(source, "material guid reference");
                File.WriteAllText(source + ".meta", meta);
                File.WriteAllText(destination, "existing");
                string actual = GameCreationBenchmarkPrefabPublisher.MovePreservingGuid(source, destination, false);
                Assert.AreEqual(meta, File.ReadAllText(actual + ".meta"));
                Assert.AreEqual("material guid reference", File.ReadAllText(actual));
                Assert.AreEqual("existing", File.ReadAllText(destination));
                Assert.IsFalse(File.Exists(source));
                Assert.IsFalse(File.Exists(source + ".meta"));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Test]
        public void FileFallback_MissingMetaRefusesWithoutMovingAsset()
        {
            string directory = Path.Combine(Path.GetTempPath(), "CoreAiPublish_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string source = Path.Combine(directory, "source.mat");
                File.WriteAllText(source, "material");
                Assert.Throws<FileNotFoundException>(() => GameCreationBenchmarkPrefabPublisher.MovePreservingGuid(
                    source, Path.Combine(directory, "destination.mat"), false));
                Assert.IsTrue(File.Exists(source));
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }
    }
}
