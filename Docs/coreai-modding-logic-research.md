# CoreAI Modding Logic Research

Status: research report for reimplementation, 2026-07-07.

Scope: algorithms and design logic for CoreAI's Lua-CSharp mod system, not copy/paste licensing. CoreAI's north star is live, in-session world and mechanic mutation with a future host-authoritative multiplayer model. The recommendations below assume:

- Lua runs inside CoreAI-owned mod environments.
- The registry is C# authoritative.
- Inter-mod calls/events are data-only; functions, closures, live Unity refs, and coroutine handles do not cross mod boundaries.
- Multiplayer should replicate authoritative commands and deterministic data, not arbitrary client-side Lua effects.

Primary references used:

- AceSerializer-3.0 source: https://repos.wowace.com/wow/ace3/trunk/AceSerializer-3.0/AceSerializer-3.0.lua
- AceComm-3.0 source: https://repos.wowace.com/wow/ace3/trunk/AceComm-3.0/AceComm-3.0.lua
- CallbackHandler-1.0 source: https://repos.wowace.com/wow/ace3/trunk/CallbackHandler-1.0/CallbackHandler-1.0.lua
- AceEvent-3.0 source: https://repos.wowace.com/wow/ace3/trunk/AceEvent-3.0/AceEvent-3.0.lua
- Luanti Lua API: https://github.com/luanti-org/luanti/blob/master/doc/lua_api.md
- Luanti source tree/protocol context: https://github.com/luanti-org/luanti
- Factorio data lifecycle: https://lua-api.factorio.com/latest/auxiliary/data-lifecycle.html
- Factorio migrations: https://lua-api.factorio.com/latest/auxiliary/migrations.html
- Factorio `storage`: https://lua-api.factorio.com/latest/auxiliary/storage.html
- Factorio `remote`: https://lua-api.factorio.com/latest/classes/LuaRemote.html
- Factorio `script.on_configuration_changed`: https://lua-api.factorio.com/latest/classes/LuaBootstrap.html#on_configuration_changed
- GMod Auto Refresh hotload behavior: https://wiki.facepunch.com/gmod/Auto_Refresh

Note on uncertainty: the AceSerializer/AceComm/CallbackHandler descriptions are based on source-level knowledge. I could open the AceSerializer URL through the web tool, but external network fetches from PowerShell failed in this environment and some web searches returned no renderable snippets. I therefore mark details that are source-version-sensitive, especially exact throttling constants, instead of overstating them.

## 1. AceSerializer-3.0: Lua Values to Transmittable String

### Core idea

AceSerializer serializes a Lua value graph into a compact single string where every value begins with a marker character and a one-letter type tag. There are no JSON-style field names or separators. Tables are emitted as a stream:

```text
table-start, key1, value1, key2, value2, ..., table-end
```

This is close to a pre-order tree walk. The decoder consumes one value at a time from a cursor and recursively decodes nested tables.

### Type tags

The stable conceptual tag set:

| Lua value | Tag logic |
|---|---|
| `nil` | nil sentinel tag |
| boolean true/false | separate true/false tags, or boolean tag plus value bit depending on Ace3 version |
| number | number tag followed by string form of the number |
| string | string tag followed by escaped bytes until next marker |
| table | table-start tag, encoded key/value pairs, table-end tag |

AceSerializer uses a reserved marker/delimiter character to introduce tags. Strings escape the marker and control characters so that the parser can find the next marker without scanning quoted string syntax. Exact escape characters are implementation details; the important invariant is:

```text
Encoded strings contain no raw value-marker bytes and no raw disallowed control bytes.
```

For CoreAI, preserve this invariant rather than depending on Ace's exact byte choices.

### Number handling

AceSerializer writes Lua numbers using Lua's `tostring`-style representation plus special handling for values that do not round-trip portably:

- finite integers/floats: write a decimal/scientific string that Lua/C# can parse back;
- positive infinity: write a reserved infin tag;
- negative infinity: write a reserved negative-infinity tag;
- NaN/indeterminate: write a reserved NaN tag;
- huge values: treated as finite if the runtime can stringify/parse them, otherwise use infin tags.

For CoreAI, do not rely on locale-sensitive formatting. Use invariant culture and an exact round-trip format:

- integers: decimal string;
- finite doubles: `R` or `G17` with invariant culture;
- `NaN`: `nan`;
- `+Infinity`: `inf`;
- `-Infinity`: `-inf`.

### Table handling and cycles

AceSerializer serializes tables structurally, not by identity. It does not encode table references or shared identity. Cycles are invalid because recursion would never terminate. Shared subtables are duplicated on decode and are no longer the same object.

CoreAI should explicitly detect cycles and reject them with a clear serialization error. It should also enforce max depth and max encoded byte length to protect network and save paths.

### Recommended CoreAI wire grammar

This grammar keeps the AceSerializer logic but makes the implementation explicit and portable in C#:

```text
value      := marker tag payload?
marker     := '^'
nil        := '^Z'
true       := '^B'
false      := '^b'
number     := '^N' number-text-until-next-marker
string     := '^S' escaped-text-until-next-marker
table      := '^T' { value value } '^t'
```

String escape rule:

```text
Raw '^'       => '^~' or another two-byte escape
Raw control c => '^' + escape-code(c)
Other bytes   => unchanged UTF-8 bytes
```

Use one escape table for bytes `0..31`, byte `127`, and marker `^`. If CoreAI serializes .NET strings rather than Lua byte strings, first encode to UTF-8 bytes, escape bytes, then decode back during parse. That keeps embedded NUL/control values representable.

### Encoder data structures

```csharp
sealed class ModSerializerOptions
{
    public int MaxDepth = 64;
    public int MaxTableEntries = 100_000;
    public int MaxBytes = 1_000_000;
    public bool RejectFunctions = true;
    public bool RejectCycles = true;
}

sealed class SerializeContext
{
    public StringBuilder Output;
    public HashSet<LuaTable> ActiveTables; // recursion stack only
    public int Depth;
    public int EntryCount;
}
```

