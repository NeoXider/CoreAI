using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// The run predicate of MVP4 design note SC-9: rulebook R1.3, R1.5-R1.8, R1.10-R1.12 and the R2.1 container matrix,
    /// the side per topology and the wait for the local player (R2.2).
    /// </summary>
    [TestFixture]
    public sealed class ScriptRunRulesEditModeTests
    {
        private static ScriptRunInputs Eligible(string className, int runContext, ScriptContainer container,
            ScriptContext declared = ScriptContext.Legacy)
        {
            return new ScriptRunInputs
            {
                ClassName = className,
                RunContext = runContext,
                DeclaredContext = declared,
                IsLoaded = true,
                HasBoundInstance = true,
                Enabled = true,
                Container = container,
                PlayerScope = ScriptPlayerScope.None,
                Topology = RbxNetworkTopology.Solo,
                HasLocalPlayer = true
            };
        }

        private static ScriptRunState Evaluate(ScriptRunInputs inputs)
        {
            return ScriptRunRules.Evaluate(in inputs);
        }

        [Test]
        public void R1_3_ScriptRunsOnlyWhenEnabledAndInARunningContainer()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Legacy, ScriptContainer.ServerScriptService);
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
            inputs.Container = ScriptContainer.Other;
            Assert.AreEqual(ScriptRunState.ContainerDoesNotRun, Evaluate(inputs));
        }

        [Test]
        public void R1_3_DisabledScriptDoesNotRun()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Legacy, ScriptContainer.ServerScriptService);
            inputs.Enabled = false;
            Assert.AreEqual(ScriptRunState.Disabled, Evaluate(inputs));
        }

        [TestCase(ScriptContainer.Workspace)]
        [TestCase(ScriptContainer.ServerScriptService)]
        public void R1_5_LegacyScriptRunsInWorkspaceAndServerScriptService(ScriptContainer container)
        {
            Assert.AreEqual(ScriptRunState.Running, Evaluate(Eligible("Script", RunContextValues.Legacy, container)));
        }

        [TestCase(ScriptContainer.ReplicatedStorage)]
        [TestCase(ScriptContainer.ReplicatedFirst)]
        [TestCase(ScriptContainer.StarterPlayerScripts)]
        [TestCase(ScriptContainer.Players)]
        public void R1_5_LegacyScriptInReplicatedStorageDoesNotRun(ScriptContainer container)
        {
            Assert.AreEqual(ScriptRunState.ContainerDoesNotRun,
                Evaluate(Eligible("Script", RunContextValues.Legacy, container)));
        }

        [TestCase(ScriptContainer.Workspace)]
        [TestCase(ScriptContainer.ServerScriptService)]
        [TestCase(ScriptContainer.ReplicatedStorage)]
        public void R1_6_ServerRunContextAlsoRunsInReplicatedStorage(ScriptContainer container)
        {
            Assert.AreEqual(ScriptRunState.Running,
                Evaluate(Eligible("Script", RunContextValues.Server, container, ScriptContext.Server)));
        }

        [TestCase("Script", RunContextValues.Legacy)]
        [TestCase("Script", RunContextValues.Server)]
        [TestCase("Script", RunContextValues.Client)]
        [TestCase("LocalScript", RunContextValues.Legacy)]
        public void R2_1_NothingRunsInServerStorage(string className, int runContext)
        {
            ScriptRunInputs inputs = Eligible(className, runContext, ScriptContainer.ServerStorage);
            inputs.PlayerScope = ScriptPlayerScope.LocalPlayer;
            Assert.AreEqual(ScriptRunState.ContainerDoesNotRun, Evaluate(inputs));
        }

        [TestCase(ScriptContainer.ReplicatedStorage, RbxNetworkTopology.Solo)]
        [TestCase(ScriptContainer.ReplicatedFirst, RbxNetworkTopology.Host)]
        [TestCase(ScriptContainer.StarterPlayerScripts, RbxNetworkTopology.Client)]
        [TestCase(ScriptContainer.StarterCharacterScripts, RbxNetworkTopology.Client)]
        [TestCase(ScriptContainer.Workspace, RbxNetworkTopology.Client)]
        public void R1_7_ClientRunContextRunsOnClientSides(ScriptContainer container, RbxNetworkTopology topology)
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Client, container, ScriptContext.Client);
            inputs.Topology = topology;
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
        }

        [TestCase(ScriptContainer.ServerScriptService)]
        [TestCase(ScriptContainer.StarterGui)]
        [TestCase(ScriptContainer.Other)]
        public void R1_7_ClientRunContextOutsideClientContainers_DoesNotRun(ScriptContainer container)
        {
            Assert.AreEqual(ScriptRunState.ContainerDoesNotRun,
                Evaluate(Eligible("Script", RunContextValues.Client, container, ScriptContext.Client)));
        }

        [Test]
        public void ContextMatrix_ClientScriptNeverRunsOnDedicatedServer()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Client, ScriptContainer.ReplicatedStorage,
                ScriptContext.Client);
            inputs.Topology = RbxNetworkTopology.DedicatedServer;
            Assert.AreEqual(ScriptRunState.NotOnThisSide, Evaluate(inputs));
        }

        [TestCase(RbxNetworkTopology.Solo)]
        [TestCase(RbxNetworkTopology.Host)]
        [TestCase(RbxNetworkTopology.DedicatedServer)]
        public void ContextMatrix_ServerScriptRunsOnSoloHostAndDedicated(RbxNetworkTopology topology)
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Server, ScriptContainer.ServerScriptService,
                ScriptContext.Server);
            inputs.Topology = topology;
            inputs.HasLocalPlayer = false;
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
        }

        [TestCase(RunContextValues.Server, ScriptContext.Server)]
        [TestCase(RunContextValues.Legacy, ScriptContext.Legacy)]
        public void ContextMatrix_ServerScriptNeverRunsOnPureClient(int runContext, ScriptContext declared)
        {
            ScriptRunInputs inputs = Eligible("Script", runContext, ScriptContainer.Workspace, declared);
            inputs.Topology = RbxNetworkTopology.Client;
            Assert.AreEqual(ScriptRunState.NotOnThisSide, Evaluate(inputs));
        }

        [TestCase(ScriptContainer.Players, ScriptPlayerScope.LocalPlayer)]
        [TestCase(ScriptContainer.Workspace, ScriptPlayerScope.LocalPlayer)]
        [TestCase(ScriptContainer.ReplicatedFirst, ScriptPlayerScope.None)]
        public void R2_1_LocalScriptRunsUnderTheLocalPlayerAndInReplicatedFirst(ScriptContainer container,
            ScriptPlayerScope scope)
        {
            ScriptRunInputs inputs = Eligible("LocalScript", RunContextValues.Legacy, container);
            inputs.PlayerScope = scope;
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
        }

        [TestCase(ScriptContainer.ReplicatedStorage)]
        [TestCase(ScriptContainer.Workspace)]
        [TestCase(ScriptContainer.StarterPlayerScripts)]
        public void R2_1_LocalScriptOutsidePlayerContainers_DoesNotRun(ScriptContainer container)
        {
            Assert.AreEqual(ScriptRunState.ContainerDoesNotRun,
                Evaluate(Eligible("LocalScript", RunContextValues.Legacy, container)));
        }

        [Test]
        public void R2_1_LocalScriptOfAnotherPlayer_IsNotOnThisSide()
        {
            ScriptRunInputs inputs = Eligible("LocalScript", RunContextValues.Legacy, ScriptContainer.Players);
            inputs.PlayerScope = ScriptPlayerScope.OtherPlayer;
            Assert.AreEqual(ScriptRunState.NotOnThisSide, Evaluate(inputs));
        }

        [Test]
        public void R2_1_ClientScriptInAnotherPlayersCharacter_RunsOnEveryClient()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Client, ScriptContainer.Workspace,
                ScriptContext.Client);
            inputs.PlayerScope = ScriptPlayerScope.OtherPlayer;
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
        }

        [Test]
        public void R2_1_ClientScriptInAnotherPlayersGui_IsNotOnThisSide()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Client, ScriptContainer.Players,
                ScriptContext.Client);
            inputs.PlayerScope = ScriptPlayerScope.OtherPlayer;
            Assert.AreEqual(ScriptRunState.NotOnThisSide, Evaluate(inputs));
        }

        [Test]
        public void R1_12_DetachToNilKeepsARunningScript()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Legacy, ScriptContainer.OutsideDataModel);
            inputs.WasRunning = true;
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
        }

        [Test]
        public void R1_12_ScriptOutsideTheDataModel_DoesNotStart()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Legacy, ScriptContainer.OutsideDataModel);
            Assert.AreEqual(ScriptRunState.ContainerDoesNotRun, Evaluate(inputs));
        }

        [Test]
        public void R1_12_MoveIntoServerStorageStopsIt()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Legacy, ScriptContainer.ServerStorage);
            inputs.WasRunning = true;
            Assert.AreEqual(ScriptRunState.ContainerDoesNotRun, Evaluate(inputs));
        }

        [TestCase(ScriptContainer.ReplicatedStorage)]
        [TestCase(ScriptContainer.ServerScriptService)]
        [TestCase(ScriptContainer.Workspace)]
        public void R2_1_ModuleScriptNeverRunsOnItsOwn(ScriptContainer container)
        {
            ScriptRunInputs inputs = Eligible("ModuleScript", RunContextValues.Legacy, container,
                ScriptContext.Shared);
            inputs.Enabled = false;
            Assert.AreEqual(ScriptRunState.Module, Evaluate(inputs));
        }

        [Test]
        public void R2_2_ClientScriptWaitsForTheLocalPlayer()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Client, ScriptContainer.ReplicatedStorage,
                ScriptContext.Client);
            inputs.Topology = RbxNetworkTopology.Client;
            inputs.HasLocalPlayer = false;
            Assert.AreEqual(ScriptRunState.WaitingForLocalPlayer, Evaluate(inputs));
            inputs.HasLocalPlayer = true;
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
        }

        [Test]
        public void R2_2_ServerScriptDoesNotWaitForAPlayer()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Server, ScriptContainer.ServerScriptService,
                ScriptContext.Server);
            inputs.HasLocalPlayer = false;
            Assert.AreEqual(ScriptRunState.Running, Evaluate(inputs));
        }

        [Test]
        public void Sc3_EnabledNeverOverridesQuarantineOrSuspension()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Legacy, ScriptContainer.ServerScriptService);
            inputs.IsQuarantined = true;
            Assert.AreEqual(ScriptRunState.Quarantined, Evaluate(inputs));
            inputs.IsQuarantined = false;
            inputs.IsSuspended = true;
            Assert.AreEqual(ScriptRunState.Suspended, Evaluate(inputs));
        }

        [Test]
        public void Evaluate_ReportsTheFirstFailingCheckInOrder()
        {
            ScriptRunInputs inputs = Eligible("Script", RunContextValues.Plugin, ScriptContainer.ServerStorage);
            inputs.Topology = RbxNetworkTopology.Client;
            inputs.Enabled = false;
            inputs.IsSuspended = true;
            inputs.IsQuarantined = true;
            inputs.HasBoundInstance = false;
            inputs.IsLoaded = false;
            ScriptRunState[] expected =
            {
                ScriptRunState.Unloaded, ScriptRunState.Detached, ScriptRunState.Quarantined,
                ScriptRunState.Suspended, ScriptRunState.InvalidRunContext, ScriptRunState.Disabled,
                ScriptRunState.NotOnThisSide, ScriptRunState.ContainerDoesNotRun
            };
            List<ScriptRunState> seen = new List<ScriptRunState>();
            seen.Add(Evaluate(inputs));
            inputs.IsLoaded = true;
            seen.Add(Evaluate(inputs));
            inputs.HasBoundInstance = true;
            seen.Add(Evaluate(inputs));
            inputs.IsQuarantined = false;
            seen.Add(Evaluate(inputs));
            inputs.IsSuspended = false;
            seen.Add(Evaluate(inputs));
            inputs.RunContext = RunContextValues.Legacy;
            seen.Add(Evaluate(inputs));
            inputs.Enabled = true;
            seen.Add(Evaluate(inputs));
            inputs.Topology = RbxNetworkTopology.Solo;
            seen.Add(Evaluate(inputs));
            CollectionAssert.AreEqual(expected, seen);
        }

        [Test]
        public void Evaluate_InvalidRunContext_IsReportedNotThrown()
        {
            ScriptRunInputs inputs = Eligible("LocalScript", RunContextValues.Client, ScriptContainer.Players);
            inputs.PlayerScope = ScriptPlayerScope.LocalPlayer;
            Assert.AreEqual(ScriptRunState.InvalidRunContext, Evaluate(inputs));
            inputs.ClassName = "Part";
            Assert.AreEqual(ScriptRunState.InvalidRunContext, Evaluate(inputs));
        }

        [Test]
        public void WireNames_AreStableAndDistinct()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ScriptRunState state in (ScriptRunState[])Enum.GetValues(typeof(ScriptRunState)))
            {
                Assert.IsTrue(names.Add(ScriptRunRules.ToWireName(state)), state.ToString());
            }

            Assert.AreEqual("not-on-this-side", ScriptRunRules.ToWireName(ScriptRunState.NotOnThisSide));
            Assert.AreEqual("waiting-for-local-player",
                ScriptRunRules.ToWireName(ScriptRunState.WaitingForLocalPlayer));
            Assert.AreEqual("detached", ScriptRunRules.ToWireName(ScriptRunState.Detached));
        }

        [Test]
        public void DefaultContainer_ServerSideInServerScriptService_ClientAndSharedInReplicatedStorage()
        {
            Assert.AreEqual("ServerScriptService", ScriptRunRules.DefaultContainerClass(ScriptContext.Server));
            Assert.AreEqual("ServerScriptService", ScriptRunRules.DefaultContainerClass(ScriptContext.Legacy));
            Assert.AreEqual("ReplicatedStorage", ScriptRunRules.DefaultContainerClass(ScriptContext.Client));
            Assert.AreEqual("ReplicatedStorage", ScriptRunRules.DefaultContainerClass(ScriptContext.Shared));
        }

        [Test]
        public void Containers_NearestKnownContainerGoverns()
        {
            Assert.AreEqual(ScriptContainer.Workspace,
                ScriptContainers.FromAncestorClassNames(new string[] { "Part", "Model", "Workspace", "DataModel" }));
            Assert.AreEqual(ScriptContainer.StarterPlayerScripts,
                ScriptContainers.FromAncestorClassNames(new string[]
                    { "Folder", "StarterPlayerScripts", "StarterPlayer", "DataModel" }));
            Assert.AreEqual(ScriptContainer.Players,
                ScriptContainers.FromAncestorClassNames(new string[] { "ScreenGui", "PlayerGui", "Player", "Players", "DataModel" }));
        }

        [Test]
        public void Containers_NoContainerOrNoDataModel_IsReported()
        {
            Assert.AreEqual(ScriptContainer.Other,
                ScriptContainers.FromAncestorClassNames(new string[] { "Lighting", "DataModel" }));
            Assert.AreEqual(ScriptContainer.Other,
                ScriptContainers.FromAncestorClassNames(new string[] { "StarterPlayer", "DataModel" }));
            Assert.AreEqual(ScriptContainer.OutsideDataModel,
                ScriptContainers.FromAncestorClassNames(new string[] { "Folder", "Model" }));
            Assert.AreEqual(ScriptContainer.OutsideDataModel,
                ScriptContainers.FromAncestorClassNames(Array.Empty<string>()));
        }

        [Test]
        public void Containers_ServerOnlyAreTheTwoClientsNeverReceive()
        {
            foreach (ScriptContainer container in (ScriptContainer[])Enum.GetValues(typeof(ScriptContainer)))
            {
                bool expected = container == ScriptContainer.ServerStorage
                                || container == ScriptContainer.ServerScriptService;
                Assert.AreEqual(expected, ScriptContainers.IsServerOnly(container), container.ToString());
            }
        }
    }
}
