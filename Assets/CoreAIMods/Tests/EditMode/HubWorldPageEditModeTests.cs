#if COREAI_HAS_HUB
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using CoreAI.Ai;
using CoreAI.Ai.Hub;
using CoreAI.Authority;
using CoreAI.Mods.WorldPackages;
using Cysharp.Threading.Tasks;
using NUnit.Framework;
using UnityEngine.UIElements;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// The Hub's World page puts Save, Load and New world on one page: Save writes the live world to the slot in its
    /// field, the slot is listed under Saved worlds and its Load goes through the pending confirmation, and New world
    /// asks first and reaches the world service only after the second click.
    /// </summary>
    [TestFixture]
    public sealed class HubWorldPageEditModeTests
    {
        [Test]
        public void WorldPage_IsTheWorldTab_WithSaveLoadAndNewWorldTogether()
        {
            PlayerWorldService service = new();
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();

                Assert.AreEqual("World", page.DisplayName);
                Assert.IsNotNull(root.Q<Button>("coreai-world-save"));
                Assert.IsNotNull(root.Q<TextField>("coreai-world-save-slot"));
                Assert.IsNotNull(root.Q<Button>("coreai-world-new"));
                Assert.IsNotNull(root.Q<VisualElement>("coreai-manual-saves-rows"));
                Assert.IsNotNull(root.Q<VisualElement>("coreai-autosaves-rows"));
                Assert.IsNotNull(root.Q<Label>("coreai-manual-saves-empty"), "no saved world yet says so");
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldPage_SaveThenLoad_SavesTheSlot_ListsIt_AndLoadsItAfterTheConfirmation()
        {
            PlayerWorldService service = new();
            ActorContext actor = new LocalActorIdentityProvider("hub-player").GetActorContext(BuiltInAgentRoleIds.Programmer);
            HubWorldLoadConfirmationPage page = new(service, actorContext: actor);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                root.Q<TextField>("coreai-world-save-slot").value = "castle";

                InvokeButton(root.Q<Button>("coreai-world-save"));

                CollectionAssert.AreEqual(new[] { "castle" }, service.SavedSlots);
                Assert.AreEqual("hub-player", service.SaveCallers[0].ActorId, "the page saves as its own actor");
                StringAssert.Contains("Saved the live world as 'castle'", Outcome(root));
                StringAssert.StartsWith("world-", root.Q<TextField>("coreai-world-save-slot").value,
                    "the field offers a fresh name for the next save");
                Button load = root.Q<Button>("coreai-manual-save-load-castle");
                Assert.IsNotNull(load, "the saved world is listed with its Load");
                Assert.AreEqual(0, service.AppliedCount);

                InvokeButton(load);

                Assert.AreEqual(0, service.AppliedCount, "Load only asks; the world changes after the confirmation");
                RbxPendingWorldLoadRequest pending = service.GetPendingManualLoads()[0];
                Assert.AreEqual("castle", pending.Slot);
                InvokeButton(root.Q<Button>("coreai-world-load-confirm-" + pending.RequestId));

                Assert.AreEqual(1, service.AppliedCount);
                Assert.AreEqual("castle", service.LiveWorld);
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldPage_ARefusedSave_SaysWhy_AndListsNothing()
        {
            PlayerWorldService service = new() { SaveError = "Manual slot 'castle' already exists and cannot be overwritten." };
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                root.Q<TextField>("coreai-world-save-slot").value = "castle";

                InvokeButton(root.Q<Button>("coreai-world-save"));

                StringAssert.Contains("The world was not saved: Manual slot 'castle' already exists", Outcome(root));
                Assert.IsNull(root.Q<Button>("coreai-manual-save-load-castle"));
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldPage_NewWorld_AsksFirst_AndOnlyTheSecondClickReachesTheService()
        {
            PlayerWorldService service = new();
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();
                VisualElement confirmation = root.Q<VisualElement>("coreai-world-new-confirmation");
                Assert.AreEqual(DisplayStyle.None, confirmation.style.display.value);

                InvokeButton(root.Q<Button>("coreai-world-new"));

                Assert.AreEqual(DisplayStyle.Flex, confirmation.style.display.value, "the first click asks");
                Assert.AreEqual(0, service.NewWorldCalls, "nothing changes before the player confirms");

                InvokeButton(root.Q<Button>("coreai-world-new-cancel"));

                Assert.AreEqual(DisplayStyle.None, confirmation.style.display.value);
                Assert.AreEqual(0, service.NewWorldCalls, "Cancel changes nothing");
                Assert.AreEqual("old", service.LiveWorld);

                InvokeButton(root.Q<Button>("coreai-world-new"));
                InvokeButton(root.Q<Button>("coreai-world-new-confirm"));

                Assert.AreEqual(1, service.NewWorldCalls);
                CollectionAssert.AreEqual(new[] { true }, service.NewWorldConfirmations);
                Assert.AreEqual("new", service.LiveWorld);
                Assert.AreEqual(
                    "A new world is open; active mods started: 3. It also opens on the next start.",
                    Outcome(root));
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void WorldPage_ServiceWithoutPlayerActions_KeepsSave_ButHasNoNewWorldOrSavedWorlds()
        {
            LoadOnlyWorldService service = new();
            HubWorldLoadConfirmationPage page = new(service);
            try
            {
                VisualElement root = (VisualElement)page.CreatePageContent();

                Assert.IsNotNull(root.Q<Button>("coreai-world-save"));
                Assert.IsNull(root.Q<Button>("coreai-world-new"));
                Assert.IsNull(root.Q<VisualElement>("coreai-manual-saves-rows"));
            }
            finally
            {
                page.OnDestroyed();
            }
        }

        [Test]
        public void DescribeNewWorld_SaysWhetherTheNewWorldReopens()
        {
            StringAssert.EndsWith("It also opens on the next start.",
                HubWorldLoadConfirmationPage.DescribeNewWorld(new RbxWorldLoadResult(true, "", 2, true, "")));
            StringAssert.EndsWith("It will NOT reopen after a restart: this world is not kept between starts.",
                HubWorldLoadConfirmationPage.DescribeNewWorld(
                    new RbxWorldLoadResult(true, "", 2, false, "this world is not kept between starts")));
        }

        private static string Outcome(VisualElement root)
        {
            return root.Q<Label>("coreai-world-load-outcome").text;
        }

        private static void InvokeButton(Button button)
        {
            Assert.IsNotNull(button);
            MethodInfo invoke = typeof(Clickable).GetMethod(
                "Invoke",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(EventBase) },
                null);
            Assert.IsNotNull(invoke, "Unity UI Toolkit Clickable.Invoke(EventBase) must be available.");
            invoke.Invoke(button.clickable, new object[] { null });
        }

        /// <summary>A world service without the player surface, as a host composing its own may have.</summary>
        private class LoadOnlyWorldService : IRbxWorldRuntimeService
        {
            protected readonly List<RbxPendingWorldLoadRequest> Pending = new();
            private int _nextRequest;

            public event Action<RbxPendingWorldLoadRequest> ManualLoadConfirmationRequested;

            public List<string> SavedSlots { get; } = new();

            public List<ActorContext> SaveCallers { get; } = new();

            public string SaveError { get; set; }

            public int AppliedCount { get; private set; }

            public string LiveWorld { get; protected set; } = "old";

            public RbxWorldPackagePayload CaptureCurrent()
            {
                return null;
            }

            public IReadOnlyList<RbxPendingWorldLoadRequest> GetPendingManualLoads()
            {
                return Pending.ToArray();
            }

            public IReadOnlyList<RbxAutoSaveInfo> ListAutoSaves()
            {
                return Array.Empty<RbxAutoSaveInfo>();
            }

            public UniTask<RbxWorldPackageWriteResult> SaveManualAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                SaveCallers.Add(caller);
                if (SaveError != null)
                {
                    return UniTask.FromResult(new RbxWorldPackageWriteResult(false, "", SaveError));
                }

                SavedSlots.Add(slot);
                return UniTask.FromResult(new RbxWorldPackageWriteResult(true, slot + ".world", ""));
            }

            public UniTask<RbxWorldLoadRequest> RequestManualLoadAsync(
                ActorContext caller,
                string slot,
                CancellationToken cancellationToken = default)
            {
                _nextRequest++;
                DateTime now = new(2035, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                RbxPendingWorldLoadRequest pending = new("request-" + _nextRequest, slot, slot, now, now.AddMinutes(1));
                Pending.Add(pending);
                ManualLoadConfirmationRequested?.Invoke(pending);
                return UniTask.FromResult(new RbxWorldLoadRequest(
                    pending.RequestId, slot, slot, pending.RequestedAtUtc, pending.ExpiresAtUtc));
            }

            public UniTask<RbxWorldLoadRequest> RequestAutoLoadAsync(
                ActorContext caller,
                string autoFileName,
                CancellationToken cancellationToken = default)
            {
                return RequestManualLoadAsync(caller, autoFileName, cancellationToken);
            }

            public UniTask<RbxWorldLoadResult> ConfirmManualLoadAsync(
                string requestId,
                bool playerConfirmed,
                CancellationToken cancellationToken = default)
            {
                int index = Pending.FindIndex(request => request.RequestId == requestId);
                if (index < 0)
                {
                    return UniTask.FromResult(new RbxWorldLoadResult(false, "unknown", 0));
                }

                string slot = Pending[index].Slot;
                Pending.RemoveAt(index);
                if (!playerConfirmed)
                {
                    return UniTask.FromResult(new RbxWorldLoadResult(false, "rejected", 0));
                }

                AppliedCount++;
                LiveWorld = slot;
                return UniTask.FromResult(new RbxWorldLoadResult(true, "", 1));
            }

            public UniTask<RbxWorldLoadResult> LoadConfirmedAsync(
                RbxWorldPackagePayload payload,
                CancellationToken cancellationToken = default)
            {
                throw new NotSupportedException();
            }
        }

        /// <summary>A world service with the player surface, as the production session controller has.</summary>
        private sealed class PlayerWorldService : LoadOnlyWorldService, IRbxWorldPlayerActions
        {
            public int NewWorldCalls { get; private set; }

            public List<bool> NewWorldConfirmations { get; } = new();

            public IReadOnlyList<string> ListManualSaves()
            {
                return SavedSlots.ToArray();
            }

            public UniTask<RbxWorldLoadResult> OpenNewWorldAsync(
                bool playerConfirmed,
                CancellationToken cancellationToken = default)
            {
                NewWorldCalls++;
                NewWorldConfirmations.Add(playerConfirmed);
                if (playerConfirmed)
                {
                    LiveWorld = "new";
                }

                return UniTask.FromResult(new RbxWorldLoadResult(true, "", 3, true, ""));
            }
        }
    }
}
#endif