Use a recursion-stack set, not a global visited set, if duplicated non-cyclic subtables are allowed. A table only causes a cycle if it appears again on the active stack.

### `encode(value)` algorithm

```pseudo
function encode(value):
    ctx = new SerializeContext()
    writeValue(ctx, value)
    if ctx.Output.Length > MaxBytes:
        error "serialized payload too large"
    return ctx.Output.ToString()

function writeValue(ctx, value):
    if ctx.Depth > MaxDepth:
        error "max serialization depth exceeded"

    switch type(value):
        case nil:
            append("^Z")

        case boolean:
            append(value ? "^B" : "^b")

        case number:
            append("^N")
            if isNaN(value): append("nan")
            else if isPositiveInfinity(value): append("inf")
            else if isNegativeInfinity(value): append("-inf")
            else append(formatInvariantRoundTrip(value))

        case string:
            append("^S")
            append(escapeString(value))

        case table:
            if ctx.ActiveTables.Contains(value):
                error "cannot serialize cyclic table"

            ctx.ActiveTables.Add(value)
            ctx.Depth += 1
            append("^T")

            // Determinism note:
            // Ace uses Lua pairs order. CoreAI MP should not.
            // Sort keys by canonical encoded key bytes for replicated payloads.
            entries = enumerateEntries(value)
            if deterministic:
                entries.SortBy(canonicalKeySort)

            for each (k, v) in entries:
                ctx.EntryCount += 1
                if ctx.EntryCount > MaxTableEntries:
                    error "too many table entries"
                writeValue(ctx, k)
                writeValue(ctx, v)

            append("^t")
            ctx.Depth -= 1
            ctx.ActiveTables.Remove(value)

        default:
            error "unsupported serialized type"
```

### `escapeString(s)` algorithm

```pseudo
function escapeString(s):
    bytes = utf8(s)
    out = new ByteBuilder()
    for b in bytes:
        if b == byte('^'):
            out.add('^')
            out.add('^')        // literal marker escape
        else if b in ControlEscapeMap:
            out.add('^')
            out.add(ControlEscapeMap[b])
        else:
            out.add(b)
    return utf8DecodeNoValidationOrLatin1(out)
```

The second byte after `^` must never be a valid type tag unless the parser knows it is in string mode. In Ace-style parsing, string payload is read as raw text until the next marker and then unescaped; therefore the escape sequence itself includes the marker and consumes the escaped byte. A simpler CoreAI implementation can use a different escape prefix inside string payloads, for example `~xx` hex escapes. That is easier to reason about and still follows the AceSerializer mechanism.

### Decoder data structures

```csharp
sealed class DecodeCursor
{
    public string Text;
    public int Index;
    public int Depth;
}
```

Decoder must be strict:

- value must start with marker;
- unknown tag is an error;
- table must end with table-end tag;
- unexpected EOF is an error;
- trailing bytes after the root value are an error unless decoding a variadic list.

### `decode(text)` algorithm

```pseudo
function decode(text):
    cursor = new Cursor(text)
    value = readValue(cursor)
    if cursor.Index != text.Length:
        error "trailing bytes after serialized value"
    return value

function readValue(cursor):
    require cursor.Text[cursor.Index] == '^'
    cursor.Index += 1
    tag = cursor.Text[cursor.Index]
    cursor.Index += 1

    switch tag:
        case 'Z':
            return nil

        case 'B':
            return true

        case 'b':
            return false

        case 'N':
            token = readUntilNextMarker(cursor)
            if token == "nan": return NaN
            if token == "inf": return +Infinity
            if token == "-inf": return -Infinity
            return parseInvariantNumber(token)

        case 'S':
            token = readUntilNextMarker(cursor)
            return unescapeString(token)

        case 'T':
            cursor.Depth += 1
            if cursor.Depth > MaxDepth:
                error "max decode depth exceeded"
            table = new LuaTable()
            while not peekTag(cursor, 't'):
                key = readValue(cursor)
                value = readValue(cursor)
                table[key] = value
            consume("^t")
            cursor.Depth -= 1
            return table

        case 't':
            error "unexpected table end"

        default:
            error "unknown serialized tag"
```

### Edge cases for CoreAI

- `nil` as a table value: Lua cannot store `t[k] = nil` as an entry. If C# can represent nullable map values, decide whether serialized tables may contain nil values. Ace's table stream can encode a nil value, but a Lua table assignment will delete it. For deterministic mod data, reject nil table values or encode array/map DTOs explicitly.
- Mixed numeric/string keys: sort deterministically for MP; Lua `pairs` order is not stable.
- Floating point precision: use invariant round-trip. If deterministic simulation depends on numeric equality across platforms, restrict network mod data to integers/fixed-point or authoritative host values.
- Function/thread/userdata: reject at boundary. Store IDs/handles only if the C# registry can resolve them authoritatively.
- Table metatables: ignore/reject. Do not serialize metatable behavior.

## 2. AceComm-3.0: Chunking, Multipart Framing, Reassembly, Throttle

### Core idea

AceComm sits above WoW addon messages. The underlying channel has a maximum payload size, so AceComm:

1. validates a short addon-message prefix;
2. splits large serialized payloads into fixed-size chunks;
3. sends one message for single-part payloads or a multipart sequence;
4. frames multipart messages with "first", "next", and "last" control bytes;
5. reassembles chunks per sender/prefix;
6. uses ChatThrottleLib to queue and rate-limit sends by priority.

This maps well to CoreAI's future mod-data transport: the serializer makes one canonical string, the transport makes it deliverable.

### Framing model

AceComm's conceptual message categories:

