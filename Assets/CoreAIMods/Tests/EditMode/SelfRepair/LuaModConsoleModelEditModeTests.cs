using CoreAI.Ai.Logging;
using NUnit.Framework;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;

namespace CoreAI.Tests.EditMode.SelfRepair
{
    /// <summary>
    /// The in-game console's engine-free model: a bounded ring (a mod printing every frame neither grows it nor
    /// allocates), newest at the bottom, filters by mod and level, a scroll window, and row kinds for prints,
    /// warnings, errors with script and line, the load timeline and the self-repair lines.
    /// </summary>
    public sealed class LuaModConsoleModelEditModeTests
    {
        [Test]
        public void Ring_IsBounded_AndKeepsTheNewestAtTheBottom()
        {
            LuaModConsoleModel model = new(16);
            for (int index = 1; index <= 100; index++)
            {
                model.Append(Entry(index, "spam", LuaLogLevel.Print, "frame " + index));
            }

            LuaModConsoleRow[] rows = new LuaModConsoleRow[8];
            int written = model.CopyVisible(rows, out int matching);

            Assert.AreEqual(16, model.Count);
            Assert.AreEqual(84L, model.Dropped);
            Assert.AreEqual(16, matching);
            Assert.AreEqual(8, written);
            Assert.AreEqual("[spam] frame 93", rows[0].Text);
            Assert.AreEqual("[spam] frame 100", rows[7].Text, "newest last");
        }

