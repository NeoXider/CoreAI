// WHY: This file no longer drives FS.syncfs by hand. Unity 6.3 deprecated the manual
// Application.persistentDataPath sync (the player prints that deprecation at boot) and the engine
// now owns persistence: when createUnityInstance() is given config.autoSyncPersistentDataPath = true,
// Unity mounts IDBFS with { autoPersist: true } and queues an IndexedDB persist from the filesystem
// node hooks on every write/close, rename, unlink, mkdir and rmdir.
// The manual channel was not merely redundant, it was DEAD: a served WebGL player on 2026-09-09
// called FS.syncfs(false, cb) without throwing and the callback never arrived - not once, not after
// minutes - so every awaited durability confirmation blocked until its caller's own timeout
// (artifacts/testresults/g11-browser-console-run2-after-fix.log). It is removed rather than kept
// "for later": a confirmation channel that never confirms is worse than none, because callers
// report a false failure for data that is in fact durable.
// What remains is the one thing C# genuinely cannot see: WHETHER the engine's automatic persistence
// is actually armed for this page. Without it a write stays in the tab's in-memory filesystem and
// dies with the tab, so CoreAI must fail loudly instead of reporting a durability it does not have.
mergeInto(LibraryManager.library, {
  // Returns 1 when Unity's automatic persistentDataPath synchronization is armed for this instance.
  // Reads the mount that Unity itself created (prejs/IdbFs.js keeps it on Module.__unityIdbfsMount
  // and its opts.autoPersist is the flag the engine actually acts on), and falls back to the raw
  // config value when a host replaced Module.unityFileSystemInit with its own mount.
  CoreAi_PersistFsAutoSyncEnabled: function () {
    try {
      if (typeof Module === 'undefined' || !Module) {
        return 0;
      }

      var mountRoot = Module['__unityIdbfsMount'];
      if (mountRoot && mountRoot.mount && mountRoot.mount.opts) {
        return mountRoot.mount.opts.autoPersist ? 1 : 0;
      }

      return Module['autoSyncPersistentDataPath'] ? 1 : 0;
    } catch (error) {
      return 0;
    }
  }
});