```text
NORMAL       prefix + payload                // payload fits one message
MULTI_FIRST  prefix + first chunk
MULTI_NEXT   prefix + next chunk
MULTI_LAST   prefix + final chunk
```

WoW addon messaging already carries an addon prefix separately. In a custom CoreAI transport, include it in the frame:

```csharp
enum ModFrameKind : byte
{
    Single = 0,
    First = 1,
    Next = 2,
    Last = 3,
    Abort = 4
}

struct ModFrame
{
    string Channel;       // e.g. "coreai.moddata"
    string Prefix;        // logical protocol/mod prefix
    ulong MessageId;      // CoreAI addition for safe interleaving
    int Sequence;         // CoreAI addition
    ModFrameKind Kind;
    byte[] PayloadChunk;
}
```

AceComm can rely on ordered messages from one sender enough to reassemble by sender+prefix. CoreAI should not. Add `MessageId` and sequence numbers because future transports may reorder or interleave messages.

### Chunking algorithm

```pseudo
function chunk(prefix, bytes, maxPayloadBytes):
    headerBudget = estimateFrameHeaderBytes(prefix)
    chunkSize = maxPayloadBytes - headerBudget
    if chunkSize <= 0:
        error "prefix/header too large"

    if bytes.Length <= chunkSize:
        yield Frame(Single, messageId=nextId(), sequence=0, chunk=bytes)
        return

    messageId = nextId()
    offset = 0
    sequence = 0
    while offset < bytes.Length:
        count = min(chunkSize, bytes.Length - offset)
        chunk = bytes.slice(offset, count)
        if offset == 0:
            kind = First
        else if offset + count == bytes.Length:
            kind = Last
        else:
            kind = Next

        yield Frame(kind, messageId, sequence, chunk)
        offset += count
        sequence += 1
```

AceComm's max chunk size is tied to WoW addon-message limits and prefix bytes. CoreAI should make it transport-specific, for example 900 to 1200 bytes for conservative UDP-like datagrams or larger for reliable streams.

### Reassembly state

```csharp
sealed class ReassemblyKey
{
    public PeerId Sender;
    public string Prefix;
    public ulong MessageId;
}

sealed class ReassemblyState
{
    public MemoryStream Buffer;
    public int NextSequence;
    public double StartedAtTime;
    public double LastFrameTime;
    public int TotalBytes;
}

Dictionary<ReassemblyKey, ReassemblyState> inflight;
```

AceComm's old-world model uses sender+prefix and an accumulation table. CoreAI needs `MessageId` to prevent two large messages from the same sender/prefix from corrupting each other.

### `onReceiveFrame(frame)` algorithm

```pseudo
function onReceiveFrame(sender, frame):
    if frame.Kind == Single:
        deliver(sender, frame.Prefix, frame.PayloadChunk)
        return

    key = (sender, frame.Prefix, frame.MessageId)

    if frame.Kind == First:
        if inflight.Contains(key):
            discard inflight[key] // or reject duplicate start
        st = new ReassemblyState()
        st.NextSequence = 0
        inflight[key] = st
        appendExpected(st, frame)
        return

    st = inflight.Get(key)
    if st == null:
        // Late next/last without a first. Drop and optionally request resend.
        return

    if frame.Sequence != st.NextSequence:
        inflight.Remove(key)
        emitTransportError("multipart sequence gap/reorder", key)
        return

    appendExpected(st, frame)

    if frame.Kind == Last:
        bytes = st.Buffer.ToArray()
        inflight.Remove(key)
        deliver(sender, frame.Prefix, bytes)

function appendExpected(st, frame):
    if frame.PayloadChunk.Length == 0:
        error/drop "empty multipart chunk"
    if st.TotalBytes + frame.PayloadChunk.Length > MaxReassemblyBytes:
        drop state and error "message too large"
    st.Buffer.Write(frame.PayloadChunk)
    st.TotalBytes += frame.PayloadChunk.Length
    st.NextSequence += 1
    st.LastFrameTime = now()
```

Periodic cleanup:

```pseudo
function cleanupInflight():
    for each (key, st) in inflight:
        if now() - st.LastFrameTime > ReassemblyTimeout:
            inflight.Remove(key)
            emitTransportError("multipart timeout", key)
```

### Send throttling/queueing

AceComm delegates throttling to ChatThrottleLib. The mechanism is a priority queue plus a byte budget replenished over time. Exact ChatThrottleLib constants are source-version-sensitive and were not directly verified in this run. The reimplementation logic is:

- each outgoing frame has byte cost = header bytes + payload bytes;
- each priority lane has a queue, commonly "ALERT", "NORMAL", and "BULK";
- a token bucket refills at `bytesPerSecond * deltaTime`;
- high-priority queues drain before low-priority queues;
- frames that do not fit the current bucket remain queued;
- drain runs on update/timer until queues are empty.

```csharp
enum SendPriority { Alert, Normal, Bulk }

sealed class SendItem
{
    public PeerId Target;
    public ModFrame Frame;
    public int ByteCost;
    public SendPriority Priority;
}

sealed class ThrottledSender
{
    double tokens;
    double maxBurstBytes;
    double bytesPerSecond;
    Queue<SendItem> alert, normal, bulk;
}
```

```pseudo
function enqueueSend(item):
    queueFor(item.Priority).Enqueue(item)

function pumpSend(deltaTime):
    tokens = min(maxBurstBytes, tokens + bytesPerSecond * deltaTime)

    while true:
        item = peekNextByPriority()
        if item == null:
            return
        if item.ByteCost > maxSingleFrameBytes:
            drop item and error "frame too large"
            continue
        if item.ByteCost > tokens:
            return

        dequeue item
        tokens -= item.ByteCost
        transportSend(item.Target, item.Frame)
```

### CoreAI edge cases

