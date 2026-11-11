using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.RuntimeUi
{
    /// <summary>
    /// Dependency direction of the UxmlService Lua surface: the rules (Instances) know neither the engine,
    /// the Lua VM nor the runtime UI host; the Lua binding knows the VM but not the host; only the
    /// adapter and the installer name the host. A file that crosses its line fails with its name.
    /// </summary>
    [TestFixture]
    public sealed class UxmlServiceArchitectureFitnessEditModeTests
    {
        private static readonly Regex EngineUsing = new(@"^\s*using\s+(UnityEngine|UnityEditor)\b", RegexOptions.Multiline);
        private static readonly Regex VmUsing = new(@"^\s*using\s+(static\s+)?Lua(\s*;|\s*\.)", RegexOptions.Multiline);
        private static readonly Regex HostUsing = new(@"^\s*using\s+CoreAI\.RuntimeUi\s*;", RegexOptions.Multiline);
        private static readonly Regex ContainerUsing = new(@"^\s*using\s+VContainer\b", RegexOptions.Multiline);

        private static readonly HashSet<string> HostAdapters = new()
        {
            "RuntimeUiHostBackend.cs", "RuntimeUiLuaInstaller.cs"
        };

        [Test]
        public void P18_TheRules_NameNoEngineVmHostOrContainer()
        {
            foreach (string file in Files("Runtime", "RbxApi", "Instances", "RuntimeUi"))
            {
                string text = File.ReadAllText(file);
                Assert.IsFalse(EngineUsing.IsMatch(text), file);
                Assert.IsFalse(VmUsing.IsMatch(text), file);
                Assert.IsFalse(HostUsing.IsMatch(text), file);
                Assert.IsFalse(ContainerUsing.IsMatch(text), file);
            }
        }

        [Test]
        public void P18_TheFocusReporter_NamesNoEngineOrVm()
        {
            foreach (string file in Files("Runtime", "RbxApi", "Binding", "RuntimeUi"))
            {
                string text = File.ReadAllText(file);
                Assert.IsFalse(EngineUsing.IsMatch(text), file);
                Assert.IsFalse(VmUsing.IsMatch(text), file);
            }
        }

        [Test]
        public void P18_OnlyTheAdapterAndTheInstaller_NameTheRuntimeUiHost()
        {
            int adapters = 0;
            foreach (string file in Files("Runtime", "Scripting", "LuaCs", "RuntimeUi"))
            {
                string text = File.ReadAllText(file);
                bool adapter = HostAdapters.Contains(Path.GetFileName(file));
                Assert.IsFalse(EngineUsing.IsMatch(text), file);
                Assert.AreEqual(adapter, HostUsing.IsMatch(text), file);
                if (adapter)
                {
                    adapters++;
                    Assert.IsFalse(VmUsing.IsMatch(text), file);
                }
                else
                {
                    Assert.IsFalse(ContainerUsing.IsMatch(text), file);
                }
            }

            Assert.AreEqual(HostAdapters.Count, adapters, "Both host-facing files exist.");
        }

        private static string[] Files(params string[] parts)
        {
            string folder = Path.Combine(Application.dataPath, "CoreAIMods");
            for (int i = 0; i < parts.Length; i++)
            {
                folder = Path.Combine(folder, parts[i]);
            }

            Assert.IsTrue(Directory.Exists(folder), folder);
            string[] files = Directory.GetFiles(folder, "*.cs");
            Assert.IsNotEmpty(files, folder);
            return files;
        }
    }
}
