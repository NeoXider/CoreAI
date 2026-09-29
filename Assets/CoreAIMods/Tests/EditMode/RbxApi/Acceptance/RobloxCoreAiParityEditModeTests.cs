using System.IO;
using System.Threading;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode.RbxApi.Acceptance
{
    /// <summary>Runs the same Lua file used by Roblox Studio's official RunScript task.</summary>
    [TestFixture]
    public sealed class RobloxCoreAiParityEditModeTests
    {
        [Test]
        public void RobloxStudioParityScript_MatchesTheReferenceResult()
        {
            string path = Path.Combine(Application.dataPath, "CoreAIMods", "Tests", "EditMode",
                "RbxApi", "Acceptance", "RobloxCoreAiParity.lua");
            string script = File.ReadAllText(path);
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                using Mvp1AcceptanceWorld world = new();
                world.Stack.Runtime.LoadMod("parity", script);
                Assert.AreEqual("before=2;after=1;clone=ProbeCopy;position=-2,4,9",
                    world.Store.Get("parity", "summary"));
                Assert.IsNull(world.Workspace.FindFirstChild("CoreAiParityRoot"));
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }
}