- Use reliable ordered channels for state-changing commands if available. If not, add ack/resend or reject out-of-order chunks.
- Reassembly must be bounded by time and bytes.
- Prefix should identify protocol version: `coreai.moddata.v1`.
- Do not deliver partial state. Only deliver after full message decode and validation.
- For live mod changes, frame commands and snapshots separately so a huge snapshot cannot block urgent mechanic commands. Use priorities.

## 3. CallbackHandler-1.0 / AceEvent-3.0: Event Fan-out

### Core idea

CallbackHandler is a small callback registry. AceEvent wraps it for WoW events/messages. The key mechanisms:

- callbacks are grouped by event name;
- each registration is associated with a target object/table or function;
- unregister removes one callback or all callbacks for a target;
- firing an event fans out to registered callbacks;
- errors in one callback are caught/reported so remaining handlers still run;
- re-entrancy is handled by stable iteration over the current callback set.

### Data structures

A reimplementation-friendly structure:

```csharp
sealed class HandlerRecord
{
    public long Id;
    public string EventName;
    public string OwnerModId;
    public LuaFunction Function;       // not cross-boundary; lives in owner VM
    public bool Removed;
    public int ActiveDispatchCount;
}

sealed class EventBucket
{
    public List<HandlerRecord> Ordered = new();
    public Dictionary<long, HandlerRecord> ById = new();
    public bool NeedsCompaction;
}

sealed class ModEventBus
{
    public Dictionary<string, EventBucket> Buckets = new();
    public long NextHandlerId;
    public int DispatchDepth;
}
```

For deterministic MP, order by `(loadOrder, registrationSequence)` rather than hash/dictionary order.

### Register

```pseudo
function mods_on(ownerModId, eventName, luaFunction):
    validate eventName
    assert luaFunction belongs to ownerModId environment

    bucket = buckets.GetOrCreate(eventName)
    rec = new HandlerRecord(
        Id = NextHandlerId++,
        EventName = eventName,
        OwnerModId = ownerModId,
        Function = luaFunction,
        Removed = false)

    bucket.Ordered.Add(rec)
    bucket.ById[rec.Id] = rec
    return rec.Id
```

### Unregister

```pseudo
function mods_off(ownerModId, handlerId):
    rec = find handler by id
    if rec == null:
        return false
    if rec.OwnerModId != ownerModId:
        error "cannot unregister another mod's handler"

    rec.Removed = true
    bucket.ById.Remove(handlerId)
    if DispatchDepth > 0:
        bucket.NeedsCompaction = true
    else:
        remove rec from bucket.Ordered
    return true
```

### Fire with re-entrancy and error isolation

CallbackHandler's important behavior is that changing registration during a fire does not corrupt iteration. Use a snapshot or tombstones. Snapshot is simpler; tombstones allocate less.

Snapshot algorithm:

```pseudo
function mods_emit(eventName, data):
    payload = deepCopyPlainData(data)
    bucket = buckets.Get(eventName)
    if bucket == null:
        return 0

    snapshot = bucket.Ordered.ToArray()
    DispatchDepth += 1
    fired = 0

    for rec in snapshot:
        if rec.Removed:
            continue
        // A handler registered after snapshot creation does not receive this fire.
        // A handler removed before its turn is skipped.
        try:
            callHandlerUnderOwnerBudget(rec, eventName, deepCopyPlainData(payload))
            fired += 1
        catch ex:
            logModHandlerError(rec.OwnerModId, eventName, ex)
            // continue fan-out

    DispatchDepth -= 1
    if DispatchDepth == 0:
        compactBucketsWithTombstones()

    return fired
```

Tombstone algorithm:

```pseudo
function compact(bucket):
    bucket.Ordered.RemoveAll(rec => rec.Removed)
    bucket.NeedsCompaction = false
```

### AceEvent mapping

AceEvent registers host/game events and forwards them into CallbackHandler. For CoreAI, use two layers:

1. Host event adapters: `world_ready`, `object_spawned`, `tick`, `save`, `load`, `mechanic_applied`, etc.
2. Mod event bus: data-only `mods_emit` for inter-mod and host-to-mod signals.

Do not allow a Lua handler to call another mod's closure directly. The bus owns dispatch and budget.

### Edge cases

- Handler unregisters itself: mark removed; current invocation completes; later events skip it.
- Handler unregisters another handler for the same event: if that handler has not fired in the current snapshot, skip it.
- Handler registers a new handler during fire: new handler does not run until the next fire.
- Handler emits the same event recursively: allowed only with max recursion depth and per-mod instruction budget.
- Handler errors: log and continue.
- Mod unload/reload: unregister all handlers owned by mod id before binding new chunk.

## 4. Luanti / Minetest: Server-Authoritative Modding

### Architecture summary

Luanti is the closest open-source model for CoreAI's future host-authoritative modding:

- The server owns the authoritative map, active objects, inventories, privileges, time, and game rules.
- Server-side Lua mods register callbacks and mutate authoritative state through engine APIs.
- Clients render, send controls/actions, and may do local prediction for responsiveness, but the server validates and broadcasts the accepted state.
- The server step/tick drives global callbacks, timers, object logic, environment updates, and network sends.
- Clients receive mapblocks, object updates, formspec/UI data, media, item/node definitions, and other replicated state from the server.

For CoreAI, the important design is not the exact block protocol; it is the authority split:

```text
Client input/request -> Host validates through C# command channel -> Host Lua mods may react -> Host mutates state -> Host replicates accepted result.
```

### Registration model

Luanti mods call registration functions during load:

```lua
minetest.register_node("mymod:stone", def)
minetest.register_globalstep(function(dtime) ... end)
minetest.register_on_joinplayer(function(player) ... end)
minetest.register_on_punchnode(function(pos, node, puncher, pointed_thing) ... end)
minetest.after(delay, function(...) ... end, args...)
```

The engine stores callbacks in C++/Lua-owned registries. At runtime, engine events call the registered Lua functions in a defined callback phase.

CoreAI equivalent:

