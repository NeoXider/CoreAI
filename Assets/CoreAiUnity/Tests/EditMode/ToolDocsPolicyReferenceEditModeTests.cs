#if COREAI_LLM
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using CoreAI.Infrastructure.Llm;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The tool-calling docs are read as a contract by people writing tools, and a doc that names a policy
    /// member which no longer exists sends them to edit package source that is not there. That is not a
    /// hypothetical: the guide told hosts to add their mutating tool to
    /// <c>ToolExecutionPolicy.SerializedMutatingToolNames</c> and to read
    /// <c>ToolExecutionPolicy.CheckDuplicate</c> long after both had been renamed away, and nothing went
    /// red because prose is not compiled.
    /// <para>
    /// This fixture compiles the prose against reflection instead: every <c>ToolExecutionPolicy.Member</c>
    /// the docs mention must resolve on the real type. It fails on a BROKEN reference, not on a mention,
    /// so it does not punish a doc for talking about the policy.
    /// </para>
    /// </summary>
    public sealed class ToolDocsPolicyReferenceEditModeTests
    {
        /// <summary>
        /// The tool-calling doc set: the four documents an author of a tool is pointed at. Scoped on
        /// purpose — a repo-wide sweep would also flag prose about constructor parameters and historical
        /// notes in the changelog, and a guard nobody can keep green stops being read.
        /// </summary>
        private static readonly string[] ToolDocs =
        {
            "Assets/CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md",
            "Assets/CoreAI/Docs/AGENT_BUILDER.md",
            "Assets/CoreAiUnity/Docs/TOOL_AUTHORING_GUIDE.md",
            "Assets/CoreAiUnity/Docs/TOOL_CALL_SPEC.md"
        };

        private const BindingFlags AnyMember =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.FlattenHierarchy;

        [Test]
        public void ToolDocs_NamePolicyMembersThatActuallyExist()
        {
            string root = RequireRepository();
            Type policy = typeof(ToolExecutionPolicy);
            Regex reference = new(@"ToolExecutionPolicy\.([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);
            List<string> broken = new();

            foreach (string relative in ToolDocs)
            {
                string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Assert.IsTrue(File.Exists(path), $"{relative} is part of the documented tool contract and must exist.");

                foreach (Match match in reference.Matches(File.ReadAllText(path)))
                {
                    string member = match.Groups[1].Value;

                    // "ToolExecutionPolicy.cs" is a file path, not a member reference.
                    if (member == "cs")
                    {
                        continue;
                    }

                    if (policy.GetMember(member, AnyMember).Length == 0 &&
                        policy.GetNestedType(member, AnyMember) == null)
                    {
                        broken.Add($"{relative}: ToolExecutionPolicy.{member}");
                    }
                }
            }

            CollectionAssert.IsEmpty(broken,
                "These docs point a tool author at policy members that do not exist:\n" +
                string.Join("\n", broken));
        }

        /// <summary>
        /// Both docs print the echo no-op payload so an author can recognize it in a log. The expected text
        /// comes from the code that emits it, so a reworded payload turns the stale quotes red instead of
        /// leaving readers matching a message the pipeline stopped sending.
        /// </summary>
        [Test]
        [TestCase("Assets/CoreAI/Docs/TOOL_CALLING_BEST_PRACTICES.md", "world_command")]
        [TestCase("Assets/CoreAI/Docs/AGENT_BUILDER.md", "memory")]
        public void ToolDocs_QuoteTheEchoNoOpPayloadTheCodeEmits(string relative, string toolName)
        {
            string root = RequireRepository();
            string documented = File.ReadAllText(
                Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

            string payload = ToolExecutionPolicy.BuildDuplicateNoOpPayload(toolName);
            string message = Newtonsoft.Json.Linq.JObject.Parse(payload).Value<string>("message");

            StringAssert.Contains(message, documented,
                $"{relative} quotes an echo no-op message the policy no longer emits.");
        }

        /// <summary>
        /// The repository root, or an ignored test. The package also ships to games that only consume it,
        /// and there these documents are not part of the checkout — ignoring is honest, failing is not.
        /// </summary>
        private static string RequireRepository()
        {
            foreach (string start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            {
                for (DirectoryInfo directory = new(start); directory != null; directory = directory.Parent)
                {
                    if (File.Exists(Path.Combine(directory.FullName, "Assets", "CoreAI", "Docs",
                            "TOOL_CALLING_BEST_PRACTICES.md")))
                    {
                        return directory.FullName;
                    }
                }
            }

            Assert.Ignore("Not a CoreAI checkout: the tool-calling documents are not present to check.");
            return null;
        }
    }
}
#endif
