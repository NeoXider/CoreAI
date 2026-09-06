# MVP8 acceptance manifest

Gate P8.5 in `dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` §E.1 cites "the exact ids listed in the MVP8
manifest". This is that manifest. It exists so the frozen corpus is a written commitment rather than
a number that moves with whatever the test source happens to say today; the closure audit of
2026-09-06 recorded its absence as a finding.

Frozen 2026-09-06 against `main`. Every id below must exist on disk, be discovered by the catalog,
and execute to its recorded classification — the directory count and the catalog count are
cross-checked by `FrozenTierBCatalog_MatchesItsFilesAndIds` and
`FrozenCatalog_HasTwentyUniqueFixturesAndCompleteClassificationMetadata`, so adding a file without
adding it here fails the run.

## Tier A — 20 fixtures (MVP1/MVP2 surface)

| id | id | id | id |
|---|---|---|---|
| `TAC-001-instance-parent-last` | `TAC-006-signal-wait` | `TAC-011-cframe-math` | `TAC-016-generic-for-descendants` |
| `TAC-002-part-properties` | `TAC-007-task-scheduling` | `TAC-012-color` | `TAC-017-waitforchild-yield` |
| `TAC-003-attributes-change-signal` | `TAC-008-runservice-heartbeat-loop` | `TAC-013-getservice-identity` | `TAC-018-contextaction-bind` |
| `TAC-004-signal-connect-disconnect` | `TAC-009-userinput-began` | `TAC-014-destroy-pcall-cleanup` | `TAC-019-tween-create` |
| `TAC-005-signal-once` | `TAC-010-vector` | `TAC-015-script-parent-property-signal` | `TAC-020-players-localplayer` |

Tier-A threshold: **≥ 30 %** unmodified (`TierACorpusEditModeTests.MinimumUnmodifiedPercent`).
Recorded result: **18 / 20 = 90 %** unmodified.

## Tier B — 10 fixtures (MVP8 gameplay idioms)

| id | what it exercises |
|---|---|
| `TBC-001-kill-brick` | `Touched` from real contact, `Humanoid:TakeDamage` |
| `TBC-002-touch-pickup-with-leaderstats` | `Touched` + `leaderstats` Value objects + `Changed` |
| `TBC-003-door-tween` | `TweenService:Create` over two properties, `Completed` |
| `TBC-004-raycast-ground-check` | `workspace:Raycast` with `RaycastParams` |
| `TBC-005-humanoid-damage-loop` | `Humanoid.HealthChanged`, `Died` |
| `TBC-006-collection-service-respawner` | `CollectionService` tags and the added/removed signals |
| `TBC-007-player-leave-save` | `Players.PlayerRemoving` reading player state during teardown |
| `TBC-008-tween-cancel-restart` | `Tween:Cancel` → `Completed(Cancelled)`, then replay |
| `TBC-009-attribute-driven-config` | attributes as configuration, `GetAttributeChangedSignal` |
| `TBC-010-gravity-low-jump` | `workspace.Gravity` and jump behaviour on scaled physics |

Three of these are named in gate P8.5 itself and must pass **unmodified**: `TBC-001-kill-brick`,
`TBC-002-touch-pickup-with-leaderstats`, `TBC-003-door-tween`.

Combined threshold: **≥ 60 %** of Tier A + Tier B unmodified
(`CombinedCorpus_MeetsTheMvp8UnmodifiedThreshold`). Recorded result: **28 / 30 = 93 %**.

## What "unmodified" means here

A fixture counts as unmodified only when it runs the fixture file byte-for-byte as authored, reaches
its exact recorded completion marker, and raises **zero** loud stubs. The zero-stub half was
unenforced until 2026-09-06 (the harness scraped logger text for `NOT_IMPLEMENTED`, and nothing in
the runtime logs on raise, so a `pcall`-wrapped stub passed clean). It is now counted at the raise
itself, and `Negative_PcallWrappedStubHit_CountsAsFailing` proves a fixture that hides a stub behind
`pcall` is classified as failing.

`TAC-014-destroy-pcall-cleanup` uses `pcall` legitimately — it is a destroy-semantics fixture, not a
stub-hiding one — which is why the guard counts stub raises specifically rather than penalising
`pcall`.

## Corrupted twins

`Negative_CorruptedTierBFixtures_Fail` runs deliberately broken copies of three fixtures and
requires the expected diagnostic text (`Vaporize`, `IntValue`, `CanCollide`). A corpus that passes
its twins is not measuring anything.