```lua
coreai.register_mechanic("dash", {
  version = 1,
  inputs = {"player_input"},
  config = { speed = 12, cooldown = 0.8 }
})

coreai.on("player_input", function(e)
  if e.action == "dash" then
    coreai.command("player.apply_impulse", { player = e.player, x = 0, y = 0, z = 12 })
  end
end)

coreai.after(0.8, "dash.cooldown_reset", { player = e.player })
```

### Tick/step model

Luanti has a server step loop. In each step, conceptually:

```pseudo
while server running:
    dtime = now - lastStep
    receive client packets
    process player inputs/commands
    run scheduled timers whose time <= now
    run registered globalstep callbacks with dtime
    process environment/map/object simulation
    flush authoritative changes to clients
    sleep/yield until next step
```

The key is that Lua callbacks are part of the server simulation step, not client-local truth.

CoreAI should use a fixed or semi-fixed host simulation tick for replicated mod changes:

```pseudo
HostTick(tickIndex):
    drain client command requests tagged with tick/input seq
    validate and enqueue accepted CoreAI commands
    run due mod timers
    run mod event bus for queued host events
    apply world/mechanic command log in deterministic order
    produce replication delta for this tick
    send delta/acks to clients
```

### `minetest.after` timer logic

`minetest.after(delay, func, ...)` stores a timer with:

- target time = current server time + delay;
- Lua callback function;
- captured arguments.

On each server step, due timers are invoked. Important hot-reload lesson: a timer captures an old closure. If a mod reloads, outstanding timers must be owned by mod id and cancelled or rebound.

CoreAI should not store raw callback timers across reload. Use named data timers:

```csharp
sealed class ModTimer
{
    public string OwnerModId;
    public string TimerName;
    public double DueTime;
    public PlainData Payload;
    public int Generation;
}
```

Lua API:

```lua
coreai.after(0.8, "dash.cooldown_reset", { player = player_id })

function on_timer(name, payload)
  if name == "dash.cooldown_reset" then ...
  end
end
```

### Sync flow for a mod-driven world change

Example: a server mod turns water into ice around a player.

```pseudo
Client:
    player presses ability key
    send InputCommand { playerId, action="freeze_area", inputSeq=123 }

Host:
    receive InputCommand
    validate player can act
    emit host event "player_action" to Lua mods

Host Lua mod:
    on player_action:
        if action == "freeze_area":
            coreai.command("world.batch_set_tiles", {
                reason = "freeze_area",
                positions = [...],
                tile = "ice"
            })

Host C#:
    validate command capability and arguments
    apply to authoritative world state
    record command in tick log
    compute world delta

Network:
    send Delta { tick, commandsApplied, changedTiles, ack inputSeq=123 }

Clients:
    apply authoritative delta
    correct prediction if needed
    render ice
```

### Consistency rules to borrow

- Definitions/registrations are server-owned. Clients can know them for rendering/UI, but cannot authoritatively instantiate mechanics.
- Client prediction is allowed for player feel, never for final game state.
- Every mutating mod API routes through host validation.
- Randomness must be host-owned or seed/tick-owned.
- Callback order must be deterministic.
- Long-running Lua must be budgeted; server tick cannot hang.

## 5. Factorio: `on_configuration_changed`, `storage`, and `remote`

### Runtime lifecycle model

Factorio separates mod code loading from persistent state. Key concepts:

- `storage` is the persistent Lua table saved with the game.
- Mod code can change between loads.
- `script.on_init` initializes storage for a new save.
- `script.on_load` re-registers runtime-only things after load; it must not mutate game state.
- `script.on_configuration_changed` runs when mods/versions/configuration changed and is where migrations/upgrades happen.
- Migration files can run before/around configuration change to transform saved state.

The exact engine order has details, but the reimplementation-relevant flow is:

```text
Load save -> load current mod code/control.lua -> reconstruct `storage` -> run migrations/config-change handlers if mod set/version changed -> continue simulation.
```

### CoreAI state model

Separate state into:

```csharp
sealed class ModPersistentState
{
    public string ModId;
    public SemanticVersion SchemaVersion;
    public PlainData Storage;        // saved, data-only
}

sealed class ModRuntimeState
{
    public string ModId;
    public int Generation;           // increments on reload
    public LuaEnvironment Env;
    public List<long> HandlerIds;
    public List<ModTimer> Timers;
    public Dictionary<string, ModRemoteFunction> RemoteExports;
}
```

Persistent `storage` survives reloads and save/load. Runtime registrations do not; they are rebuilt from the current script.

### Configuration changed flow

When a mod version or dependency set changes:

```pseudo
function reloadOrUpgradeMod(modId, newSource, newVersion):
    oldPersistent = persistentStore.Load(modId)
    oldVersion = oldPersistent.SchemaVersion

    // 1. Load code into a fresh environment, but do not expose it yet.
    newEnv = compileAndInstantiate(newSource)

    // 2. Attach existing storage by value/reference according to isolation policy.
    newEnv.storage = deepCopyOrOwnedProxy(oldPersistent.Storage)

    // 3. Run explicit migration chain if schema changed.
    for migration in migrations where oldVersion < migration.Version <= newVersion:
        runUnderBudget(newEnv, migration.Function, newEnv.storage, {
            old_version = oldVersion,
            new_version = migration.Version
        })
        validatePlainData(newEnv.storage)
        oldVersion = migration.Version

    // 4. Run on_configuration_changed hook once after migrations.
    if newEnv.has("on_configuration_changed"):
        runUnderBudget(newEnv, "on_configuration_changed", {
            mod_changes = diff,
            old_version = oldPersistent.SchemaVersion,
            new_version = newVersion
        })
        validatePlainData(newEnv.storage)

    // 5. Commit persistent state atomically.
    persistentStore.Save(modId, newVersion, newEnv.storage)

    // 6. Swap runtime generation only after migration succeeded.
    unloadOldRuntime(modId)
    bindRuntimeRegistrations(newEnv)
    runtime[modId] = new ModRuntimeState(newEnv, generation + 1)
```

