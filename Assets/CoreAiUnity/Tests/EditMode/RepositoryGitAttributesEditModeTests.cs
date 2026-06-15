using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    public sealed class RepositoryGitAttributesEditModeTests
    {
        private static readonly string[] RequiredRules =
        {
            "* text=auto",
            ".gitattributes text eol=lf",
            "*.cs text eol=lf diff=csharp",
            "*.asmdef text eol=lf",
            "*.asmref text eol=lf",
            "*.meta text eol=lf",
            "*.unity text eol=lf",
            "*.prefab text eol=lf",
            "*.asset text eol=lf",
            "*.mat text eol=lf",
            "*.sln text eol=crlf",
            "*.csproj text eol=crlf",
            "*.png binary",
            "*.jpg binary",
            "*.fbx binary",
            "*.dll binary",
            "*.unitypackage binary"
        };

        [Test]
        public void GitAttributes_DefinesUnityFriendlyTextAndBinaryRules()
        {
            string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            string path = Path.Combine(root, ".gitattributes");

            Assert.That(File.Exists(path), Is.True, $"Missing {path}");

            HashSet<string> lines = File.ReadAllLines(path)
                .Select(static line => line.Trim())
                .Where(static line => line.Length > 0 && !line.StartsWith("#", StringComparison.Ordinal))
                .ToHashSet(StringComparer.Ordinal);

            foreach (string rule in RequiredRules)
            {
                Assert.That(lines.Contains(rule), Is.True, $"Missing .gitattributes rule: {rule}");
            }
        }
    }
}
