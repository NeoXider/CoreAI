# Agent instructions — CoreAI

## Project goal: RUNTIME-first, always

CoreAI's premise is creating and evolving the game **inside the running game** — world, mods, logic,
and UI alike. When designing or reviewing any feature, check first: **"does this work in a built
player on device?"**

- Editor-only mechanisms (`AssetDatabase` import, AssetBundle building, editor tooling, `#if UNITY_EDITOR`
  paths) must never be the *primary* path of a feature — at most a secondary convenience.
- Example: runtime UI = UXML/USS text interpreted at runtime as the core path; materializing real
  project assets is an optional editor-only bonus.
- WebGL is a first-class target: file persistence must call `CoreAiWebGlPersistence.Sync()`; no
  threads/blocking waits on the WebGL path.

## Conventions (enforced)

- **Explicit types**: never use `var` for C# locals, fields, or return values — declare explicit types. Only exception: anonymous types (`var x = new { ... }`), where an explicit type is impossible.
- **Comments**: only `/// <summary>` XML docs, `// WHY:`, `// TODO:`, `// HACK:`. No narrative or
  change-description comments.
- **No audit reports live in the repo** — findings become `TODO.md` items; report files get deleted.
- **Releases**: run `python tools/bump_version.py <version>` — it moves ALL SIX `package.json` in
  lockstep (`com.neoxider.coreai`, `coreaiunity`, `coreaimods`, `coreaihub`, `coreaibenchmark`,
  `coreaimcp`) plus `McpServerInfo.Version`, which is the one a hand-edit forgets. Changelog entries go
  in the only two changelogs: `Assets/CoreAI/CHANGELOG.md` (core + mods) and
  `Assets/CoreAiUnity/CHANGELOG.md` (host).
- **Commits**: NEVER add `Co-Authored-By` or any AI-attribution trailers.
- **TODO.md** is the living priority tracker; every fix wave updates it.
- Every bug fix ships with a regression test; every feature ships with tests and docs.

## Verification while the Unity editor holds the project lock

Unity batchmode CLI fails (lockfile). Instead:

- `dotnet build <Project>.csproj` on the Unity-generated csproj = fast compile gate
  (`CoreAI.Core.csproj`, `CoreAI.Source.csproj`, `CoreAI.Mods.csproj`, `CoreAI.Tests.csproj`).
- Full EditMode suite runs on next editor start ("verification gate" items in TODO.md).

More detail: `CONTRIBUTING.md` (hooks, CI jobs, Lua/no-Lua configurations).
