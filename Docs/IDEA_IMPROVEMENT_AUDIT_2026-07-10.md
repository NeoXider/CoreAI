# CoreAI Idea Improvement Audit

**Date:** 2026-07-10
**Companion to:** [REPOSITORY_AUDIT_2_2026-07-10.md](REPOSITORY_AUDIT_2_2026-07-10.md) (technical)
**Subject:** the idea itself — positioning, differentiation, audience, and where the concept
(not the code) should move next.
**Grounding:** root `README.md`, `INSTALL.md`, `Docs/LocalBusinessPlans/` (monetization + CoreAiPro
plans), the five demo experiences (`Assets/CoreAI.Demos/`), the benchmark package
(`Assets/CoreAIBenchmark/`), and the current license model (PolyForm Noncommercial + paid
commercial). Where a claim is market judgment rather than repo fact, it is phrased as such.

---

## 1. What the idea actually is — and how strong each pillar is

The README sells one sentence: *"LLM agents that play your game."* Underneath it there are five
distinguishable pillars. They are not equally valuable.

| # | Pillar | Assessment |
|---|--------|-----------|
| 1 | **Small-local-model resilience layer** — tool-name repair, retry-with-feedback, split `<think>` handling, runaway caps, dual-backend fallback; the whole suite passes on a 4 GB GGUF | **The crown jewel.** Nobody else makes "a 4B model reliably drives gameplay" a tested, first-class contract. Every competitor demos GPT-4-class cloud models; CoreAI's moat is the messy reality below 8B. This is also the hardest pillar to copy, because it is accumulated scar tissue (test suites, repair heuristics), not a feature. |
| 2 | **AI-authored runtime content (Lua mods)** — the model writes Lua that defines units, waves, hooks; capability tiers (WorldEdit vs Full); version history/revert; Unit Forge ships an *empty arena* | **Genuinely novel as a product story** ("build a whole game from mods alone"), and the demos prove it. Risk: it is also the pillar with the largest safety/platform surface (see §6). |
| 3 | **Game-Creation Benchmark** — G1–G7 scored suite, cloud + local model rankings, castle gallery | **Undervalued asset.** Currently framed as internal QA + README tables. As a *community artifact* ("the leaderboard for 'can this model build a game'") it is the cheapest marketing engine the project could have — nothing comparable exists. |
| 4 | **Production plumbing** — orchestrator queue, memory, context compaction, audit log, token budget overlay, skills (~91% token savings) | Solid and increasingly true (see technical audit), but **undifferentiated in story**: every agent framework claims plumbing. It justifies the price; it does not win the click. |
| 5 | **Drop-in chat UI + Hub** | Table stakes. Necessary for the 5-minute demo, not a reason to choose CoreAI. |

**Sharpest wedge:** pillars 1 + 3 together — *"the framework and the benchmark for LLMs that run
games on the player's machine."* Local-first is not a feature here; it is the identity. Cloud
compatibility is the escape hatch, not the pitch.

## 2. Competitive position (market judgment)

- **Inworld / Convai** — character-AI platforms: hosted, voice-first, per-usage pricing, strong
  authoring UX. They win on polish and voice; they lose on: local/offline, owning your own code
  path, modding, cost at scale, and "the model calls *your* C#". CoreAI should never fight them
  on "lifelike NPC dialogue"; it fights on *agency inside your systems*.
- **LLMUnity / NobodyWho** — local inference plumbing. They are CoreAI's *substrate*, not
  competitors (CoreAI already treats LLMUnity as an optional backend). The gap CoreAI fills above
  them — tools, memory, guardrails, mods — is exactly right. Keep the relationship symbiotic;
  contributing fixes upstream buys goodwill and stability.
- **Unity Sentis / Behavior** — official, but aimed at tensors and behavior graphs, not
  LLM-tool-calling agents. Risk is long-term platform absorption (Unity ships a first-party
  "AI NPC" package), which argues for moving fast on the community/benchmark front where a
  platform vendor is slow.
- **Generic agent SDKs (OpenAI/Anthropic/MS)** — strong loops, zero Unity empathy (main thread,
  IL2CPP, WebGL, domain reload, .meta files). CoreAI's Unity-native pain absorption is a real
  barrier to entry; the MEAI-based core keeps it compatible with that ecosystem rather than at
  war with it.

**Positioning that follows:** "The local-first agent runtime for Unity games — proven by the only
game-creation benchmark for LLMs." One sentence, both moats.

## 3. Audience and the license problem

The business plans (`Docs/LocalBusinessPlans/`) correctly identify indies, small studios,
education/simulation, and WebGL/multiplayer teams. Two frictions stand between the funnel and
those users:

1. **PolyForm Noncommercial is a silent filter on the best adopters.** The indie who would
   evangelize CoreAI *is* commercial the moment they put a game on itch.io for $3. Today the path
   is "email the author for a commercial license" — high friction, opaque price, no self-serve.
   The plans already lean the right way (free runtime strong, Pro sells time). Concretely: keep
   noncommercial for the core if revenue protection matters, but publish a **self-serve, priced
   indie tier** (e.g., flat per-title under a revenue threshold). An unpriced "write to me"
   license reads as "no" to a solo dev at 2 a.m.
2. **The 5-minute promise carries a NuGet + git-deps toll.** INSTALL.md is honest about it, but
   the first-session reality (NuGetForUnity → MEAI package → git URLs → optional MoonSharp/
   LLMUnity → scene wizard) is the single biggest drop-off risk. An installer/bootstrapper that
   performs the whole chain (the `CoreAI → Setup` menu already merges git deps — extend it to
   drive the NuGet step too) is worth more adoption than any new runtime feature.

