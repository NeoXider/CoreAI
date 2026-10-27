# PR-MODS persistence repair

Repairs PR-MODS-001/002/003 in the Mods persistence path.

## PR-MODS-001

The existing `ILuaModSourceStrictWriter` and `ILuaModSourceStrictRemover` interfaces retain
their exact members. The new optional `ILuaModSourceStrictCapabilities` exposes only
`SupportsStrictSave` and `SupportsStrictDelete`. The public helpers
`LuaModSourceStoreCapabilities.CanSaveStrict` and `CanDeleteStrict` combine the original
interface check with those optional flags. Stores without the optional capability interface
keep the previous interface-based inference.

`RbxWorldRuntimeSessionController.SessionSourceStore` and `ActiveLuaModSourceStore` evaluate
those flags against their current inner store on every call, including after a session
swap. Their strict methods still throw before effects when the inner store is not capable.
`HubModServiceBase` chooses strict save, delete and active-state updates only when the
store both implements the strict interface and reports capability; otherwise it calls the
legacy `Save` / `Delete` / `SetActive` path and reports an unconfirmed result.

## PR-MODS-002

A brand-new canonical package is staged in an owned, unique `.staging-*` directory under
the store root and published with `Directory.Move` only after both manifest and source are
written. A transient I/O failure during that first installation leaves no invalid
canonical directory, so the identical retry installs the pair and confirms. Staging is
deleted only when it is owned; foreign or seeded corrupt canonical evidence is never
deleted or overwritten. Existing-directory updates, admission, arming, layout markers and
Windows path checks are unchanged.

A bounded test seam, `FailFirstCanonicalInstallationForTesting`, can force the failure
before the first manifest is written.

## PR-MODS-003

`LuaModStoreId.ApplyTo` now derives its SHA-256 from `UTF8Encoding(false, true)`. Unpaired
or otherwise malformed UTF-16 is rejected before any filesystem I/O, while valid
supplementary characters such as emoji keep their existing digests. Legacy migration
validates through the same path before moving data.
