# Mod folders

Mods live in folders. A mod's folder is the `category` of its `@coreai` header, read as a `/`-separated path
(`category: Games/Arcade`). A one-level category such as `Samples` is a one-folder path, so mods and world
packages written before folders existed load unchanged. The empty category is the top level.

This folder holds the engine-free model (folder paths, the folder tree, folder operations and their
persistence format) and a UI Toolkit tree view driven only by that model. The Hub Mods page shows mods
through it (`HubModsPage`, `HubModFolderStore`) and `manage_mods` lists, loads into and moves between
folders with it; player and AI behaviour is described in `Docs/CoreAIMods/mod-system.md` (sections 1 and 4).

## Layer map

| Assembly | Folder | Holds | References |
|---|---|---|---|
| `CoreAI.Mods.Folders` (Domain) | `Domain/` | `ModFolderPath`, `ModFolderTree`, `ModFolderFoldState`, `ModFolderService` over `IModFolderStore`, `ModFolderHeaderEditor`, `ModFolderStateCodec` | nothing (`noEngineReferences`, no precompiled references) |
| `CoreAI.Mods.Folders.Unity` | `Unity/` | `ModFolderTreeView`, a virtualized `ListView` over the tree's flat rows | `CoreAI.Mods.Folders`, UnityEngine |

The Domain has no application layer of its own: the mod service in `CoreAI.Mods` implements `IModFolderStore`
over its source store and runtime. The view does not depend on the Hub, so a game can show the same tree in its
own UI.

## Path rules

`ModFolderPath.Parse` reads every path that is written (by a player, a tool or an operation) and fails with a
named `ModFolderErrorCode`:

| Rule | Error |
|---|---|
| A segment holds letters, combining marks and digits of any script, spaces and `_ - . ( ) & + ! ' , #` | `InvalidCharacter` |
| No empty segment between two separators (`Games//Arcade`) | `EmptySegment` |
| A segment is not `.` or `..` | `ReservedName` |
| A segment has at most 64 characters | `SegmentTooLong` |
| A path has at most 8 segments | `TooDeep` |
| A path has at most 255 characters | `PathTooLong` |

Spaces around segments and leading or trailing separators are dropped, runs of spaces become one space, and a
backslash reads as `/`. Folder names compare case-insensitively: `Games` and `games` are one folder, shown in the
casing first seen. `ModFolderPath.FromStored` reads a stored category leniently and never fails, so a legacy
category the rules refuse still shows as a folder and can be renamed to a valid name.

## Tree

`ModFolderTree.Build(mods, persistedFolders, foldState)` builds the tree once. It gives per-branch counts (mods
and mods switched on), a search over name, id, tags and folder path that keeps every matching branch open, the
viewer's fold state, and a flat list of the visible rows (`VisibleRowCount`, `GetVisibleRow`). Folding,
unfolding and searching re-flatten into a preallocated buffer and allocate nothing.

## Operations

`ModFolderService` creates, renames and moves folders, moves a mod, deletes an empty folder and switches a whole
branch on or off (only the branch: `GamesOld` is not inside `Games`). Each operation checks every rule before it
writes and answers a `ModFolderResult` with a named error: `FolderNotFound`, `ModNotFound`, `FolderAlreadyExists`,
`FolderNotEmpty`, `MoveIntoItself`, `RootNotAllowed`, `InvalidName`, `StoreFailed`, or a path rule. Moving a mod
rewrites only the `category:` entry of its header (`ModFolderHeaderEditor`), because every manifest writer
derives the category from the header. An emptied folder is kept; only a delete removes a folder.

## Persistence

Two small text documents, written by `ModFolderStateCodec`, one entry per line:

- the persisted folders of a mod store (created or kept folders, empty ones included): `coreai-mod-folders 1`
  followed by `folder <path>` lines;
- a viewer's fold state: `coreai-mod-folds 1`, `default expanded|collapsed`, then `toggled <path>` lines.

`IModFolderListStore` is where a store keeps its persisted folders; `TextModFolderListStore` keeps them in one
document through the host's read and write callbacks and refuses a document it does not recognise instead of
overwriting it. The fold state belongs to the viewer and never travels in a world package.

## View

`ModFolderTreeView` is a `VisualElement` with a search field, expand and collapse buttons and a fixed-height
`ListView`. Rows show a fold arrow, an on/off toggle (mixed for a partly enabled folder), the name, the counts, a
Move button and the host's own actions. It raises requests (`ModEnableRequested`, `BranchEnableRequested`,
`MoveRequested`, `ModActionRequested`, `FolderActionRequested`, `FoldChanged`) and changes nothing itself: the
host runs the operation and hands the rebuilt tree back with `SetTree`.
