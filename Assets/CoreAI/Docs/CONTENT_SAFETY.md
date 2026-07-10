# Content Safety (Pluggable Filters)

Decision rule: if generated text ever reaches a player's screen — chat NPCs,
narration, lesson feedback — put an `IContentFilter` on both sides of the model
call. Ship `PassthroughContentFilter` while prototyping, a `WordlistContentFilter`
as a stop-gap, and a real moderation backend before targeting education or
console segments.

## Why

CoreAI's declared segments include education and player-facing NPC chat. Both
demand a moderation story: consoles have certification requirements for
user-visible generated text, and classroom deployments cannot show unfiltered
model output. CoreAI therefore ships the **mechanism** (a stable filter
contract plus a baseline implementation) and deliberately ships **no policy**:
there is no built-in profanity list, and nothing is filtered unless the host
wires a filter in.

## The Contract

Everything lives in the portable core assembly (`CoreAI.Core`, namespace
`CoreAI.Ai`, folder `Assets/CoreAI/Runtime/Core/Features/Llm/ContentSafety/`).
No Unity dependencies.

```csharp
public interface IContentFilter
{
    ContentFilterVerdict Evaluate(string text, ContentFilterContext context);
}
```

- `ContentFilterContext` (readonly struct) carries:
  - `Direction` — `UserInput` (player text about to be sent to the model) or
    `ModelOutput` (model text about to be shown to the player). Filters are
    meant to run on **both** directions.
  - `RoleId` — the agent role the conversation belongs to (`""` when unknown),
    so one filter can apply different strictness per role.
- `ContentFilterVerdict` (readonly struct) carries:
  - `Action` — `Allow` (pass the original text through unchanged), `Redact`
    (substitute `RedactedText`), or `Block` (do not deliver the message; the
    caller decides how to surface the refusal).
  - `RedactedText` — non-null only for `Redact`.
  - `Reason` — optional diagnostics string; may be null.
- `ContentFilterVerdict.Allow` is the struct `default`, so the common Allow
  path allocates nothing. Implementations must be thread-safe, must allow
  null/empty text, and must never throw on arbitrary input.

## Shipped Implementations

| Type | Behavior |
|------|----------|
| `PassthroughContentFilter.Instance` | Always `Allow`. The safe wiring default — call sites never need a null check. |
| `WordlistContentFilter` | Baseline over a caller-supplied blocked-term list. `ContentFilterMode.RedactTerms` replaces each match with same-length asterisks; `ContentFilterMode.BlockMessage` blocks the whole message on any match. Case-insensitive ordinal, whole-word-ish (a term never matches inside a larger word), Unicode-safe (Cyrillic included). Empty/null wordlist behaves as passthrough. |

## Usage

Pipeline auto-wiring does not exist yet (see below), so today you wrap your own
send/receive call site. Example around a chat service call:

```csharp
using CoreAI.Ai;

IContentFilter filter = new WordlistContentFilter(
    myStudioBlockedTerms,               // you supply the list; CoreAI ships none
    ContentFilterMode.RedactTerms);

async Task<string> SendPlayerMessageAsync(string playerText, string roleId)
{
    // 1. Player input, before it reaches the model.
    ContentFilterVerdict inVerdict = filter.Evaluate(
        playerText, new ContentFilterContext(ContentFilterDirection.UserInput, roleId));
    if (inVerdict.Action == ContentFilterAction.Block)
    {
        return "[message removed]";
    }

    string toSend = inVerdict.Action == ContentFilterAction.Redact
        ? inVerdict.RedactedText
        : playerText;

    string reply = await chatService.SendAsync(roleId, toSend);

    // 2. Model output, before it reaches the screen.
    ContentFilterVerdict outVerdict = filter.Evaluate(
        reply, new ContentFilterContext(ContentFilterDirection.ModelOutput, roleId));
    return outVerdict.Action switch
    {
        ContentFilterAction.Block => "[response removed]",
        ContentFilterAction.Redact => outVerdict.RedactedText,
        _ => reply
    };
}
```

## Planned Follow-Up: Pipeline Auto-Wiring

DI registration (defaulting to `PassthroughContentFilter.Instance`) and
automatic invocation inside the chat/orchestrator pipeline — so a host only
registers a filter and every role is covered, including streaming output — is a
planned follow-up. Until then the filter is invoked manually as shown above;
no existing pipeline file consults `IContentFilter` yet.

## Wordlists Are a Baseline, Not a Moderation Story

`WordlistContentFilter` exists so a project has *something* deterministic and
offline on day one. It cannot catch paraphrase, context, or adversarial
spelling. Real deployments — especially education and console submissions —
should implement `IContentFilter` over a proper moderation model or service
(a hosted moderation API, or a small local classifier) and keep the wordlist,
if at all, as a fast pre-pass.

## Tests

EditMode coverage lives in
`Assets/CoreAiUnity/Tests/EditMode/ContentFilterEditModeTests.cs`
(passthrough, redaction length/surrounding-text preservation, block-on-any-term,
case-insensitivity, whole-word behavior, empty wordlist, Cyrillic samples,
zero-payload Allow verdicts).
