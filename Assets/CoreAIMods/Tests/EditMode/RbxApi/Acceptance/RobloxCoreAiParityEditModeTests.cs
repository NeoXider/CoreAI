using System.IO;
using System.Threading;
using CoreAI.Mods.Rbx.Instances;
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

        [Test]
        public void CastleQuestParityScript_CompletesTheSameCollectibleGateAndVictoryRoute()
        {
            string path = Path.Combine(Application.dataPath, "CoreAIMods", "Tests", "EditMode",
                "RbxApi", "Acceptance", "CastleQuestParity.lua");
            string script = File.ReadAllText(path);
            SynchronizationContext previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(null);
            try
            {
                using Mvp1AcceptanceWorld world = new();
                world.Stack.Runtime.LoadMod("castle-quest", script);
                Assert.AreEqual("parts=51>47;coins=3;key=true;health=2;gate=true;win=true;gateY=14",
                    world.Store.Get("castle-quest", "castleQuestSummary"));
                RbxInstance castle = world.Workspace.FindFirstChild("CastleQuestParity");
                Assert.IsNotNull(castle);
                RbxInstance tower = castle.FindFirstChild("Tower");
                Assert.IsNotNull(tower);
                Renderer renderer = world.BoundObject(tower).GetComponentInChildren<Renderer>();
                Assert.IsNotNull(renderer);
                Assert.Greater(renderer.bounds.size.y, renderer.bounds.size.x * 1.5f,
                    "the Roblox X-axis Cylinder must stand upright after the CFrame roll");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }
}
