using System;
using System.Reflection;
using CoreAI.Hub.UI;
using CoreAI.Infrastructure.World;
using NUnit.Framework;
using UnityEngine.UIElements;
#if COREAI_HAS_LLMUNITY
using CoreAI.WebGl;
#endif

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Verifies truthful local-operation/automatic-persistence receipts without an IndexedDB
    /// completion claim, plus bounded unsupported-local-model scene scanning. Browser durability
    /// requires a separate real reload check; Unity supplies no browser-completion callback here.
    /// </summary>
    public sealed class CoreAiWebGlQaEditModeTests
    {
        /// <summary>
        /// Controls a synchronous operation receipt, including callbacks before the operation returns.
        /// Global durability confirmation deliberately returns true to detect false promotion of failures.
        /// </summary>
        private sealed class ControlledReceiptWorldStateManager : IWorldStateManager, IWorldStateWriteResults
        {
            public bool HasSavedState { get; set; }
            public WorldStateWriteResult Result = new(WorldStateWriteStatus.Succeeded, true);
            public Exception Failure;
            public Action BeforeReceipt;

            public bool WorldRestoreCompleted => true;

            public int SaveCount { get; private set; }

            public int ResetCount { get; private set; }

            public int ConfirmationCalls { get; private set; }

            public event Action StateReset;

            public event Action RestoreCompleted
            {
                add { }
                remove { }
            }

            public void Save() => SaveWithResult();
            public void Reset() => ResetWithResult();
            public WorldStateWriteResult SaveWithResult() => Write(false);
            public WorldStateWriteResult ResetWithResult() => Write(true);

            public bool TryLoad(string sceneName = null)
            {
                return false;
            }

            private WorldStateWriteResult Write(bool reset)
            {
                if (reset) { ResetCount++; }
                else { SaveCount++; }
                if (Failure != null) { throw Failure; }
                if (Result.LocalOperationSucceeded)
                {
                    HasSavedState = !reset;
                    if (reset) { StateReset?.Invoke(); }
                }

                BeforeReceipt?.Invoke();
                return Result;
            }

            public void StartAutoSave(float intervalSeconds)
            {
            }

            public void ConfirmDurability(Action<bool> onConfirmed)
            {
                ConfirmationCalls++;
                onConfirmed(true);
            }
        }

        [Test]
        public void StatusText_WhileLocalOperationIsPending_KeepsTheLastInventoryText()
        {
            Assert.AreEqual(
                "Has saved state: No",
                WorldStateHubPage.ComposeStatusText(true, "Has saved state: No", true),
                "An operation that has not returned must not publish its changed inventory.");
            Assert.AreEqual(
                "Has saved state: Yes",
                WorldStateHubPage.ComposeStatusText(true, "Has saved state: Yes", false),
                "Reset World must also retain the previous inventory until its operation returns.");
            Assert.AreEqual(
                "Has saved state: Yes",
                WorldStateHubPage.ComposeStatusText(false, "Has saved state: No", true),
                "Completed inventory reports local presence without asserting browser durability.");
            Assert.AreEqual(
                "Has saved state: No",
                WorldStateHubPage.ComposeStatusText(false, "Has saved state: Yes", false));
        }

        [Test]
        public void SaveNow_LocalWriteAndArmingReceiptDoesNotClaimBrowserCompletion()
        {
            ControlledReceiptWorldStateManager manager = new();
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Label status = FindByName<Label>(panel, "coreai-hub-worldstate-status");
            Button save = FindByName<Button>(panel, "coreai-hub-worldstate-save");

            Click(save);

            Assert.AreEqual(1, manager.SaveCount, "Save Now must still perform the write.");
            Assert.AreEqual(0, manager.ConfirmationCalls);
            Assert.AreEqual("Has saved state: Yes", status.text);
            Label durability = FindByName<Label>(panel, "coreai-hub-worldstate-durability");
            Assert.AreEqual(WorldStateHubPage.FlushConfirmedText, durability.text);
            StringAssert.Contains("automatic persistence is armed", durability.text);
            StringAssert.Contains("Browser completion is not confirmed", durability.text);
            StringAssert.DoesNotContain("survives a reload", durability.text);
            page.OnDestroyed();
        }

        [TestCase(WorldStateWriteStatus.Failed, true, "unconfirmed")]
        [TestCase(WorldStateWriteStatus.Succeeded, false, "NOT armed")]
        public void SaveNow_FailedLocalWriteOrRefusedArmingCannotUseGlobalConfirmation(
            WorldStateWriteStatus status, bool armed, string expected)
        {
            ControlledReceiptWorldStateManager manager = new()
            {
                Result = new WorldStateWriteResult(status, armed, "write refused")
            };
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Label durability = FindByName<Label>(panel, "coreai-hub-worldstate-durability");
            Button save = FindByName<Button>(panel, "coreai-hub-worldstate-save");

            Click(save);
            StringAssert.Contains(expected, durability.text);
            Assert.AreEqual(0, manager.ConfirmationCalls,
                "A global true arming result cannot acknowledge a failed local operation.");
            Assert.IsTrue(save.enabledSelf);
            page.OnDestroyed();
        }

        [Test]
        public void ResetWorld_UpdatesLocalInventoryWithoutClaimingBrowserCommit()
        {
            ControlledReceiptWorldStateManager manager = new() { HasSavedState = true };
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Label status = FindByName<Label>(panel, "coreai-hub-worldstate-status");
            Button reset = FindByName<Button>(panel, "coreai-hub-worldstate-reset");
            Assert.AreEqual("Has saved state: Yes", status.text);

            Click(reset);

            Assert.AreEqual(1, manager.ResetCount);
            Assert.AreEqual("Has saved state: No", status.text);
            StringAssert.Contains("Browser completion is not confirmed",
                FindByName<Label>(panel, "coreai-hub-worldstate-durability").text);
            Assert.AreEqual(0, manager.ConfirmationCalls);
            page.OnDestroyed();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OperationReceipt_DisablesControlsAndSuppressesReentrantWritesUntilItReturns(bool resetOperation)
        {
            ControlledReceiptWorldStateManager manager = new() { HasSavedState = resetOperation };
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Button save = FindByName<Button>(panel, "coreai-hub-worldstate-save");
            Button reset = FindByName<Button>(panel, "coreai-hub-worldstate-reset");

            bool controlsDisabled = false;
            bool callbackCompleted = false;
            string pendingText = null;
            string inventoryText = null;
            int writesBeforeReceipt = 0;
            manager.BeforeReceipt = () =>
            {
                controlsDisabled = !save.enabledSelf && !reset.enabledSelf;
                pendingText = FindByName<Label>(panel, "coreai-hub-worldstate-durability").text;
                inventoryText = FindByName<Label>(panel, "coreai-hub-worldstate-status").text;
                Click(save);
                Click(reset);
                writesBeforeReceipt = manager.SaveCount + manager.ResetCount;
                callbackCompleted = true;
            };

            Click(resetOperation ? reset : save);

            Assert.IsTrue(callbackCompleted, "The controlled operation must reach the receipt boundary.");
            Assert.IsTrue(controlsDisabled);
            Assert.AreEqual(WorldStateHubPage.FlushPendingText, pendingText);
            Assert.AreEqual(resetOperation ? "Has saved state: Yes" : "Has saved state: No", inventoryText);
            Assert.AreEqual(1, writesBeforeReceipt,
                "Reentrant actions cannot start a second write before the current receipt returns.");
            Assert.IsTrue(save.enabledSelf, "Controls must re-enable after the operation returns.");
            Assert.IsTrue(reset.enabledSelf);
            Assert.AreEqual(0, manager.ConfirmationCalls);
            page.OnDestroyed();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void OperationExceptionOrCancellationRestoresControlsAndAllowsAnHonestRetry(bool cancelled)
        {
            ControlledReceiptWorldStateManager manager = new()
            {
                Failure = cancelled ? new OperationCanceledException() : new InvalidOperationException("write refused")
            };
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Button save = FindByName<Button>(panel, "coreai-hub-worldstate-save");
            Label durability = FindByName<Label>(panel, "coreai-hub-worldstate-durability");
            Click(save);
            StringAssert.Contains(cancelled ? "cancelled" : "write refused", durability.text);
            Assert.IsTrue(save.enabledSelf);
            manager.Failure = null;
            Click(save);
            Assert.AreEqual(2, manager.SaveCount);
            Assert.AreEqual(WorldStateHubPage.FlushConfirmedText, durability.text);
            Assert.AreEqual(0, manager.ConfirmationCalls);
            page.OnDestroyed();
        }

#if COREAI_HAS_LLMUNITY
        [Test]
        public void LlmUnitySceneGuard_DoesNotRescanEveryFrame_AfterTheSettlingWindow()
        {
            Assert.IsTrue(
                CoreAiWebGlLlmUnitySceneGuard.ShouldRescan(0, 0f),
                "The frames right after a scene load must still rescan: LLMUnity behaviours are " +
                "disabled post-Awake.");
            Assert.IsTrue(
                CoreAiWebGlLlmUnitySceneGuard.ShouldRescan(
                    CoreAiWebGlLlmUnitySceneGuard.SettlingFrames - 1, 0f));

            Assert.IsFalse(
                CoreAiWebGlLlmUnitySceneGuard.ShouldRescan(
                    CoreAiWebGlLlmUnitySceneGuard.SettlingFrames, 0f),
                "Past the settling window a FindObjectsByType<MonoBehaviour>(Include) sweep of the " +
                "whole scene on every browser frame is unbounded per-frame cost and garbage.");
            Assert.IsFalse(
                CoreAiWebGlLlmUnitySceneGuard.ShouldRescan(
                    int.MaxValue,
                    CoreAiWebGlLlmUnitySceneGuard.RescanIntervalSeconds - 0.01f));

            Assert.IsTrue(
                CoreAiWebGlLlmUnitySceneGuard.ShouldRescan(
                    int.MaxValue,
                    CoreAiWebGlLlmUnitySceneGuard.RescanIntervalSeconds),
                "Containment must not stop: a later additive load still gets swept.");
        }
#endif

        private static void Click(Button button)
        {
            Assert.IsNotNull(button);
            // WHY: UIElements pointer events need a live panel, which an EditMode fixture has not
            // built; the page's own click handler is the unit under test, so invoke the delegate the
            // Button was constructed with, read straight off the Clickable.
            Clickable clickable = button.clickable;
            Assert.IsNotNull(clickable, "Button.clickable was not set.");
            Action clicked = null;
            for (Type type = clickable.GetType(); type != null && clicked == null; type = type.BaseType)
            {
                foreach (FieldInfo field in type.GetFields(
                             BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (field.FieldType == typeof(Action) && field.GetValue(clickable) is Action handler)
                    {
                        clicked = handler;
                        break;
                    }
                }
            }

            Assert.IsNotNull(clicked, "No click handler is bound to the button.");
            clicked.Invoke();
        }

        private static T FindByName<T>(VisualElement root, string name) where T : VisualElement
        {
            T found = root.Q<T>(name);
            Assert.IsNotNull(found, $"Element '{name}' not found on the World page.");
            return found;
        }
    }
}
