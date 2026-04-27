# CoreAI Monetization Plan

Updated: 2026-04-27

## Positioning

CoreAI is a Unity-oriented framework for in-game AI agents: chat UI, streaming, tool calling, memory, WebGL, local models, and OpenAI-style HTTP backends.

Sell it as **infrastructure for production AI gameplay in Unity**, not as a “generic chat bot in Unity.” Core message:

> Add controllable AI agents in Unity without building orchestration, memory, streaming UI, backend routing, and tool-calling infrastructure yourself.

Primary buyers:

- **Unity indie developers** — AI NPCs, mentors, companions, dynamic quests, prototyping.
- **Small studios** — ready-made templates, support, faster adoption.
- **Education / simulations** — local or private AI inside Unity without mandatory player data in the cloud.

## Business model

Open core: the free package builds trust and audience; the paid layer saves implementation time.

```text
CoreAI Free
  - Core runtime
  - Chat panel
  - Streaming
  - Tool calling
  - Memory
  - HTTP / local model routing
  - Documentation and examples

CoreAiPro
  - Production agent templates
  - Editor workflows (wizard, tools)
  - Debugging / analytics
  - RAG helpers
  - Premium integrations
  - Priority support

Enterprise
  - Custom implementation for a project
  - On-prem / private deployment consulting
  - SLA-style support
  - Team onboarding
```

The free package must stay genuinely useful: **Free is the magnet**, **Pro is time saved**.

## Free vs Pro boundary

| Area | Free | Pro |
|------|------|-----|
| Core runtime | Yes | Yes |
| Chat and streaming | Yes | Yes |
| Tool calling | Yes | Yes |
| Basic memory | Yes | Yes |
| Sample game / demo agents | Yes | Yes |
| Production agent templates | Limited examples | Full template set |
| Agent editor | No | Yes (wizard / tools) |
| RAG | Manual APIs / examples | Editor-assisted indexing and search |
| Cost / latency | Logs | Dashboard and reports |
| Third-party package integrations | Minimal examples | Supported connectors |
| Support | Community / issues | Priority |

Critical runtime primitives are **not** hidden behind Pro. Paid value is **faster work**, **templates**, **tools**, **integrations**, **support**.

## Pricing

### Launch prices

| Product | Channel | Price | Notes |
|---------|---------|-------|-------|
| CoreAI Free | GitHub / Unity Asset Store | Free | Traffic, trust, docs, demos |
| CoreAiPro Early Access | Gumroad / Lemon Squeezy | $49 | First 25–50 buyers, feedback |
| CoreAiPro v1 | Gumroad / Lemon Squeezy | $99 | 1 year of updates |
| CoreAiPro Asset Store | Unity Asset Store | $79–$99 | Wider reach, ~30% platform fee |

At launch, use **one-time purchase**. Add a subscription only when support load and release cadence are clear.

### Enterprise

| Service | Price range | Deliverable |
|---------|-------------|-------------|
| Implementation review | $300–$750 | Architecture, integration recommendations |
| Custom agent template | $750–$2,500 | One production template for the project |
| Studio support | $300–$1,000/mo | Priority, bug triage, release support |
| Team onboarding | $300–$600 per session | Workshop + Q&A on the project |

Keep Enterprise tightly scoped: no “unlimited custom features” promises.

## Sales channels

### 1. Unity Asset Store

Trust and discovery channel.

Launch order:

1. Publish CoreAI Free.
2. Gather ratings, screenshots, short videos.
3. Publish CoreAiPro when the Pro offering is unlikely to drive mass refunds.

On the Asset Store page:

- GIF or ~30s video: NPC with tool calling.
- Link to WebGL demo if available.
- Compatibility matrix: Unity version, WebGL, local models, HTTP API.
- Clear Free vs Pro comparison.

### 2. Direct sales

Gumroad, Lemon Squeezy, or Stripe Payment Links — higher margin and faster iteration.

Direct sales page:

- One strong demo video.
- Three ready scenarios: AI NPC, mentor, quest giver.
- Plain-language license.
- Refund policy.
- Support expectations.

### 3. Content and community

Content sells **outcomes**, not class diagrams.

Topics for first pieces:

- “AI NPC in Unity in 10 minutes”
- “Local agents without sending player data to the cloud”
- “Tool calling: the model safely drives game systems”
- “WebGL: streaming chat in Unity”

Channels: YouTube Shorts + long-form, Reddit r/Unity3D, Unity forums, X/Twitter, Discord for Unity and AI in games, Habr / Dev.to for deep articles.

## 30 / 60 / 90 day plan

### First 30 days: sales readiness

- Stabilize CoreAI Free: stop generation, persistent settings, WebGL, package versioning.
- One polished demo scene: NPC or mentor with tool calling.
- Asset Store copy and screenshots.
- Simple landing page + email capture.
- Clearly describe CoreAiPro Early Access contents.
- Direct sales / early access page.

Exit criteria:

- A new user imports the package and runs the demo in **under 15 minutes**.
- **One** public demo video exists.
- There is a purchase page or waitlist.

### Days 31–60: first revenue

- Public CoreAI Free release.
- Sell CoreAiPro Early Access direct.
- 2–3 clear production templates.
- Discord or GitHub Discussions.
- Recurring questions → documentation.
- Outreach to 20–30 developers/studios with a short demo.

Exit criteria:

- 10+ early access buyers or qualified leads.
- 3+ real projects trying integration.
- Backlog prioritized by actual usage.

### Days 61–90: productization

- CoreAiPro v1.
- Asset Store submission (Free and/or Pro).
- License and commercial terms in the repository.
- Onboarding and troubleshooting checklists.
- Buyer feedback → case studies, examples.

Exit criteria:

- Repeatable install and first-run path.
- Stable paid package.
- At least one public case study or demo project.

## Revenue benchmarks

Conservatively: early revenue is **validation**, not scale.

| Period | Benchmark |
|--------|-----------|
| Month 1 | $0–500, mostly hypothesis testing |
| Months 2–3 | $500–2,000/mo |
| Months 4–6 | $2,000–5,000/mo |
| 6+ months | $5,000+/mo with accumulated content, templates, and support |

The most realistic path to first dollars: **direct Pro sales + small custom engagements**, not marketplace alone.

## Metrics

Track only what drives decisions:

- Demo views and CTR.
- GitHub stars / package downloads.
- Free → waitlist conversion.
- Waitlist → purchase.
- Activation: import + successful demo run.
- Support load per buyer.
- Refund rate.
- Real projects (prototypes and shipped) on CoreAI.

## Risks and mitigation

| Risk | Mitigation |
|------|------------|
| Hard onboarding | Demo scenes, setup wizard, diagnostics, docs before new features |
| Pro boundary feels like “half the game was cut” | Keep runtime in Free; Pro is templates, tools, integrations, support |
| Support consumes engineering | Support tiers, FAQ, clear Enterprise scope |
| Long Asset Store review | Direct sales first; Store as second channel |
| AI hype without real users | Lead with practical Unity demos and production constraints |
| Over-heavy license and DRM before demand | Simple commercial license + purchase records; key activation later if piracy is noticeable |

## Current priorities

1. Stable CoreAI Free: no settings reset, reliable stop, clear WebGL story.
2. One “hero” demo with tool calling in Unity.
3. Minimal CoreAiPro Early Access offering that can be bought today.
4. Landing page and first demo video.
5. Real users and feedback **before** large Pro-only systems.
