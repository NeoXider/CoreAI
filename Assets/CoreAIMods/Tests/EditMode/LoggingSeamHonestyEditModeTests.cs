using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Architecture-fitness guard for the Lua logging module. The core (service, contract, formatter,
    /// entry, level, query) is kept engine-free so it can later split into its own
    /// <c>noEngineReferences</c> assembly; only the adapters (<c>LuaLogFileSink</c>, the Unity console
    /// mirror, <c>GetModLogsLlmTool</c>) may touch the engine. This scans the core files for engine/VM
    /// <c>using</c>s — the intended split (see the module README TODO) rots the moment one leaks in.
    /// </summary>
    [TestFixture]
    public sealed class LoggingSeamHonestyEditModeTests
    {
        private static readonly string[] EngineFreeCore =
        {
            "ILuaLogService.cs",
            "LuaLogService.cs",
            "LuaLogFormatter.cs",
            "LuaLogEntry.cs",
            "LuaLogLevel.cs",
            "LuaLogQuery.cs"
        };

        private static readonly Regex EngineOrVmUsing = new(
            @"^\s*using\s+(static\s+)?(UnityEngine|UnityEditor|Lua)(\s*;|\s*\.)",
            RegexOptions.Compiled);

        [Test]
        public void LoggingCore_HasNoEngineOrVmUsings()
        {
            string loggingRoot = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime", "Logging");
            Assert.IsTrue(Directory.Exists(loggingRoot), $"Logging folder not found: {loggingRoot}");

            List<string> offenders = new();
            foreach (string fileName in EngineFreeCore)
            {
                string path = Path.Combine(loggingRoot, fileName);
                Assert.IsTrue(File.Exists(path), $"Expected logging core file missing: {path}");
                foreach (string line in File.ReadLines(path))
                {
                    if (EngineOrVmUsing.IsMatch(line))
                    {
                        offenders.Add($"{fileName}: {line.Trim()}");
                        break;
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "The logging core must stay engine- and VM-free so it can split into its own assembly; " +
                "keep engine dependencies in the adapters (LuaLogFileSink, GetModLogsLlmTool):\n" +
                string.Join("\n", offenders));
        }
    }
}
