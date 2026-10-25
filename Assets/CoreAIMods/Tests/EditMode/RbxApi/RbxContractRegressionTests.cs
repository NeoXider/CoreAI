using System;
using System.Collections.Generic;
using CoreAI.Mods.Rbx.Datatypes;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Networking;
using CoreAI.Mods.Rbx.Instances.Scheduling;
using NUnit.Framework;

namespace CoreAI.Tests.EditMode.RbxApi
{
    /// <summary>Engine-free regressions for interpolation, cloning and remote destruction contracts.</summary>
    [TestFixture]
    public sealed class RbxContractRegressionTests
    {
        [TestCase(-1073741824, 1073741824)]
        [TestCase(1073741824, -1073741824)]
        [TestCase(int.MinValue, int.MaxValue)]
        [TestCase(int.MaxValue, int.MinValue)]
        public void RBX002_LerpPromotesOffsetsBeforeSubtractionAndPreservesEndpoints(int start, int end)
        {
            RbxUDim2 from = new(0.25f, start, 0.75f, end);
            RbxUDim2 to = new(0.75f, end, 0.25f, start);

            Assert.AreEqual(from, from.Lerp(to, 0f));
            Assert.AreEqual(to, from.Lerp(to, 1f));
            RbxUDim2 midpoint = from.Lerp(to, 0.5f);
            Assert.AreEqual(0, midpoint.X.Offset);
            Assert.AreEqual(0, midpoint.Y.Offset);
            Assert.AreEqual(0.5f, midpoint.X.Scale);
            Assert.AreEqual(0.5f, midpoint.Y.Scale);
        }

        [Test]
        public void RBX002_LerpKeepsEvenRoundingAndRejectsUnrepresentableResults()
        {
            RbxUDim2 from = RbxUDim2.FromOffset(0, 0);
            RbxUDim2 to = RbxUDim2.FromOffset(11, 5);
            Assert.AreEqual(RbxUDim2.FromOffset(6, 2), from.Lerp(to, 0.5f));
            Assert.AreEqual(RbxUDim2.FromOffset(-6, -2), from.Lerp(to, -0.5f));
            RbxUDim2 extreme = RbxUDim2.FromOffset(int.MaxValue, int.MinValue);
            Assert.Throws<RbxApiStubException>(() => from.Lerp(extreme, 2f));
            Assert.Throws<RbxApiStubException>(() => from.Lerp(to, float.NaN));
        }

        [TestCase(InstanceAccessScope.Owned)]
        [TestCase(InstanceAccessScope.SharedWritable)]
        [TestCase(InstanceAccessScope.HostProtected)]
        public void RBX005_CloneCopiesSubclassPropertiesAndAclWithoutConnections(InstanceAccessScope scope)
        {
            InstanceRegistry registry = new(worldAclVersion: InstanceRegistry.CurrentWorldAclVersion);
            string origin = OriginTag.FromMod("clone-mod");
            RbxInstance folder = registry.Create("Folder", "clone-mod", origin,
                ownerActorId: "clone-actor", accessScope: scope);
            RbxClickDetector detector = (RbxClickDetector)registry.Create("ClickDetector", "clone-mod",
                origin, ownerActorId: "clone-actor", accessScope: scope);
            RbxMaterialVariant variant = (RbxMaterialVariant)registry.Create("MaterialVariant", "clone-mod",
                origin, ownerActorId: "clone-actor", accessScope: scope);
            detector.Parent = folder;
            variant.Parent = folder;
            detector.MaxActivationDistance = 8d;
            variant.BaseMaterial = new RbxMaterialId("Brick", 848);
            variant.ColorMap = "color";
            variant.NormalMap = "normal";
            variant.RoughnessMap = "roughness";
            variant.MetalnessMap = "metalness";
            variant.StudsPerTile = 4f;
            ModScheduler scheduler = Scheduler();
            detector.MouseClick.BindScheduler(scheduler);
            variant.Changed.BindScheduler(scheduler);
            int clicks = 0;
            int sourceChanges = 0;
            RbxScriptConnection click = detector.MouseClick.Connect((Action<object[]>)(_ => clicks++));
            RbxScriptConnection changed = variant.Changed.Connect((Action<object[]>)(_ => sourceChanges++));

            RbxInstance clone = folder.Clone();
            RbxClickDetector clonedDetector = (RbxClickDetector)clone.FindFirstChild("ClickDetector");
            RbxMaterialVariant clonedVariant = (RbxMaterialVariant)clone.FindFirstChild("MaterialVariant");

            Assert.IsNull(clone.Parent);
            Assert.AreEqual(8d, clonedDetector.MaxActivationDistance);
            Assert.AreEqual(variant.BaseMaterial, clonedVariant.BaseMaterial);
            Assert.AreEqual("color", clonedVariant.ColorMap);
            Assert.AreEqual("normal", clonedVariant.NormalMap);
            Assert.AreEqual("roughness", clonedVariant.RoughnessMap);
            Assert.AreEqual("metalness", clonedVariant.MetalnessMap);
            Assert.AreEqual(4f, clonedVariant.StudsPerTile);
            foreach (RbxInstance copied in new[] { clone, clonedDetector, clonedVariant })
            {
                InstanceRecord record = registry.GetRecord(copied.Id);
                Assert.AreEqual("clone-mod", record.OwnerModId);
                Assert.AreEqual(origin, record.OriginTag);
                Assert.AreEqual("clone-actor", record.OwnerActorId);
                Assert.AreEqual(scope, record.AccessScope);
            }

            Assert.AreNotEqual(folder.Id, clone.Id);
            Assert.AreNotEqual(detector.Id, clonedDetector.Id);
            Assert.AreNotEqual(variant.Id, clonedVariant.Id);
            clonedDetector.MouseClick.BindScheduler(scheduler);
            clonedDetector.MouseClick.Fire();
            clonedVariant.ColorMap = "clone-only";
            scheduler.Advance(0.016d);
            Assert.AreEqual(0, clicks);
            Assert.AreEqual(0, sourceChanges);
            Assert.AreEqual("color", variant.ColorMap);
            Assert.IsTrue(click.Connected);
            Assert.IsTrue(changed.Connected);
            clone.Destroy();
            folder.Destroy();
        }

