# Agent instructions — CoreAI

## Project goal: RUNTIME-first, always

CoreAI's premise is creating and evolving the game **inside the running game** — world, mods, logic,
and UI alike. When designing or reviewing any feature, check first: **"does this work in a built
player on device?"**

- Editor-only mechanisms (`AssetDatabase` import, AssetBundle building, editor tooling, `#if UNITY_EDITOR`
  paths) must never be the *primary* path of a feature — at most a secondary convenience.
- Example: runtime UI = UXML/USS text interpreted at runtime as the core path; materializing real
  project assets is an optional editor-only bonus.
- WebGL is a first-class target: after a file write, ask `CoreAiWebGlPersistence.Sync()` whether the
  engine's automatic `persistentDataPath` persistence is armed, and surface `false` as a failure — do
  not drive `FS.syncfs` by hand (deprecated since Unity 6.3, its callback never fires) and do not
  await a durability confirmation the browser never sends. No threads/blocking waits on the WebGL path.

## Conventions (enforced)

- **Explicit types**: never use `var` for C# locals, fields, or return values — declare explicit types. Only exception: anonymous types (`var x = new { ... }`), where an explicit type is impossible.
- **Comments**: only `/// <summary>` XML docs, `// WHY:`, `// TODO:`, `// HACK:`. No narrative or
  change-description comments.
- **No audit reports live in the repo** — findings become `TODO.md` items; report files get deleted.
- **Releases**: run `python tools/bump_version.py <version>` — it moves ALL SEVEN `package.json` in
  lockstep (`com.neoxider.coreai`, `coreaiunity`, `coreaimods`, `coreaihub`, `coreaibenchmark`,
  `coreaimcp`, `coreaimirror`) plus `McpServerInfo.Version`, which is the one a hand-edit forgets. Changelog entries go
  in the only two changelogs: `Assets/CoreAI/CHANGELOG.md` (core + mods) and
  `Assets/CoreAiUnity/CHANGELOG.md` (host).
- **Commits**: NEVER add `Co-Authored-By` or any AI-attribution trailers.
- **TODO.md** is the living priority tracker; every fix wave updates it.
- Every bug fix ships with a regression test; every feature ships with tests and docs.
- **Language: English for all prose, and it is enforced.** READMEs, `Docs/`, package docs,
  `TODO.md`/`PLAN.md` additions, code comments (`///` XML docs, `// WHY:`), test names, assertion
  messages, exception text and log strings are written in English.
  The guard is `EnglishOnlyProseEditModeTests` (CoreAiUnity EditMode): it scans every shipped package
  for Cyrillic and fails with the exact file and line.
  The one exception is non-Latin text that is **quoted rather than written**, in two shapes: a string
  literal that is the subject under test (token estimation, think-block filtering and response
  sanitising all have to survive Cyrillic input — rewriting their data in English deletes the
  coverage), and a **verbatim sample of observed model output** pasted into a comment as evidence for
  the WHY around it (translate the sample and a piece of evidence becomes a paraphrase of one).
  Such files go in the guard's `QuotedNonLatin` list **with a written reason**. The excuse is
  mechanical and per line: every Cyrillic character on the line must sit inside quotes, so an
  ordinary Russian comment in one of those files still fails.
  This rule used to end with "legacy prose is migrated gradually, never as a standalone rewrite
  wave". That sentence is why 99 files were still Russian on 2026-09-10, including the load-bearing
  WHYs on the memory store and the streaming client: every wave had a better use for its time, so the
  debt only grew, and the owner read a Russian comment in a package meant to ship to anyone. There is
  no legacy allowance any more — the guard makes the question decidable when the file is written.
  Mirror rule: RedoSchool is a Russian-language project; when copying text or patterns between the
  repos, translate the language layer.
  Not covered: `Docs/LocalBusinessPlans/` — the owner's own business documents, written for a
  Russian-speaking reader and never shipped inside a package.

## Verification while the Unity editor holds the project lock

Unity batchmode CLI fails (lockfile). Instead:

- `dotnet build <Project>.csproj` on the Unity-generated csproj = fast compile gate
  (`CoreAI.Core.csproj`, `CoreAI.Source.csproj`, `CoreAI.Mods.csproj`, `CoreAI.Tests.csproj`).
- Full EditMode suite runs on next editor start ("verification gate" items in TODO.md).

More detail: `CONTRIBUTING.md` (hooks, CI jobs, Lua/no-Lua configurations).
