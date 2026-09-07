using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The prose-vs-native tool-channel decision lives in exactly one place:
    /// <c>LlmToolChannelPolicy.InterpretsProse</c>. Call sites (streaming loop, MeaiLlmClient,
    /// AiOrchestrator) only supply inputs — native capability, explicit fallback opt-in, tool
    /// mode — and never re-spell the rule inline. An inline re-spelling
    /// (<c>...AllowTextShapedToolCallsOnNativeEndpoint == true</c> next to a native check) is how
    /// the three gates drifted apart before; this guard fails the suite if one is added.
    /// </summary>
    [TestFixture]
    public sealed class LlmToolChannelSinglePredicateGuardEditModeTests
    {
        private static readonly string[] ScannedRoots =
        {
            "Assets/CoreAI/Runtime",
            "Assets/CoreAiUnity/Runtime",
            "Assets/CoreAIMods/Runtime"
        };

        private const string PolicyFile =
            "Assets/CoreAI/Runtime/Core/Features/Llm/SmartToolCallingChatClient.cs";

        private static readonly Regex InlineFallbackComparison = new(
            @"AllowTextShapedToolCallsOnNativeEndpoint\s*(==|!=)|explicitNativeFallback\s*(==|!=)",
            RegexOptions.Compiled);

        [Test]
        public void NoInlineRespellingOfTheChannelRule_OutsideTheSharedPredicate()
        {
            List<string> violations = new();
            int scannedFiles = 0;

            foreach (string root in ScannedRoots)
            {
                string absoluteRoot = ToAbsolute(root);
                Assert.IsTrue(Directory.Exists(absoluteRoot), "Scan root not found: " + absoluteRoot);

                foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
                {
                    scannedFiles++;
                    string relative = ToRelative(file);
                    if (string.Equals(relative, PolicyFile, System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    string code = StripCommentsAndStrings(File.ReadAllText(file));
                    foreach (Match match in InlineFallbackComparison.Matches(code))
                    {
                        violations.Add(relative + ": " + match.Value.Trim());
                    }
                }
            }

            Assert.Greater(scannedFiles, 0, "The scan found no files — the project path is broken.");
            Assert.IsEmpty(
                violations,
                "The prose-vs-native rule is duplicated inline instead of calling LlmToolChannelPolicy.InterpretsProse:\n" +
                string.Join("\n", violations));
        }

        [Test]
        public void Matcher_FlagsInlineComparison_AndIgnoresArgumentsAndComments()
        {
            Assert.IsTrue(
                InlineFallbackComparison.IsMatch("x = explicitNativeFallback == true;"),
                "The matcher must flag a direct comparison of the flag.");
            Assert.IsFalse(
                InlineFallbackComparison.IsMatch(StripCommentsAndStrings(
                    "// request.AllowTextShapedToolCallsOnNativeEndpoint == true — legacy code\n" +
                    "bool ok = InterpretsProse(native, request.AllowTextShapedToolCallsOnNativeEndpoint, mode);")),
                "Passing the flag as an argument, or mentioning it in a comment, is not a violation.");
        }

        private static string ToAbsolute(string relative)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ToRelative(string absolute)
        {
            string assets = Application.dataPath.Replace('\\', '/');
            string normalized = absolute.Replace('\\', '/');
            return normalized.StartsWith(assets, System.StringComparison.Ordinal)
                ? "Assets" + normalized.Substring(assets.Length)
                : normalized;
        }

        private static string StripCommentsAndStrings(string text)
        {
            StringBuilder code = new(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];
                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n')
                        {
                            code.Append('\n');
                        }

                        i++;
                    }

                    i = System.Math.Min(i + 2, text.Length);
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    while (i < text.Length && text[i] != '"' && text[i] != '\n')
                    {
                        i += text[i] == '\\' ? 2 : 1;
                    }

                    i = System.Math.Min(i + 1, text.Length);
                    continue;
                }

                code.Append(c);
                i++;
            }

            return code.ToString();
        }
    }
}