If migration fails, keep old runtime and old persistent state. Do not partially swap.

### `storage` rules

Factorio's `storage` is a global persistent table for a mod. For CoreAI:

- `storage` is per mod.
- It contains only plain data: nil/booleans/numbers/strings/tables with no cycles, no functions, no Unity refs.
- It has a schema version.
- It is validated after each reload/migration and before save.
- Writes can be proxied so CoreAI can mark dirty and enforce size limits.

Recommended Lua API:

```lua
storage.player_dash = storage.player_dash or {}
storage.schema_version = 2
```

C# should wrap with:

- max bytes per mod;
- max depth;
- no metatables;
- deterministic serialization for network/save;
- optional transaction around reload/migration.

### `remote.add_interface` / `remote.call`

Factorio's remote model lets a mod expose named functions under an interface name. Other mods call by interface and function name. Functions are not passed as data; they stay inside the owning mod's interface table.

Conceptual semantics:

```lua
remote.add_interface("my_mod", {
  get_score = function(player_id) return storage.scores[player_id] or 0 end,
  set_score = function(player_id, value) storage.scores[player_id] = value end
})

local score = remote.call("my_mod", "get_score", player_id)
```

CoreAI should implement the registry in C#:

```csharp
sealed class RemoteInterface
{
    public string InterfaceName;
    public string OwnerModId;
    public int OwnerGeneration;
    public Dictionary<string, RemoteFunction> Functions;
}

sealed class RemoteFunction
{
    public string Name;
    public LuaFunction Function; // callable only by C# dispatcher
    public DataSchema? ArgSchema;
    public DataSchema? ReturnSchema;
}
```

Registration:

```pseudo
function mods_register(ownerModId, interfaceName, functionTable):
    validate interfaceName globally unique or version-resolved
    for each (name, fn) in functionTable:
        require type(name) == string
        require type(fn) == function
    registry[interfaceName] = RemoteInterface(ownerModId, currentGeneration, functions)
```

Call:

```pseudo
function mods_call(callerModId, interfaceName, functionName, args):
    validate args are plain data
    iface = registry[interfaceName] or error
    fn = iface.Functions[functionName] or error

    // Deep-copy prevents caller and callee sharing live tables.
    copiedArgs = deepCopyPlainData(args)

    result = callUnderBudget(
        ownerModId = iface.OwnerModId,
        function = fn,
        args = copiedArgs,
        callContext = { caller = callerModId })

    validate result is plain data
    return deepCopyPlainData(result)
```

### Data-only rule

Do not allow:

- function values in args/returns;
- coroutine/thread values;
- userdata;
- Unity object references;
- live tables shared between mods;
- metatables that can smuggle behavior.

Allow handles only as immutable IDs:

```lua
mods_call("inventory", "add_item", { entity = "entity:1024", item = "wood", count = 3 })
```

The C# authoritative registry resolves handles and validates capabilities.

### Hot-reload/migration edge cases

- Interface removed: callers should get `interface not found`, not a stale closure.
- Function renamed: expose introspection so AI can adapt: `mods_interfaces()`, `mods_has(iface, fn)`.
- Reload while another mod calls it: serialize calls through a runtime lock or reject during swap.
- Migration updates storage and interface together atomically.

## 6. Live Hot-Reload Sequence

### Common mechanism across hotload systems

Whether in GMod Auto Refresh, Mudlet-style reloads, Barotrauma LuaCs patterns, or Factorio's mod lifecycle, robust hot reload requires these separations:

- durable state is data;
- runtime bindings are disposable;
- event handlers/timers/coroutines are owned by a mod generation;
- reload creates a fresh environment;
- old closures are not left reachable from central registries.

### CoreAI runtime ownership model

```csharp
sealed class LoadedMod
{
    public string ModId;
    public int Generation;
    public LuaEnvironment Env;
    public PlainData Storage;
    public List<long> EventHandlerIds;
    public List<long> HostHookIds;
    public List<ModTimer> Timers;
    public List<ModCoroutine> Coroutines;
    public List<string> RemoteInterfaces;
    public CancellationTokenSource Cancellation;
}
```

Every central registration stores `(modId, generation)`. A callback whose generation is not current is ignored and then compacted.

### Reload algorithm

```pseudo
function ReloadMod(modId, newSource, reason):
    old = runtime.Get(modId)
    oldStorage = old?.Storage ?? persistentStore.LoadOrDefault(modId)
    oldGeneration = old?.Generation ?? 0

    // A. Stop old execution from scheduling more work.
    if old != null:
        old.Cancellation.Cancel()
        markModAsReloading(modId)

    // B. Snapshot durable state.
    storageSnapshot = deepCopyPlainData(oldStorage)

    // C. Compile new source in isolation.
    compileResult = compileLua(newSource)
    if compileResult.Failed:
        unmarkReloading(modId)
        keepOldRuntimeAlive(old)
        return error compileResult

    newEnv = createFreshEnvironment(modId)
    newEnv.storage = storageSnapshot
    newGeneration = oldGeneration + 1

    // D. Let new code define functions and register static exports into a staging registry.
    staging = new StagingRegistrations(modId, newGeneration)
    newEnv.BindRegistrationSink(staging)

    runResult = runChunkUnderBudget(newEnv, compileResult.Chunk)
    if runResult.Failed:
        unmarkReloading(modId)
        keepOldRuntimeAlive(old)
        return error runResult

    // E. Run migration/reload hook before exposing new callbacks.
    if newEnv.hasFunction("on_reload"):
        hookResult = callUnderBudget(newEnv, "on_reload", {
            reason = reason,
            old_generation = oldGeneration,
            new_generation = newGeneration
        })
        if hookResult.Failed:
            unmarkReloading(modId)
            keepOldRuntimeAlive(old)
            return error hookResult

    validatePlainData(newEnv.storage)

    // F. Commit: remove old runtime registrations, then publish staged new ones.
    beginRuntimeSwapLock()
    try:
        if old != null:
            unregisterAllOwnedBy(modId, old.Generation)
            cancelTimersOwnedBy(modId, old.Generation)
            cancelCoroutinesOwnedBy(modId, old.Generation)
            removeRemoteInterfacesOwnedBy(modId, old.Generation)
            disposeLuaEnvironment(old.Env)

        publishStagedRegistrations(staging)
        runtime[modId] = LoadedMod(
            ModId = modId,
            Generation = newGeneration,
            Env = newEnv,
            Storage = newEnv.storage,
            registrations = staging.ids)

        persistentStore.Save(modId, newEnv.storage)
    finally:
        endRuntimeSwapLock()
        unmarkReloading(modId)

    emitHostEvent("mod_reloaded", { mod = modId, generation = newGeneration })
    return success
```

