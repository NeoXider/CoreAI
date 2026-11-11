using System.Collections.Generic;
using System.Text;
using CoreAI.Infrastructure.Luau;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The export lint of MVP13 (RT6, RT7): its definition of done is that it flags every construct of a seeded list
    /// of CoreAI-only and Lua 5.2-only constructs, each with its stable code, line and column; each case has a twin
    /// that must stay clean (the same text in a string or a comment, the same name as a local or as a global the
    /// script defines, the Roblox spelling of the same idea).
    /// </summary>
    [TestFixture]
    public sealed class LuauExportLintEditModeTests
    {
        private static string Describe(IReadOnlyList<LuauExportFinding> findings)
        {
            StringBuilder sb = new();
            foreach (LuauExportFinding finding in findings)
            {
                sb.Append(finding).Append('\n');
            }

            return sb.Length == 0 ? "(no findings)" : sb.ToString();
        }

        private static List<string> Codes(string source)
        {
            List<string> codes = new();
            foreach (LuauExportFinding finding in LuauExportLint.Check(source))
            {
                codes.Add(finding.Code + ":" + finding.Subject);
            }

            return codes;
        }

        /// <summary>The seeded list: a script, and the code and subject the lint must report for it.</summary>
        private static readonly string[][] Seeded =
        {
            new[] { "goto done\n::done::", "LX101:goto" },
            new[] { "goto done\n::done::", "LX102:label" },
            new[] { "local _ENV = {}", "LX103:_ENV" },
            new[] { "print(_ENV)", "LX103:_ENV" },
            new[] { "local x = 0x1p4", "LX104:0x1p4" },
            new[] { "local y = 0x1.8", "LX104:0x1.8" },
            new[] { "hooks_every(1, function() end)", "LX201:hooks_every" },
            new[] { "hooks_on('tick', print)", "LX201:hooks_on" },
            new[] { "report('done')", "LX201:report" },
            new[] { "coreai_world_spawn('Cube')", "LX201:coreai_world_spawn" },
            new[] { "local p = coreai_world_find('x')", "LX201:coreai_world_find" },
            new[] { "unity_find('Main Camera')", "LX201:unity_find" },
            new[] { "mods_export('api', {})", "LX201:mods_export" },
            new[] { "local v = store_get('k')", "LX201:store_get" },
            new[] { "events_emit('e')", "LX201:events_emit" },
            new[] { "if input_key_down('E') then end", "LX201:input_key_down" },
            new[] { "logic_define('x', {})", "LX201:logic_define" },
            new[] { "local t = time_now()", "LX201:time_now" },
            new[] { "camera_set_cframe(CFrame.new())", "LX201:camera_set_cframe" },
            new[] { "local id = mod_id()", "LX201:mod_id" },
            new[] { "UserInputService.InputBegan:Connect(print)", "LX201:UserInputService" },
            new[] { "for k, v in __coreai_luau_iter({}) do end", "LX201:__coreai_luau_iter" },
            new[] { "local ai = game:GetService(\"AIService\")", "LX301:AIService" },
            new[] { "game:GetService('UxmlService'):CreateScreen('hud', '<ui:Label/>')", "LX301:UxmlService" },
            new[] { "local ai = game:FindService(\"AIService\")", "LX301:AIService" },
            new[] { "local ui = game.UxmlService", "LX301:UxmlService" },
            new[] { "game:GetService(\"ScriptContext\"):SetTimeout(5)", "LX303:ScriptContext:SetTimeout" },
            new[] { "task._waitForChildBridge()", "LX203:task._waitForChildBridge" },
            new[] { "function string.trim(s) return s end", "LX203:string.trim" },
            new[] { "local run = os.execute", "LX203:os.execute" },
            new[] { "-- @coreai capabilities: WorldEdit\nprint(1)", "LX401:capabilities" },
            new[] { "--[[@coreai\nid: door\ncapabilities: Read\n]]\nprint(1)", "LX401:id" },
            new[] { "--[[@coreai\nid: door\ncapabilities: Read\n]]\nprint(1)", "LX401:capabilities" }
        };

        [Test]
        public void TheLintFlagsEveryConstructOfTheSeededList()
        {
            List<string> missed = new();
            foreach (string[] seed in Seeded)
            {
                if (!Codes(seed[0]).Contains(seed[1]))
                {
                    missed.Add(seed[1] + " in `" + seed[0] + "`: " + Describe(LuauExportLint.Check(seed[0])));
                }
            }

            Assert.IsEmpty(missed, "the definition of done is 100% of the seeded list");
        }

        [Test]
        public void EverySeededConstruct_IsAnError_ButTheHeaderKeys()
        {
            foreach (string[] seed in Seeded)
            {
                foreach (LuauExportFinding finding in LuauExportLint.Check(seed[0]))
                {
                    if (finding.Code + ":" + finding.Subject == seed[1])
                    {
                        LuauExportSeverity expected = finding.Code == "LX401"
                            ? LuauExportSeverity.Warning
                            : LuauExportSeverity.Error;
                        Assert.AreEqual(expected, finding.Severity, seed[1]);
                    }
                }
            }
        }

        [Test]
        public void Findings_CarryTheAuthorsLineAndColumn()
        {
            IReadOnlyList<LuauExportFinding> findings = LuauExportLint.Check(
                "local x: number = 1\nlocal s = `{x}`\n  hooks_on('x', print)\nlocal ai = game:GetService('AIService')");
            Assert.AreEqual(2, findings.Count, Describe(findings));
            Assert.AreEqual("LX201", findings[0].Code);
            Assert.AreEqual(3, findings[0].Line);
            Assert.AreEqual(3, findings[0].Column, "type annotations and interpolation before it change no position");
            Assert.AreEqual("LX301", findings[1].Code);
            Assert.AreEqual(4, findings[1].Line);
            Assert.AreEqual(28, findings[1].Column, "the service name's own position");
        }

        [TestCase("local hooks_every = function() end\nhooks_every()")]
        [TestCase("function report(x) end\nreport(1)")]
        [TestCase("store_get = function() return 1 end\nlocal v = store_get()")]
        [TestCase("print('hooks_every goto _ENV AIService 0x1p4 coreai_world_spawn')")]
        [TestCase("-- goto done\n--[[ unity_find() ::label:: ]]\nprint(1)")]
        [TestCase("local s = `report {1} hooks_on`")]
        [TestCase("local players = game:GetService('Players')\nlocal rs = game:GetService(\"RunService\")")]
        [TestCase("task.wait(1)\nlocal p = string.split('a,b', ',')\nlocal i = table.find({1}, 1)\nlocal c = utf8.char(65)")]
        [TestCase("local goto = 5\nprint(goto)")]
        [TestCase("local T = {}\nfunction T:m() return self end\nT:m()")]
        [TestCase("local function f(a: number, ...): number return a end\nlocal n = 0\nn += 1\nfor i, v in {1, 2} do continue end")]
        [TestCase("local x = 0x10 + 0b101 + 1_000")]
        [TestCase("local ScriptContext = game:GetService('ScriptContext')\nScriptContext.Error:Connect(print)")]
        [TestCase("local function f(_ENVIRONMENT) return _ENVIRONMENT end")]
        [TestCase("local cs = game:GetService('CollectionService')\nlocal ok = pcall(function() return workspace.Gravity end)")]
        [TestCase("print('-- @coreai capabilities: All')")]
        public void TheTwins_StayClean(string source)
        {
            IReadOnlyList<LuauExportFinding> findings = LuauExportLint.Check(source);
            Assert.IsEmpty(findings, Describe(findings));
        }

        [Test]
        public void AGlobalNeitherRobloxNorTheScriptDefines_IsAWarning()
        {
            IReadOnlyList<LuauExportFinding> findings = LuauExportLint.Check("print(undefinedThing)");
            Assert.AreEqual(1, findings.Count, Describe(findings));
            Assert.AreEqual("LX202", findings[0].Code);
            Assert.AreEqual(LuauExportSeverity.Warning, findings[0].Severity);
            Assert.IsEmpty(LuauExportLint.Check("undefinedThing = 1\nprint(undefinedThing)"), "defined by the script");
        }

        [Test]
        public void AServiceRobloxDoesNotHave_IsAWarning_ARobloxServiceIsNot()
        {
            IReadOnlyList<LuauExportFinding> findings = LuauExportLint.Check("game:GetService('NotAService')");
            Assert.AreEqual(1, findings.Count, Describe(findings));
            Assert.AreEqual("LX302", findings[0].Code);
            Assert.IsEmpty(LuauExportLint.Check("game:GetService('TextChatService')"));
        }

        [Test]
        public void AScriptThatDoesNotParse_IsOneSyntaxFinding_AndTheLintNeverThrows()
        {
            IReadOnlyList<LuauExportFinding> findings = LuauExportLint.Check("local = 1");
            Assert.AreEqual(1, findings.Count, Describe(findings));
            Assert.AreEqual("LX001", findings[0].Code);
            Assert.AreEqual(1, findings[0].Line);
            Assert.IsEmpty(LuauExportLint.Check(null), "null reads as an empty script");
            Assert.IsEmpty(LuauExportLint.Check(""));
        }

        [Test]
        public void ADerivedSurface_FlagsTheNamesItWasGiven_AndNotTheRobloxOnes()
        {
            LuauExportLintSurface surface = LuauExportLintSurface.FromEnvironment(
                new[] { "print", "Vector3", "spawn_npc" }, new[] { "Players", "QuestService" });
            List<string> codes = new();
            foreach (LuauExportFinding finding in LuauExportLint.Check(
                         "spawn_npc()\nprint(Vector3.new())\nlocal q = game:GetService('QuestService')", surface))
            {
                codes.Add(finding.Code + ":" + finding.Subject);
            }

            CollectionAssert.AreEquivalent(new[] { "LX201:spawn_npc", "LX301:QuestService" }, codes);
            Assert.IsTrue(LuauExportLintSurface.IsRobloxGlobal("Vector3"));
            Assert.IsFalse(LuauExportLintSurface.Default.IsCoreAiGlobal("print"), "a Roblox name is never CoreAI's");
        }
    }
}
