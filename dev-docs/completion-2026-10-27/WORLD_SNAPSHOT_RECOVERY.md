# World snapshot recovery evidence - 2026-10-27

## Result

No independently attributable, complete pre-overwrite copy of the user's WorldStateManager world_state.json was found in the inspected locations. No exact original bytes or pre-overwrite SHA-256 are available from this audit. Recovery remains unverified; this finding does not establish that every possible backup or shadow copy is absent.

Ten earlier .world packages exist in CoreAI/Saves/Auto. Their filesystem and package-manifest timestamps predate the incident, but they use the separate coreai-rbx-world format. None contains world_state.json. They do not establish a restorable copy of the overwritten WorldStateManager JSON, and no conversion or restoration was attempted.

## Scope and safeguards

- Investigation was read-only except for this report. No user file was restored, replaced, renamed, deleted, or rewritten.
- No Unity/MCP operation, Git command, service change, elevation, backup activation, or raw disk recovery was performed.
- File contents, conversations, mod sources, and secrets were not emitted. Package inspection emitted format and timestamp metadata only.
- Evidence was inspected on 2026-10-27 at approximately 06:10-06:18 UTC (11:10-11:18 Asia/Yekaterinburg).
- The actual repository is D:/Git/CoreAI; the supplied C:/Git/CoreAI path is absent.

## Incident file and retained post-overwrite copy

| Artifact | Size | Created UTC | Last write UTC | SHA-256 |
| --- | ---: | --- | --- | --- |
| C:/Users/User/AppData/LocalLow/DefaultCompany/CoreAI/CoreAI/WorldState/world_state.json | 127 bytes | 2026-07-09T08:09:30.9355347Z | 2026-10-27T05:42:38.4831126Z | 5A2C140C57CC00D314810A025F3D32A902B69CE115B0C1E8CF6D9C4E14FF2642 |
| C:/AI/work/coreai-completion-20261027/world_state-after-overwrite.json | 127 bytes | 2026-10-27T05:52:43 UTC | 2026-10-27T05:42:38 UTC | 5A2C140C57CC00D314810A025F3D32A902B69CE115B0C1E8CF6D9C4E14FF2642 |

The live file and retained copy have identical current bytes. Their metadata is consistent with the reported overwrite at 10:42 local time; filesystem metadata alone does not identify the writer or reveal the previous contents. The WorldState directory contains only the live world_state.json; no sibling .tmp, .bak, or other version was found.

## Earlier .world package evidence

Common absolute directory:

    C:/Users/User/AppData/LocalLow/DefaultCompany/CoreAI/CoreAI/Saves/Auto/

All ten files opened successfully as ZIP archives, report manifest format coreai-rbx-world version 1, and contain the expected world.json entry. No archive has an entry named world_state.json, including nested entries. This is a metadata/structure check, not execution of the package reader, a full semantic validation, or verification that a package represents the user's earlier world.

| File in the directory above | Bytes | Filesystem last write UTC | Manifest created UTC | SHA-256 |
| --- | ---: | --- | --- | --- |
| 20261027T050717576Z-0000-manage_mods-load.world | 29238 | 2026-10-27T05:07:17.7107964Z | 2026-10-27T05:07:17.0205905Z | 7CECB67EEDFFB250DA878DD4F34BCF4BD0FF2C7F7B0A4CF707C51FFEA7907426 |
| 20261027T050717798Z-0000-manage_mods-forget.world | 29769 | 2026-10-27T05:07:17.8376745Z | 2026-10-27T05:07:17.7726326Z | 43E8581B74111F66824AC36A2316F1EBFEF2A2E9712E8D3425FD12258C360802 |
| 20261027T050749360Z-0000-load_world-pre.world | 1104 | 2026-10-27T05:07:49.3632456Z | 2026-10-27T05:07:49.3607403Z | 2D1977764CFCFB17F5C6E9CC1F2EEE42C4D1391BB9E1BFE210E36B1DB4C1FBCD |
| 20261027T050749445Z-0000-load_world-pre.world | 1099 | 2026-10-27T05:07:49.4478473Z | 2026-10-27T05:07:49.4452045Z | 2A4C15511105C1D13FB34664A8B9BDD5A195F09A41254E05C0F0C0E6912285F7 |
| 20261027T050836925Z-0000-execute_lua.world | 29285 | 2026-10-27T05:08:36.9330241Z | 2026-10-27T05:08:36.8948519Z | 0017E7FFB1B0DFE6EE749A5B2E06BC999A918632610C17889E614AE170151F4E |
| 20261027T053134655Z-0000-manage_mods-load.world | 29238 | 2026-10-27T05:31:34.7305801Z | 2026-10-27T05:31:34.6076136Z | 1E99E17B68EEECE49242C9191ABEC7C5DEF02810B1484532ADA813C48C4B0CEB |
| 20261027T053134813Z-0000-manage_mods-forget.world | 29770 | 2026-10-27T05:31:34.8177754Z | 2026-10-27T05:31:34.7719336Z | 1E5CEAD3FC728027B9FB5DC8D8A1CD3FDCF0C84F05B979C1C8B47C93E9F9BEC4 |
| 20261027T053156316Z-0000-load_world-pre.world | 1104 | 2026-10-27T05:31:56.3178982Z | 2026-10-27T05:31:56.3163584Z | 5E647522FDD89B7E99869C4EC9E41674FC2C1D9937B525BD0A991B9DC973A835 |
| 20261027T053156359Z-0000-load_world-pre.world | 1099 | 2026-10-27T05:31:56.3617783Z | 2026-10-27T05:31:56.3594195Z | 8E580F126E6869D5F4679EB0838AC6A4203B59BB73FA14C4F810BE22B6B91A80 |
| 20261027T053217381Z-0000-execute_lua.world | 29284 | 2026-10-27T05:32:17.3875948Z | 2026-10-27T05:32:17.3504939Z | AC5F9937D589174AD2C1DC8BB4EEC0B424636E4D21D901B8DF6CA6700C5BA314 |

