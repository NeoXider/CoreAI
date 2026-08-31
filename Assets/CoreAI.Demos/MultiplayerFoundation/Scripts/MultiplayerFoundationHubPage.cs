#if COREAI_LUA
using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Hub;
using UnityEngine;
using UnityEngine.UIElements;

namespace CoreAI.Demos
{
    /// <summary>Bright guided UI Toolkit proof for actor-scoped chat, mods, world ownership, and quotas.</summary>
    public sealed class MultiplayerFoundationHubPage : HubPageBase
    {
        /// <summary>Default registry id for the multiplayer-foundation page.</summary>
        public const string DefaultPageId = "coreai.demo.multiplayer.foundation";

        private static readonly Color Pass = new Color(0.36f, 0.94f, 0.58f, 1f);
        private static readonly Color Warning = new Color(1f, 0.47f, 0.43f, 1f);
        private static readonly Color Panel = new Color(0.055f, 0.075f, 0.11f, 0.96f);
        private static readonly Color PanelRaised = new Color(0.085f, 0.12f, 0.17f, 1f);

        private readonly Func<MultiplayerFoundationDemoController> _controllerProvider;
        private VisualElement _actorCards;
        private Label _actorCountLabel;
        private DropdownField _chatActorDropdown;
        private TextField _chatInput;
        private ScrollView _chatTranscript;
        private Label _chatStatus;
        private Button _decreaseActorsButton;
        private Button _increaseActorsButton;
        private VisualElement _proofBoard;
        private Label _proofSummary;
        private Button _sendChatButton;
        private Label _statusLabel;
        private MultiplayerFoundationDemoController _controller;
        private IVisualElementScheduledItem _tick;
        private int _lastRevision = -1;

        /// <param name="controllerProvider">Resolves the scene's production-path demo controller.</param>
        public MultiplayerFoundationHubPage(
            Func<MultiplayerFoundationDemoController> controllerProvider,
            string pageId = DefaultPageId,
            string displayName = "Multiplayer Proof",
            int order = 0)
            : base(
                string.IsNullOrWhiteSpace(pageId) ? DefaultPageId : pageId,
                string.IsNullOrWhiteSpace(displayName) ? "Multiplayer Proof" : displayName,
                order)
        {
            _controllerProvider = controllerProvider;
        }

        /// <inheritdoc />
        public override Func<object> CreatePageContent => Build;

        /// <inheritdoc />
        public override void OnActivated()
        {
            _tick?.Resume();
            _lastRevision = -1;
            RefreshIfChanged();
        }

        /// <inheritdoc />
        public override void OnDeactivated()
        {
            _tick?.Pause();
        }

        /// <inheritdoc />
        public override void OnDestroyed()
        {
            _tick?.Pause();
            _tick = null;
        }

        private object Build()
        {
            ScrollView scroll = DemoHubWidgets.CreatePage("MULTIPLAYER FOUNDATION", out VisualElement body);
            body.style.backgroundColor = Panel;
            body.Add(BuildHero());
            body.Add(BuildGuideStrip());
            body.Add(BuildActorControls());

            _statusLabel = DemoHubWidgets.MakeBody("Resolving production services...");
            StyleStatus(_statusLabel, false);
            body.Add(_statusLabel);

            body.Add(DemoHubWidgets.MakeSection("1  OWN LANES - private chat + owned live mods"));
            _actorCards = new VisualElement();
            _actorCards.style.flexDirection = FlexDirection.Row;
            _actorCards.style.flexWrap = Wrap.Wrap;
            body.Add(_actorCards);

            body.Add(DemoHubWidgets.MakeSection("2  EXPECTED DENIALS - the security result is the feature"));
            _proofSummary = DemoHubWidgets.MakeBody("Waiting for the proof run...");
            body.Add(_proofSummary);
            _proofBoard = new VisualElement();
            body.Add(_proofBoard);

            body.Add(BuildPrivateChatPanel());
            body.Add(DemoHubWidgets.MakeNote(
                "The proof itself needs no provider call. The optional chat box uses the configured live " +
                "provider and keeps its transcript inside the selected actor's production chat service."));

            _tick = scroll.schedule.Execute(RefreshIfChanged).Every(150);
            RefreshIfChanged();
            return scroll;
        }

