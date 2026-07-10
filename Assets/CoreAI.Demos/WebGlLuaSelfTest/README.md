# WebGL Lua sandbox self-test

A runtime self-test for the CoreAI Lua sandbox, intended for a **WebGL player build**
where the EditMode/PlayMode test runners are unavailable. It verifies that the secure Lua
sandbox still behaves correctly after IL2CPP stripping.

## What it does

[`WebGlLuaSelfTest`](WebGlLuaSelfTest.cs) is a `MonoBehaviour`. On `Start()` it runs a set of
Lua-CSharp `LuaCsSecureEnvironment` sandbox invariants (host callback marshalling, stripped
globals, `string.rep` / `string.format` caps) and then:

- logs `PASS` (or `FAIL` as an error) with the report to the Unity console, and
- renders the same PASS/FAIL report on screen via `OnGUI`, so the result is visible inside a
  built WebGL player without a console.

When the Lua runtime is stripped (a no-lua build: `COREAI_NO_LUA` defined), the script compiles
to a no-op fallback that just shows
"Lua sandbox self-test unavailable: COREAI_NO_LUA build."

## How to use

1. Attach `WebGlLuaSelfTest` to a GameObject in a scene.
2. Ensure `COREAI_NO_LUA` is **not** defined.
3. Set `CoreAISettingsAsset.EnableLuaOnWebGl = true`.
4. Build to WebGL and open the player — the on-screen box reports PASS/FAIL.

## Requirements

- `COREAI_NO_LUA` **not** defined (otherwise the fallback runs).
- No LLM backend required — this is a pure sandbox check.

## Related

- `Assets/CoreAI/Docs/LUA_SANDBOX_SECURITY.md` — sandbox boundary and escape coverage.
