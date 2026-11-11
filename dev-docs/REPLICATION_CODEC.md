# Replication wire codec v1, spike S2 and the per-tick publisher (2026-11-11)

Status: engine-free core of rung MVP6 ("World-state replication + write authority",
`Docs/CoreAIMods/ROBLOX_API_ROADMAP.md`), built on the phase-0 core described in
`REPLICATION_PHASE0.md`. Portable-tested, **not wired**: no composition root builds a publisher or a
receiver, no bridge carries the bytes, and part layers do not report or apply part members yet. The
wiring list at the end is what makes it a feature.

All code is in `Assets/CoreAIMods/Runtime/RbxApi/Instances/Replication/` (the `noEngineReferences`
assembly `CoreAI.RbxApi.Instances`):

| File | What it holds |
|---|---|
| `ReplicationPartState.cs` | `ReplicationPartMembers` (`CFrame`, `Color`, `Transparency`, `Anchored`), `ReplicationPartState`, the server-side `IReplicationPartReader` and replica-side `IReplicationPartSink` seams |
| `ReplicationWireFormat.cs` | constants, record kinds, `ReplicationWireError`, `ReplicationWireException`, the CFrame quantizer |
| `ReplicationWireBuffers.cs` | the reusable writer and the bounds-checked `ref struct` reader |
| `ReplicationWireEncoder.cs` | `IReplicationWireSource`, `RegistryReplicationWireSource`, `ReplicationWireEncoder` |
| `ReplicationWireDecoder.cs` | `ReplicationWireDecoder`, `ReplicationWireBatch` (an `IReplicationStateSource`), `ReplicationWirePartEntry` |
| `ReplicationPublisher.cs` | `ReplicationPublisher`, its options, budget, packet and recipient-info types, the internal backlog |
| `ReplicationWireReceiver.cs` | `ReplicationWireReceiver`: decode, arm the resync for a world packet, apply, hand part members to the sink |