### Avoiding orphaned closures

Problem sources:

- event bus holds old Lua functions;
- timers hold old Lua functions;
- coroutines suspended in old environment;
- remote registry holds old function refs;
- C# delegates/lambdas wrap old Lua functions;
- async tasks complete after reload and call back into old env.

Solutions:

- Central registries store owner generation and support `unregisterAllOwnedBy(modId, generation)`.
- Timers are data timers, not closure timers.
- Coroutines get cancellation token per generation; reload cancels them.
- Remote interface registry is rebuilt from staging; old interface functions are removed before publish.
- Async completions check `isCurrentGeneration(modId, generation)` before applying effects.
- Old Lua environment is disposed after registry removal.

### Reload hook shape

Prefer:

```lua
function on_reload(ctx)
  storage.schema_version = storage.schema_version or 1
  if storage.schema_version < 2 then
    storage.cooldowns = storage.cooldowns or {}
    storage.schema_version = 2
  end
end
```

Avoid passing old function tables to new code. If old code needs to export migration data, it should have stored that data in `storage` before reload.

### File-watch reload store

```pseudo
onFileChanged(modId):
    debounce for 200-500 ms
    read full source
    if hash == lastLoadedHash:
        return
    result = ReloadMod(modId, source, reason="file_changed")
    if result.success:
        lastLoadedHash = hash
    else:
        log error and keep old generation
```

Do not unload the working generation until the new generation compiles and migrates successfully.

## 7. Multiplayer Sync Recommendation for CoreAI

### Chosen model

Use host-authoritative command replication with periodic serialized-state snapshots.

Do not use pure deterministic lockstep as the primary model. Do not use client-authored serialized-state snapshots as truth.

### Why not deterministic lockstep

Lockstep requires every client to run the same Lua, same floating point, same callback order, same random stream, and same timing. CoreAI's value is live mechanic mutation by AI/Lua while playing. That is exactly where lockstep becomes fragile:

- scripts can change mid-session;
- AI can generate code that has platform-sensitive behavior;
- Unity physics is not deterministic enough across clients;
- Lua table iteration order can differ unless aggressively constrained;
- hot-reload timing introduces generation boundaries.

Use deterministic ordering on the host for reproducibility and debugging, but do not require every client to independently simulate truth.

### Why not serialized-state snapshot only

Snapshots are robust but too blunt for live mechanic changes:

- large world snapshots are expensive;
- clients cannot easily predict intent;
- debugging "why did state change?" is harder;
- live mechanics need semantic events: mechanic created, parameter changed, object spawned, rule enabled.

Snapshots are still necessary for join-in-progress, recovery, and drift repair.

### Recommended hybrid

```text
Host command log = truth for live changes.
Host snapshots = recovery and join state.
Clients send requests/inputs, not authoritative mutations.
```

Message types:

```csharp
enum ReplicationMessageKind
{
    ClientRequest,          // client -> host
    CommandAccepted,        // host -> clients, semantic command
    CommandRejected,        // host -> requesting client
    WorldDelta,             // host -> clients, changed state
    MechanicDefinitionDelta,// host -> clients, live mechanic changes
    StateSnapshot,          // host -> client, checkpoint/join/recovery
    ModDataChunk            // AceComm-style chunked serialized data
}
```

Host tick:

```pseudo
HostTick(tick):
    requests = drainClientRequestsSortedBy(peerId, inputSeq)

    for req in requests:
        if validateRequest(req):
            command = convertToAuthoritativeCommand(req)
            enqueueHostCommand(command)
        else:
            sendReject(req)

    runLuaEventsAndTimers()

    // Lua may enqueue more host commands through coreai.command(...)
    commands = drainCommandQueueSortedBy(authorityOrder)
    for cmd in commands:
        if validateCommand(cmd):
            applyCommand(cmd)
            appendCommandLog(tick, cmd)

    delta = buildDeltaSinceLastTick()
    sendToClients(CommandAccepted/WorldDelta/MechanicDefinitionDelta)

    if tick % snapshotInterval == 0:
        sendChunkedSnapshot()
```

### AceComm/AceSerializer mapping

- AceSerializer pattern serializes plain data command payloads and snapshots.
- AceComm pattern chunks large snapshots or mod-storage deltas.
- CallbackHandler pattern dispatches host events and inter-mod events on the host.
- Luanti pattern supplies the authority split.
- Factorio pattern supplies persistent state migration and data-only remote calls.

### CoreAI deterministic constraints

For all replicated commands:

- include `tick`, `commandId`, `sourceModId`, `sourceGeneration`, and schema version;
- validate payload as plain data;
- sort command application by deterministic host-owned order;
- host owns random seeds/results;
- clients apply deltas from host, not local Lua decisions;
- if clients run cosmetic Lua, isolate it from authoritative APIs.

## 8. Live World/Mechanic Mutation API Shape

