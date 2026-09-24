# MVP8 acceptance manifest

Gate P8.5 in `dev-docs/MVP25_BUILD_PLAN_2026-09-04.md` §E.1 cites "the exact ids listed in the MVP8
manifest". This is that manifest. It exists so the frozen corpus is a written commitment rather than
a number that moves with whatever the test source happens to say today; the closure audit of
2026-09-06 recorded its absence as a finding.

Frozen 2026-09-06 against `main`. Every id below must exist on disk, be discovered by the catalog,
and execute to its recorded classification — the directory count and the catalog count are
cross-checked by `FrozenTierBCatalog_MatchesItsFilesAndIds` and
`FrozenCatalog_HasTwentyUniqueFixturesAndCompleteClassificationMetadata`, and the id list in
this file is parsed back out of the markdown and compared to the catalog by
`FrozenManifest_ListsExactlyTheCatalogIds`, so adding a file without adding it here — or
editing either side without the other — fails the run.

## Tier A — 20 fixtures (MVP1/MVP2 surface)

| id | id | id | id |
|---|---|---|---|
| `TAC-001-instance-parent-last` | `TAC-006-signal-wait` | `TAC-011-cframe-math` | `TAC-016-generic-for-descendants` |
| `TAC-002-part-properties` | `TAC-007-task-scheduling` | `TAC-012-color3-math` | `TAC-017-waitforchild-yield` |
| `TAC-003-attributes-change-signal` | `TAC-008-runservice-heartbeat-loop` | `TAC-013-getservice-identity` | `TAC-018-contextaction-bind` |
| `TAC-004-signal-connect-disconnect` | `TAC-009-userinput-began` | `TAC-014-destroy-pcall-cleanup` | `TAC-019-tween-create` |
| `TAC-005-signal-once` | `TAC-010-vector3-math` | `TAC-015-script-parent-property-signal` | `TAC-020-players-localplayer` |

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

## Gameplay-service audit (2026-09-24)

The 2026-09-24 audit of the MVP8 services found defects under several gates of
`dev-docs/MVP25_BUILD_PLAN_2026-09-04.md`; the fixes are unreleased and verified by EditMode tests that
still have to be run in Unity. The corpus above is unchanged.

- **Gate P8.4 (`Debris:AddItem`).** Capability: a mod without `WorldEdit` is refused
  (`AddItem_ReadOnlyMod_IsRefusedForMissingWorldEdit`). Subtree: the caller must be allowed to destroy
  the whole subtree, checked at the call and again when the item fires
  (`AddItem_CrossOwnerDescendant_RefusedAtCallTime_SubtreeUntouched`,
  `AddItem_DescendantReownedAfterScheduling_FireDroppedWithOneLogLine`, positive twin
  `AddItem_OwnSubtree_DestroysEveryDescendant`). Singletons: services, the DataModel and
  `workspace.CurrentCamera` are refused in every world, ACL-versioned or not, and a `Player` is refused
  with a pointer to `Player:Kick()` (`AddItem_NonAclWorld_ProtectedSingletonsAreRefused`,
  `AddItem_Player_IsRefusedWithAKickHint`). Memory: re-adding an item replaces its lifetime in place and
  keeps its eviction order, and the queues and scheduler callbacks stay bounded under churn
  (`AddItem_ReAddedItem_KeepsItsOriginalInsertionOrderForEviction`,
  `AddItem_ChurnAndReAdds_KeepInternalQueuesAndSchedulerCallbacksBounded`).
- **TweenService.** Stepping is O(1) per tween and frame, non-finite goals are refused at `Create`, a
  faulting tween is cancelled alone, each tween is owned by and charged to the creating actor with at
  most 256 finished tweens kept per actor, and Create/Play/Pause/Cancel are authorized against the
  calling actor (`Step_TinyDurationForeverRepeat_WritesOncePerFrame_AndCarriesTheRemainder`,
  `CreatePlayComplete_TenThousandCycles_KeepsAtMost256IdleTweensPerActor`,
  `Replay_OfAReleasedTween_RaisesInstanceDestroyed`, `Play_WithACaller_AuthorizesTheCallerNotTheCreator`).
