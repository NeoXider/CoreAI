# WebGL Lua sandbox self-test

A runtime self-test for the CoreAI Lua sandbox, intended for a **WebGL player build**
where the EditMode/PlayMode test runners are unavailable. It verifies that the secure Lua
sandbox still behaves correctly after IL2CPP stripping.

## What it does

[`WebGlLuaSelfTest`](WebGlLuaSelfTest.cs) is a `MonoBehaviour`. On `Start()` it runs a set of
Lua-CSharp `LuaCsSecureEnvironment` sandbox invariants (host callback marshalling, stripped
globals, `string.rep` / `string.format` caps) and then:

- logs `PASS` (or `FAIL` as an error) with the report to the Unity console, and
- renders the same PASS/FAIL report on screen in a uGUI `CoreAiDemoPanel`, so the result is visible
  inside a built WebGL player without a console.

When Lua is not enabled (`COREAI_LUA` absent), the script compiles to a fallback that only shows a
`CoreAiDemoPanel` saying "Unavailable: COREAI_LUA is not set for this build."

## How to use

1. Attach `WebGlLuaSelfTest` to a GameObject in a scene.
2. Ensure `COREAI_LUA` is defined.
3. Build to WebGL and open the player — the on-screen box reports PASS/FAIL.

## Requirements

- `COREAI_LUA` defined (otherwise the fallback runs).
- No LLM backend required — this is a pure sandbox check.

## Related

- [LUA_SANDBOX_SECURITY.md](../../CoreAI/Docs/LUA_SANDBOX_SECURITY.md) — sandbox boundary and escape coverage.
