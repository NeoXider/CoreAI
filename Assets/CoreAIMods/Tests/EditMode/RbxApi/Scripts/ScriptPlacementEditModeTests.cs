using System.Collections.Generic;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Scripts;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi.Scripts
{
    /// <summary>
    /// MVP4 step C1-1, engine-free: the placement planner (rules R1 and R2, the SC-5 context precedence, the SC-6
    /// default placement and <c>parent:</c> paths, the load refusals), the run states a listing reports, and the
    /// package format choice of rule R4. Every rule has its negative twin.
    /// </summary>
    [TestFixture]
    public sealed class ScriptPlacementEditModeTests
    {
        private static ScriptPlacementRequest Request(string headerContext = "", string headerParent = "",
            string fallbackContext = "", params string[] folders)
        {
            return new ScriptPlacementRequest
            {
                SourceId = "door",
                HeaderContext = headerContext,
                HeaderParent = headerParent,
                FallbackContext = fallbackContext,
                FolderSegments = folders
            };
        }

        private static ScriptPlacementRequest Bound(ScriptPlacementRequest request, string className, int runContext)
        {
            request.HasBoundInstance = true;
            request.BoundClassName = className;
            request.BoundRunContext = runContext;
            return request;
        }

        private static ScriptPlacementPlan Plan(ScriptPlacementRequest request)
        {
            bool planned = ScriptPlacementPlanner.TryPlan(in request, out ScriptPlacementPlan plan, out RbxError refusal);
            Assert.IsTrue(planned, refusal?.Message);
            Assert.IsNull(refusal);
            return plan;
        }

        private static RbxError Refusal(ScriptPlacementRequest request)
        {
            bool planned = ScriptPlacementPlanner.TryPlan(in request, out ScriptPlacementPlan plan, out RbxError refusal);
            Assert.IsFalse(planned);
            Assert.IsNull(plan);
            Assert.IsNotNull(refusal);
            return refusal;
        }

        // ---- R1 and R2 ------------------------------------------------------------------------

        [Test]
        public void C1_R1_ASourceWithoutContextOrParent_KeepsTheSyntheticPlacement()
        {
            ScriptPlacementPlan plan = Plan(Request(folders: new[] { "Games", "Arena" }));

            Assert.AreSame(ScriptPlacementPlan.Synthetic, plan);
            Assert.AreEqual(ScriptPlacementKind.Synthetic, plan.Kind);
            Assert.AreEqual(ScriptContext.Legacy, plan.Context);
            Assert.IsNull(plan.ClassName);
            Assert.IsNull(plan.DefaultPath, "the synthetic placement ignores the folder, as before MVP4");
        }

        [Test]
        public void C1_R2_AParentAlone_PlacesADurableLegacyScriptAtThePath()
        {
            ScriptPlacementPlan plan = Plan(Request(headerParent: "Workspace/Door"));

            Assert.AreEqual(ScriptPlacementKind.Durable, plan.Kind);
            Assert.AreEqual(ScriptContext.Legacy, plan.Context, "a parent: declares where, not which side");
            Assert.AreEqual(ScriptClassNames.Script, plan.ClassName);
            Assert.AreEqual(RunContextValues.Legacy, plan.RunContext);
            CollectionAssert.AreEqual(new[] { "Workspace", "Door" }, plan.ParentPath);
            Assert.IsNull(plan.DefaultPath);
            Assert.IsFalse(plan.ReusesBoundInstance);
        }

        [Test]
        public void C1_R2_DeclaredServer_CreatesAServerScriptUnderServerScriptServiceAndItsFolder()
        {
            ScriptPlacementPlan plan = Plan(Request("server", folders: new[] { "Games", "Arena" }));

            Assert.AreEqual(ScriptPlacementKind.Durable, plan.Kind);
            Assert.AreEqual(ScriptContext.Server, plan.Context);
            Assert.AreEqual(ScriptClassNames.Script, plan.ClassName);
            Assert.AreEqual(RunContextValues.Server, plan.RunContext);
            CollectionAssert.AreEqual(new[] { "ServerScriptService", "Games", "Arena" }, plan.DefaultPath);
            Assert.IsNull(plan.ParentPath);
            Assert.IsNull(plan.PreviousDefaultPath, "a new instance has no previous placement");
        }

        [Test]
        public void C1_R2_DeclaredClient_CaseInsensitive_CreatesAClientScriptInReplicatedStorage()
        {
            ScriptPlacementPlan plan = Plan(Request(" CLIENT "));

            Assert.AreEqual(ScriptContext.Client, plan.Context);
            Assert.AreEqual(ScriptClassNames.Script, plan.ClassName);
            Assert.AreEqual(RunContextValues.Client, plan.RunContext);
            CollectionAssert.AreEqual(new[] { "ReplicatedStorage" }, plan.DefaultPath);
        }

        [Test]
        public void C1_R2_DeclaredContextAndParent_UsesTheParentPath()
        {
            ScriptPlacementPlan plan = Plan(Request("client", "@game/Workspace/Lobby", "", "Games"));

            CollectionAssert.AreEqual(new[] { "Workspace", "Lobby" }, plan.ParentPath);
            Assert.IsNull(plan.DefaultPath, "a parent: replaces the default container and the folder");
            Assert.AreEqual(RunContextValues.Client, plan.RunContext);
        }

        // ---- refusals ------------------------------------------------------------------------

        [Test]
        public void C1_UnknownContextValue_IsBadArgumentNamingIt()
        {
            RbxError refusal = Refusal(Request("clinet"));

            Assert.AreEqual(RbxErrorCode.BadArgument, refusal.Code);
            StringAssert.Contains("@coreai context 'clinet' is not server, client or shared", refusal.Message);
            StringAssert.Contains("fix:", refusal.Message);
        }

        [Test]
        public void C1_6_SharedContext_PlacesAModuleScriptInReplicatedStorageUnderTheFolder()
        {
            ScriptPlacementPlan plan = Plan(Request("shared", folders: new[] { "Games" }));

            Assert.AreEqual(ScriptPlacementKind.Durable, plan.Kind);
            Assert.AreEqual(ScriptContext.Shared, plan.Context);
            Assert.AreEqual(ScriptClassNames.ModuleScript, plan.ClassName);
            Assert.AreEqual(RunContextValues.Legacy, plan.RunContext, "a ModuleScript has no RunContext of its own");
            Assert.IsFalse(plan.ReusesBoundInstance);
            CollectionAssert.AreEqual(new[] { "ReplicatedStorage", "Games" }, plan.DefaultPath);
        }

        [Test]
        public void C1_6_ABoundModuleScriptWithAHeaderThatDeclaresNothing_StaysShared_AndIsReused()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request(), ScriptClassNames.ModuleScript, RunContextValues.Legacy));

            Assert.AreEqual(ScriptContext.Shared, plan.Context, "SC-5: the bound instance decides without a header context");
            Assert.IsTrue(plan.ReusesBoundInstance);
            Assert.IsFalse(plan.ReplacesBoundInstance);
            Assert.AreEqual(ScriptClassNames.ModuleScript, plan.ClassName);
        }

        [TestCase("Workspace//Door", "empty component")]
        [TestCase("Workspace/./Door", "'.' component")]
        [TestCase("Workspace/../Lighting", "'..' component")]
        [TestCase("@game/", "names no instance")]
        public void C1_MalformedParentPath_IsBadArgumentNamingThePath(string parent, string detail)
        {
            RbxError refusal = Refusal(Request("server", parent));

            Assert.AreEqual(RbxErrorCode.BadArgument, refusal.Code);
            StringAssert.Contains("@coreai parent '" + parent.Trim() + "'", refusal.Message);
            StringAssert.Contains(detail, refusal.Message);
        }

        [Test]
        public void C1_Negative_ABlankParent_IsNoDirective()
        {
            Assert.AreSame(ScriptPlacementPlan.Synthetic, Plan(Request(headerParent: "   ")));
            ScriptPlacementPlan plan = Plan(Request("server", "   "));
            Assert.IsNull(plan.ParentPath, "a blank parent: leaves the server script at its default placement");
            CollectionAssert.AreEqual(new[] { "ServerScriptService" }, plan.DefaultPath);
        }

        [Test]
        public void C1_ParentPath_ParsesNamesFromGame_KeepingInnerSpaces()
        {
            Assert.IsTrue(ScriptPlacementPlanner.TryParseParentPath(" Workspace/Door Frame ",
                out IReadOnlyList<string> path, out string error), error);
            CollectionAssert.AreEqual(new[] { "Workspace", "Door Frame" }, path);
            Assert.IsTrue(ScriptPlacementPlanner.TryParseParentPath("@game/ServerScriptService", out path, out error));
            CollectionAssert.AreEqual(new[] { "ServerScriptService" }, path);
        }

        // ---- SC-5 precedence -----------------------------------------------------------------

        [Test]
        public void SC_5_WithoutAHeaderContext_TheBoundInstanceDecides_AndIsReused()
        {
            ScriptPlacementRequest request = Bound(Request(fallbackContext: "server"), ScriptClassNames.Script,
                RunContextValues.Client);

            ScriptPlacementPlan plan = Plan(request);

            Assert.AreEqual(ScriptContext.Client, plan.Context, "the instance outranks the stored context");
            Assert.IsTrue(plan.ReusesBoundInstance);
            Assert.AreEqual(RunContextValues.Client, plan.RunContext);
        }

        [Test]
        public void SC_5_AHeaderContext_OutranksTheBoundInstance_AndRewritesItsRunContext()
        {
            ScriptPlacementRequest request = Bound(Request("server", folders: new[] { "Games" }),
                ScriptClassNames.Script, RunContextValues.Client);
            request.PreviousFolderSegments = new[] { "Old" };

            ScriptPlacementPlan plan = Plan(request);

            Assert.AreEqual(ScriptContext.Server, plan.Context);
            Assert.AreEqual(RunContextValues.Server, plan.RunContext);
            CollectionAssert.AreEqual(new[] { "ServerScriptService", "Games" }, plan.DefaultPath);
            CollectionAssert.AreEqual(new[] { "ReplicatedStorage", "Old" }, plan.PreviousDefaultPath,
                "the instance is looked for at the default placement of its current context and previous folder");
        }

        [Test]
        public void SC_5_WithoutHeaderOrInstance_TheFallbackContextDecides()
        {
            ScriptPlacementPlan plan = Plan(Request(fallbackContext: "client"));

            Assert.AreEqual(ScriptPlacementKind.Durable, plan.Kind);
            Assert.AreEqual(ScriptContext.Client, plan.Context);
        }

        [Test]
        public void SC_5_Negative_AnUnreadableFallbackContext_IsLegacy_NotARefusal()
        {
            Assert.AreSame(ScriptPlacementPlan.Synthetic, Plan(Request(fallbackContext: "clinet")));
        }

        [Test]
        public void SC_5_ABoundLegacyScript_StaysDurableAndLegacy_WhatEverTheFallbackSays()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request(fallbackContext: "client"), ScriptClassNames.Script,
                RunContextValues.Legacy));

            Assert.AreEqual(ScriptPlacementKind.Durable, plan.Kind, "a bound instance never falls back to the synthetic one");
            Assert.AreEqual(ScriptContext.Legacy, plan.Context);
            Assert.AreEqual(RunContextValues.Legacy, plan.RunContext);
        }

        [Test]
        public void SC_4_DeclaringServerOnALegacyScript_KeepsItsLegacyRunContext()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request("server"), ScriptClassNames.Script, RunContextValues.Legacy));

            Assert.AreEqual(ScriptContext.Server, plan.Context);
            Assert.AreEqual(RunContextValues.Legacy, plan.RunContext,
                "a Legacy Script is a server script (R1.5); declaring it changes the checks, not where it runs");
        }

        [Test]
        public void SC_4_DeclaringClientOnALegacyScript_SetsTheClientRunContext()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request("client"), ScriptClassNames.Script, RunContextValues.Legacy));

            Assert.AreEqual(RunContextValues.Client, plan.RunContext);
        }

        [Test]
        public void C1_ABoundLocalScriptDeclaredClient_IsReusedAsALocalScript()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request("client"), ScriptClassNames.LocalScript,
                RunContextValues.Legacy));

            Assert.AreEqual(ScriptClassNames.LocalScript, plan.ClassName);
            Assert.AreEqual(RunContextValues.Legacy, plan.RunContext, "a LocalScript keeps RunContext Legacy (R1.8)");
        }

        [Test]
        public void SC_4_ABoundLocalScriptDeclaredServer_IsReplacedByAServerScript()
        {
            ScriptPlacementRequest request = Bound(Request("server"), ScriptClassNames.LocalScript,
                RunContextValues.Legacy);

            ScriptPlacementPlan plan = Plan(request);

            Assert.IsTrue(plan.ReusesBoundInstance, "the source had a bound instance");
            Assert.IsTrue(plan.ReplacesBoundInstance);
            Assert.AreEqual(ScriptClassNames.Script, plan.ClassName);
            Assert.AreEqual(RunContextValues.Server, plan.RunContext);
            CollectionAssert.AreEqual(new[] { "ReplicatedStorage" }, plan.PreviousDefaultPath,
                "a LocalScript's default placement is the client one, so one still there moves to the server one");
            CollectionAssert.AreEqual(new[] { "ServerScriptService" }, plan.DefaultPath);
        }

        [Test]
        public void SC_4_ABoundModuleScriptDeclaredServer_IsReplacedByAServerScript()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request("server"), ScriptClassNames.ModuleScript,
                RunContextValues.Legacy));

            Assert.IsTrue(plan.ReplacesBoundInstance);
            Assert.AreEqual(ScriptClassNames.Script, plan.ClassName);
            Assert.AreEqual(RunContextValues.Server, plan.RunContext);
        }

        [Test]
        public void SC_4_ABoundServerScriptDeclaredShared_IsReplacedByAModuleScript()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request("shared"), ScriptClassNames.Script, RunContextValues.Server));

            Assert.IsTrue(plan.ReplacesBoundInstance);
            Assert.AreEqual(ScriptClassNames.ModuleScript, plan.ClassName);
            Assert.AreEqual(RunContextValues.Legacy, plan.RunContext);
        }

        [Test]
        public void SC_4_Negative_AServerScriptDeclaredClient_KeepsItsClass_AndOnlyChangesItsRunContext()
        {
            ScriptPlacementPlan plan = Plan(Bound(Request("client"), ScriptClassNames.Script, RunContextValues.Server));

            Assert.IsFalse(plan.ReplacesBoundInstance, "Script to Script needs no new instance");
            Assert.AreEqual(ScriptClassNames.Script, plan.ClassName);
            Assert.AreEqual(RunContextValues.Client, plan.RunContext);
        }

        // ---- run states ----------------------------------------------------------------------

        private static ScriptRunState Describe(bool loaded, bool active, ScriptContext declared, bool bound,
            bool quarantined = false, bool suspended = false)
        {
            ScriptRunFacts facts = new()
            {
                IsLoaded = loaded,
                IsActive = active,
                DeclaredContext = declared,
                HasBoundInstance = bound,
                IsQuarantined = quarantined,
                IsSuspended = suspended
            };
            return ScriptRunStates.Describe(in facts);
        }

        [Test]
        public void RunState_ALoadedSource_IsRunning_UnlessQuarantinedOrSuspended()
        {
            Assert.AreEqual(ScriptRunState.Running, Describe(true, true, ScriptContext.Legacy, false));
            Assert.AreEqual(ScriptRunState.Running, Describe(true, true, ScriptContext.Server, true));
            Assert.AreEqual(ScriptRunState.Quarantined, Describe(true, true, ScriptContext.Server, true, true, true));
            Assert.AreEqual(ScriptRunState.Suspended, Describe(true, false, ScriptContext.Server, true, false, true));
        }

        [Test]
        public void RunState_R3_AnActiveDeclaredSourceWithoutAnInstance_IsDetached()
        {
            Assert.AreEqual(ScriptRunState.Detached, Describe(false, true, ScriptContext.Client, false));
            Assert.AreEqual("detached", ScriptRunRules.ToWireName(Describe(false, true, ScriptContext.Server, false)));
        }

        [Test]
        public void RunState_R3_Negative_AnActiveLegacySourceWithoutAnInstance_IsNotDetached()
        {
            Assert.AreEqual(ScriptRunState.Unloaded, Describe(false, true, ScriptContext.Legacy, false),
                "a legacy source starts at the synthetic placement; it never needs a bound instance");
            Assert.AreEqual(ScriptRunState.Unloaded, Describe(false, false, ScriptContext.Client, false),
                "an inactive source is unloaded, whatever it declares");
            Assert.AreEqual(ScriptRunState.Suspended, Describe(false, false, ScriptContext.Client, false, false, true));
        }

        private static ScriptRunState DescribeRegistered(ScriptRunState notRunning, bool bound)
        {
            ScriptRunFacts facts = new()
            {
                IsLoaded = true,
                IsActive = true,
                HasBoundInstance = bound,
                IsRegisteredWithoutRun = true,
                NotRunningState = notRunning
            };
            return ScriptRunStates.Describe(in facts);
        }

        [TestCase(ScriptRunState.Module)]
        [TestCase(ScriptRunState.NotOnThisSide)]
        [TestCase(ScriptRunState.ContainerDoesNotRun)]
        [TestCase(ScriptRunState.Disabled)]
        public void RunState_C1_6_ASourceRegisteredWithoutARun_ReportsWhyItDidNotStart(ScriptRunState notRunning)
        {
            Assert.AreEqual(notRunning, DescribeRegistered(notRunning, true));
        }

        [Test]
        public void RunState_C1_6_ARegisteredSourceWhoseInstanceIsGone_IsDetached()
        {
            Assert.AreEqual(ScriptRunState.Detached, DescribeRegistered(ScriptRunState.Module, false),
                "a module whose ModuleScript was destroyed has nothing left to require");
            Assert.AreEqual(ScriptRunState.Detached, DescribeRegistered(ScriptRunState.Detached, false));
        }

        // ---- R4 ------------------------------------------------------------------------------

        private static List<InstanceSnapshot> Tree(params string[] classNames)
        {
            List<InstanceSnapshot> nodes = new();
            ulong id = 1;
            foreach (string className in classNames)
            {
                nodes.Add(new InstanceSnapshot { Id = id++, ClassName = className, Name = className });
            }

            return nodes;
        }

        [Test]
        public void R4_AWorldOfLegacyMods_NeedsNoScriptBindings()
        {
            List<InstanceSnapshot> tree = Tree("DataModel", "Workspace", "ServerScriptService", "Folder", "Part");

            Assert.IsFalse(ScriptPackageFormat.RequiresScriptBindings(tree, new[] { "", " ", null }));
            Assert.IsFalse(ScriptPackageFormat.RequiresScriptBindings(tree, null));
        }

        [TestCase("Script")]
        [TestCase("LocalScript")]
        [TestCase("ModuleScript")]
        public void R4_ASavedScriptInstance_NeedsScriptBindings(string className)
        {
            Assert.IsTrue(ScriptPackageFormat.RequiresScriptBindings(Tree("DataModel", className), new[] { "" }));
        }

        [Test]
        public void R4_AManifestThatDeclaresAContext_NeedsScriptBindings_EvenWithoutAnInstance()
        {
            Assert.IsTrue(ScriptPackageFormat.RequiresScriptBindings(Tree("DataModel"), new[] { "", "client" }),
                "a declared source without an instance must be written as detached, which only version 2 can say");
        }
    }
}
