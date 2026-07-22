using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Regression tripwire for the engine abstraction seam: outside the <c>Runtime/Scripting</c> adapter
    /// layer, no CoreAIMods runtime source may import the Lua-CSharp VM. This is the guard the old
    /// MoonSharp removal lacked — without it, VM types silently leak back into runtime/binder code and
    /// the "swap the engine by reimplementing Scripting/ only" promise rots.
    /// </summary>
    [TestFixture]
    public sealed class ScriptingSeamHonestyEditModeTests
    {
        private static readonly Regex LuaUsing = new(@"^\s*using\s+(static\s+)?Lua(\s*;|\s*\.)",
            RegexOptions.Compiled);

        [Test]
        public void RuntimeOutsideScripting_HasNoLuaVmUsings()
        {
            string runtimeRoot = Path.Combine(Application.dataPath, "CoreAIMods", "Runtime");
            Assert.IsTrue(Directory.Exists(runtimeRoot), $"Runtime folder not found: {runtimeRoot}");

            string scriptingRoot = Path.GetFullPath(Path.Combine(runtimeRoot, "Scripting"))
                                       .Replace('\\', '/')
                                       .TrimEnd('/') + "/";

            List<string> offenders = new();
            foreach (string file in Directory.GetFiles(runtimeRoot, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = Path.GetFullPath(file).Replace('\\', '/');
                if (normalized.StartsWith(scriptingRoot, System.StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (string line in File.ReadLines(file))
                {
                    if (LuaUsing.IsMatch(line))
                    {
                        offenders.Add($"{normalized}: {line.Trim()}");
                        break;
                    }
                }
            }

            Assert.IsEmpty(offenders,
                "VM-neutral runtime code must not import the Lua VM; move engine-specific code into " +
                "Runtime/Scripting/ instead:\n" + string.Join("\n", offenders));
        }
    }
}
