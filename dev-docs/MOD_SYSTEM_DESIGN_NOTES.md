# Mod system design notes (dev)

Moved here from `Docs/CoreAIMods/mod-system.md` on 2026-09-17, when that spec was trimmed to
user-facing content. This is the record of the original performance investigation and the phase plan
the spec was built from. The user-facing spec keeps the resulting rule (§6) and the delivery status
(§7).

## Performance investigation (original §6)

Root cause of the observed 6 FPS with the mod panel open: `LiveMechanicsModsChatPersistenceController`
called disk-backed `ILuaScriptVersionStore` (`GetKnownKeys`/`TryGetSnapshot`) from `OnGUI`.
`FileLuaScriptVersionStore` re-read and re-parsed the whole JSON file on every call.
`GetInactiveSavedMods()` did about two reads per saved mod, was called twice per `OnGUI`, and `OnGUI`
fired two or more times per frame, so dozens of full-file reads and parses happened per frame.

Fixes as planned:

1. **Quick fix:** in the F9 controller, cache the active/inactive lists and the inactive count;
   recompute only on source load/unload notifications and after user actions, never in the draw path.
   Done: the controller caches its lists and now draws on the uGUI `CoreAiDemoPanel`.
2. **Store-level:** `FileLuaScriptVersionStore` should keep an in-memory cache and reload from disk
   only when the file changed (mtime or dirty flag). Still open as of 2026-09-17: the store reads and
   parses its file on every synchronous call (`LoadStaged`).
3. **Structural:** the UI Toolkit Hub removes per-frame IMGUI layout. Done for the Hub Mods tab.

## Phase plan (original §7)

- **Phase 1 — Mod Core:** header parser, manifest, Resources source + seeder + wiring, LLM category,
  tests, plus the perf quick fix and the store cache in parallel.
- **Phase 2 — CoreAI Hub:** UI Toolkit shell and tabs; Mods tab with Add/Paste/Copy/Update/tree;
  migrate Backend/Tokens/Chat.
- **Phase 3:** StreamingAssets and Addressables sources; the world event contract; store cache
  finalization; retire the IMGUI panels.
