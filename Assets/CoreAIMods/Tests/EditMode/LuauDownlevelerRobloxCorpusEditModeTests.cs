using System.Threading;
using CoreAI.Infrastructure.Luau;
using Lua;
using Lua.CodeAnalysis.Syntax;
using Lua.Standard;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// End-to-end corpus coverage: realistic Roblox/DevForum-style Luau gameplay scripts run through
    /// the downleveler and the output must parse under the bundled Lua-CSharp (Lua 5.2) VM.
    /// The snippets are original, written in the typical tutorial style — no Roblox code is copied.
    /// </summary>
    [TestFixture]
    public sealed class LuauDownlevelerRobloxCorpusEditModeTests
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

        /// <summary>
        /// Syntax gate through the real VM pipeline: only <see cref="LuaParseException"/> fails the
        /// test. Runtime errors are expected — the corpus references Roblox globals (game, script,
        /// task) that do not exist here, and every script faults fast on them, so nothing loops.
        /// WHY: the standalone LuaSyntaxTree.Parse API rejects chunks that begin with a comment
        /// (library quirk); DoStringAsync is the path mods actually go through.
        /// </summary>
        static void AssertParsesUnderLuaCs(string lua)
        {
            LuaState state = LuaState.Create();
            state.OpenBasicLibrary();
            state.OpenMathLibrary();
            state.OpenStringLibrary();
            state.OpenTableLibrary();
            try
            {
                state.DoStringAsync(lua, "corpus").GetAwaiter().GetResult();
            }
            catch (LuaParseException ex)
            {
                Assert.Fail("Downleveled output must parse as Lua 5.2: " + ex.Message + "\n" + lua);
            }
            catch (LuaRuntimeException)
            {
            }
        }

        static DownlevelResult AssertDownlevelsAndParses(string luau)
        {
            DownlevelResult result = LuauDownleveler.Process(luau, "corpus");
            var sb = new System.Text.StringBuilder();
            foreach (DownlevelDiagnostic d in result.Diagnostics)
            {
                sb.Append(d).Append('\n');
            }

            Assert.IsFalse(result.HasErrors, "Downlevel errors:\n" + sb);
            AssertParsesUnderLuaCs(result.LuaSource);
            return result;
        }

        [Test]
        public void SanityCheck_LuauConstructs_DoNotParseWithoutDownleveling()
        {
            Assert.Throws<LuaParseException>(() => LuaSyntaxTree.Parse("local x: number = 5", "sanity"));
            Assert.Throws<LuaParseException>(() => LuaSyntaxTree.Parse("x += 1", "sanity"));
            Assert.Throws<LuaParseException>(() => LuaSyntaxTree.Parse("for i = 1, 3 do continue end", "sanity"));
        }

        [Test]
        public void KillBrick_DownlevelsAndParses()
        {
            string luau =
                "-- Classic kill brick: drop this script into any Part\n" +
                "local part = script.Parent\n" +
                "local DAMAGE: number = 100\n" +
                "\n" +
                "local function onTouched(hit: BasePart)\n" +
                "\tlocal humanoid = hit.Parent:FindFirstChild(\"Humanoid\")\n" +
                "\tif humanoid then\n" +
                "\t\thumanoid.Health -= DAMAGE\n" +
                "\tend\n" +
                "end\n" +
                "\n" +
                "part.Touched:Connect(onTouched)\n";
            DownlevelResult result = AssertDownlevelsAndParses(luau);
            Assert.IsTrue(result.Changed);
        }

        [Test]
        public void LeaderstatsSetup_DownlevelsAndParses()
        {
            string luau =
                "type PlayerStats = { points: number, wins: number }\n" +
                "\n" +
                "local Players = game:GetService(\"Players\")\n" +
                "\n" +
                "Players.PlayerAdded:Connect(function(player: Player)\n" +
                "\tlocal leaderstats = Instance.new(\"Folder\")\n" +
                "\tleaderstats.Name = \"leaderstats\"\n" +
                "\tleaderstats.Parent = player\n" +
                "\n" +
                "\tlocal points = Instance.new(\"IntValue\")\n" +
                "\tpoints.Name = \"Points\"\n" +
                "\tpoints.Value = 0\n" +
                "\tpoints.Parent = leaderstats\n" +
                "\n" +
                "\tprint(`{player.Name} joined the game!`)\n" +
                "end)\n";
            DownlevelResult result = AssertDownlevelsAndParses(luau);
            Assert.IsTrue(result.Changed);
        }

        [Test]
        public void PartSpawnerLoop_DownlevelsAndParses()
        {
            string luau =
                "local SPAWN_LIMIT: number = 25\n" +
                "local spawned = 0\n" +
                "\n" +
                "while true do\n" +
                "\ttask.wait(1)\n" +
                "\tif spawned >= SPAWN_LIMIT then\n" +
                "\t\tcontinue\n" +
                "\tend\n" +
                "\tlocal part = Instance.new(\"Part\")\n" +
                "\tpart.Position = Vector3.new(math.random(-50, 50), 30, math.random(-50, 50))\n" +
                "\tpart.BrickColor = if spawned % 2 == 0 then BrickColor.Red() else BrickColor.Blue()\n" +
                "\tpart.Parent = workspace\n" +
                "\tspawned += 1\n" +
                "end\n";
            DownlevelResult result = AssertDownlevelsAndParses(luau);
            Assert.IsTrue(result.Changed);
        }

        [Test]
        public void TouchedDoorWithGridSnap_DownlevelsAndParses()
        {
            string luau =
                "local GRID_SIZE = 4\n" +
                "\n" +
                "local function snapToGrid(value: number): number\n" +
                "\treturn value // GRID_SIZE * GRID_SIZE\n" +
                "end\n" +
                "\n" +
                "local door = workspace:WaitForChild(\"Door\")\n" +
                "local openCount = 0\n" +
                "\n" +
                "door.Touched:Connect(function(hit)\n" +
                "\topenCount += 1\n" +
                "\tlocal goal = {}\n" +
                "\tgoal.Transparency = if openCount > 3 then 1 else 0.5\n" +
                "\tprint(`door opened {openCount} times, snapped x = {snapToGrid(door.Position.X)}`)\n" +
                "end)\n";
            DownlevelResult result = AssertDownlevelsAndParses(luau);
            Assert.IsTrue(result.Changed);
        }

        [Test]
        public void TeamAssigner_DownlevelsAndParses()
        {
            string luau =
                "local Teams = game:GetService(\"Teams\")\n" +
                "local Players = game:GetService(\"Players\")\n" +
                "\n" +
                "local function assignTeam(player: Player)\n" +
                "\tlocal smallest: Team? = nil\n" +
                "\tlocal smallestCount = math.huge\n" +
                "\tfor _, team in pairs(Teams:GetTeams()) do\n" +
                "\t\tlocal count = #team:GetPlayers()\n" +
                "\t\tif count >= smallestCount then\n" +
                "\t\t\tcontinue\n" +
                "\t\tend\n" +
                "\t\tsmallestCount = count\n" +
                "\t\tsmallest = team\n" +
                "\tend\n" +
                "\tif smallest then\n" +
                "\t\tplayer.Team = smallest\n" +
                "\tend\n" +
                "end\n" +
                "\n" +
                "Players.PlayerAdded:Connect(assignTeam)\n";
            DownlevelResult result = AssertDownlevelsAndParses(luau);
            Assert.IsTrue(result.Changed);
        }

        [Test]
        public void RoundTimerWithRepeatContinue_DownlevelsAndParses()
        {
            string luau =
                "export type RoundState = \"waiting\" | \"running\"\n" +
                "\n" +
                "local roundLength: number = 30\n" +
                "local status = \"\"\n" +
                "\n" +
                "repeat\n" +
                "\ttask.wait(1)\n" +
                "\troundLength -= 1\n" +
                "\tif roundLength % 5 ~= 0 then\n" +
                "\t\tcontinue\n" +
                "\tend\n" +
                "\tstatus ..= `tick {roundLength} `\n" +
                "until roundLength <= 0\n" +
                "\n" +
                "print(status)\n";
            DownlevelResult result = AssertDownlevelsAndParses(luau);
            Assert.IsTrue(result.Changed);
        }

        [Test]
        public void CombinedConstructScript_DownlevelsAndParses()
        {
            string luau =
                "type Config = { speed: number, name: string }\n" +
                "export type Mode = \"easy\" | \"hard\"\n" +
                "\n" +
                "local config: Config = { speed = 16, name = \"test\" }\n" +
                "local total: number = 0\n" +
                "local report = \"\"\n" +
                "\n" +
                "local function score<T>(items: {T}, weight: number): number\n" +
                "\tlocal sum = 0\n" +
                "\tfor index: number, item in ipairs(items) do\n" +
                "\t\tif index % 2 == 0 then\n" +
                "\t\t\tcontinue\n" +
                "\t\tend\n" +
                "\t\tsum += weight // 2\n" +
                "\tend\n" +
                "\treturn sum :: number\n" +
                "end\n" +
                "\n" +
                "total = score({1, 2, 3}, 10)\n" +
                "total //= 2\n" +
                "report ..= `total = {total}, mode = {if total > 5 then \"hard\" else \"easy\"}`\n" +
                "print(report)\n";
            DownlevelResult result = AssertDownlevelsAndParses(luau);
            Assert.IsTrue(result.Changed);
        }

        [Test]
        public void PlainLuaTutorialScript_ParsesAndPassesThrough()
        {
            string lua =
                "local counter = 0\n" +
                "local function bump(amount)\n" +
                "\tcounter = counter + amount\n" +
                "\treturn counter\n" +
                "end\n" +
                "for i = 1, 5 do\n" +
                "\tbump(i)\n" +
                "end\n" +
                "print(string.format(\"counter = %d\", counter))\n";
            DownlevelResult result = LuauDownleveler.Process(lua);
            Assert.IsFalse(result.Changed);
            Assert.IsFalse(result.HasErrors);
            AssertParsesUnderLuaCs(result.LuaSource);
        }
    }
}
