using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Logging;
using CoreAI.Mods.WorldPackages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
#if COREAI_LLM
using CoreAI.Infrastructure.Llm;
using MEAI = Microsoft.Extensions.AI;
#endif

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// How the model reads and changes an existing mod through <c>manage_mods</c>: <c>get_source</c> returns pages of
    /// whole lines that each fit the general tool-result cap (<see cref="ICoreAISettings.MaxToolResultChars"/>, which
    /// <c>ToolExecutionPolicy</c> applies to the serialized result), so following <c>next_from_line</c> reads a mod of
    /// any size whole; <c>edit</c> replaces an exact piece of the source and reloads the mod like <c>reload</c>.
    /// Engine-free, so the portable Lua tier runs it.
    /// </summary>
    public sealed class LuaModsLlmToolSourceEditingEditModeTests
    {
        private const string BlockWorldModsFolder = "Assets/CoreAIMods/Runtime/Resources/CoreAIMods/BlockWorld";

        private SynchronizationContext _savedContext;

        [SetUp]
        public void SetUp()
        {
            // WHY: the Lua-CSharp runtime bridges its async VM to a synchronous call site; without a context its
            // WHY: continuations complete on the thread pool instead of deadlocking the blocked Unity main thread.
            _savedContext = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
        }

        [TearDown]
        public void TearDown()
        {
            SynchronizationContext.SetSynchronizationContext(_savedContext);
        }

        private static int DefaultCap => new CoreAISettingsOptions().MaxToolResultChars;

        private static CoreAISettingsOptions Settings(int maxToolResultChars)
        {
            return new CoreAISettingsOptions
            {
                MaxToolResultChars = maxToolResultChars,
                LogToolCalls = false,
                LogToolCallResults = false
            };
        }

        private static LuaModsLlmTool Tool(ILuaModRuntime runtime, ICoreAISettings settings,
            bool allowManagement = true, IActorIdentityProvider identity = null,
            IConfirmedWorldMutationGate gate = null, LuaGenerationRateLimiter rateLimiter = null)
        {
            return new LuaModsLlmTool(runtime, settings, NullLog.Instance, LuaCapabilities.All, allowManagement,
                identity ?? CoreServicesInstaller.DefaultLocalHostIdentityProvider, BuiltInAgentRoleIds.Programmer,
                gate, rateLimiter);
        }

        private static async Task<JObject> GetSourceAsync(LuaModsLlmTool tool, string modId, int fromLine = 0,
            int lineCount = 0)
        {
            return JObject.Parse(await tool.ExecuteAsync("get_source", modId, from_line: fromLine,
                line_count: lineCount));
        }

        private static async Task<JObject> EditAsync(LuaModsLlmTool tool, string modId, string oldText,
            string newText, bool replaceAll = false)
        {
            return JObject.Parse(await tool.ExecuteAsync("edit", modId, old_text: oldText, new_text: newText,
                replace_all: replaceAll));
        }

        /// <summary>
        /// Reads a mod the way the result tells the model to: the first page without <c>from_line</c>, then every
        /// <c>next_from_line</c> until there is none. Every page must fit <paramref name="cap"/> as serialized.
        /// </summary>
        private static async Task<(string Text, int Pages)> ReadAllPagesAsync(LuaModsLlmTool tool, string modId,
            int cap)
        {
            StringBuilder text = new();
            int pages = 0;
            int fromLine = 0;
            int expectedFirst = 1;
            while (true)
            {
                string json = await tool.ExecuteAsync("get_source", modId, from_line: fromLine);
                pages++;
                if (cap > 0)
                {
                    Assert.LessOrEqual(json.Length, cap,
                        "page " + pages + " of '" + modId + "' would be cut by the general tool-result cap");
                }
                else
                {
                    Assert.LessOrEqual(JObject.Parse(json).Value<string>("data").Length,
                        LuaModsLlmTool.MaxSourceLengthReturned);
                }

                JObject page = JObject.Parse(json);
                Assert.IsTrue(page.Value<bool>("success"), json);
                Assert.AreEqual(expectedFirst, page.Value<int>("first_line"), "pages follow each other");
                Assert.IsNull(page["cut_line"], "no line of this source is longer than one page");
                text.Append(page.Value<string>("data"));
                JToken next = page["next_from_line"];
                if (next == null)
                {
                    Assert.AreEqual(page.Value<int>("total_lines"), page.Value<int>("last_line"));
                    StringAssert.DoesNotContain("NOT the whole source", page.Value<string>("message"));
                    return (text.ToString(), pages);
                }

                Assert.AreEqual(page.Value<int>("last_line") + 1, next.Value<int>());
                StringAssert.Contains("from_line=" + next.Value<int>(), page.Value<string>("message"),
                    "the message says how to ask for the next part");
                fromLine = next.Value<int>();
                expectedFirst = fromLine;
                Assert.Less(pages, 500, "paging must end");
            }
        }

        /// <summary>A mod source of <paramref name="lines"/> lines full of characters JSON escapes.</summary>
        private static string LargeSource(int lines, string newline = "\n")
        {
            StringBuilder source = new();
            source.Append("-- Лёд и \"кирпич\": a source that JSON has to escape\tcolumn").Append(newline);
            source.Append("local SLOTS = {}").Append(newline);
            for (int index = 1; index <= lines; index++)
            {
                source.Append("SLOTS[").Append(index).Append("] = { name = \"slot \\\"").Append(index)
                    .Append("\\\"\", path = \"blocks\\\\").Append(index).Append("\" } -- \"quoted\" \\ note")
                    .Append(newline);
            }

            source.Append("store_set('slots', tostring(#SLOTS))").Append(newline);
            return source.ToString();
        }

        [TestCase(8000)]
        [TestCase(3000)]
        [TestCase(1500)]
        [TestCase(0)]
        public async Task GetSource_AModLargerThanTheCap_IsReadWholeByFollowingNextFromLine(int cap)
        {
            LuaCsModRuntime runtime = new(store: new MemoryModStore());
            string source = LargeSource(900);
            runtime.LoadMod("big", source, persistToStore: false);
            LuaModsLlmTool tool = Tool(runtime, Settings(cap));

            (string text, int pages) = await ReadAllPagesAsync(tool, "big", cap);

            Assert.AreEqual(source, text, "the pages concatenate to the exact source");
            Assert.Greater(pages, 1, "this source does not fit one result");
        }

        [Test]
        public async Task GetSource_EveryShippedBlockWorldMod_IsReadWholeUnderTheDefaultCap_InLfAndCrlf()
        {
            Assume.That(Directory.Exists(BlockWorldModsFolder), "the test runs from the project root");
            string[] files = Directory.GetFiles(BlockWorldModsFolder, "*.lua").OrderBy(path => path).ToArray();
            Assert.GreaterOrEqual(files.Length, 9);
            Dictionary<string, string> sources = new();
            foreach (string file in files)
            {
                string lf = File.ReadAllText(file, Encoding.UTF8).Replace("\r\n", "\n");
                sources[Path.GetFileNameWithoutExtension(file)] = lf;
                sources[Path.GetFileNameWithoutExtension(file) + "_crlf"] = lf.Replace("\n", "\r\n");
            }

            LuaModsLlmTool tool = Tool(SourceOnlyRuntime.Create(sources), Settings(DefaultCap));
            int multiPage = 0;
            foreach (KeyValuePair<string, string> mod in sources)
            {
                (string text, int pages) = await ReadAllPagesAsync(tool, mod.Key, DefaultCap);
                Assert.AreEqual(mod.Value, text, mod.Key + ": the pages concatenate to the exact source");
                if (pages > 1)
                {
                    multiPage++;
                }
            }

            Assert.Greater(multiPage, 8, "most Block World mods are longer than one result");
        }

        [Test]
        public async Task GetSource_ASmallMod_ReturnsTheWholeSourceAsData_InOnePage()
        {
            LuaCsModRuntime runtime = new();
            const string code = "local a = 1\nlocal b = 2\n";
            runtime.LoadMod("small", code, persistToStore: false);

            JObject page = await GetSourceAsync(Tool(runtime, Settings(DefaultCap)), "small");

            Assert.IsTrue(page.Value<bool>("success"), page.ToString());
            Assert.AreEqual(code, page.Value<string>("data"));
            Assert.AreEqual(1, page.Value<int>("first_line"));
            Assert.AreEqual(2, page.Value<int>("last_line"));
            Assert.AreEqual(2, page.Value<int>("total_lines"));
            Assert.AreEqual(code.Length, page.Value<int>("total_chars"));
            Assert.IsNull(page["next_from_line"]);
            StringAssert.Contains("the whole source", page.Value<string>("message"));
        }

        [Test]
        public async Task GetSource_FromLineAndLineCount_ReturnThoseLinesAndNameTheNext()
        {
            LuaCsModRuntime runtime = new();
            runtime.LoadMod("lines", "local a = 1\nlocal b = 2\nlocal c = 3\nlocal d = 4\nlocal e = 5", persistToStore: false);
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));

            JObject middle = await GetSourceAsync(tool, "lines", 2, 2);
            JObject tail = await GetSourceAsync(tool, "lines", 4);

            Assert.AreEqual("local b = 2\nlocal c = 3\n", middle.Value<string>("data"));
            Assert.AreEqual(2, middle.Value<int>("first_line"));
            Assert.AreEqual(3, middle.Value<int>("last_line"));
            Assert.AreEqual(4, middle.Value<int>("next_from_line"));
            Assert.AreEqual("local d = 4\nlocal e = 5", tail.Value<string>("data"), "the last line has no newline");
            Assert.IsNull(tail["next_from_line"]);
            StringAssert.Contains("to the end", tail.Value<string>("message"));
        }

        [Test]
        public async Task GetSource_FromLinePastTheEnd_IsRefusedWithTheLineCount()
        {
            LuaCsModRuntime runtime = new();
            runtime.LoadMod("short", "local a = 1\nlocal b = 2", persistToStore: false);

            JObject result = await GetSourceAsync(Tool(runtime, Settings(DefaultCap)), "short", 9);

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("has 2 lines", result.Value<string>("message"));
        }

        [Test]
        public async Task GetSource_ALineLongerThanAPage_IsShownCutWithAMarker_AndTheNextPageGoesOn()
        {
            LuaCsModRuntime runtime = new();
            string longLine = new string('-', LuaModsLlmTool.MaxSourceLengthReturned + 500);
            string code = "local x = 1\n" + longLine + "\nlocal y = 2\n";
            runtime.LoadMod("long_line", code, persistToStore: false);
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));

            string firstJson = await tool.ExecuteAsync("get_source", "long_line");
            string cutJson = await tool.ExecuteAsync("get_source", "long_line", from_line: 2);
            JObject first = JObject.Parse(firstJson);
            JObject cut = JObject.Parse(cutJson);
            JObject last = await GetSourceAsync(tool, "long_line", 3);

            Assert.AreEqual("local x = 1\n", first.Value<string>("data"), "a page holds whole lines only");
            Assert.AreEqual(2, first.Value<int>("next_from_line"));
            Assert.LessOrEqual(cutJson.Length, DefaultCap);
            Assert.AreEqual(2, cut.Value<int>("cut_line"));
            Assert.AreEqual(3, cut.Value<int>("next_from_line"));
            string data = cut.Value<string>("data");
            int shown = data.IndexOf('\n');
            Assert.Greater(shown, 1000);
            StringAssert.EndsWith("\n--[[ …[+" + (longLine.Length + 1 - shown) + " chars] ]]", data,
                "the cut is visible in the text and counts what was left out");
            StringAssert.Contains("too long for one result", cut.Value<string>("message"));
            Assert.AreEqual("local y = 2\n", last.Value<string>("data"));
        }

