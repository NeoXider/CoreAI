#if COREAI_LUA
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Infrastructure.Luau;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Scripting;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Keeps the export lint's CoreAI side from drifting from the code (MVP13, RT7): every global a real mod
    /// environment has that Roblox does not, every service the DataModel's catalog registers that Roblox does not,
    /// every member the source gates to the elevated tier (DEV-14), every key the mod header parser reads and every
    /// non-Luau member of a library table must be flagged by <see cref="LuauExportLintSurface.Default"/>. A new
    /// CoreAI global, service or library member fails here with its name until the lint knows it. When the offline
    /// Roblox mirror sits next to the checkout, the Roblox side is compared with it too.
    /// </summary>
    [TestFixture]
    public sealed class LuauExportLintSurfaceEditModeTests
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

        private sealed class MemoryStore : ILuaModStore
        {
            private readonly Dictionary<(string ModId, string Key), string> _values = new();

            public string Get(string modId, string key)
            {
                return _values.TryGetValue((modId, key), out string value) ? value : "";
            }

            public void Set(string modId, string key, string value)
            {
                if (value == null)
                {
                    _values.Remove((modId, key));
                    return;
                }

                _values[(modId, key)] = value;
            }

            public void Clear(string modId)
            {
            }
        }

        private sealed class SilentLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }

            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null)
            {
            }
        }

        /// <summary>What a mod with every capability stores under "result" after running <paramref name="body"/>.</summary>
        private static string RunAsMod(string body)
        {
            MemoryStore store = new();
            LuaCsModStack stack = LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentLogger(),
                ModStore = store,
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RbxApi = new LuaCsRbxApiBindings()
            });
            stack.Runtime.LoadMod("surface", body, persistToStore: false);
            return store.Get("surface", "result");
        }

        private static bool HasFinding(string source, string code)
        {
            foreach (LuauExportFinding finding in LuauExportLint.Check(source))
            {
                if (finding.Code == code)
                {
                    return true;
                }
            }

            return false;
        }

        [Test]
        public void EveryGlobalOfAModsEnvironment_ThatRobloxDoesNotHave_IsACoreAiGlobalToTheLint()
        {
            string globals = RunAsMod("local names = {} for k in pairs(_G) do names[#names + 1] = tostring(k) end "
                                      + "store_set('result', table.concat(names, ' '))");
            List<string> unflagged = new();
            int coreAi = 0;
            foreach (string name in globals.Split(' '))
            {
                if (name.Length == 0 || LuauExportLintSurface.IsRobloxGlobal(name))
                {
                    continue;
                }

                coreAi++;
                if (!LuauExportLintSurface.Default.IsCoreAiGlobal(name) || !HasFinding("local v = " + name, "LX201"))
                {
                    unflagged.Add(name);
                }
            }

            Assert.Greater(coreAi, 40, "the environment was enumerated");
            Assert.IsEmpty(unflagged, "CoreAI globals the lint's default surface does not know; add them to "
                                      + "LuauExportLintSurface.DefaultCoreAiGlobals or a prefix");
        }

        [Test]
        public void EveryLibraryMemberOfAModsEnvironment_ThatLuauDoesNotHave_IsFlagged()
        {
            string members = RunAsMod(@"
                local names = {}
                for _, lib in ipairs({'table', 'string', 'math', 'utf8', 'bit32', 'os', 'coroutine', 'task', 'debug'}) do
                  for k in pairs(_G[lib]) do names[#names + 1] = lib .. '.' .. tostring(k) end
                end
                store_set('result', table.concat(names, ' '))");
            List<string> wrong = new();
            int foreign = 0;
            foreach (string member in members.Split(' '))
            {
                if (member.Length == 0)
                {
                    continue;
                }

                string[] parts = member.Split('.');
                bool luau = LuauExportLintSurface.TryGetLibrary(parts[0], out IReadOnlyCollection<string> known)
                            && new List<string>(known).Contains(parts[1]);
                foreign += luau ? 0 : 1;
                if (luau == HasFinding("local v = " + member, "LX203"))
                {
                    wrong.Add(member + (luau ? " (Luau's, flagged)" : " (not Luau's, unflagged)"));
                }
            }

            Assert.Greater(foreign, 0, "the task table carries CoreAI's internal bridges, which Roblox has not");
            Assert.IsEmpty(wrong);
        }

        [Test]
        public void EveryServiceTheDefaultCatalogRegisters_ThatRobloxDoesNotHave_IsACoreAiServiceToTheLint()
        {
            List<string> unflagged = new();
            int coreAi = 0;
            foreach (string name in ServiceCatalog.CreateMvp2().ServiceNames)
            {
                if (LuauExportLintSurface.IsRobloxService(name))
                {
                    continue;
                }

                coreAi++;
                if (!HasFinding("local s = game:GetService('" + name + "')", "LX301"))
                {
                    unflagged.Add(name);
                }
            }

            Assert.GreaterOrEqual(coreAi, 2, "AIService and UxmlService at least");
            Assert.IsEmpty(unflagged, "CoreAI services the lint does not know; add them to DefaultCoreAiServices");
        }

        [Test]
        public void EveryMemberTheSourceGatesToTheElevatedTier_IsFlagged()
        {
            Regex gate = new("RequireUnrestricted\\(\"([A-Za-z]+):([A-Za-z]+)\"\\)");
            List<string> gated = new();
            foreach (string file in Directory.GetFiles(Path.Combine("Assets", "CoreAIMods", "Runtime"), "*.cs",
                         SearchOption.AllDirectories))
            {
                foreach (Match match in gate.Matches(File.ReadAllText(file)))
                {
                    gated.Add(match.Groups[1].Value + ":" + match.Groups[2].Value);
                }
            }

            Assert.IsNotEmpty(gated, "ScriptContext:SetTimeout is gated today");
            foreach (string member in gated)
            {
                string method = member.Substring(member.IndexOf(':') + 1);
                Assert.IsTrue(HasFinding("local x = game:GetService('" + member.Split(':')[0] + "'):" + method + "(1)",
                    "LX303"), member + " is gated to the elevated tier but the lint does not flag it");
            }
        }

        [Test]
        public void EveryKeyTheModHeaderParserReads_IsFlagged()
        {
            Regex key = new("key\\.Equals\\(\"([a-z]+)\"");
            string parser = File.ReadAllText(Path.Combine("Assets", "CoreAIMods", "Runtime", "LuaExecution",
                "LuaModHeader.cs"));
            List<string> keys = new();
            foreach (Match match in key.Matches(parser))
            {
                keys.Add(match.Groups[1].Value);
            }

            Assert.Greater(keys.Count, 5, "id, name, version, capabilities, ...");
            foreach (string headerKey in keys)
            {
                Assert.IsTrue(HasFinding("-- @coreai " + headerKey + ": x\nprint(1)", "LX401"), headerKey);
                Assert.IsTrue(HasFinding("--[[@coreai\n" + headerKey + ": x\n]]\nprint(1)", "LX401"), headerKey);
            }

            Assert.AreEqual("door", LuaModHeader.Parse("-- @coreai id: door\nprint(1)", "fallback").Id,
                "the lint reads the header by the parser's rules");
        }

        [Test]
        public void TheRobloxSide_MatchesTheOfflineMirror_WhenItIsPresent()
        {
            string mirror = Path.GetFullPath(Path.Combine("..", "RobloxDocs", "creator-docs", "content", "en-us",
                "reference", "engine"));
            if (!Directory.Exists(mirror))
            {
                Assert.Ignore("the offline Roblox mirror is not next to this checkout");
            }

            List<string> missing = new();
            foreach (string file in Directory.GetFiles(Path.Combine(mirror, "libraries"), "*.yaml"))
            {
                string library = Path.GetFileNameWithoutExtension(file);
                Assert.IsTrue(LuauExportLintSurface.TryGetLibrary(library, out IReadOnlyCollection<string> known),
                    library);
                List<string> members = new(known);
                foreach (Match match in Regex.Matches(File.ReadAllText(file), "^  - name: " + library + "\\.(\\w+)\\s*$",
                             RegexOptions.Multiline))
                {
                    if (!members.Contains(match.Groups[1].Value))
                    {
                        missing.Add(library + "." + match.Groups[1].Value);
                    }
                }
            }

            foreach (string file in Directory.GetFiles(Path.Combine(mirror, "datatypes"), "*.yaml"))
            {
                string datatype = Path.GetFileNameWithoutExtension(file);
                if (!LuauExportLintSurface.IsRobloxGlobal(datatype))
                {
                    missing.Add(datatype);
                }
            }

            Assert.IsEmpty(missing, "the mirror (" + LuauExportLintSurface.RobloxMirrorVersion
                                    + ") has members the lint's Roblox side lacks: regenerate LuauRobloxSurfaceData");
        }
    }
}
#endif