        private VisualElement BuildHero()
        {
            VisualElement hero = new VisualElement();
            hero.style.backgroundColor = new Color(0.05f, 0.25f, 0.34f, 1f);
            hero.style.borderTopLeftRadius = 12f;
            hero.style.borderTopRightRadius = 12f;
            hero.style.borderBottomLeftRadius = 12f;
            hero.style.borderBottomRightRadius = 12f;
            hero.style.paddingLeft = 18f;
            hero.style.paddingRight = 18f;
            hero.style.paddingTop = 14f;
            hero.style.paddingBottom = 14f;
            hero.style.marginBottom = 10f;

            Label hook = new Label("4 ACTORS.  1 RBX WORLD.  0 CROSSED WIRES.");
            hook.style.color = Color.white;
            hook.style.fontSize = 20f;
            hook.style.unityFontStyleAndWeight = FontStyle.Bold;
            hook.style.whiteSpace = WhiteSpace.Normal;
            hero.Add(hook);

            Label promise = DemoHubWidgets.MakeBody(
                "Every card below resolves its own production chat and loads then reloads an animated " +
                "beacon mod. Green REFUSED cards prove actor boundaries held - they are successful results.");
            promise.style.color = new Color(0.82f, 0.97f, 1f, 1f);
            promise.style.marginTop = 6f;
            hero.Add(promise);
            return hero;
        }

        private VisualElement BuildGuideStrip()
        {
            VisualElement row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.flexWrap = Wrap.Wrap;
            row.style.marginBottom = 8f;
            row.Add(BuildGuideStep("01", "OWN", "Distinct chat + edited beacon"));
            row.Add(BuildGuideStep("02", "ATTACK", "Cross actor + protected host"));
            row.Add(BuildGuideStep("03", "REFUSE", "32 succeeds; mod 33 stops"));
            return row;
        }

        private static VisualElement BuildGuideStep(string number, string title, string detail)
        {
            VisualElement card = new VisualElement();
            card.style.backgroundColor = PanelRaised;
            card.style.borderTopLeftRadius = 8f;
            card.style.borderTopRightRadius = 8f;
            card.style.borderBottomLeftRadius = 8f;
            card.style.borderBottomRightRadius = 8f;
            card.style.paddingLeft = 10f;
            card.style.paddingRight = 10f;
            card.style.paddingTop = 8f;
            card.style.paddingBottom = 8f;
            card.style.marginRight = 7f;
            card.style.marginBottom = 7f;
            card.style.minWidth = 190f;
            card.style.flexGrow = 1f;

            Label heading = new Label(number + "  " + title);
            heading.style.color = DemoHubWidgets.Accent;
            heading.style.unityFontStyleAndWeight = FontStyle.Bold;
            heading.style.fontSize = 14f;
            card.Add(heading);
            Label detailLabel = DemoHubWidgets.MakeNote(detail);
            detailLabel.style.marginTop = 2f;
            card.Add(detailLabel);
            return card;
        }

        private VisualElement BuildActorControls()
        {
            VisualElement controls = DemoHubWidgets.MakeButtonRow();
            _decreaseActorsButton = DemoHubWidgets.MakeButton("- ACTOR", () => ChangeActorCount(-1));
            _increaseActorsButton = DemoHubWidgets.MakeButton("+ ACTOR", () => ChangeActorCount(1));
            Button rerun = DemoHubWidgets.MakePrimaryButton("RUN THE ATTACKS AGAIN", RerunProof);
            _actorCountLabel = DemoHubWidgets.MakeBody("4 actors  |  range 2-20");
            _actorCountLabel.style.unityFontStyleAndWeight = FontStyle.Bold;
            _actorCountLabel.style.marginLeft = 8f;
            _actorCountLabel.style.marginRight = 8f;
            _actorCountLabel.style.marginTop = 10f;
            controls.Add(_decreaseActorsButton);
            controls.Add(_actorCountLabel);
            controls.Add(_increaseActorsButton);
            controls.Add(rerun);
            return controls;
        }