One existing file changed: `ReplicationStream.cs` gained an `internal Plan(IReadOnlyList<ReplicationDelta>)`
overload (the public `Plan()` now calls it with the dirty set's view). It is the seam per-recipient
budgets need: a recipient the budget held back is planned from its own merged backlog plus the
current step, while the dirty set stays shared and is cleared every tick.

## Spike S2: bytes per CFrame patch

Same 200 CFrame patches (positions within 512 studs, random rotations, ids and revisions in the
low thousands) through both codecs. The JSON leg is a patch the way a script replicates a move today,
`FireAllClients(part, revision, cframe)`, through `Scripting/LuaCs/LuaCsRbxNetworkCodec.cs`. Times
are .NET 8 on the dev machine with the JIT fully optimizing (`DOTNET_TieredCompilation=0`; with
tiering on, the first few thousand calls run unoptimized code and the binary encode reads about
1.5–2.5 µs), shared with other builds, so read them as ranges. Bytes do not depend on the machine.

| Leg | One patch alone | Per patch, 200 per packet | Encode / patch | Decode / patch | Alloc encode / patch | Alloc decode / patch | Error |
|---|---|---|---|---|---|---|---|
| JSON (`LuaCsRbxNetworkCodec`) | 191 B | 196.7 B | 6.5–13.4 µs | 8.9–17.9 µs | 4,924 B | 5,512 B | exact |
| Binary v1 (`ReplicationWireEncoder`) | 25–27 B | 18.9 B | 0.23–0.43 µs | 0.21–0.32 µs | 0 B | 147 B | position ≤ 1/2048 stud, rotation ≤ 1.6e-4 rad |

In the DoD's own shape — a server moving 200 parts every tick at 20 Hz for 100 ticks, through the
publisher, bytes counted as handed to the send function, headers included — the binary average is
**18.88 B per CFrame patch** for free motion and **13.57 B** for blocks stepping on a 4-stud grid with
quarter turns (exact, no rotation quantization). The draft bar of 24 B holds with room; S2 sets no
other bar. The single-patch sizes are 25 B (id 18, revision 1500) and 27 B (id 1234, revision 4321,
sequence 5000); a lone patch pays the 5–6 byte packet header and absolute rather than delta-coded
id and revision.

Measured error over 20,000 random CFrames: position 0.000488 stud (the bound, reached), rotation
1.25e-4 rad (0.0072 degrees) against the stated bound of 1.6e-4 rad.

Fixtures: `ReplicationWireS2BinaryEditModeTests` (engine-free; asserts the 24 B bar, the grid case,
the single-patch size and 0 B encode allocation) and `ReplicationWireS2JsonEditModeTests` (Lua tier;
prints the table above and asserts the binary bar and that JSON is at least four times larger).

## Wire format, version 1

Little-endian. A *varint* is unsigned LEB128, at most ten bytes, minimally encoded (an overlong form
is refused). A *zigzag* value is a signed integer mapped to a varint. A *string* is a varint
reference: `0` nil, `1` a new string inline (varint byte length, then strict UTF-8), `n >= 2` the
`(n - 2)`th string this packet already carried — class names, member names and repeated values are
written once per packet.

**Packet**: `0xCA` (magic, refuses a JSON remote payload or stray bytes) · `0x01` (version; another
version is refused with `UnsupportedVersion`) · flags byte (bit 0 = world packet: the answer to a join
or a resync; other bits reserved, must be 0) · varint sequence (>= 1, the plan's) · varint record
count (must fit the bytes that remain at two bytes per record) · records · no trailing bytes.

**Record head byte**: bits 0–3 the kind — `1` Spawn, `2` Patch, `3` Remove; `0` and `4`–`15` are
reserved, `4` held for the voxel-terrain chunk delta, and a v1 decoder refuses them (it cannot know a
reserved record's length or what skipping it would leave behind); bit 4 part block follows; bit 5
member list follows; bits 6–7 reserved. A Remove carries no flags.

- Remove: head · zigzag id delta (from the previous record's id; the first record's from 0).
- Spawn and Patch: head · zigzag id delta · zigzag revision delta (from the previous Spawn or Patch
  revision; the first from 0) · Spawn only: varint parent id (0 = nil) and class name string ·
  optional part block · optional member list.

Delta-coding ids and revisions is what keeps a moving part at one byte each: a plan lists patches
parent-first in dirty order, which for a loop over parts is creation order, and parts moved together
carry near-equal revisions. Ids must decode to a server-assigned id (>= 1, authority bit clear).

**Part block**: varint mask, then the fields in bit order.

| Bit | Meaning |
|---|---|
| 0 | CFrame |
| 1, 2 | rotation mode, with bit 0: `00` identity, `01` (bit 1) quantized, `10` (bit 2) raw, `11` axis-aligned |
| 3 | Color: three bytes |
| 4 | Transparency: float32, exact |
| 5, 6 | Anchored present, Anchored value (bit 6 without bit 5 is refused) |
| 7+ | reserved; refused |

- Position: three zigzag counts of 1/1024 stud. Error <= 1/2048 stud per axis below 8,192 studs;
  from 8,192 to 2,097,151 studs a float32 coordinate is already on the grid and travels exactly;
  beyond that, or non-finite, the CFrame travels raw. Grid positions (integers, halves, any multiple of
  1/1024) are exact everywhere.
- Quantized rotation: 6 bytes, bits 0–1 the index (x, y, z, w) of the largest quaternion component,
  made non-negative and dropped; then three 15-bit levels `0..32766` for the other components in
  x, y, z, w order over [-1/sqrt 2, 1/sqrt 2]; bit 47 zero. The level count is odd so a zero component
  is exact (a part spun about Y never wobbles about X or Z). Error <= 1.6e-4 rad
  (`ReplicationWireFormat.RotationErrorBoundRadians`).
- Axis-aligned: one byte, bits 0–2 the axis of the right vector, bits 3–5 of the up vector
  (0 +X, 1 −X, 2 +Y, 3 −Y, 4 +Z, 5 −Z), bits 6–7 zero; exact. With the identity, the 24 rotations of
  a block world never touch the quantizer.
- Raw: twelve float32 in `GetComponents` order; exact. Used for reflections, skewed or scaled
  matrices (outside `OrthonormalTolerance` 1e-3), non-finite values and far positions.
- Color: eight bits per channel after clamping to [0, 1], error <= 1/510. This is Roblox's own
  replicated precision: Full-API-Dump tags `BasePart.Color` NotReplicated and replicates the hidden
  `BasePart.Color3uint8`, so a replica reads back what a Roblox client reads.

**Member list**: varint count (>= 1), then entries of a tag byte and a payload, in the plan's member
order, so the decoded operation names the same members in the same order.

| Tag | Member | Payload |
|---|---|---|
| `0x01` | `Name` | string (not nil) |
| `0x02` | `Archivable` | byte 0/1 |
| `0x03` | `Parent` | varint id |
| `0x04` | `Attribute:<n>` set | name, kind byte (`AttributeValueKind`), value: String string, Number float64, Bool byte, Vector3 and Color3 3 x float32, Vector2 2 x float32, UDim float32 + zigzag int32 |
| `0x05` | `Attribute:<n>` cleared | name |
| `0x06` / `0x07` | `Tag:<t>` added / removed | name |
| `0x08` | `Value` | kind byte: 0 none, 1 Int zigzag, 2 Number float64, 3 String, 4 Bool, 5 Object id, 6 Vector3, 7 CFrame (12 x float32), 8 Color3 |
| `0x09` | `PrimaryPart` | byte present, varint id |
| `0x0A` | `WorldPivot` | byte: 0 no Model state, 1 no stored pivot, 2 then 12 x float32 |
| `0x0B` | `Character` | byte present, varint id |
| `0x0C` | `DisplayName` | string |
| `0x0D` | any other member | its name only, no value (the replica's applier reports it as a member it does not apply) |
| `0x20` | Spawn only: Player identity | actor id string, zigzag UserId |
| `0x21`–`0x23` | Spawn only: ClickDetector, MaterialVariant, Humanoid state | the snapshot's strings (and the int and bool fields) verbatim |

Datatype floats travel as float32 parsed from the captured snapshot and are rendered back with the
tree serializer's round-trip format, so the replica rebuilds the very string the server captured. An
unknown tag is refused. Owner, origin and access fields of a captured node are never written: the
wire half of "strip owner/ACL metadata at capture for a client".

Golden packet (pinned by `ReplicationWireCodecEditModeTests.Golden_ASpawnAPatchAndARemove_MatchTheFormatNote`):
a Spawn of Part 10 under 2 at revision 3 named `Block` with attribute `Hp = 10`, tag `Ore` and a full
part state, a Patch of 11 renaming it `Block` again (a string reference), and a Remove of 4.

```
CA 01 00 07 03                                   magic, v1, flags, sequence 7, 3 records
31 14 06 02 01 04 50 61 72 74                    Spawn+part+members, id +10, rev +3, parent 2, "Part"
79 80 40 80 08 00 FF 80 00 00 00 80 3E           mask, x 4, y 0.5, z 0, color FF 80 00, transparency 0.25
04 01 01 05 42 6C 6F 63 6B 02 01                 4 entries: Name "Block", Archivable true
04 01 02 48 70 01 00 00 00 00 00 00 24 40        Attribute "Hp" Number 10
06 01 03 4F 72 65                                Tag "Ore"
22 02 00 01 01 03                                Patch+members, id +1, rev +0, Name = string #1
03 0D                                            Remove, id -7
```

### Decoding guarantees

- A packet over the decoder's cap (default `DefaultMaxPacketBytes` = 65,536, the remote codec's
  `MaxPayloadBytes`) is refused before a field is read; measured allocation is the error message only.
- Nothing is sized by a count the remaining bytes cannot back (records >= 2 bytes, entries >= 2
  bytes, strings by their byte length), and the first list allocation is capped at 256 entries, so a
  packet claiming two billion records costs the refusal and nothing else.
- Lists then grow only as records parse: the worst measured case, a 40,007-byte packet of 20,000
  records refused at its last one, allocated 2.0 MB (about 50 bytes per packet byte; bounded by the
  64 KB cap at roughly 3.3 MB).
- Nothing throws: every read is bounds-checked; a fault inside the decoder itself would be refused as
  `MalformedValue` with the exception text, and the fuzz fixtures assert it never happens.

### Allocation per patch

- Encode, a patch naming only part members: **0 B** once the reused buffer has grown — no node
  capture, no strings (asserted over 2,000 patches). Members of the engine-free core capture the node
  (`InstanceTreeSerializer.CaptureNode`) and parse its strings, which allocates; they are not the
  per-tick hot path.
- Decode, a CFrame patch: **about 147 B** — one `ReplicationOperation`, its list slots and one
  `ReplicationWirePartEntry` (asserted <= 160 B). A part-only patch is described by one reused node per
  batch (the `HACK:` in `ReplicationWireBatch.Describe`: the applier reads only its id). Engine-free
  members allocate a snapshot and their strings.

## The publisher

`ReplicationPublisher(dirty, clock, send, source, options)`: engine-free, single-threaded, no
threads. `Update()` once a frame runs at most one tick when the injected clock (monotonic seconds)
reaches it (default 20 Hz); a stall is caught up by one coalesced tick, not a burst. Each tick plans
every recipient from the shared dirty set, encodes, hands the bytes to the injected
`ReplicationPacketSend` (a reused buffer, valid during the call) and clears the dirty set.

- Coalescing: one record per instance per tick with values read at the tick, so the last write of a
  member wins; an instance spawned and removed between two ticks is named by no record (its parent
  may still get a revision-only patch, as the phase-0 planner sends today); a removal wins over every
  change.
- Ordering, per recipient: consecutive sequences handed to send in order; the first packet is the
  world (`AddRecipient` and `RequestWorld` both schedule one); records in plan order;
  budget-deferred changes go out merged into the next packet, never after a later one;
  `EnqueueAfterState(actor, action)` runs right after the first packet planned at or after the call,
  so a remote event fired after a write reaches the client after that write.
- Budgets: a token bucket per recipient (`ReplicationRecipientBudget(bytesPerSecond, burst)`,
  unlimited by default). An empty bucket skips the recipient for the tick and merges its changes into
  a backlog by the dirty set's rules; a world packet is always sent when due and paid for afterwards.
- Faults: a batch that cannot be encoded (larger than `MaxPacketBytes`, a local id, a spawn with no
  state) or a send function that throws faults that recipient only, reported once through
  `Options.Diagnostics`; nothing partial is sent, and the recipient gets nothing until
  `RequestWorld`, which is also the answer to its replica's `ResyncRequested`.

`ReplicationWireReceiver(replica, localActorId, partSink)` is the client end: decode (a refused packet
is counted and reported at powers of two; the next good packet is then a gap the applier asks a
resync for), `BeginResync` for a world packet, `ReplicationApplier.Apply`, then the part members to the
sink inside the replica's apply scope.

## Tests

EditMode fixtures under `Assets/CoreAIMods/Tests/EditMode/RbxApi/Replication/`, run without Unity by
two new portable projects:

- `tools/portable/ReplicationWire/CoreAI.Portable.ReplicationWire.Tests.csproj` (engine-free, 55
  tests): `ReplicationWireCodecEditModeTests` (goldens, every member kind, every value class,
  quantization bounds, the 24 exact axis rotations, raw fallbacks, encode refusals, zero-allocation
  encode), `ReplicationWireMalformedInputEditModeTests` (empty, oversized, foreign, every truncation,
  every single-byte corruption, random bytes, huge counts, 32 malformed fields and their well-formed
  twins, a refused packet becoming a gap), `ReplicationPublisherEditModeTests` (schedule, coalescing
  with negative twins, ordering, budgets, after-state dispatch, a late joiner converging from bytes
  alone through the existing applier, players and an owner-only Backpack over the wire, a gap answered
  by `RequestWorld` in place, oversize and send faults), `ReplicationWireS2BinaryEditModeTests`.
- `tools/portable/ReplicationWireS2/CoreAI.Portable.ReplicationWireS2.Tests.csproj` (Lua tier, 1
  test): `ReplicationWireS2JsonEditModeTests`.

Shared helpers: `ReplicationWireTestKit.cs` (`WireWorld`, `WireClient`, `PartStore`, `WireAssert`).

## Wiring (root, later)

1. **Bridge seam** (`Networking/INetworkBridge.cs`): server-to-client replication packets
   (`SendReplicationPacket(recipient, ArraySegment<byte>)` / `ReplicationPacketReceived`) on the
   ReliableOrdered channel, and a client-to-server world request (or the planned `SendIntent`) that
   ends in `publisher.RequestWorld(actor)`. The transport copies the segment; the receiver's decoder
   cap must equal the publisher's `MaxPacketBytes`.
2. **Mirror** (`Assets/CoreAIMirror`): one `NetworkMessage` carrying the bytes on the reliable
   channel each way; carry `ReplicationWireFormat.Version` in the admission handshake (risk R9) so a
   mismatch is refused once at join rather than per packet.
3. **Server composition** (`CoreAiModsInstaller` or the host preset): a `ReplicationDirtySet` over
   the world registry, a `ReplicationPublisher` with `clock = IRbxClockSource.ProcessTimeSeconds`,
   `send` = the bridge, `source = new RegistryReplicationWireSource(registry, partReader)`;
   `Update()` once per frame after the frame's script writes (post-Heartbeat); `AddRecipient` on
   admission, `RemoveRecipient` on disconnect.
4. **Client composition**: the client's registry as `RegistryAuthority.Replica` with a
   `ReplicationWireReceiver(replica, localActorId, partSink)` fed by the bridge;
   `ResyncRequested` goes to the server as the world request.
5. **Part layer** (`RbxApi/Binding`, `Scripting/LuaCs`): an `IReplicationPartReader` over the
   part-property seam on the server and an `IReplicationPartSink` that sets BasePart properties on
   the replica (so `Changed` fires there); `LuaCsRbxInstanceBindings.RecordMutation` and
   `LuaCsTweenPropertyHost` must report `registry.AdvanceRevision(id, ReplicationPartMembers.X)`
   instead of a whole-node mark — the existing "member-level marks for BasePart" item. Until then a
   part change reaches a replica only with a spawn or a world.
6. **Remotes** (`LuaCsRbxApiBindings.cs`, `FireClient`/`FireAllClients`): dispatch through
   `publisher.EnqueueAfterState(recipient, ...)` (one per recipient for `FireAllClients`) so an event
   never overtakes the state it refers to — what retires the synchronous fan-out of "MVP6/MVP9
   scalability debt". Remote latency becomes "next tick" (<= 50 ms at 20 Hz).
7. **Member vocabulary**: move `ReplicationPartMembers` into `ReplicationMembers` with the
   member-level marks, enumerate them for BaseParts in `EnumerateReplicable`, and give each a verdict
   in `GuardedReplicationFilterEditModeTests`' member transcription (`Color` replicates as
   `Color3uint8`).
8. **CI**: add the two portable projects to the `portable-core` job (the Lua-tier one beside
   `LuaTests`), or link the fixtures into `tools/portable/Tests` and `tools/portable/LuaTests`; they need
   no `RepositoryTestSetup`.
9. **Docs**: index this note in `dev-docs/README.md`; `REPLICATION_PHASE0.md` "What is not here"
   should point here for the codec.

## Not done

- No fragmentation: a batch larger than `MaxPacketBytes` faults its recipient. A big world's join
  snapshot needs either a larger cap on the reliable channel or chunking, sized by spike S3.
- No interpolation timestamps in the header (MVP7 needs a server tick per packet; a header flag is
  free for it).
- Spawn part state is not member-filtered: a spawn carries everything the part reader reports.
- Revision deltas restart per packet; a part alone in its packet pays its full revision (2–3 bytes).
