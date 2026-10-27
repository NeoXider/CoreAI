# Independent Hub review, 2026-10-27

## Scope and evidence

Read the full dirty diff in `Assets/CoreAIHub`: `CoreAiHubSetupMenu.cs`, `CoreAiHubUss.uss`, `CoreAiHubWindow.cs`, and `HubSettingsPage.cs`. Inspected actual page registrations/callers, `HubModsPage`, editor Escape handlers, endpoint save fencing, and the attached-editor test fixture. New editor sources were also read directly rather than assumed visible in `git diff`.

This was a read-only source review. No fixes, builds/tests, Unity/MCP calls, assets, Git mutations, or inference were performed. Real attached Hub key routing was not executed by this reviewer; the defect below follows the actual registered event phase and page interfaces.

## Confirmed finding

### HUB-REVIEW-001 [P2] The Hub consumes Escape before the hosted code editor can dismiss its transient UI

- Primary sites: `Assets/CoreAIHub/Runtime/CoreAiHubWindow.cs:251` registers `OnRootKeyDown` in `TrickleDown`; lines 355-363 handle/stop Escape before the descendant; lines 447-451 collapse the Hub when the page does not handle it.
- Integration evidence: `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPage.cs:21` implements only `IHubPage`; it hosts `HubModEditorPage` and does not implement `IHubEscapeHandler`. Actual `HubModsPages.RegisterService` at `Assets/CoreAIMods/Runtime/HubIntegration/HubModsPages.cs:99` wraps it inside `HubSubTabPage`, which also lacks Escape forwarding. The default Hub setting at `CoreAiHubWindow.cs:62` is `escapeCollapses = true`.
- Descendant behavior: `CodeEditorView.OnKeyDown` has a completion Escape branch at `Assets/CoreAIMods/Runtime/HubIntegration/CodeEditing/CodeEditorView.cs:1451`; `OnGotoLineKeyDown` dismisses the goto prompt on Escape at line 1930. Both callbacks are below the Hub root in the same tree.
- Repro: Open the actual Hub, select Mods, open a mod editor, open completion with Ctrl+Space or open Go to line with Ctrl+G, then press Escape while the relevant editor element is focused.
- Actual: Root trickle handling runs first. `TryActivePageHandleEscape` cannot delegate to the Mods page because it lacks the interface. The Hub collapses and stops propagation; the editor does not receive Escape to dismiss its popup/prompt.
- Expected: The first Escape dismisses the editor's transient UI and preserves the expanded Hub; a subsequent unhandled Escape may collapse it according to the configured Hub policy.
- Meaningful regression: Attach the real Hub root and real Mods editor, rather than attaching `CodeEditorSurface` alone. Open each transient, send a real focused Escape event, assert the transient closed and Hub stayed expanded, then assert a later unhandled Escape follows the configured collapse behavior. Also retain the existing collapse-disabled and chat-page handler cases.
- Required direction: Use the existing `IHubEscapeHandler` seam for the Mods editor, or arrange event handling so a descendant's consumed Escape is observed before Hub collapse. Avoid a game-specific policy or reliance on an unrelated editor instance's static popup flag.
- Status: Sent to root promptly; no fix or execution by this reviewer. Count this finding here only; the editor report cross-references it.

No additional confirmed Hub finding was established in the reviewed changes.

## Reviewed behavior

- The new bool result of `HandleEscapeRequested` correctly avoids swallowing an unhandled Escape when collapse is disabled, while retaining handled-page and actual-collapse consumption. The integration gap above concerns the new editor hosted inside that policy.
- Endpoint save fencing now counts only actual draft controls. Status labels and the Advanced foldout no longer invalidate the editor's own save generation. Discovered model selection updates the actual model control; it does not need its own draft-field entry.
- Existing caller code snapshots the endpoint draft/controller/generation/operation, fences stale results, preserves a newer draft, and clears a write-only key only after a current saved receipt.
- Setup-menu changes add optional Mods/world binders through assembly-qualified discovery and keep the generated prefab composition consistent with runtime instances. No Editor mechanism was treated as the primary runtime feature path.
- Styling retains the separate raw input/rich-text presentation layers. Source-owning events are filtered by exact event target in the editor rather than by decoration ancestry.

## Source snapshot (SHA-256)

```text
08FE61071C1E835AFB54EF548A73E560E6A2FCA268A4D741C51882DDFA70E4A8  Assets/CoreAIHub/Editor/CoreAiHubSetupMenu.cs
B81A0B6E1D69EF5A47FB90EA7E9DCAD1D0ACD541C26E1E4E708B07DD65F3417E  Assets/CoreAIHub/Runtime/CoreAiHubUss.uss
35C52C96178C30CCF98280613F0E94A149EB347997FCF7B920F0384989616C1F  Assets/CoreAIHub/Runtime/CoreAiHubWindow.cs
80C2A7AD38FA6014B019329CB712F26D0003A79A12EEF5014C46C7E79B0827EC  Assets/CoreAIHub/Runtime/HubSettingsPage.cs
```

## Handoff

### Accepted candidate follow-up (staged only)

Root approved the optional generic Escape path and the additional `Assets/CoreAIHub/Runtime/HubSubTabPage.cs` scope after the actual grouping caller was checked. The staged route is `CoreAiHubWindow -> HubSubTabPage active child -> HubModsPage -> HubModEditorPage -> CodeEditorSurface -> CodeEditorView`. Only an existing completion or goto transient is dismissed and reported handled. No required member was added to the portable editor-view interface. The grouping page refuses forwarding after destruction; Logs remains unhandled.

The new staged native fixture uses the actual `HubModsPages.Register` grouping, real Hub `Rebuild` with the shipped UXML, a real Lua Hub service, and an attached native panel. It sends Escape through the actual Hub ancestor for completion/goto and verifies later unhandled collapse plus Logs/destroyed behavior. It has not been executed by this worker.

Original grouping source SHA-256: `9645BBA0FF0B012860780E7C425C26929ED65698DAB5291F1AC5E7504BA53FD6`. The complete manifest/diff and byte-exact originals are in `C:/AI/work/coreai-completion-20261027/editor-host-candidates/`. All production input SHAs were rechecked unchanged. Promotion and final independent/native acceptance remain with root.

- CHANGED: This review document and the subsequently authorized mirrored Hub/editor forwarding candidates; production Assets remain unchanged.
- VERIFY OUTPUT: Full four-file Hub diff, actual event phase, page interface, editor handlers, save fences, and related test sources inspected; input hashes recorded.
- NOT DONE: Candidate promotion, real Hub/editor Escape regression execution, native layout/key verification, and root acceptance.
- CONCERNS: Editor-only attachment tests cannot verify interception by the actual Hub ancestor.
- QUESTIONS: None.