The Manual save directory has no files. An earlier timestamp is evidence of an earlier package, not independent provenance tying it to the overwritten JSON. Current hashes identify the observed packages; no pre-incident receipt for the original JSON was found.

## Source evidence for the format distinction

- D:/Git/CoreAI/Assets/CoreAiUnity/Runtime/Source/Features/World/WorldStateManager.cs:124-130 defines its JSON SaveData with version, timestamp, scene, and objects.
- The same file:420-429 writes a temporary JSON and uses File.Replace(tmpPath, _saveFilePath, null) when the destination exists. That call requests no named backup. It explains why this write path does not itself provide a recoverable sibling version; it does not prove that another backup system has no copy.
- D:/Git/CoreAI/Assets/CoreAIMods/Runtime/Infrastructure/RbxWorldPackageSerializer.cs:22-27 defines coreai-rbx-world, manifest.json, and world.json.
- The same serializer:678-719 creates a ZIP from its Rbx payload, world DTO, and optional mod entries. It does not embed the WorldStateManager JSON file.

These inspected source paths explain the storage formats. They are not proof of the identity or completeness of the world that any individual earlier package represents.

## Backup and versioned artifact searches

### Windows File History and system image locations

The following paths do not exist in the inspected active Windows profile/local volumes:

- C:/Users/User/AppData/Local/Microsoft/Windows/FileHistory
- C:/Users/User/AppData/Local/Microsoft/Windows/FileHistory/Configuration
- C:/ProgramData/Microsoft/Windows/FileHistory
- C:/ProgramData/Microsoft/Windows/FileHistory/Configuration
- C:/FileHistory
- D:/FileHistory
- E:/FileHistory
- C:/WindowsImageBackup
- D:/WindowsImageBackup
- E:/WindowsImageBackup

The active logical-disk query reported C:, D:, and E: as local fixed NTFS volumes.

HKCU:/Software/Microsoft/Windows/CurrentVersion/FileHistory exists with no direct value names and only a RestoreUI child. HKLM:/SOFTWARE/Microsoft/Windows/CurrentVersion/FileHistory exists with no direct value names and only a Policies child. These registry key names alone do not establish that a backup catalog or a usable backup target exists.

No File History configuration/catalog was available at the checked locations to identify or inspect an older copy. This is a bounded negative result, not proof that no historical, custom, disconnected, or remote File History target exists.

### VSS / Previous Versions

The parent investigator previously reported that vssadmin list shadows required privileges. This worker did not repeat that command or request elevation. A read-only Get-CimInstance Win32_ShadowCopy query failed with a CIM initialization error. Therefore no VSS snapshot inventory or Previous Versions restoration source was verified.

### CoreAI task artifacts and temporary directories

Case-insensitive filename searches for world-state/world-snapshot JSON, .world, archive, and backup candidates covered:

- C:/AI/work/coreai-pr-review-20261025
- C:/AI/work/coreai-completion-20261027
- D:/Git/CoreAI, including relevant artifacts and dev-docs

The searches excluded repository internals, Unity Library/Temp, generated obj/bin, and dependency package folders. The only matching persisted world-state artifact was the explicitly post-overwrite copy already identified above. Source files with WorldState names are code, not saved user snapshots. No matching backup archive or earlier JSON was found in those searches.

Three CoreAI-related immediate temporary directories were searched for relevant world-state, .world, snapshot, .bak, and .backup filenames, with no matches:

- C:/Users/User/AppData/Local/Temp/CoreAI-legacy-repair-04b726dd0c34452ea904163e3e6edc5b
- C:/Users/User/AppData/Local/Temp/CoreAI-legacy-repair-7422dce651d54ea8a1345c5b1cc09b91
- C:/Users/User/AppData/Local/Temp/CoreAI.LiveQA

No immediate CoreAI-named directories were found under C:/AI/outputs, D:/AI/work, or D:/AI/outputs. Those unrelated roots were not recursively searched.

## Limitations and disposition

- Original pre-overwrite bytes, length, timestamp field, and hash were not captured by this worker.
- A prior log's object count, as described by the parent investigator, cannot reconstruct exact serialized bytes or independently prove that no other state was present.
- ZIP readability and a manifest timestamp do not establish full restore acceptance or user-world provenance.
- Shadow copies, disconnected/removable/remote backups, other Windows profiles, arbitrary custom backup paths, and filesystem-level recovery were not exhaustively inspected.
- No exact restorable original snapshot was found, so no restoration proposal was made and no user-file mutation was performed.
- The live file and retained post-overwrite copy were re-hashed after the investigation; both still matched 5A2C140C57CC00D314810A025F3D32A902B69CE115B0C1E8CF6D9C4E14FF2642.

The supported outcome is: recovery not established from the inspected evidence. The earlier Rbx .world packages must not be presented as restoration of the overwritten WorldStateManager JSON.
