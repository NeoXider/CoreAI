using System;
using System.Collections.Generic;
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
    /// Adversarial WebGL QA for the G11 §6.5 browser acceptance surface.
    /// <para>
    /// Two production defects are pinned here. (1) The Hub World page reported
    /// <c>Has saved state: Yes</c> the instant <c>Save Now</c> was pressed, i.e. at IDBFS
    /// request-issuance time, while the browser <c>FS.syncfs</c> callback had not run yet — MVP2.5
    /// gate W3.5 fails a build that "reports success before sync". The same applied to
    /// <c>Reset World</c> reporting <c>No</c>. (2) The unsupported-local-model scene guard rescanned
    /// every <c>MonoBehaviour</c> in the scene on every single frame of the browser player.
    /// </para>
    /// </summary>
    public sealed class CoreAiWebGlQaEditModeTests
    {
        /// <summary>
        /// World-state double whose durability confirmation is released by the test, standing in for
        /// the browser's asynchronous <c>FS.syncfs</c> completion callback.
        /// </summary>
        private sealed class DeferredDurabilityWorldStateManager : IWorldStateManager
        {
            private readonly List<Action<bool>> _pending = new();

            public bool HasSavedState { get; private set; }

            public bool WorldRestoreCompleted => true;

            public int SaveCount { get; private set; }

            public int ResetCount { get; private set; }

            public int PendingConfirmations => _pending.Count;

            public event Action StateReset;

            public event Action RestoreCompleted
            {
                add { }
                remove { }
            }

            public void Save()
            {
                SaveCount++;
                HasSavedState = true;
            }

            public bool TryLoad(string sceneName = null)
            {
                return false;
            }

            public void Reset()
            {
                ResetCount++;
                HasSavedState = false;
                StateReset?.Invoke();
            }

            public void StartAutoSave(float intervalSeconds)
            {
            }

            public void ConfirmDurability(Action<bool> onConfirmed)
            {
                _pending.Add(onConfirmed);
            }

            /// <summary>Releases every deferred confirmation, as the browser callback would.</summary>
            public void ReleaseDurability(bool durable)
            {
                List<Action<bool>> released = new(_pending);
                _pending.Clear();
                for (int index = 0; index < released.Count; index++)
                {
                    released[index]?.Invoke(durable);
                }
            }
        }

        [Test]
        public void StatusText_WhileTheBrowserFlushIsPending_KeepsTheLastConfirmedText()
        {
            Assert.AreEqual(
                "Has saved state: No",
                WorldStateHubPage.ComposeStatusText(true, "Has saved state: No", true),
                "W3.5: a pending IDBFS flush must not promote the label to Yes; the browser has not " +
                "confirmed the write, so the last confirmed text stands.");
            Assert.AreEqual(
                "Has saved state: Yes",
                WorldStateHubPage.ComposeStatusText(true, "Has saved state: Yes", false),
                "The same rule in reverse: Reset World must not publish No before the delete is flushed.");
            Assert.AreEqual(
                "Has saved state: Yes",
                WorldStateHubPage.ComposeStatusText(false, "Has saved state: No", true),
                "Once the callback has arrived the label reports the real state.");
            Assert.AreEqual(
                "Has saved state: No",
                WorldStateHubPage.ComposeStatusText(false, "Has saved state: Yes", false));
        }

        [Test]
        public void SaveNow_DoesNotClaimSavedState_BeforeTheBrowserFlushCallback()
        {
            DeferredDurabilityWorldStateManager manager = new();
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Label status = FindByName<Label>(panel, "coreai-hub-worldstate-status");
            Button save = FindByName<Button>(panel, "coreai-hub-worldstate-save");

            Click(save);

            Assert.AreEqual(1, manager.SaveCount, "Save Now must still perform the write.");
            Assert.AreEqual(1, manager.PendingConfirmations,
                "Save Now must request a durability confirmation, not assume durability.");
            Assert.AreEqual("Has saved state: No", status.text,
                "W3.5: the page must not report a durable save at IDBFS request-issuance time; the " +
                "FS.syncfs completion callback has not run yet.");

            manager.ReleaseDurability(true);

            Assert.AreEqual("Has saved state: Yes", status.text,
                "Once the browser confirms the flush the page must report the saved state.");
        }

        [Test]
        public void SaveNow_FailedBrowserFlush_ReportsUnconfirmedDurability()
        {
            DeferredDurabilityWorldStateManager manager = new();
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Label durability = FindByName<Label>(panel, "coreai-hub-worldstate-durability");
            Button save = FindByName<Button>(panel, "coreai-hub-worldstate-save");

            Click(save);
            Assert.AreEqual(WorldStateHubPage.FlushPendingText, durability.text);

            manager.ReleaseDurability(false);

            Assert.AreEqual(WorldStateHubPage.FlushUnconfirmedText, durability.text,
                "A syncfs error (quota, blocked storage) must be surfaced, not hidden behind 'Yes'.");
        }

        [Test]
        public void ResetWorld_DoesNotClaimClearedState_BeforeTheBrowserFlushCallback()
        {
            DeferredDurabilityWorldStateManager manager = new();
            manager.Save();
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Label status = FindByName<Label>(panel, "coreai-hub-worldstate-status");
            Button reset = FindByName<Button>(panel, "coreai-hub-worldstate-reset");
            Assert.AreEqual("Has saved state: Yes", status.text);

            Click(reset);

            Assert.AreEqual(1, manager.ResetCount);
            Assert.AreEqual("Has saved state: Yes", status.text,
                "§6.5 requires the reload after Reset World to still read No; the page must not " +
                "claim the delete is durable before the IndexedDB flush callback confirms it.");

            manager.ReleaseDurability(true);

            Assert.AreEqual("Has saved state: No", status.text);
        }

        [Test]
        public void DurabilityWait_DisablesBothControls_UntilTheCallbackArrives()
        {
            DeferredDurabilityWorldStateManager manager = new();
            WorldStateHubPage page = new(manager);
            VisualElement panel = (VisualElement)page.CreatePageContent();
            Button save = FindByName<Button>(panel, "coreai-hub-worldstate-save");
            Button reset = FindByName<Button>(panel, "coreai-hub-worldstate-reset");

            Click(save);

            Assert.IsFalse(save.enabledSelf, "A second Save Now must not stack another pending flush.");
            Assert.IsFalse(reset.enabledSelf);

            manager.ReleaseDurability(true);

            Assert.IsTrue(save.enabledSelf, "Controls must re-enable once the browser answers.");
            Assert.IsTrue(reset.enabledSelf);
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
