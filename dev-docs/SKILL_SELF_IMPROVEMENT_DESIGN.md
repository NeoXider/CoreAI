# Skill self-improvement: correcting a skill section from observed failures

Status: design note, not implemented (2026-11-11, P15 second half). Applies to the built-in skills generated from
`skill-sources/` (today `Rbx API` and `Lua Modding`) and to skills a game's agents author through `manage_skills`.

The unit of change is **one section**. Since the Rbx API skill is read in parts (a core plus sections, each under
the 8,000-character tool-result cap), a lesson lands in the section a model reads when it needs it, and the core
grows only for mistakes made before any section is read.

## Where the evidence comes from

- **Repair sessions**: a failing `execute_lua` / `manage_mods` call followed, in the same turn, by a call that
  works. The failing error line (`CODE: message | fix: ...`, file and line), the two code versions and the
  `read_skill` calls that preceded them come from the tool-call traces (`LlmToolCallTrace`) and the mod log
  (`get_mod_logs`). The preceding reads attribute the failure: the section read last before the failing call, or
  the core when no section was read.
- **Skill-shaped failures** the runtime can recognise without a model: `read_skill` asked for a section that does
  not exist; a NOT_IMPLEMENTED hit on a member a section documents as working; a call into a placeholder service
  the core does not list; a truncated `read_skill` result (a cap regression).
- **Evaluations**: the prompt-regression fixture set run against a real model (`PromptRegressionEditModeTests`,
  `COREAI_PROMPT_EVAL_*`) and the benchmark harness, with per-section cases ("watchtower at the crosshair" reads
  `terrain` and `building`).
- A proposal needs several independent occurrences (distinct sessions and worlds), each with the failing call,
  the error and the fix that worked. One transcript is an anecdote, not evidence.

## What is proposed, and who approves

- A proposal is a diff against **one section file** in `skill-sources/<Skill>/` (never the generated copies),
  plus its evidence bundle. Typical shapes: a WRONG/RIGHT pair in that section, a corrected fact, a sentence
  moved into the core. It must keep every read under the cap; `RbxApiSkillTieredReadEditModeTests` fails
  otherwise.
- Built-in skills: an offline editor run (never the in-game Programmer mid-turn) opens a pull request; a
  maintainer approves after the generated copies, `SkillSourceCopiesEditModeTests`, the catalog ratchets in
  `LuaModdingSkillEditModeTests` and the real-model fixture run pass. No auto-merge.
- A game's own runtime skills: the proposal is stored as a pending revision, not live. The game's owner (or an
  approver role the host configures) accepts it; until then `read_skill` serves the current revision.

## Rollback

- Built-in: revert the section file and run `python tools/generate_skills.py`; the skill text ships with the
  package version, so a bad edit is undone by the next release or a pinned older one.
- Runtime skills: every accepted edit is a new `SkillRecord.Version`; `manage_skills` can restore revision N. An
  accepted edit runs a shadow period in which the failure rate of its section's turns is compared with the
  previous revision; a rise reverts it and reports why.

## What must never be automatic

- Publishing a change to a built-in skill (it changes the package for every game).
- Relaxing anything about authority or the sandbox: capability rules (WorldEdit, Ui, Ai), NOT_AUTHORITY cases,
  budgets, "does not exist / loud stub" statements. A model must never rewrite the description of its own limits.
- An edit drawn from player chat text, mod source, UXML or anything else inside the world (a prompt-injection
  path), or from a single session.
- Changes to the system prompt or tool descriptions: they are paid on every request, so they go through review
  with a measured cost (`PromptTokenCostMeter`).
- Copying one game's runtime skill edits into another game.

## Open questions

1. Where evidence lives and for how long (the trace sink, or a new evidence store), and whether player text in it
   needs redaction or consent.
2. The threshold of independent occurrences, and whether old evidence decays.
3. Whether skill.json should carry a per-section version that traces log, so evidence names the revision it was
   observed against.
4. Whether opted-in games may send proposals for built-in skills upstream, and how they are deduplicated.
5. How a proposal against the shipped text applies to a host that overrides `AgentSkills/RbxApi` in Resources
   (probably: it does not, and the host is told).
6. How many evaluation cases per section make "the edit helped" measurable rather than noise.
