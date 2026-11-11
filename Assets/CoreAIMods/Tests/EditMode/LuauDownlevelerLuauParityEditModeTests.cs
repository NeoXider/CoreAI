using System.Text;
using System.Threading;
using CoreAI.Infrastructure.Luau;
using Lua;
using Lua.Standard;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The Luau syntax MVP13 added to the downlevel, each construct run on the real VM with its twin (the same text
    /// in a string or a comment stays as it is) and its author line kept: a type assertion on a call or <c>...</c>
    /// keeps one value, as in Luau (it used to keep every value, a wrong program); attribute lists
    /// (<c>@[deprecated]</c>) and attributes on function expressions; and type functions, which only the type
    /// checker runs.
    /// </summary>
    [TestFixture]
    public sealed class LuauDownlevelerLuauParityEditModeTests
    {
        private SynchronizationContext _savedContext;

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

        private static DownlevelResult ProcessOk(string luau)
        {
            DownlevelResult result = LuauDownleveler.Process(luau, "exec");
            StringBuilder sb = new();
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                sb.Append(d).Append('\n');
            }

            Assert.IsFalse(result.HasErrors, "Downlevel errors:\n" + sb);
            return result;
        }

        private static string Run(string luau)
        {
            DownlevelResult result = ProcessOk(luau);
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenMathLibrary();
            state.OpenStringLibrary();
            state.OpenTableLibrary();
            LuaValue[] values = state.DoStringAsync(result.LuaSource).GetAwaiter().GetResult();
            StringBuilder sb = new();
            for (int i = 0; i < values.Length; i++)
            {
                sb.Append(i == 0 ? "" : "|").Append(values[i].ToString());
            }

            return sb.ToString();
        }

        private static int LineCount(string text)
        {
            int lines = 1;
            foreach (char c in text)
            {
                if (c == '\n')
                {
                    lines++;
                }
            }

            return lines;
        }

        // ---- type assertions keep one value -------------------------------------------------------------

        [Test]
        public void CastOfACall_KeepsOnlyTheFirstValue_AsLuauDoes()
        {
            Assert.AreEqual("1", Run("local function f() return 1, 2 end\nreturn select('#', f() :: any)"));
            Assert.AreEqual("1|nil", Run("local function f() return 1, 2 end\nlocal a, b = f() :: number\nreturn a, b"));
            Assert.AreEqual("1", Run("local o = {m = function(self) return 1, 2 end}\nreturn select('#', o:m() :: any)"),
                "a method call is a call too");
        }

        [Test]
        public void CastOfVarargs_KeepsOnlyTheFirstValue()
        {
            Assert.AreEqual("1", Run("local function g(...) return select('#', ... :: any) end\nreturn g(1, 2, 3)"));
        }

        [Test]
        public void CastOfASingleValue_IsLeftAlone_NegativeTwin()
        {
            Assert.AreEqual("local n = (2 ) + 1", ProcessOk("local n = (2 :: number) + 1").LuaSource,
                "no parentheses are added where nothing can be truncated");
            Assert.AreEqual("2", Run("local function f() return 1, 2 end\nreturn select('#', f())"),
                "a call without an assertion still keeps every value");
            Assert.AreEqual("return 'f() :: any' -- g() :: any",
                ProcessOk("return 'f() :: any' -- g() :: any").LuaSource, "text in a string or comment is not code");
        }

        // ---- attributes -----------------------------------------------------------------------------------

        [Test]
        public void AttributeLists_AndAttributesOnFunctionExpressions_AreDeleted()
        {
            Assert.AreEqual("42", Run("@[deprecated] function f() return 42 end\nreturn f()"));
            Assert.AreEqual("42", Run("@[deprecated {use = \"g\", reason = \"old\"}]\nlocal function f() return 42 end\nreturn f()"));
            Assert.AreEqual("42", Run("@native @checked function f() return 42 end\nreturn f()"));
            Assert.AreEqual("42", Run("local f = @native function() return 42 end\nreturn f()"));
            Assert.AreEqual("42", Run("local t = { run = @[native] function() return 42 end }\nreturn t.run()"));
        }

        [Test]
        public void Attributes_KeepTheAuthorsLines_AndTextInStringsStays()
        {
            string luau = "@[deprecated {\n  use = 'g'\n}]\nlocal function f() error('line 4') end\nreturn f";
            DownlevelResult result = ProcessOk(luau);
            Assert.AreEqual(LineCount(luau), LineCount(result.LuaSource), "a multi-line attribute keeps its newlines");
            Assert.AreEqual("return '@[deprecated] @native'", ProcessOk("return '@[deprecated] @native'").LuaSource);
        }

        [Test]
        public void AnAttributeWithoutAFunction_IsALoadErrorNamingTheLine()
        {
            DownlevelResult result = LuauDownleveler.Process("local x = 1\nlocal y = @native 5", "exec");
            Assert.IsTrue(result.HasErrors);
            bool found = false;
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                found |= d.Severity == DownlevelSeverity.Error && d.Line == 2;
            }

            Assert.IsTrue(found, "the error names the author's line");
        }

        // ---- type functions -------------------------------------------------------------------------------

        [Test]
        public void TypeFunctions_AreDeleted_LinesKept()
        {
            string luau = "type function identity(t)\n  local n = 0\n  n += 1\n  return t\nend\nexport type function keep(t) return t end\nreturn 'ok', 1";
            DownlevelResult result = ProcessOk(luau);
            Assert.AreEqual(LineCount(luau), LineCount(result.LuaSource));
            Assert.AreEqual("ok|1", Run(luau));
        }

        [Test]
        public void TypeAndFunctionAsNames_AreNotTypeFunctions_NegativeTwin()
        {
            Assert.AreEqual("5", Run("local type = function(x) return x end\nreturn type(5)"));
            Assert.AreEqual("function", Run("local t = {}\nfunction t.type() end\nreturn _G.type(t.type)"));
            Assert.AreEqual("return 'type function f() end'", ProcessOk("return 'type function f() end'").LuaSource);
        }
    }
}
