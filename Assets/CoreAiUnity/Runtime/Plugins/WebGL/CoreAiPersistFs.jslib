// Persist Unity's WebGL Application.persistentDataPath into IndexedDB.
//
// On WebGL builds Emscripten mounts an IDBFS at /idbfs/<hash>; Unity directs
// File.WriteAllText / File.ReadAllText through that mount. Writes land in an
// **in-memory** copy of the FS — they only become durable after the runtime
// calls FS.syncfs(false, cb) to push the in-memory tree into IndexedDB.
// Unity auto-syncs on Application.Quit(), but a tab close / page reload does
// not invoke Quit, so saved data is lost. Call CoreAi_PersistFsSync after each
// write you want to survive a reload.

mergeInto(LibraryManager.library, {
  CoreAi_PersistFsSync: function () {
    try {
      if (typeof FS === 'undefined' || typeof FS.syncfs !== 'function') {
        console.warn('[CoreAiPersistFs] FS or FS.syncfs unavailable; persistentDataPath writes will not survive reload');
        return;
      }
      FS.syncfs(false, function (err) {
        if (err) {
          console.warn('[CoreAiPersistFs] FS.syncfs failed:', err && err.message ? err.message : err);
        }
      });
    } catch (e) {
      console.warn('[CoreAiPersistFs] FS.syncfs threw:', e && e.message ? e.message : e);
    }
  }
});
