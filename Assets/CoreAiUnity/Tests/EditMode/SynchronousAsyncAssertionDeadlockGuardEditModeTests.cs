using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Guards F12: a <c>[Test]</c> that calls <c>Assert.ThrowsAsync</c>/<c>Assert.CatchAsync</c> blocks the
    /// calling thread waiting for the delegate, while any continuation inside that delegate (an
    /// <c>await Task.Yield()</c>, an <c>await Task.Delay(...)</c>) is posted to the CURRENT
    /// SynchronizationContext — which, on that same blocked thread, never runs. The result is a silent
    /// editor deadlock with no results file.
    /// WHY an <c>async</c> test is NOT exempt: both assertions are synchronous methods that wait on the
    /// task themselves, so declaring the enclosing test <c>async Task</c> changes nothing about the block.
    /// Exempting them is what let the whole EditMode run hang again after the assembly-wide detach was
    /// narrowed away.
    /// </summary>
    /// <remarks>
    /// WHY there is no assembly-wide detach backing this: a <c>[SetUpFixture]</c> only reaches fixtures in
    /// its own assembly, so a blanket detach also hits fixtures that await and then call main-thread-only
    /// Unity object APIs (new GameObject, ScriptableObject.CreateInstance, DestroyImmediate) — moving their
    /// continuation onto the thread pool breaks them the first time a stub actually yields. So coverage
    /// here is a fixture-local <c>[SetUp]</c> detach only, added to exactly the fixtures that need it. WHY
    /// this scans every EditMode folder instead of just one: a namespace match is NOT proof of coverage —
    /// <c>[SetUpFixture]</c> scoping is per-assembly, so a fixture can share a namespace with an unrelated
    /// assembly's SetUpFixture and still run with zero coverage. Namespace is therefore never treated as a
    /// signal here; only an actual local detach in the same file counts.
    /// </remarks>
    public sealed class SynchronousAsyncAssertionDeadlockGuardEditModeTests
    {
        private static readonly string[] ScannedRoots =
        {
            "Assets/CoreAI/Tests/EditMode",
            "Assets/CoreAIMcp/Tests/EditMode",
            "Assets/CoreAIMirror/Tests/EditMode",
            "Assets/CoreAIMods/Tests/EditMode",
            "Assets/CoreAiUnity/Tests/EditMode",
            "Assets/_exampleGame/Tests/EditMode",
        };

        [Test]
        public void TestsUsingThrowsAsyncOrCatchAsync_HaveALocalSynchronizationContextDetach()
        {
            List<string> violations = new();
            int scannedFiles = 0;

            foreach (string scannedRoot in ScannedRoots)
            {
                string absoluteRoot = ToAbsolute(scannedRoot);
                Assert.IsTrue(Directory.Exists(absoluteRoot), $"Scan root not found: {absoluteRoot}");

                foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
                {
                    scannedFiles++;
                    string relative = ToRelative(file);
                    foreach (string methodName in FindUncoveredViolations(File.ReadAllText(file)))
                    {
                        violations.Add($"{relative}: {methodName}");
                    }
                }
            }

            Assert.Greater(scannedFiles, 0, "Scan found no files — the project path is broken.");
            Assert.IsEmpty(
                violations,
                "The [Test] methods below call Assert.ThrowsAsync/CatchAsync without a fixture-local " +
                "SynchronizationContext detach ([SetUp] + SetSynchronizationContext(null)) in the same file " +
                "— this is the F12 deadlock shape:\n" + string.Join("\n", violations));
        }

        /// <summary>
        /// Seeded case for the shape that actually hung the editor: a synchronous [Test] that never
        /// mentions ThrowsAsync and simply reads Task.Result. It also pins the two false positives the
        /// stricter rule must NOT produce — an unrelated object's Result property, and a bounded Wait.
        /// </summary>
        [Test]
        public void Scanner_FlagsBlockingTaskResult_ButNotResultPropertiesOrBoundedWaits()
        {
            const string blockingResult = @"
namespace SomeOther.Namespace
{
    public sealed class Fixture
    {
        [Test]
        public void SyncTest_BlocksOnTaskResult()
        {
            Task<int> first = Start();
            Assert.IsTrue(first.Result > 0);
        }
    }
}
";
            CollectionAssert.AreEqual(
                new[] { "SyncTest_BlocksOnTaskResult" }, FindUncoveredViolations(blockingResult));

            const string unrelatedResultProperty = @"
namespace SomeOther.Namespace
{
    public sealed class Fixture
    {
        [Test]
        public void SyncTest_ReadsAResultProperty()
        {
            ToolCallResult call = Execute();
            Assert.IsNotNull(call.Result);
        }
    }
}
";
            CollectionAssert.IsEmpty(FindUncoveredViolations(unrelatedResultProperty),
                "Only locals declared as Task count; result objects with a Result property are not waits.");

            const string boundedWait = @"
namespace SomeOther.Namespace
{
    public sealed class Fixture
    {
        [Test]
        public void SyncTest_WaitsWithATimeout()
        {
            Task observer = Start();
            Assert.IsTrue(observer.Wait(TimeSpan.FromSeconds(5)));
        }
    }
}
";
            CollectionAssert.IsEmpty(FindUncoveredViolations(boundedWait),
                "A bounded wait gives up on its own and cannot hang the run.");
        }

        /// <summary>Seeded case: without it, a scanner broken into always-empty would read as green.</summary>
        [Test]
        public void Scanner_FlagsUncoveredThrowsAsync_ButIgnoresCoveredAndComments()
        {
            const string uncovered = @"
namespace SomeOther.Namespace
{
    public sealed class Fixture
    {
        [Test]
        public void SyncTest_UsesThrowsAsync()
        {
            Assert.ThrowsAsync<Exception>(async () => await Task.Yield());
        }
    }
}
";
            CollectionAssert.AreEqual(
                new[] { "SyncTest_UsesThrowsAsync" }, FindUncoveredViolations(uncovered));

            const string sameNamespaceAsAnUnrelatedAssemblyIsNotCoverage = @"
namespace CoreAI.Tests.EditMode
{
    public sealed class Fixture
    {
        [Test]
        public void SyncTest_UsesCatchAsync()
        {
            Assert.CatchAsync<Exception>(async () => await Task.Yield());
        }
    }
}
";
            CollectionAssert.AreEqual(
                new[] { "SyncTest_UsesCatchAsync" },
                FindUncoveredViolations(sameNamespaceAsAnUnrelatedAssemblyIsNotCoverage),
                "Namespace match must never count as coverage: [SetUpFixture] scoping is per-assembly, so " +
                "this fixture could share a namespace with a wholly different assembly's detach and still " +
                "run with none.");

            const string coveredByLocalDetach = @"
namespace SomeOther.Namespace
{
    public sealed class Fixture
    {
        [SetUp]
        public void Detach()
        {
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [Test]
        public void SyncTest_UsesThrowsAsync()
        {
            Assert.ThrowsAsync<Exception>(async () => await Task.Yield());
        }
    }
}
";
            CollectionAssert.IsEmpty(FindUncoveredViolations(coveredByLocalDetach));

            const string asyncTestBlocksJustTheSame = @"
namespace SomeOther.Namespace
{
    public sealed class Fixture
    {
        [Test]
        public async Task AsyncTest_UsesThrowsAsync()
        {
            await Task.CompletedTask;
            Assert.ThrowsAsync<Exception>(async () => await Task.Yield());
        }
    }
}
";
            CollectionAssert.AreEqual(
                new[] { "AsyncTest_UsesThrowsAsync" },
                FindUncoveredViolations(asyncTestBlocksJustTheSame),
                "Assert.ThrowsAsync waits on the task itself, so an async test blocks the calling " +
                "thread exactly like a synchronous one.");

            const string commentOnlyMention = @"
namespace SomeOther.Namespace
{
    public sealed class Fixture
    {
        [Test]
        public void SyncTest_JustTalksAboutIt()
        {
            // Assert.ThrowsAsync would deadlock here, so we don't use it.
            Assert.Pass();
        }
    }
}
";
            CollectionAssert.IsEmpty(FindUncoveredViolations(commentOnlyMention));
        }

        /// <summary>
        /// Names of <c>[Test]</c> methods whose body calls <c>Assert.ThrowsAsync</c>/
        /// <c>Assert.CatchAsync</c> and are not covered by a fixture-local SynchronizationContext detach.
        /// A namespace match is deliberately NOT treated as coverage — see the class remarks.
        /// </summary>
        private static List<string> FindUncoveredViolations(string source)
        {
            string code = StripCommentsAndStringLiterals(source);
            List<string> violations = new();

            if (HasLocalSynchronizationContextDetach(code))
            {
                return violations;
            }

            foreach (Match testAttribute in Regex.Matches(code, @"\[Test\]"))
            {
                int braceIndex = code.IndexOf('{', testAttribute.Index);
                if (braceIndex < 0)
                {
                    continue;
                }

                string signature = code.Substring(testAttribute.Index, braceIndex - testAttribute.Index);
                int bodyEnd = FindMatchingBrace(code, braceIndex);
                if (bodyEnd < 0)
                {
                    continue;
                }

                string body = code.Substring(braceIndex, bodyEnd - braceIndex + 1);
                if (!BlocksOnAnIncompleteTask(body))
                {
                    continue;
                }

                Match methodName = Regex.Match(signature, @"(\w+)\s*\([^)]*\)\s*$");
                violations.Add(methodName.Success ? methodName.Groups[1].Value : signature.Trim());
            }

            return violations;
        }

        /// <summary>
        /// Whether a test body waits on a task from the calling thread. Two shapes count.
        /// </summary>
        /// <remarks>
        /// WHY the second shape had to be added: the whole EditMode run hung on a test that never used
        /// either assertion — it ended on <c>first.Result</c>, where <c>first</c> was a Task the test had
        /// just started. A guard that only knew about ThrowsAsync/CatchAsync declared that file clean.
        /// WHY only locals DECLARED as Task are matched, rather than every <c>.Result</c>: this codebase
        /// is full of result objects with a <c>Result</c> property (tool calls, completions), and flagging
        /// those would bury the real hits under noise until the guard was disabled. A bounded
        /// <c>Wait(timeout)</c> is left alone on purpose — it cannot hang the run.
        /// </remarks>
        private static bool BlocksOnAnIncompleteTask(string body)
        {
            if (Regex.IsMatch(body, @"\b(ThrowsAsync|CatchAsync)\b"))
            {
                return true;
            }

            if (Regex.IsMatch(body, @"\.GetAwaiter\(\)\s*\.\s*GetResult\(\)"))
            {
                return true;
            }

            foreach (Match declaration in Regex.Matches(body, @"\bTask\s*(<[^;=]*>)?\s+(\w+)\s*="))
            {
                string name = declaration.Groups[2].Value;
                string escaped = Regex.Escape(name);

                // WHY a bounded wait clears the whole variable: a test that first proves the task
                // finished within a timeout and only then reads Result cannot hang the run — the
                // timeout is the escape hatch this guard exists to require.
                if (Regex.IsMatch(body, @"\b" + escaped + @"\s*\.\s*Wait\s*\(\s*[^)\s]"))
                {
                    continue;
                }

                if (Regex.IsMatch(body, @"\b" + escaped + @"\s*\.\s*Result\b") ||
                    Regex.IsMatch(body, @"\b" + escaped + @"\s*\.\s*Wait\s*\(\s*\)"))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasLocalSynchronizationContextDetach(string code)
        {
            return Regex.IsMatch(code, @"\[SetUp\]") &&
                   Regex.IsMatch(code, @"SetSynchronizationContext\s*\(\s*null\s*\)");
        }

        /// <summary>Index of the brace closing the one at <paramref name="openBraceIndex"/>, or -1.</summary>
        private static int FindMatchingBrace(string code, int openBraceIndex)
        {
            int depth = 0;
            for (int i = openBraceIndex; i < code.Length; i++)
            {
                if (code[i] == '{')
                {
                    depth++;
                }
                else if (code[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return i;
                    }
                }
            }

            return -1;
        }

        /// <summary>
        /// Blanks out comments and string literals, keeping every newline so offsets stay usable.
        /// </summary>
        /// <remarks>
        /// WHY this exists: without it the guard reports itself. The seeded cases below hold whole
        /// fixtures as verbatim strings, and a WHY comment may legitimately name the very assertion the
        /// guard rejects — neither is code that can deadlock anything.
        /// </remarks>
        private static string StripCommentsAndStringLiterals(string text)
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

                    i = Math.Min(i + 2, text.Length);
                    continue;
                }

                if (c == '@' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    i += 2;
                    while (i < text.Length)
                    {
                        if (text[i] == '"')
                        {
                            if (i + 1 < text.Length && text[i + 1] == '"')
                            {
                                i += 2;
                                continue;
                            }

                            i++;
                            break;
                        }

                        if (text[i] == '\n')
                        {
                            code.Append('\n');
                        }

                        i++;
                    }

                    continue;
                }

                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    i++;
                    while (i < text.Length && text[i] != quote)
                    {
                        if (text[i] == '\\')
                        {
                            i++;
                        }

                        if (i < text.Length && text[i] == '\n')
                        {
                            break;
                        }

                        i++;
                    }

                    i++;
                    continue;
                }

                code.Append(c);
                i++;
            }

            return code.ToString();
        }

        private static string ToAbsolute(string projectRelative)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.GetFullPath(Path.Combine(projectRoot, projectRelative));
        }

        private static string ToRelative(string absolute)
        {
            string projectRoot = Directory.GetParent(Application.dataPath)!.FullName;
            string full = Path.GetFullPath(absolute);
            return full.StartsWith(projectRoot, StringComparison.OrdinalIgnoreCase)
                ? full.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar).Replace('\\', '/')
                : full.Replace('\\', '/');
        }
    }
}
