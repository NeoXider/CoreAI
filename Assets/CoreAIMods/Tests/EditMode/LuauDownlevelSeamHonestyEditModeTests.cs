using System;
using System.Collections.Generic;
using System.Reflection;
using CoreAI.Infrastructure.Luau;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Architecture-fitness guard for the Luau downleveler: it lives in its own engine-free,
    /// VM-free assembly (<c>CoreAI.LuauDownlevel</c>, <c>noEngineReferences: true</c>). Reflecting over
    /// the compiled assembly's referenced assemblies proves the dependency direction directly — if any
    /// UnityEngine/UnityEditor or Lua-CSharp VM reference leaks in, the downleveler stops being a pure
    /// text transform and this test fails.
    /// </summary>
    [TestFixture]
    public sealed class LuauDownlevelSeamHonestyEditModeTests
    {
        [Test]
        public void LuauDownlevelAssembly_ReferencesNoEngineOrVmAssemblies()
        {
            Assembly assembly = typeof(LuauDownleveler).Assembly;
            Assert.AreEqual("CoreAI.LuauDownlevel", assembly.GetName().Name,
                "The downleveler must live in its own engine-free assembly.");

            List<string> offenders = new();
            foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            {
                string name = reference.Name;
                if (name.StartsWith("UnityEngine", StringComparison.Ordinal) ||
                    name.StartsWith("UnityEditor", StringComparison.Ordinal) ||
                    name.Equals("Lua", StringComparison.Ordinal) ||
                    name.StartsWith("Lua.", StringComparison.Ordinal))
                {
                    offenders.Add(name);
                }
            }

            Assert.IsEmpty(offenders,
                "CoreAI.LuauDownlevel must stay engine- and VM-free (it is a pure Luau->Lua 5.2 text " +
                "transform); referenced offenders: " + string.Join(", ", offenders));
        }
    }
}