- **Humanoid.** `TakeDamage`/`MoveTo`/`ChangeState` need `WorldEdit` and write authority
  (`HumanoidMethods_CrossActor_AreRefusedByTheWorldAcl`); `MoveTo` arrival is measured on the ground
  plane (`MoveTo_ArrivalIsMeasuredOnTheGroundPlane_ATargetBelowTheRootIsReached`); `Died` fires only
  inside the Workspace (`HealthZero_OutsideTheWorkspace_DiesOnceOnTheFirstHeartbeatInside`). Later in
  the same waves: a script, `PivotTo` or tween move of the `HumanoidRootPart` ends `MoveTo` with `false`
  (`MoveTo_EndsFalse_WhenAScriptMovesTheRootPart_ByCFramePositionOrPivotTo`,
  `MoveTo_EndsFalse_WhenATweenMovesTheRootPart_ByCFramePositionOrOrientation`); `Humanoid:Clone` keeps
  its state and a dead clone dies on its first Heartbeat inside
  (`Clone_KeepsTheTemplatesHealthMovementParametersAndDisplayName`,
  `Clone_OfADeadHumanoid_KeepsHealthZero_AndDiesOnItsFirstHeartbeatInTheWorkspace`); a character is not
  archivable (`Character_IsNotArchivable_SoCloneIsNil_UntilAScriptSetsArchivableTrue`).
- **ClickDetector (M8-08, M8-11).** `MouseClick` passes the player who clicked, a detector under a
  `Model` or `Folder` answers and the deepest one wins, the range is measured from the character with a
  camera fallback, and a detector parked under `Workspace` claims nothing
  (`Lua_ClickDetector_MouseClick_PassesThePlayerWhoClicked_FromAModelLevelDetector`,
  `Lua_ClickDetector_FolderLevelDetectorFires_AndTheDeepestDetectorWins`,
  `Lua_ClickDetector_Distance_IsMeasuredFromTheCharacter_NotTheCamera`,
  `Lua_ClickDetector_WithoutACharacter_FallsBackToTheCameraDistance`,
  `Negative_Lua_ClickDetector_ParentedToWorkspace_DoesNotClaimEveryClick`); destroying a detector
  disconnects its handlers (`ClickDetector_Destroy_DisconnectsItsMouseClickHandlers`).
- **CollectionService (M8-10).** `TagAdded`/`TagRemoved`/`GetAllTags` count only holders inside the
  DataModel: a nil-parented holder moves neither, the last holder leaving the tree fires `TagRemoved`
  and a returning one `TagAdded` again, and tags applied before the bindings attach count too
  (`TagGlobals_FollowTheLastHolderOutOfTheDataModelAndBackIn`,
  `Negative_OutOfTreeHolders_MoveNeitherTagGlobalNorGetAllTags`,
  `Negative_TagRemovedGlobal_WaitsForTheLastHolderInTheDataModel`,
  `TagsAppliedBeforeTheBindingsAttach_CountOnlyTheirInTreeHolders`).
- **Tween lifetime (M8-22).** A disposed world detaches its `TweenService`, and killing a mod's scheduled
  work destroys the tweens it created
  (`M8_22_Dispose_DetachesTweenService_SoTheOldSchedulerStepsNoTween`,
  `KillAllScheduledOwnedBy_AlsoDestroysTheTweensTheModCreated`).
- **Players.** `Player:Kick(message)` hands its text to the transport, cut to the 1,024-byte wire
  ceiling; a number is sent as its `tostring` text, as in Roblox (since RBX-COERCE, `c0f6fdbc`), and any
  other non-string is refused before anything is kicked
  (`Kick_HandsTheScriptsMessageToTheTransport_AndNoMessageLeavesTheTransportsDefault`,
  `Kick_ALongMessage_ReachesTheTransportCutToTheWireCeiling`,
  `Negative_Kick_WithAMessageThatIsNotAString_IsRefusedBeforeAnythingIsKicked`,
  `Kick_WithANumberMessage_KicksWithTheTextTostringGivesIt`).
- **Physics.** `CanCollide = false` lets bodies through but keeps `Touched` and raycast hits
  (`CanCollide_False_KeepsTheColliderEnabledAsATrigger`,
  `CanCollideFalsePart_IsHitByADefaultRay_AndSkippedWhenTheRayRespectsCanCollide`,
  `CanCollideFalsePart_TriggerOverlap_IsReportedAsAContactThenItsEnd`), only Workspace content is
  physical (`D5_OnlyWorkspaceIsActive_AmongTheDataModelsChildren`), and cylinders are hit and touched
  (`PartShapeMaterializationEditModeTests`, `Mvp8PhysicsPlayModeTests`).