#if COREAI_LLM
        /// <summary>
        /// The cap is applied by <c>ToolExecutionPolicy</c> to the serialized result. A page pushed through the real
        /// policy (a scripted turn) arrives uncut, while the export bundle of the same mod is cut and says that a cut
        /// bundle cannot be imported.
        /// </summary>
        [Test]
        public async Task ScriptedTurn_AGetSourcePageArrivesUncut_WhileTheExportOfTheSameModIsCut()
        {
            LuaCsModRuntime runtime = new(store: new MemoryModStore());
            string source = LargeSource(900);
            runtime.LoadMod("big", source, persistToStore: false);
            CoreAISettingsOptions settings = Settings(DefaultCap);
            LuaModsLlmTool tool = Tool(runtime, settings);
            ScriptedModel model = new(iteration => iteration switch
            {
                1 => Call("call-page", new Dictionary<string, object> { ["action"] = "get_source", ["mod_id"] = "big" }),
                2 => Call("call-export", new Dictionary<string, object> { ["action"] = "export", ["mod_id"] = "big" }),
                _ => new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant, "Read."))
            });
            SmartToolCallingChatClient client = new(model, NullLog.Instance, settings, true,
                new List<ILlmTool> { tool }, BuiltInAgentRoleIds.Programmer, 3);
            MEAI.ChatOptions options = new() { Tools = new List<MEAI.AITool> { tool.CreateAIFunction() } };

            await client.GetResponseAsync(new[] { new MEAI.ChatMessage(MEAI.ChatRole.User, "Read the mod big.") },
                options, CancellationToken.None);

            Assert.AreEqual(3, model.Requests.Count, "page, export, answer");
            string page = ResultOf(model.Requests[1], "call-page");
            StringAssert.DoesNotContain("...[truncated:", page);
            JObject parsed = JObject.Parse(page);
            Assert.IsTrue(parsed.Value<bool>("success"), page);
            StringAssert.StartsWith(parsed.Value<string>("data"), source);
            Assert.Greater(parsed.Value<int>("next_from_line"), 2);
            string export = ResultOf(model.Requests[2], "call-export");
            StringAssert.Contains("...[truncated:", export, "negative twin: the cap is live in this turn");
            StringAssert.Contains("a cut bundle cannot be imported", export);
            StringAssert.Contains("get_source", export);
        }

        private static MEAI.ChatResponse Call(string callId, IDictionary<string, object> arguments)
        {
            return new MEAI.ChatResponse(new MEAI.ChatMessage(MEAI.ChatRole.Assistant,
                new List<MEAI.AIContent> { new MEAI.FunctionCallContent(callId, "manage_mods", arguments) }));
        }

        private static string ResultOf(IEnumerable<MEAI.ChatMessage> request, string callId)
        {
            List<MEAI.FunctionResultContent> results = request.SelectMany(message => message.Contents)
                .OfType<MEAI.FunctionResultContent>().Where(result => result.CallId == callId).ToList();
            Assert.AreEqual(1, results.Count, "call " + callId + " is answered once");
            return results[0].Result?.ToString() ?? "";
        }

        /// <summary>A recorded model: answers request N with the scripted response for N, keeping each request.</summary>
        private sealed class ScriptedModel : MEAI.IChatClient
        {
            private readonly Func<int, MEAI.ChatResponse> _script;

            public ScriptedModel(Func<int, MEAI.ChatResponse> script)
            {
                _script = script;
            }

            public List<List<MEAI.ChatMessage>> Requests { get; } = new();

            public Task<MEAI.ChatResponse> GetResponseAsync(IEnumerable<MEAI.ChatMessage> messages,
                MEAI.ChatOptions options = null, CancellationToken cancellationToken = default)
            {
                Requests.Add(messages.ToList());
                return Task.FromResult(_script(Requests.Count));
            }

            public IAsyncEnumerable<MEAI.ChatResponseUpdate> GetStreamingResponseAsync(
                IEnumerable<MEAI.ChatMessage> messages, MEAI.ChatOptions options = null,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }

            public object GetService(Type serviceType, object serviceKey = null)
            {
                return null;
            }

            public void Dispose()
            {
            }
        }
