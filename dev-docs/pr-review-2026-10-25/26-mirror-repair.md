# Mirror repair: PR-NET-001 same-ID replacement preservation

Status: implemented (static review only; Unity/runtime verification not run by worker).

Scope: `Assets/CoreAIMirror/Runtime/MirrorNetworkBridge.cs`,
`Assets/CoreAIMirror/Runtime/CoreAiMirrorSessionHost.cs`,
`Assets/CoreAIMirror/Tests/EditMode/MirrorSessionHostEditModeTests.cs`,
`Assets/CoreAIMirror/Tests/EditMode/MirrorBridgeRulesEditModeTests.cs` (unchanged by this repair),
new `dev-docs/pr-review-2026-10-25/26-mirror-repair.md`.

Change summary: added `BindingReleasedWithPeer` carrying the captured departing peer; bridge now invokes it
at the same release points/timing as `BindingReleased`. `CoreAiMirrorSessionHost.OnBindingReleased`
accepts that peer and ignores the notification when the current session's actor/session differs, so
a replacement admitted under a reused connection id survives while the legacy `Action<int>` event
remains compatible. Added a same-id replacement regression test.