### Principle

Lua should request authoritative mutations through a command channel. It should not directly mutate Unity objects or client-local world state when the change affects gameplay or shared state.

Good API:

```lua
coreai.command("mechanic.define", {
  id = "dash",
  version = 3,
  inputs = { "player_input" },
  params = {
    speed = 12,
    cooldown = 0.8
  }
})

coreai.command("world.spawn", {
  prefab = "crate",
  position = { x = 3, y = 0, z = 8 },
  tags = { "dynamic", "loot" }
})

coreai.command("mechanic.set_param", {
  id = "dash",
  key = "cooldown",
  value = 0.6
})
```

Bad API for multiplayer truth:

```lua
unity.get_object("Player").transform.position.x = 999
some_other_mod.internal_table.cooldown = 0
client_world.spawn_prefab("crate")
```

Those bypass validation, authority, logging, rollback, and replication.

### Command data structure

```csharp
sealed class CoreAiCommand
{
    public string Type;              // "mechanic.define", "world.spawn"
    public string SourceModId;
    public int SourceGeneration;
    public long LocalSequence;
    public long? HostCommandId;
    public int? HostTick;
    public PlainData Payload;
    public CommandPolicy Policy;
}

sealed class CommandPolicy
{
    public bool Replicate;
    public bool Persist;
    public bool RequiresHostOnly;
    public string RequiredCapability;
}
```

### Command application pipeline

```pseudo
function coreai.command(type, payload):
    validate type exists in C# command registry
    validate payload plain data
    validate mod has capability for type
    enqueue command into host command queue
    return { queued = true, local_sequence = nextSeq }

HostApplyCommand(cmd):
    schema = commandRegistry[cmd.Type]
    args = schema.ValidateAndNormalize(cmd.Payload)
    if not capabilityPolicy.Allows(cmd.SourceModId, cmd.Type, args):
        reject

    result = schema.Apply(authoritativeWorld, args)
    if result.Success:
        record command log
        emit event "command_applied"
        replicate if policy.Replicate
    else:
        emit rejection/error to source mod
```

### Mechanic definition API

Represent mechanics as data plus named hooks, not raw cross-boundary functions:

```lua
coreai.mechanics.define({
  id = "freeze_area",
  version = 1,
  events = { "player_action" },
  config = {
    radius = 4,
    tile = "ice"
  }
})

coreai.on("player_action", function(e)
  if e.action == "freeze_area" then
    coreai.command("world.batch_set_tiles", {
      center = e.position,
      radius = storage.freeze_area.radius,
      tile = storage.freeze_area.tile
    })
  end
end)
```

The definition is replicated as data. The actual Lua handler is host-only for authority. Clients can receive:

```json
{
  "mechanic": "freeze_area",
  "version": 1,
  "presentation": {
    "icon": "snowflake",
    "cooldown": 2.0
  }
}
```

### Why route through commands

Command routing gives CoreAI:

- permission checks;
- deterministic ordering;
- host-authoritative multiplayer;
- replay/debug logs;
- rollback/rejection;
- snapshot/delta generation;
- AI-readable introspection;
- hot-reload generation boundaries;
- data-only serialization.

It also keeps Unity object mutation behind C# systems that can validate scene existence, prefab IDs, component capabilities, physics constraints, and platform limitations.

## Implementation Checklist for CoreAI

### Serialization

- Implement AceSerializer-like plain-data codec in C#.
- Reject cycles, functions, threads, userdata, live refs, metatables.
- Add deterministic key ordering for MP.
- Add max depth/bytes/entries.
- Use invariant number formatting.

### Transport

- Implement AceComm-like chunking with `MessageId` and `Sequence`.
- Add reassembly timeouts and byte caps.
- Add priority token-bucket send queue.
- Keep urgent commands separate from bulk snapshots.

### Event Bus

- Implement CallbackHandler-like registry.
- Snapshot or tombstone during dispatch.
- Catch/log handler errors and continue.
- Store owner mod id and generation on every handler.
- Unregister all owned handlers on unload/reload.

### Server Authority

- Host owns all gameplay-affecting commands.
- Clients send requests.
- Host Lua reacts and queues commands.
- Host applies, logs, replicates deltas.
- Snapshots are periodic recovery/join data.

### Persistence / Migration

- Per-mod `storage` is plain data.
- `on_reload` and `on_configuration_changed` migrate storage.
- Migration succeeds before runtime swap.
- Runtime registrations are rebuilt every generation.

### Hot Reload

- Compile new chunk in fresh environment.
- Attach copied storage.
- Stage registrations.
- Run migration/reload hook.
- Validate storage.
- Atomically unregister old generation and publish new generation.
- Cancel old timers/coroutines/async work.
- Keep old generation alive if reload fails.

## Minimal CoreAI API Proposal

```lua
-- Events
local id = coreai.on("player_action", function(e) ... end)
coreai.off(id)
coreai.emit("mod_event_name", { value = 1 }) -- host-mediated, data-only

-- Commands
coreai.command("world.spawn", { prefab = "crate", position = {x=0,y=0,z=0} })
coreai.command("mechanic.set_param", { id = "dash", key = "speed", value = 12 })

-- Timers
coreai.after(0.5, "timer_name", { entity = "entity:42" })
function on_timer(name, payload) ... end

-- Persistence
storage.schema_version = storage.schema_version or 1
function on_reload(ctx) ... end
function on_configuration_changed(ctx) ... end

-- Inter-mod remote
mods_register("dash_api", {
  get_config = function() return storage.config end,
  set_speed = function(args)
    coreai.command("mechanic.set_param", { id = "dash", key = "speed", value = args.speed })
  end
})

local cfg = mods_call("dash_api", "get_config", {})
```

The host/C# implementation owns the registries, validates all plain data, applies all gameplay changes, and serializes only canonical data across the future multiplayer boundary.
