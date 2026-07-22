using System.Threading;
using CoreAI.Chat;
using Lua;
using Lua.Standard;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Validates the built-in chat example prompts (<see cref="CoreAiChatExamples"/>): the list is non-empty,
    /// every entry has a title and message, and the embedded Tetris / arena Lua is syntactically valid on the
    /// real Lua-CSharp VM (a parse failure fails the test; runtime faults on absent host globals are expected).
    /// </summary>
    [TestFixture]
    public sealed class CoreAiChatExamplesEditModeTests
    {
        private SynchronizationContext _savedContext;

        // The Lua-CSharp VM is driven synchronously via GetAwaiter().GetResult(); detaching Unity's main-thread
        // SynchronizationContext lets VM continuations complete on the thread pool instead of deadlocking.
        [SetUp]
        public void DetachSynchronizationContext()
        {
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void RestoreSynchronizationContext()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        [Test]
        public void All_IsNonEmpty_WithTitledMessages()
        {
            Assert.IsNotEmpty(CoreAiChatExamples.All);
            foreach (CoreAiChatExample example in CoreAiChatExamples.All)
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(example.Id), "id must be set");
                Assert.IsFalse(string.IsNullOrWhiteSpace(example.Title), "title must be set: " + example.Id);
                Assert.IsFalse(string.IsNullOrWhiteSpace(example.Message), "message must be set: " + example.Id);
            }
        }

        [Test]
        public void TetrisExample_LuaParses()
        {
            AssertExampleLuaParses("tetris");
        }

        [Test]
        public void ClickerExample_LuaParses()
        {
            AssertExampleLuaParses("clicker");
        }

        [Test]
        public void ArenaExample_LuaParses()
        {
            AssertExampleLuaParses("arena");
        }

        private static void AssertExampleLuaParses(string exampleId)
        {
            string lua = ExtractFencedLua(FindExample(exampleId).Message);
            Assert.IsFalse(string.IsNullOrWhiteSpace(lua), "no ```lua block found in example " + exampleId);
            AssertParsesUnderLuaCs(lua);
        }

        private static CoreAiChatExample FindExample(string exampleId)
        {
            foreach (CoreAiChatExample example in CoreAiChatExamples.All)
            {
                if (example.Id == exampleId)
                {
                    return example;
                }
            }

            Assert.Fail("example not found: " + exampleId);
            return default;
        }

        private static string ExtractFencedLua(string message)
        {
            const string open = "```lua";
            int openIndex = message.IndexOf(open, System.StringComparison.Ordinal);
            if (openIndex < 0)
            {
                return null;
            }

            int codeStart = message.IndexOf('\n', openIndex);
            if (codeStart < 0)
            {
                return null;
            }

            codeStart += 1;
            int closeIndex = message.LastIndexOf("```", System.StringComparison.Ordinal);
            if (closeIndex <= codeStart)
            {
                return null;
            }

            return message.Substring(codeStart, closeIndex - codeStart);
        }

        // Syntax gate through the real VM: only a parse error fails. Runtime faults are expected because the
        // examples call host globals (coreai_world_spawn, hooks_every, ...) that are not registered here.
        private static void AssertParsesUnderLuaCs(string lua)
        {
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenMathLibrary();
            state.OpenStringLibrary();
            state.OpenTableLibrary();
            try
            {
                state.DoStringAsync(lua, "example").GetAwaiter().GetResult();
            }
            catch (LuaParseException ex)
            {
                Assert.Fail("Example Lua must parse: " + ex.Message + "\n" + lua);
            }
            catch (LuaRuntimeException)
            {
                // Expected: absent host globals fault at runtime after a successful parse.
            }
        }
    }
}