        private VisualElement BuildPrivateChatPanel()
        {
            VisualElement panel = new VisualElement();
            panel.style.backgroundColor = PanelRaised;
            panel.style.borderTopLeftRadius = 10f;
            panel.style.borderTopRightRadius = 10f;
            panel.style.borderBottomLeftRadius = 10f;
            panel.style.borderBottomRightRadius = 10f;
            panel.style.paddingLeft = 12f;
            panel.style.paddingRight = 12f;
            panel.style.paddingTop = 10f;
            panel.style.paddingBottom = 10f;
            panel.style.marginTop = 12f;
            panel.Add(DemoHubWidgets.MakeSection("OPTIONAL LIVE CHECK - talk inside one actor lane"));

            _chatActorDropdown = new DropdownField("Actor");
            _chatActorDropdown.style.color = DemoHubWidgets.Text;
            _chatActorDropdown.RegisterValueChangedCallback(_ => RenderChatTranscript());
            panel.Add(_chatActorDropdown);

            _chatInput = new TextField("Message") { multiline = false };
            _chatInput.style.color = DemoHubWidgets.Text;
            panel.Add(_chatInput);

            _sendChatButton = DemoHubWidgets.MakePrimaryButton("SEND AS SELECTED ACTOR", SendChat);
            panel.Add(_sendChatButton);
            _chatStatus = DemoHubWidgets.MakeNote("No provider request has been sent.");
            panel.Add(_chatStatus);
            _chatTranscript = new ScrollView(ScrollViewMode.Vertical);
            _chatTranscript.style.maxHeight = 150f;
            _chatTranscript.style.marginTop = 6f;
            panel.Add(_chatTranscript);
            return panel;
        }

        private void RefreshIfChanged()
        {
            _controller = TryResolveController();
            if (_controller == null)
            {
                if (_statusLabel != null)
                {
                    _statusLabel.text = "SETUP NEEDED: MultiplayerFoundationDemoController was not found.";
                    StyleStatus(_statusLabel, false);
                }

                return;
            }

            int revision = _controller.PresentationRevision;
            if (revision == _lastRevision)
            {
                return;
            }

            _lastRevision = revision;
            MultiplayerFoundationProofReport report = _controller.Report;
            if (_statusLabel != null)
            {
                _statusLabel.text = _controller.Status;
                StyleStatus(_statusLabel, report != null && report.Passed);
            }

            if (_actorCountLabel != null)
            {
                _actorCountLabel.text =
                    _controller.ActorCount + " actors  |  shared world: " + MultiplayerFoundationDemoScenario.SharedWorldId;
            }

            _decreaseActorsButton?.SetEnabled(
                _controller.ActorCount > MultiplayerFoundationDemoScenario.MinimumActorCount);
            _increaseActorsButton?.SetEnabled(
                _controller.ActorCount < MultiplayerFoundationDemoScenario.MaximumActorCount);

            RenderActors(report);
            RenderProofs(report);
            RefreshChatChoices(report);
            RenderChatTranscript();
        }

        private void RenderActors(MultiplayerFoundationProofReport report)
        {
            if (_actorCards == null)
            {
                return;
            }

            _actorCards.Clear();
            if (report == null)
            {
                _actorCards.Add(DemoHubWidgets.MakeNote("The production scopes are still starting."));
                return;
            }

            foreach (MultiplayerFoundationActorState actor in report.Actors)
            {
                _actorCards.Add(BuildActorCard(actor));
            }

            foreach (string error in report.SetupErrors)
            {
                Label errorLabel = DemoHubWidgets.MakeBody("SETUP FAILURE: " + error);
                errorLabel.style.color = Warning;
                _actorCards.Add(errorLabel);
            }
        }

