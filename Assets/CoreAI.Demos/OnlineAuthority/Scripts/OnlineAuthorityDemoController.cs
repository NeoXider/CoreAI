using System;
using System.Collections.Generic;
using CoreAI.Ai;
using CoreAI.Authority;
using CoreAI.Composition;
using CoreAI.Mods.Rbx.Binding;
using CoreAI.Mods.Rbx.Instances;
using CoreAI.Mods.Rbx.Instances.Replication;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace CoreAI.Demos.OnlineAuthority
{
    /// <summary>
    /// Shows who may change an online world, and what happens when the host changes its mind.
    /// </summary>
    /// <remarks>
    /// WHY this demo exists: MVP11 and MVP12 are made of refusals, and a refusal is invisible in a
    /// screenshot. Here a visitor plays the client, asks to move a part, and watches the same request
    /// be refused, then allowed after the host grants access, then refused again the moment the host
    /// revokes it — with the reason printed each time. The rules are the shipped ones: the same
    /// ledger, the same gateway, the same ordered checks the tests gate.
    /// <para>
    /// What this demo deliberately does NOT claim: that the request crossed a network. It exercises
    /// the authority path in one process. The transport has its own gates, and pretending a local
    /// call is a wire would be the kind of demo that lies.
    /// </para>
    /// </remarks>
    [AddComponentMenu("CoreAI/Demos/Online Authority Demo Controller")]
    public sealed class OnlineAuthorityDemoController : MonoBehaviour
    {
        private const string HostActorId = "host";
        private const string GuestActorId = "guest-1";
        private const string WorldId = "online-authority-demo";

        [Header("Composition")]
        [SerializeField] private RbxWorldHost _worldHost;

        [Header("UI")]
        [SerializeField] private TextMeshProUGUI _statusLabel;
        [SerializeField] private TextMeshProUGUI _grantLabel;
        [SerializeField] private Button _guestMoveButton;
        [SerializeField] private Button _grantButton;
        [SerializeField] private Button _revokeButton;
        [SerializeField] private Button _hostMoveButton;

        private WriteGrantLedger _ledger;
        private IntentGateway _gateway;
        private RbxInstance _statue;
        private readonly List<string> _log = new();
        private int _operation;

        private void Start()
        {
            _worldHost = _worldHost != null ? _worldHost : FindFirstObjectByType<RbxWorldHost>();
            if (_worldHost == null || _worldHost.Registry == null)
            {
                Report("Scene is missing the RbxWorldHost.");
                return;
            }

            _statue = _worldHost.Registry.Create("Part");
            _statue.Name = "Statue";
            _statue.Parent = _worldHost.Registry.WorldRoot;
            _worldHost.Binder.SetPosition(_statue.Id,
                new CoreAI.Mods.Rbx.Datatypes.RbxVector3(0f, 2f, 0f));
            _worldHost.Binder.SetAnchored(_statue.Id, true);

            _ledger = new WriteGrantLedger(_worldHost.Registry,
                () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Report);
            _gateway = new IntentGateway(_worldHost.Registry, _ledger, ApplyIntent);

            Bind(_guestMoveButton, GuestAsksToMoveTheStatue);
            Bind(_grantButton, HostGrantsTheGuest);
            Bind(_revokeButton, HostRevokesTheGrant);
            Bind(_hostMoveButton, HostMovesTheStatue);

            Report("A statue stands in the world. The guest has no rights yet.");
            RefreshGrantLabel();
        }

        private void GuestAsksToMoveTheStatue()
        {
            MutationIntent intent = new(
                "op-" + ++_operation,
                _statue.Id,
                CurrentRevision(),
                MutationIntentAction.WriteProperty,
                "Position",
                Array.Empty<byte>());

            IntentOutcome outcome = _gateway.Handle(GuestActorId, false, WorldId, intent);
            Report(outcome.Applied
                ? "Guest moved the statue. Revision " + outcome.Revision + "."
                : "Guest refused (" + outcome.ReasonCode + "): " + outcome.Reason);
        }

        private void HostGrantsTheGuest()
        {
            // The host issues from the server process; there is no remote, Lua or intent path to
            // this ledger, which is why a client can never grant itself anything.
            _ledger.Issue(HostActorId, issuerIsUnrestricted: true, GuestActorId,
                WriteGrantScope.Instance, _statue.Id, WriteGrantActions.WriteProperty);
            Report("Host granted the guest 'write property' on the statue — and nothing else.");
            RefreshGrantLabel();
        }

        private void HostRevokesTheGrant()
        {
            int revoked = _ledger.RevokeAllFor(HostActorId, issuerIsUnrestricted: true,
                GuestActorId);
            Report(revoked > 0
                ? "Host revoked " + revoked + " grant(s). It takes effect on the next request."
                : "The guest holds no grants to revoke.");
            RefreshGrantLabel();
        }

        private void HostMovesTheStatue()
        {
            // WHY this button proves something: the host's write does not travel as an intent at
            // all. It never consults the ledger, and it works whether or not the guest has rights —
            // which is what "the host holds every right" actually means here.
            _worldHost.Binder.SetPosition(_statue.Id,
                new CoreAI.Mods.Rbx.Datatypes.RbxVector3(
                    UnityEngine.Random.Range(-6f, 6f), 2f, 0f));
            _worldHost.Registry.AdvanceRevision(_statue.Id);
            Report("Host moved the statue directly — no grant needed, no intent sent.");
        }

        private long ApplyIntent(string actorId, MutationIntent intent)
        {
            _worldHost.Binder.SetPosition(intent.TargetInstanceId,
                new CoreAI.Mods.Rbx.Datatypes.RbxVector3(
                    UnityEngine.Random.Range(-6f, 6f), 2f, 0f));
            return _worldHost.Registry.AdvanceRevision(intent.TargetInstanceId);
        }

        private long CurrentRevision()
        {
            return _worldHost.Registry.TryGetRecord(_statue.Id, out InstanceRecord record)
                ? record.Revision
                : 0L;
        }

        private void RefreshGrantLabel()
        {
            if (_grantLabel == null)
            {
                return;
            }

            IReadOnlyList<WriteGrant> live = _ledger.LiveGrantsFor(GuestActorId);
            _grantLabel.text = live.Count == 0
                ? "Guest grants: none"
                : "Guest grants: " + live.Count + " (" + live[0].Actions + " on "
                  + live[0].Scope + ")";
        }

        private void Bind(Button button, Action action)
        {
            if (button != null)
            {
                button.onClick.AddListener(() => action());
            }
        }

        private void Report(string line)
        {
            _log.Add(line);
            if (_log.Count > 9)
            {
                _log.RemoveAt(0);
            }

            if (_statusLabel != null)
            {
                _statusLabel.text = string.Join("\n", _log);
            }
        }
    }
}
