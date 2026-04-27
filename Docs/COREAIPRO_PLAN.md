# CoreAiPro Product Plan

Updated: 2026-04-27

## Product definition

CoreAiPro is a paid layer on top of **CoreAI Free**. It is not a fork of the runtime, but an **additional package** with production workflows, templates, diagnostics, and integrations.

CoreAiPro must answer one question:

> How do I ship an AI feature in Unity faster and with lower production risk?

## Principles

- **The free runtime stays strong.** CoreAI Free must remain viable for real projects.
- **Pro sells time saved.** Templates, editor tooling, debugging, dashboards, and integrations are the paid value.
- **Heavy DRM only after demand is validated.** Start with a simple commercial license; online key activation when revenue justifies it.
- **Every Pro feature has a demo.** If it cannot be shown in a short video, it does not ship.
- **Support is also a product.** Define SLA boundaries, response expectations, and escalation rules.

## Package split

```text
com.nexoider.coreai             free core (runtime)
com.nexoider.coreaiunity        free Unity integration
com.nexoider.coreaipro          paid Unity add-on
```

Recommended repository layout:

```text
CoreAI/                         public repository
  Assets/CoreAI/
  Assets/CoreAiUnity/
  Docs/

CoreAiPro/                      private until release
  Assets/CoreAiPro/
    Editor/
    Runtime/
    Templates/
    Samples/
    Docs/
```

Pro depends on Free and **does not duplicate** its code.

## CoreAiPro v1 scope

### 1. Production agent templates

Templates solve concrete game tasks in Unity. Each template must include:

- configuration via ScriptableObject;
- a sample scene;
- tool contracts;
- a setup checklist;
- notes on tokens, latency, and typical failure modes.

Initial set:

| Template | Scenario | v1 scope |
|----------|----------|----------|
| Merchant | RPG / survival, NPC shop | Dialogue, inventory tracking, safe trade actions |
| Quest Giver | RPG / open world | Quest offers, state via tools, brief progress summary |
| Companion | Narrative / RPG | Personality, memory, contextual hints |

Complex roles such as Dungeon Master and Event Director come after the first three are stable.

### 2. Template wizard, not a graph editor

In v1, ship a pragmatic wizard, not a full node-based Agent Editor.

The wizard should:

- create an agent config from a template;
- choose role ID, model routing, streaming policy, and memory;
- bind available tools from the scene / project;
- generate a starter system prompt;
- send a test message from the editor.

Do **not** build a node graph in v1: a wizard ships faster and validates demand.

### 3. Debug and diagnostics panel

For production, this is often more valuable than a “pretty editor.”

Diagnostics in v1:

- recent requests by role;
- latency and timeouts;
- streaming / non-streaming marker;
- tool call log;
- last error and retry count;
- token estimates when data is available.

Reuse existing CoreAI logs where possible instead of building parallel telemetry.

### 4. Starter RAG kit

In v1, do not promise “a full vector DB as a product.” Ship a focused starter:

- index Markdown / TXT / JSON from the Unity editor;
- deterministic chunking;
- local keyword search with simple scoring;
- inject selected fragments into the prompt via documented hooks;
- a Lore Keeper example.

Full vector search lands in v1.5 / v2 if users actually ask for it.

### 5. Premium documentation and recipes

Paid value is high-quality implementation recipes:

- AI NPCs with safe actions;
- mentor / tutor;
- quest generation with validation;
- local models for privacy;
- WebGL deployment checklist;
- cost and latency optimization.

## Deferred features

Valuable but not required for first sales:

| Feature | Why later |
|---------|-----------|
| Full visual Agent Editor (node graph) | High cost, unclear buyer priority |
| Community template marketplace | Needs a user base |
| PlayMaker / Naninovel / Dialogue System connectors | Each adds support load |
| ElevenLabs / TTS | External accounts, pricing, legal |
| Online license activation | After proven demand for direct sales |
| Full analytics dashboard | Start with diagnostics; dashboard from real requests |

## Pricing and licensing

### Early Access

| Package | Price | Includes |
|---------|-------|----------|
| CoreAiPro Early Access | $49 | templates toward v1, preview of wizard and diagnostics, updates through v1 release |

Tell early access buyers explicitly what is included and what is planned.

### v1 release

| Package | Price | Includes |
|---------|-------|----------|
| CoreAiPro | $99 | templates, wizard, diagnostics, RAG starter, samples, 1 year of updates |
| CoreAiPro Studio | $249–$499 | multiple seats for small studios, priority onboarding, same codebase |

Do not introduce a subscription at launch until recurring support value is clear.

### License terms (recommended minimum)

- use CoreAiPro in the buyer’s commercial Unity projects;
- modify Pro code inside their own projects;
- no resale of sources/package as a competing asset or template pack;
- game builds may include runtime code as needed;
- support and updates within the purchased update window.

Before selling, add a dedicated `LICENSE_PRO.md` file.

## Development roadmap

### Milestone 0: foundation for sales (~1 week)

- CoreAI Free is stable for public release.
- Clean package versions and changelog.
- Reliable generation stop.
- Settings are not overwritten when opening the project.
- WebGL docs verified in practice.

### Milestone 1: Early Access (2–3 weeks)

- Merchant, Quest Giver, and Companion templates;
- minimal wizard to create assets from a template;
- basic diagnostics panel;
- one polished sample scene;
- Gumroad / Lemon Squeezy page.

### Milestone 2: v1 (4–6 weeks)

- documentation and checklists per template;
- RAG starter + Lore Keeper example;
- improved diagnostics and log export;
- Asset Store materials (screenshots, video);
- `LICENSE_PRO.md`, support and refund policy.

### Milestone 3: v1.5 (after feedback)

- additional templates from buyer requests;
- first third-party integration;
- studio package;
- public case study from a real project.

## Pro quality bar

A feature is release-ready when it has:

- a sample scene;
- a clear setup path from zero;
- error messages that hint what to fix;
- tests for logic outside Unity where appropriate;
- documentation with screenshots or short clips;
- an honest limitations list.

## Launch materials checklist

- hero video 60–90 seconds;
- 3 short clips / GIFs: wizard, in-game tool call, diagnostics;
- Free vs Pro comparison table;
- installation guide;
- “first project” tutorial;
- troubleshooting page;
- license file;
- refund and support policy;
- buyer contact.

## Success metrics

| Metric | Early Access | v1 |
|---------|--------------|-----|
| Paying users | 10–25 | 50+ |
| Refund rate | <10% | <8% |
| Time to first working demo | <15 min | <10 min |
| Support tickets per buyer | baseline measure | reduction via docs |
| Public demos | 1 | 3+ |

## Next step

Ship a **minimal** Pro package that can already be purchased:

1. Merchant template;
2. Quest Giver template;
3. Companion template;
4. wizard to create from a template;
5. basic diagnostics panel;
6. one sample scene and one demo video.

Everything else follows buyer feedback.