        private static VisualElement BuildActorCard(MultiplayerFoundationActorState actor)
        {
            Color accent = actor.DisplayColor;
            VisualElement card = new VisualElement();
            card.style.backgroundColor = new Color(
                0.055f + accent.r * 0.12f,
                0.065f + accent.g * 0.12f,
                0.08f + accent.b * 0.12f,
                1f);
            card.style.borderLeftWidth = 5f;
            card.style.borderLeftColor = accent;
            card.style.borderTopLeftRadius = 9f;
            card.style.borderTopRightRadius = 9f;
            card.style.borderBottomLeftRadius = 9f;
            card.style.borderBottomRightRadius = 9f;
            card.style.paddingLeft = 12f;
            card.style.paddingRight = 12f;
            card.style.paddingTop = 10f;
            card.style.paddingBottom = 10f;
            card.style.marginRight = 8f;
            card.style.marginBottom = 8f;
            card.style.minWidth = 270f;
            card.style.flexGrow = 1f;
            card.style.flexBasis = 270f;

            Label title = new Label("ACTOR " + (actor.Index + 1).ToString("00") + "  " + actor.ActorId);
            title.style.color = accent;
            title.style.fontSize = 16f;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(title);

            card.Add(MakeActorLine(
                actor.ChatIsIsolated,
                "PRIVATE CHAT",
                "service #" + actor.ChatServiceIdentity + "  history=" + actor.ChatHistoryPairCount));
            card.Add(MakeActorLine(
                actor.OwnModEdited && actor.ModLoaded,
                "OWN MOD",
                "load -> reload  " + actor.ModId));
            card.Add(MakeActorLine(
                actor.TimerCount > 0,
                "RBX BEACON",
                actor.WorldObjectName + "  animated timers=" + actor.TimerCount));
            card.Add(MakeActorLine(
                actor.LoadedModCount <= actor.ModQuota,
                "MOD SLOTS",
                actor.LoadedModCount + " / " + actor.ModQuota));
            return card;
        }

        private static Label MakeActorLine(bool passed, string label, string value)
        {
            Label line = DemoHubWidgets.MakeBody(
                (passed ? "PASS  " : "CHECK  ") + label + "  |  " + value);
            line.style.color = passed ? Pass : Warning;
            line.style.fontSize = 12f;
            return line;
        }

        private void RenderProofs(MultiplayerFoundationProofReport report)
        {
            if (_proofBoard == null || _proofSummary == null)
            {
                return;
            }

            _proofBoard.Clear();
            if (report == null)
            {
                _proofSummary.text = "Waiting for the proof run...";
                return;
            }

            _proofSummary.text = report.EnforcedProofCount + " / " + report.Proofs.Count +
                                 " hostile requests refused and targets left intact.";
            _proofSummary.style.color = report.Passed ? Pass : Warning;
            _proofSummary.style.unityFontStyleAndWeight = FontStyle.Bold;

            foreach (MultiplayerFoundationProofResult proof in report.Proofs)
            {
                _proofBoard.Add(BuildProofCard(proof));
            }
        }

        private static VisualElement BuildProofCard(MultiplayerFoundationProofResult proof)
        {
            bool held = proof.Enforced && proof.TargetIntact;
            Color resultColor = held ? Pass : Warning;
            VisualElement card = new VisualElement();
            card.style.backgroundColor = held
                ? new Color(0.07f, 0.18f, 0.14f, 1f)
                : new Color(0.24f, 0.08f, 0.09f, 1f);
            card.style.borderLeftWidth = 4f;
            card.style.borderLeftColor = resultColor;
            card.style.borderTopLeftRadius = 8f;
            card.style.borderTopRightRadius = 8f;
            card.style.borderBottomLeftRadius = 8f;
            card.style.borderBottomRightRadius = 8f;
            card.style.paddingLeft = 12f;
            card.style.paddingRight = 12f;
            card.style.paddingTop = 9f;
            card.style.paddingBottom = 9f;
            card.style.marginBottom = 7f;

            Label result = new Label((held ? "REFUSED  " : "BOUNDARY FAILED  ") + proof.Category);
            result.style.color = resultColor;
            result.style.fontSize = 14f;
            result.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(result);

            Label route = DemoHubWidgets.MakeBody(
                "REQUESTER " + proof.RequesterActorId + "  ->  TARGET " + proof.Target);
            route.style.unityFontStyleAndWeight = FontStyle.Bold;
            card.Add(route);
            card.Add(DemoHubWidgets.MakeBody("ATTEMPT  " + proof.Operation));

            Label reason = DemoHubWidgets.MakeNote("EXACT REASON  " + proof.Reason);
            reason.style.color = held ? new Color(0.74f, 1f, 0.84f, 1f) : Warning;
            reason.style.fontSize = 12f;
            reason.style.whiteSpace = WhiteSpace.Normal;
            card.Add(reason);
            return card;
        }