        [Test]
        public void Append_AllocatesNothing_OnceWarm()
        {
            LuaModConsoleModel model = new(64);
            LuaLogEntry entry = Entry(1, "spam", LuaLogLevel.Print, "every frame");
            model.Append(entry);

            Assert.That(() => model.Append(entry), Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void Filters_ByModAndLevel()
        {
            LuaModConsoleModel model = new(32);
            model.Append(Entry(1, "a", LuaLogLevel.Print, "a print"));
            model.Append(Entry(2, "b", LuaLogLevel.Warn, "b warn"));
            model.Append(Entry(3, "a", LuaLogLevel.RuntimeError, "a error"));
            LuaModConsoleRow[] rows = new LuaModConsoleRow[10];

            model.SetModFilter("a");
            Assert.AreEqual(2, model.CopyVisible(rows, out _));
            model.CycleLevelFilter();
            Assert.AreEqual(LuaModConsoleLevelFilter.WarningsAndErrors, model.LevelFilter);
            Assert.AreEqual(1, model.CopyVisible(rows, out _));
            Assert.AreEqual("[a] error: a error", rows[0].Text);
            model.SetModFilter("");
            Assert.AreEqual(2, model.CopyVisible(rows, out _));
            model.CycleLevelFilter();
            Assert.AreEqual(LuaModConsoleLevelFilter.Errors, model.LevelFilter);
            Assert.AreEqual(1, model.CopyVisible(rows, out _));
            model.CycleLevelFilter();
            Assert.AreEqual(LuaModConsoleLevelFilter.All, model.LevelFilter);
        }

        [Test]
        public void ModFilter_CyclesThroughTheModsSeen_ThenBackToAll()
        {
            LuaModConsoleModel model = new(8);
            model.Append(Entry(1, "a", LuaLogLevel.Print, "x"));
            model.Append(Entry(2, "b", LuaLogLevel.Print, "y"));

            model.CycleModFilter();
            Assert.AreEqual("a", model.ModFilter);
            Assert.AreEqual("Mod: a", model.ModFilterLabel);
            model.CycleModFilter();
            Assert.AreEqual("b", model.ModFilter);
            model.CycleModFilter();
            Assert.AreEqual("", model.ModFilter);
            Assert.AreEqual("All mods", model.ModFilterLabel);
        }

        [Test]
        public void Scroll_ShowsOlderRows_AndStaysPutWhileNewOnesArrive()
        {
            LuaModConsoleModel model = new(64);
            for (int index = 1; index <= 30; index++)
            {
                model.Append(Entry(index, "m", LuaLogLevel.Print, "line " + index));
            }

            LuaModConsoleRow[] rows = new LuaModConsoleRow[5];
            model.Scroll(10);
            model.CopyVisible(rows, out _);
            Assert.AreEqual("[m] line 20", rows[4].Text);
            model.Append(Entry(31, "m", LuaLogLevel.Print, "line 31"));
            model.CopyVisible(rows, out _);
            Assert.AreEqual("[m] line 20", rows[4].Text, "a new line does not move a scrolled-back window");
            model.ScrollToNewest();
            model.CopyVisible(rows, out _);
            Assert.AreEqual("[m] line 31", rows[4].Text);
            model.Scroll(1000);
            Assert.AreEqual(30, model.ScrollBack, "clamped to the oldest row");
        }

        [Test]
        public void Rows_NameTheirKind_AndTheScriptLine()
        {
            Assert.AreEqual(LuaModConsoleRowKind.Lifecycle, LuaModConsoleModel.KindOf(new LuaLogEntry
            {
                ModId = "m", Message = "reloaded", ScriptName = LuaModLifecycleLog.LifecycleScriptName
            }));
            Assert.AreEqual(LuaModConsoleRowKind.Repair, LuaModConsoleModel.KindOf(new LuaLogEntry
            {
                ModId = "m", Message = "AI is fixing", ScriptName = LuaModConsoleModel.RepairScriptName
            }));
            Assert.AreEqual(LuaModConsoleRowKind.Warning, LuaModConsoleModel.KindOf(new LuaLogEntry
            {
                ModId = "m", Level = LuaLogLevel.Warn, Message = "failing", ScriptName = LuaModConsoleModel.RepairScriptName
            }));
            Assert.AreEqual("[m] error: tower.lua:12 boom", LuaModConsoleModel.FormatRow(new LuaLogEntry
            {
                ModId = "m", Level = LuaLogLevel.RuntimeError, Message = "boom", ScriptName = "tower.lua", Line = 12
            }));
            Assert.AreEqual("[m] reloaded", LuaModConsoleModel.FormatRow(new LuaLogEntry
            {
                ModId = "m", Message = "reloaded", ScriptName = LuaModLifecycleLog.LifecycleScriptName
            }));
            Assert.AreEqual("[m] repair: AI is fixing", LuaModConsoleModel.FormatRow(new LuaLogEntry
            {
                ModId = "m", Message = "AI is fixing", ScriptName = LuaModConsoleModel.RepairScriptName
            }));
        }

        [Test]
        public void LongAndMultiLineMessages_AreClippedToOneRow()
        {
            string text = LuaModConsoleModel.FormatRow(new LuaLogEntry
            {
                ModId = "m", Message = "a\nb" + new string('x', 500)
            });

            StringAssert.DoesNotContain("\n", text);
            Assert.LessOrEqual(text.Length, LuaModConsoleModel.MaximumMessageCharacters + 12);
            StringAssert.EndsWith("...", text);
        }

        [Test]
        public void Attach_CopiesTheNewestEntries_AndFollowsUntilDetached()
        {
            LuaLogService logs = new();
            logs.Append(new LuaLogEntry { ModId = "m", Message = "before" });
            LuaModConsoleModel model = new(8);

            model.Attach(logs);
            logs.Append(new LuaLogEntry { ModId = "m", Message = "after" });
            model.Detach();
            logs.Append(new LuaLogEntry { ModId = "m", Message = "detached" });

            LuaModConsoleRow[] rows = new LuaModConsoleRow[8];
            Assert.AreEqual(2, model.CopyVisible(rows, out _));
            Assert.AreEqual("[m] before", rows[0].Text);
            Assert.AreEqual("[m] after", rows[1].Text);
        }

        [Test]
        public void Toggle_AndEveryChange_MoveTheVersion()
        {
            LuaModConsoleModel model = new(8);
            int version = model.Version;

            Assert.IsTrue(model.Toggle());
            Assert.Greater(model.Version, version);
            Assert.IsFalse(model.Toggle());
            model.SetOpen(false);
            int closed = model.Version;
            model.SetOpen(false);
            Assert.AreEqual(closed, model.Version, "no change, no redraw");
        }

        [Test]
        public void Clear_ForgetsTheRows_NotTheModLog()
        {
            LuaLogService logs = new();
            LuaModConsoleModel model = new(8);
            model.Attach(logs);
            logs.Append(new LuaLogEntry { ModId = "m", Message = "x" });

            model.Clear();

            Assert.AreEqual(0, model.Count);
            Assert.AreEqual(1, logs.Query(new LuaLogQuery()).Count);
            model.Dispose();
        }

        private static LuaLogEntry Entry(long sequence, string modId, LuaLogLevel level, string message)
        {
            return new LuaLogEntry { Sequence = sequence, ModId = modId, Level = level, Message = message };
        }
    }
}