#endif

        private const string HudSource =
            "-- hud\n" +
            "local SLOT6 = 'Brick'\n" +
            "store_set('slot6', SLOT6)\n" +
            "hooks_on('ping', function() store_set('pinged', SLOT6) end)\n";

        [Test]
        public async Task Edit_ChangesTheRunningMod_ThePersistedSource_AndRecordsARevision()
        {
            MemoryModStore modStore = new();
            MemorySourceStore sourceStore = new();
            MemoryLuaScriptVersionStore versions = new();
            LuaCsModRuntime runtime = new(store: modStore, sourceStore: sourceStore, versionStore: versions);
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));
            Assert.IsTrue(JObject.Parse(await tool.ExecuteAsync("load", "hud", HudSource)).Value<bool>("success"));
            int revisionsBefore = runtime.ListModVersions("hud").Count;

            JObject result = await EditAsync(tool, "hud", "local SLOT6 = 'Brick'", "local SLOT6 = 'Ice Glacier'");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            string expected = HudSource.Replace("'Brick'", "'Ice Glacier'");
            Assert.IsTrue(runtime.TryGetModSource("hud", out string running));
            Assert.AreEqual(expected, running);
            Assert.AreEqual(expected, sourceStore.SourceOf("hud"), "the edited source is persisted");
            Assert.AreEqual("Ice Glacier", modStore.Get("hud", "slot6"), "the main chunk ran again");
            runtime.EmitEvent("ping", "");
            runtime.Tick(0);
            Assert.AreEqual("Ice Glacier", modStore.Get("hud", "pinged"), "the new handler replaced the old one");
            Assert.AreEqual(revisionsBefore + 1, runtime.ListModVersions("hud").Count);
            JObject data = (JObject)result["data"];
            Assert.AreEqual(runtime.ListModVersions("hud").Last().Index, data.Value<int>("revision"));
            Assert.AreEqual(2, data.Value<int>("first_line"));
            Assert.AreEqual(2, data.Value<int>("last_line"));
            Assert.AreEqual(1, data.Value<int>("replacements"));
            Assert.AreEqual("clean_startup_objects", data.Value<string>("mode"), "edit reloads like reload");
            StringAssert.Contains("line 2 of 4", result.Value<string>("message"));
        }

        [Test]
        public async Task Edit_Refusals_ChangeNothing_AndSayWhatToSendNext()
        {
            MemoryModStore modStore = new();
            MemoryLuaScriptVersionStore versions = new();
            LuaCsModRuntime runtime = new(store: modStore, versionStore: versions);
            const string source =
                "local A = 1\n" +
                "    local B = 'twice'\n" +
                "local C = 'twice'\n" +
                "store_set('a', tostring(A))\n";
            runtime.LoadMod("target", source, persistToStore: false);
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));
            int revisions = runtime.ListModVersions("target").Count;

            JObject notFound = await EditAsync(tool, "target", "local A = 2", "local A = 3");
            JObject indentation = await EditAsync(tool, "target", "  local C = 'twice'\nstore_set", "x");
            JObject ambiguous = await EditAsync(tool, "target", "'twice'", "'once'");
            JObject same = await EditAsync(tool, "target", "local A = 1", "local A = 1");
            JObject sameButLineEndings = await EditAsync(tool, "target", "local A = 1\n", "local A = 1\r\n");
            JObject emptyOld = await EditAsync(tool, "target", "", "local A = 2");
            JObject nullNew = await EditAsync(tool, "target", "local A = 1", null);
            JObject missingMod = await EditAsync(tool, "nope", "local A = 1", "local A = 2");
            JObject missingId = await EditAsync(tool, null, "local A = 1", "local A = 2");

            AssertRefused(notFound, "was not found", "get_source");
            AssertRefused(indentation, "was not found", "from_line=3");
            AssertRefused(ambiguous, "occurs 2 times", "replace_all=true");
            StringAssert.Contains("line(s) 2, 3", ambiguous.Value<string>("message"));
            AssertRefused(same, "are the same", "new_text");
            AssertRefused(sameButLineEndings, "are the same", "new_text");
            AssertRefused(emptyOld, "old_text is empty", "get_source");
            AssertRefused(nullNew, "new_text is required", "empty string");
            AssertRefused(missingMod, "is not loaded", "nope");
            AssertRefused(missingId, "mod_id is required", "edit");
            Assert.IsTrue(runtime.TryGetModSource("target", out string after));
            Assert.AreEqual(source, after);
            Assert.AreEqual(revisions, runtime.ListModVersions("target").Count, "no refusal records a revision");
        }

        private static void AssertRefused(JObject result, string because, string advice)
        {
            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            string message = result.Value<string>("message");
            StringAssert.Contains(because, message);
            StringAssert.Contains(advice, message);
        }

        [Test]
        public async Task Edit_ReplaceAll_ChangesEveryOccurrence()
        {
            LuaCsModRuntime runtime = new(store: new MemoryModStore());
            const string source = "local a = 'Brick'\nlocal b = 'Brick'\nlocal c = 'Stone'\n";
            runtime.LoadMod("all", source, persistToStore: false);
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));

            JObject result = await EditAsync(tool, "all", "'Brick'", "'Ice'", replaceAll: true);

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(runtime.TryGetModSource("all", out string after));
            Assert.AreEqual("local a = 'Ice'\nlocal b = 'Ice'\nlocal c = 'Stone'\n", after);
            Assert.AreEqual(2, result["data"].Value<int>("replacements"));
            Assert.AreEqual(1, result["data"].Value<int>("first_line"));
            Assert.AreEqual(2, result["data"].Value<int>("last_line"));
        }

        [TestCase("\r\n", "\n")]
        [TestCase("\n", "\r\n")]
        [TestCase("\r\n", "\r\n")]
        public async Task Edit_LineEndingsDifferingFromTheSource_StillMatch_AndTheSourceKeepsItsOwn(
            string sourceNewline, string modelNewline)
        {
            LuaCsModRuntime runtime = new(store: new MemoryModStore());
            string source = string.Join(sourceNewline, "local a = 1", "local b = 2", "local c = 3", "");
            runtime.LoadMod("endings", source, persistToStore: false);
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));

            JObject result = await EditAsync(tool, "endings", "local a = 1" + modelNewline + "local b = 2",
                "local a = 10" + modelNewline + "local b = 20" + modelNewline + "local b2 = 21");

            Assert.IsTrue(result.Value<bool>("success"), result.ToString());
            Assert.IsTrue(runtime.TryGetModSource("endings", out string after));
            Assert.AreEqual(
                string.Join(sourceNewline, "local a = 10", "local b = 20", "local b2 = 21", "local c = 3", ""), after,
                "new_text is written in the source's own line endings");
            Assert.AreEqual(1, result["data"].Value<int>("first_line"));
            Assert.AreEqual(3, result["data"].Value<int>("last_line"));
            Assert.AreEqual(sourceNewline != modelNewline,
                result.Value<string>("message").Contains("CRLF line endings were read as LF"));
        }

        [Test]
        public async Task Edit_NewCodeThatDoesNotCompile_LeavesTheModRunningWithItsOldSource()
        {
            MemoryModStore modStore = new();
            MemorySourceStore sourceStore = new();
            LuaCsModRuntime runtime = new(store: modStore, sourceStore: sourceStore,
                versionStore: new MemoryLuaScriptVersionStore());
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));
            Assert.IsTrue(JObject.Parse(await tool.ExecuteAsync("load", "hud", HudSource)).Value<bool>("success"));
            int revisions = runtime.ListModVersions("hud").Count;

            JObject result = await EditAsync(tool, "hud", "local SLOT6 = 'Brick'", "local SLOT6 = = 'Ice'");

            AssertRefused(result, "failed to load", "nothing changed");
            Assert.IsTrue(runtime.TryGetModSource("hud", out string running));
            Assert.AreEqual(HudSource, running);
            Assert.AreEqual(HudSource, sourceStore.SourceOf("hud"));
            Assert.AreEqual(revisions, runtime.ListModVersions("hud").Count);
            runtime.EmitEvent("ping", "");
            runtime.Tick(0);
            Assert.AreEqual("Brick", modStore.Get("hud", "pinged"), "the old handler still runs");
        }

        [Test]
        public async Task Edit_IsAMutation_RefusedReadOnly_GatedWithTheReloadTrigger_AndRateLimited()
        {
            LuaCsModRuntime runtime = new(store: new MemoryModStore());
            runtime.LoadMod("gated", "local a = 1\nlocal b = 2\n", persistToStore: false);
            LuaModsLlmTool readOnly = Tool(runtime, Settings(DefaultCap), allowManagement: false);
            RecordingGate gate = new();
            LuaGenerationRateLimiter limiter = new(1, 3600d);
            LuaModsLlmTool gated = Tool(runtime, Settings(DefaultCap), gate: gate, rateLimiter: limiter);

            JObject refused = await EditAsync(readOnly, "gated", "local a = 1", "local a = 5");
            JObject first = await EditAsync(gated, "gated", "local a = 1", "local a = 5");
            JObject limited = await EditAsync(gated, "gated", "local b = 2", "local b = 6");

            AssertRefused(refused, "read-only", "get_source");
            Assert.IsTrue(first.Value<bool>("success"), first.ToString());
            CollectionAssert.AreEqual(new[] { LuaModsLlmTool.ReloadBackupTrigger }, gate.Triggers);
            Assert.AreEqual("rate_limited", limited.Value<string>("status"), limited.ToString());
            Assert.IsTrue(runtime.TryGetModSource("gated", out string after));
            Assert.AreEqual("local a = 5\nlocal b = 2\n", after);
        }

        [Test]
        public async Task Edit_AnotherActorsMod_IsRefusedWithTheCallerAndTheOwner()
        {
            LuaCsModRuntime runtime = new(versionStore: new MemoryLuaScriptVersionStore());
            IActorIdentityProvider actorA = new LocalActorIdentityProvider(
                "actor-a", "session-a", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            IActorIdentityProvider actorB = new LocalActorIdentityProvider(
                "actor-b", "session-b", "world", ActorGrantSet.None, AgentMemoryScope.Empty);
            LuaModsLlmTool toolA = Tool(runtime, Settings(DefaultCap), identity: actorA);
            LuaModsLlmTool toolB = Tool(runtime, Settings(DefaultCap), identity: actorB);
            Assert.IsTrue(JObject.Parse(await toolB.ExecuteAsync("load", "foreign", "local value = 1"))
                .Value<bool>("success"));

            JObject result = await EditAsync(toolA, "foreign", "local value = 1", "local value = 2");

            Assert.IsFalse(result.Value<bool>("success"), result.ToString());
            StringAssert.Contains("actor 'actor-a'", result.Value<string>("message"));
            StringAssert.Contains("owned by actor 'actor-b'", result.Value<string>("message"));
            Assert.IsTrue(runtime.TryGetModSource("foreign", out string after));
            Assert.AreEqual("local value = 1", after);
        }

        [Test]
        public async Task Export_ABundleLongerThanOneResult_SaysItCannotBeImported_AndPointsAtEdit()
        {
            LuaCsModRuntime runtime = new(store: new MemoryModStore());
            runtime.LoadMod("big", LargeSource(900), persistToStore: false);
            runtime.LoadMod("small", "local a = 1", persistToStore: false);
            LuaModsLlmTool tool = Tool(runtime, Settings(DefaultCap));

            JObject big = JObject.Parse(await tool.ExecuteAsync("export", "big"));
            JObject small = JObject.Parse(await tool.ExecuteAsync("export", "small"));

            Assert.IsTrue(big.Value<bool>("success"), big.ToString());
            StringAssert.Contains("a cut bundle cannot be imported", big.Value<string>("message"));
            StringAssert.Contains("edit", big.Value<string>("message"));
            StringAssert.DoesNotContain("cannot be imported", small.Value<string>("message"));
        }

        [Test]
        public void Contract_TellsTheModelToReadInPagesAndChangeWithEdit()
        {
            LuaModsLlmTool tool = Tool(new LuaCsModRuntime(), Settings(DefaultCap));
            string schema = tool.CreateAIFunction().JsonSchema.GetRawText();
            JObject parameters = (JObject)JObject.Parse(tool.ParametersSchema)["properties"];

            foreach (string parameter in new[] { "from_line", "line_count", "old_text", "new_text", "replace_all" })
            {
                StringAssert.Contains("\"" + parameter + "\"", schema, "the native schema carries " + parameter);
                Assert.IsNotNull(parameters[parameter], "the text schema carries " + parameter);
            }

            StringAssert.Contains("edit (replace old_text", tool.Description);
            StringAssert.Contains("next_from_line", tool.Description);
            StringAssert.Contains("use reload only to replace a whole mod", tool.Description);
            StringAssert.DoesNotContain("Use get_source before reload", tool.Description);
            StringAssert.Contains("edit", parameters["action"].Value<string>("description"));
        }

        [Test]
        public void LineStarts_CountLinesTheWayPagesNumberThem()
        {
            CollectionAssert.AreEqual(new int[0], LuaModSourceEditor.LineStarts(""));
            CollectionAssert.AreEqual(new[] { 0 }, LuaModSourceEditor.LineStarts("a\n"));
            CollectionAssert.AreEqual(new[] { 0, 2 }, LuaModSourceEditor.LineStarts("a\nb"));
            CollectionAssert.AreEqual(new[] { 0, 3, 4 }, LuaModSourceEditor.LineStarts("a\r\n\nb\r\n"));
        }

        [Test]
        public void Replace_DeletingTheLastLine_ReportsALineInsideTheSource()
        {
            LuaModSourceEdit edit = LuaModSourceEditor.Replace("local a = 1\nlocal b = 2\n", "local b = 2\n", "", false);

            Assert.AreEqual(LuaModSourceEditStatus.Applied, edit.Status);
            Assert.AreEqual("local a = 1\n", edit.Source);
            Assert.AreEqual(1, edit.TotalLines);
            Assert.AreEqual(1, edit.FirstLine);
            Assert.AreEqual(1, edit.LastLine);
        }

        private sealed class MemoryModStore : ILuaModStore
        {
            private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

            public string Get(string modId, string key)
            {
                return _values.TryGetValue(modId + "\n" + key, out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove(modId + "\n" + key);
                }
                else
                {
                    _values[modId + "\n" + key] = value;
                }
            }

            public void Clear(string modId)
            {
                foreach (string key in _values.Keys.Where(key => key.StartsWith(modId + "\n", StringComparison.Ordinal))
                             .ToArray())
                {
                    _values.Remove(key);
                }
            }
        }

        private sealed class MemorySourceStore : ILuaModSourceStore
        {
            private readonly Dictionary<string, (string Source, LuaModManifest Manifest)> _entries =
                new(StringComparer.Ordinal);

            public void Save(string id, string source, LuaModManifest manifest)
            {
                _entries[id] = (source, manifest);
            }

            public bool TryLoad(string id, out string source, out LuaModManifest manifest)
            {
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry))
                {
                    source = entry.Source;
                    manifest = entry.Manifest;
                    return true;
                }

                source = "";
                manifest = null;
                return false;
            }

            public IReadOnlyList<LuaModManifest> List()
            {
                return _entries.Values.Where(entry => entry.Manifest != null).Select(entry => entry.Manifest).ToList();
            }

            public void SetActive(string id, bool active)
            {
                if (_entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry) &&
                    entry.Manifest != null)
                {
                    entry.Manifest.Active = active;
                }
            }

            public void Delete(string id)
            {
                _entries.Remove(id);
            }

            public string SourceOf(string id)
            {
                return _entries.TryGetValue(id, out (string Source, LuaModManifest Manifest) entry) ? entry.Source : null;
            }
        }

        private sealed class RecordingGate : IConfirmedWorldMutationGate
        {
            public readonly List<string> Triggers = new();

            public Task<TResult> ExecuteAsync<TResult>(string trigger,
                Func<CancellationToken, Task<TResult>> mutationAsync, CancellationToken cancellationToken)
            {
                Triggers.Add(trigger);
                return mutationAsync(cancellationToken);
            }
        }

        /// <summary>
        /// A mod runtime that only answers <c>TryGetModSource</c>, from a fixed table: it lets the shipped Block World
        /// mods be paged without running them (they need the Rbx world). Every other member throws.
        /// </summary>
        public class SourceOnlyRuntime : DispatchProxy
        {
            private IReadOnlyDictionary<string, string> _sources;

            internal static ILuaModRuntime Create(IReadOnlyDictionary<string, string> sources)
            {
                ILuaModRuntime runtime = DispatchProxy.Create<ILuaModRuntime, SourceOnlyRuntime>();
                ((SourceOnlyRuntime)(object)runtime)._sources = sources;
                return runtime;
            }

            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.Name == nameof(ILuaModRuntime.TryGetModSource))
                {
                    bool found = _sources.TryGetValue((string)args[1], out string source);
                    args[2] = found ? source : "";
                    return found;
                }

                throw new NotSupportedException("SourceOnlyRuntime answers only TryGetModSource: " + targetMethod.Name);
            }
        }
    }
}