        [TestCase("RemoteEvent", false)]
        [TestCase("RemoteEvent", true)]
        [TestCase("UnreliableRemoteEvent", false)]
        [TestCase("UnreliableRemoteEvent", true)]
        public void RBX006_DestroyDisconnectsAllRemoteSignalsAndPreservesQueuedCalls(
            string className, bool destroyAncestor)
        {
            InstanceRegistry registry = new();
            RbxInstance ancestor = registry.Create("Folder");
            RbxRemoteEvent remote = (RbxRemoteEvent)registry.Create(className);
            remote.Parent = ancestor;
            ModScheduler scheduler = Scheduler();
            remote.AttachScheduler(scheduler);
            List<string> delivered = new();
            int faults = 0;
            scheduler.HostFaulted += (_, _) => faults++;
            RbxScriptSignal clientA = remote.GetOnClientEvent("a");
            RbxScriptSignal clientB = remote.GetOnClientEvent("b");
            RbxScriptConnection server = remote.OnServerEvent.Connect((Action<object[]>)(_ =>
            {
                delivered.Add("server");
                remote.Destroy();
                throw new InvalidOperationException("Expected queued callback failure.");
            }));
            RbxScriptConnection first = clientA.Connect((Action<object[]>)(_ => delivered.Add("a")));
            RbxScriptConnection second = clientB.Connect((Action<object[]>)(_ => delivered.Add("b")));
            remote.OnServerEvent.Fire();
            clientA.Fire();
            clientB.Fire();

            (destroyAncestor ? ancestor : remote).Destroy();
            remote.Destroy();

            Assert.IsFalse(server.Connected);
            Assert.IsFalse(first.Connected);
            Assert.IsFalse(second.Connected);
            Assert.AreEqual(0, remote.ClientSignalCount);
            Assert.Throws<RbxError>(() => remote.GetOnClientEvent("new-actor"));
            remote.OnServerEvent.Fire();
            clientA.Fire();
            clientB.Fire();
            scheduler.Advance(0.016d);
            CollectionAssert.AreEqual(new[] { "server", "a", "b" }, delivered);
            Assert.AreEqual(1, faults);
            scheduler.Advance(0.016d);
            Assert.AreEqual(3, delivered.Count);
            ancestor.Destroy();
        }

        [TestCase("RemoteEvent")]
        [TestCase("UnreliableRemoteEvent")]
        public void RBX006_RepeatedRemoteDestroyDoesNotRetainLiveConnectionLedgerEntries(string className)
        {
            InstanceRegistry registry = new();
            ModScheduler scheduler = Scheduler();
            ModConnectionRegistry ledger = new();
            int generation = ledger.BeginGeneration("remote-mod");
            for (int index = 0; index < 128; index++)
            {
                RbxRemoteEvent remote = (RbxRemoteEvent)registry.Create(className);
                remote.AttachScheduler(scheduler);
                ledger.Track("remote-mod", generation,
                    remote.OnServerEvent.Connect((Action<object[]>)(_ => { })));
                remote.GetOnClientEvent("unused-actor");
                remote.Destroy();
                Assert.AreEqual(0, remote.ClientSignalCount);
            }

            Assert.AreEqual(0, ledger.GetOwnedBy("remote-mod").Count);
            Assert.LessOrEqual(ledger.TrackedEntryCount("remote-mod"), 16);
        }

        private static ModScheduler Scheduler()
        {
            return new ModScheduler(new UnusedThreadFactory(), new RbxAccumulatingTimeSource());
        }

        private sealed class UnusedThreadFactory : IRbxScriptThreadFactory
        {
            public IRbxScriptThread Create(string ownerModId, object callable)
            {
                throw new InvalidOperationException("The contract probe must not create script threads.");
            }
        }
    }
}
