using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression: Unity ignores folder assets when <c>guid:</c> in <c>.meta</c> is not exactly 32 hex chars.
    /// </summary>
    public sealed class RogueliteArenaInfrastructureMetaEditModeTests
    {
        private const string RelativeMeta =
            "_exampleGame/RogueliteArena/Features/ArenaBootstrap/Infrastructure.meta";

        [Test]
        public void InfrastructureFolderMeta_GuidLine_Is32HexChars()
        {
            string fullPath = Path.Combine(Application.dataPath, RelativeMeta);
            Assert.That(File.Exists(fullPath), Is.True, $"Missing {fullPath}");

            string text = File.ReadAllText(fullPath);
            Match match = Regex.Match(text, @"^guid:\s*([0-9a-fA-F]+)\s*$", RegexOptions.Multiline);
            Assert.That(match.Success, Is.True, "Expected a guid: line in Infrastructure.meta");

            string guid = match.Groups[1].Value;
            Assert.That(guid.Length, Is.EqualTo(32),
                $"Unity requires a 32-char hex guid; got length {guid.Length} ({guid}).");
        }
    }
}