        private void RefreshChatChoices(MultiplayerFoundationProofReport report)
        {
            if (_chatActorDropdown == null)
            {
                return;
            }

            int previousIndex = _chatActorDropdown.index;
            List<string> choices = new List<string>();
            if (report != null)
            {
                foreach (MultiplayerFoundationActorState actor in report.Actors)
                {
                    choices.Add(actor.ActorId + "  |  chat #" + actor.ChatServiceIdentity);
                }
            }

            _chatActorDropdown.choices = choices;
            _chatActorDropdown.index = choices.Count == 0
                ? -1
                : Mathf.Clamp(previousIndex, 0, choices.Count - 1);
            _sendChatButton?.SetEnabled(choices.Count > 0);
        }

        private void RenderChatTranscript()
        {
            if (_chatTranscript == null)
            {
                return;
            }

            _chatTranscript.Clear();
            MultiplayerFoundationProofReport report = _controller?.Report;
            int actorIndex = _chatActorDropdown != null ? _chatActorDropdown.index : -1;
            if (report == null || actorIndex < 0 || actorIndex >= report.Actors.Count)
            {
                _chatTranscript.Add(DemoHubWidgets.MakeNote("Choose an actor after the proof is ready."));
                return;
            }

            IReadOnlyList<string> transcript = report.Actors[actorIndex].ChatTranscript;
            if (transcript.Count == 0)
            {
                _chatTranscript.Add(DemoHubWidgets.MakeNote(
                    "This actor's visible transcript is empty. Other actor cards cannot read it."));
                return;
            }

            foreach (string line in transcript)
            {
                _chatTranscript.Add(DemoHubWidgets.MakeBody(line));
            }
        }

        private async void SendChat()
        {
            if (_controller == null || _chatInput == null || _chatActorDropdown == null)
            {
                return;
            }

            string message = _chatInput.value?.Trim() ?? "";
            if (message.Length == 0)
            {
                _chatStatus.text = "Type a message first.";
                return;
            }

            int actorIndex = _chatActorDropdown.index;
            _sendChatButton.SetEnabled(false);
            _chatStatus.text = "Sending through actor " + (actorIndex + 1) + " production chat...";
            try
            {
                LlmCompletionResult result = await _controller.SendChatAsync(actorIndex, message);
                _chatStatus.text = result.Ok
                    ? "Reply stayed inside the selected actor lane."
                    : "Provider refused/error: " + (result.Error ?? "unknown");
                if (result.Ok)
                {
                    _chatInput.value = "";
                }
            }
            catch (Exception ex)
            {
                _chatStatus.text = "Provider request failed: " + ex.Message;
            }
            finally
            {
                _sendChatButton.SetEnabled(true);
                _lastRevision = -1;
                RefreshIfChanged();
            }
        }

        private void ChangeActorCount(int delta)
        {
            if (_controller == null)
            {
                return;
            }

            _controller.SetActorCountAndRerun(_controller.ActorCount + delta);
            _lastRevision = -1;
            RefreshIfChanged();
        }

        private void RerunProof()
        {
            _controller?.RerunProof();
            _lastRevision = -1;
            RefreshIfChanged();
        }

        private MultiplayerFoundationDemoController TryResolveController()
        {
            try
            {
                return _controllerProvider?.Invoke();
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static void StyleStatus(Label label, bool passed)
        {
            label.style.backgroundColor = passed
                ? new Color(0.07f, 0.27f, 0.18f, 1f)
                : new Color(0.23f, 0.13f, 0.08f, 1f);
            label.style.color = passed ? Pass : new Color(1f, 0.78f, 0.34f, 1f);
            label.style.unityFontStyleAndWeight = FontStyle.Bold;
            label.style.fontSize = 14f;
            label.style.paddingLeft = 12f;
            label.style.paddingRight = 12f;
            label.style.paddingTop = 9f;
            label.style.paddingBottom = 9f;
            label.style.borderTopLeftRadius = 8f;
            label.style.borderTopRightRadius = 8f;
            label.style.borderBottomLeftRadius = 8f;
            label.style.borderBottomRightRadius = 8f;
            label.style.whiteSpace = WhiteSpace.Normal;
        }
    }
}
#endif