Underserved persona in the current docs: **the modding-community organizer** — the person who
wants players writing/sharing AI-authored mods. Pillar 2 serves them; no doc or demo speaks to
them directly.

## 4. Idea-level gaps (things the concept, not the code, is missing)

1. **The shipping story.** Nothing in the README answers: what does the *player's* machine need?
   GGUF download UX (who hosts, resume, disk budget), min spec, fallback when the model can't
   run, expected latency (~2 s TTFT is already measured internally — publish it). "Runs on a
   local 4B" is proven in tests; "ships in a Steam game" is the claim buyers need, and one
   shipped case study (even a jam game) would be worth more than the whole feature list.
2. **Determinism and multiplayer honesty.** The README's host-authoritative section is two
   sentences. The idea needs a stated contract: seeds, audit-log replay (the tamper-evident log
   already exists — replay is the natural next step), and what happens on desync. Replay also
   unlocks debugging and anti-cheat narratives for free.
3. **Content safety as a first-class module.** Player-facing generated text with zero moderation
   story blocks education (a declared target segment) and consoles. Even a pluggable
   `IContentFilter` + a shipped profanity/threat baseline changes the answer from "no" to "yes,
   with knobs."
4. **Cloud cost story.** Token budgets and usage sinks exist; what's missing is the designer-
   facing frame: "an NPC conversation costs ~$0.00X on provider Y; here is the per-agent cap."
   One doc page + one Hub panel = the difference between "scary" and "budgeted."
5. **Beyond the chat box.** Every demo is chat-initiated. The director-AI / ambient-simulation
   pattern (agent observes game state on a cadence, acts through the same tools, no chat UI) is
   already possible with the orchestrator — one demo scene would open the "AI game director"
   audience, which is larger than the "NPC chat" audience.
6. **Mod sharing loop.** Export/import via clipboard just landed in the Hub. The idea-level next
   step is a shareable format + a curated gallery (even a GitHub repo of `.lua` mod files with a
   one-click import). UGC loops are how frameworks escape their author's marketing budget.
7. **The benchmark as a standalone community asset.** Publish the leaderboard as a page, accept
   PR'd model results, version the suite. Every new local model release becomes free CoreAI
   publicity ("how does it score on the game-creation benchmark?").

## 5. Top recommendations, ranked (impact on adoption × feasibility)

| # | Recommendation | Type |
|---|---------------|------|
| 1 | Ship the compile/CI gate and land the remediation wave (technical audit A-02/F-12) — every product claim rests on the "tested" story being true | Prerequisite |
| 2 | Publish the benchmark leaderboard as a living public artifact (page + PR process + suite versioning) | Quick win, big lever |
| 3 | Self-serve indie commercial tier with a public price | Quick win, unblocks the funnel |
| 4 | One-click bootstrapper: single menu action drives NuGet + git deps + scene | Quick win (menu already half-exists) |
| 5 | "Shipping on a player's machine" doc: model download UX, disk/min-spec, TTFT numbers, WebGL/IL2CPP matrix (much of this exists as internal memory notes — promote it) | Quick win |
| 6 | Director-AI demo (no chat box): ambient agent using existing tools on a timer | Medium |
| 7 | Audit-log replay → determinism/anti-cheat story for the multiplayer claim | Strategic bet |
| 8 | Pluggable content-safety filter + baseline, unblocks education/console segments | Medium |
| 9 | Mod gallery + shareable mod format (UGC loop for pillar 2) | Strategic bet |
| 10 | Case study: ship one tiny real game (jam scale) on CoreAI local-first, write the postmortem | Strategic bet, highest credibility yield |

## 6. Risks to the idea itself

- **Platform/UGC risk (pillar 2).** Runtime-downloaded executable content (Lua mods) is
  restricted or review-sensitive on consoles and mobile stores; even on PC, AI-authored code
  that reaches a reflection tier is a security story that must stay watertight (the sandbox
  hardening in the technical audits is exactly this). Mitigation: keep the capability-tier
  narrative loud, and treat "Full tier" as a dev-tool, not a shipping mode.
- **Model licensing drift.** The recommended models (Qwen et al.) carry their own licenses;
  a shipped game embeds them. The shipping-story doc (rec #5) must include a license matrix, or
  studios' lawyers will do it for them, slowly.
- **Solo-maintainer scope.** Five packages + Hub + benchmark + five demos + an example game +
  business plans is a studio's surface area maintained by one person. The repository's own
  history (this week's unverified 118-file wave) is the warning. Protect the core: pillars 1–3
  get investment; pillars 4–5 get maintenance; anything new (voice, avatars, hosted services)
  should be an integration point, not a subsystem. Freezing the example game and consolidating
  demos into the Hub would cut real maintenance load today.
- **Platform absorption.** If Unity ships first-party LLM-NPC tooling, plumbing (pillar 4)
  evaporates as a differentiator overnight; the benchmark, the local-model scar tissue, and the
  modding loop do not. That is the strongest argument for rebalancing effort toward
  pillars 1–3 now.

## 7. Bottom line

The idea is right and the hard part — small local models reliably driving real game code — is
already the project's demonstrated strength. The concept-level work now is not adding another
subsystem; it is (a) converting the benchmark into the public proof, (b) removing the license and
install friction that filters out the exact users who would spread it, and (c) telling the
shipping story end-to-end. The framework already plays the game; the idea now has to play the
market.
