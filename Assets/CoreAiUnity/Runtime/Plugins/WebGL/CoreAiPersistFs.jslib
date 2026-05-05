// Persist Unity's WebGL Application.persistentDataPath into IndexedDB.
//
// On WebGL builds Emscripten mounts an IDBFS at /idbfs/<hash>; Unity directs
// File.WriteAllText / File.ReadAllText through that mount. Writes land in an
// **in-memory** copy of the FS — they only become durable after the runtime
// calls FS.syncfs(false, cb) to push the in-memory tree into IndexedDB.
// Unity auto-syncs on Application.Quit(), but a tab close / page reload does
// not invoke Quit, so saved data is lost. Call CoreAi_PersistFsSync after each
// write you want to survive a reload.
//
// **Single-flight:** Emscripten warns (and can misbehave) if FS.syncfs is
// invoked while a previous sync is still in flight. FileAgentMemoryStore may
// call this after every File.WriteAllText (chat + memory in one turn), so we
// queue at most one follow-up sync when re-entrancy happens.

mergeInto(LibraryManager.library, {
  $CoreAiPersistFsQueue: {
    pending: false,
    queued: false
  },

  CoreAi_PersistFsSync__deps: ['$CoreAiPersistFsQueue'],
  CoreAi_PersistFsSync: function () {
    var q = CoreAiPersistFsQueue;
    try {
      if (typeof FS === 'undefined' || typeof FS.syncfs !== 'function') {
        console.warn('[CoreAiPersistFs] FS or FS.syncfs unavailable; persistentDataPath writes will not survive reload');
        return;
      }
      if (q.pending) {
        q.queued = true;
        return;
      }
      q.pending = true;
      function onDone(err) {
        if (err) {
          console.warn('[CoreAiPersistFs] FS.syncfs failed:', err && err.message ? err.message : err);
        }
        if (q.queued) {
          q.queued = false;
          FS.syncfs(false, onDone);
        } else {
          q.pending = false;
        }
      }
      FS.syncfs(false, onDone);
    } catch (e) {
      q.pending = false;
      q.queued = false;
      console.warn('[CoreAiPersistFs] FS.syncfs threw:', e && e.message ? e.message : e);
    }
  }
});
