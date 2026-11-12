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
// What remains is what C# genuinely cannot see: WHETHER the engine's automatic persistence is
// actually armed for this page (without it a write stays in the tab's in-memory filesystem and dies
// with the tab, so CoreAI must fail loudly instead of reporting a durability it does not have),
// whether that persistence is still busy, whether another page shares the storage, and the page's
// own "Leave site?" guard while a kept world has unsaved changes.
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
  },

  // Returns 1 while the engine's automatic persistence still has writes to carry into IndexedDB (a persist
  // queued by a file close, rename or unlink, or one running), 0 when it is idle, -1 when the mount cannot be
  // read.
  // WHY: Unity offers no completion signal, so this reads the engine's own state: prejs/IdbFs.js (and
  // Emscripten's IDBFS.queuePersist) keep it on the mount as idbPersistState - 0 when idle, a timer id when
  // queued, 'idb' or 'again' while a sync runs. A sync that fails (a full quota) also ends as 0: the engine
  // drops its error.
  CoreAi_PersistFsFlushPending: function () {
    try {
      if (typeof Module === 'undefined' || !Module) {
        return -1;
      }

      var mountRoot = Module['__unityIdbfsMount'];
      if (!mountRoot || !mountRoot.mount) {
        return -1;
      }

      return mountRoot.mount.idbPersistState ? 1 : 0;
    } catch (error) {
      return -1;
    }
  },

  // Turns the browser's "Leave site?" confirmation on (1) or off (0).
  // WHY: added and removed, because a beforeunload listener keeps a page out of the browser's back-forward
  // cache, so it is only there while unsaved work is.
  CoreAi_SetLeaveGuard: function (on) {
    try {
      var guard = Module['__coreAiLeaveGuard'];
      if (!guard) {
        guard = {
          armed: false,
          handler: function (event) {
            event.preventDefault();
            event.returnValue = '';
            return '';
          }
        };
        Module['__coreAiLeaveGuard'] = guard;
      }

      if (on && !guard.armed) {
        window.addEventListener('beforeunload', guard.handler);
        guard.armed = true;
      } else if (!on && guard.armed) {
        window.removeEventListener('beforeunload', guard.handler);
        guard.armed = false;
      }
    } catch (error) {
    }
  },

  // Returns how many other pages of this origin run CoreAI now (0 when none is seen or the browser has no
  // BroadcastChannel). Pages announce themselves on the first call, every 30 s and on pagehide; one not heard
  // for two minutes no longer counts.
  // WHY: every page of an origin shares one IndexedDB copy of persistentDataPath, and each page's persist
  // writes its own view of the whole folder and removes the files it does not have, so two open pages
  // overwrite each other's saves. WHY two minutes: a long-hidden tab's timers may run only once a minute.
  CoreAi_OtherPagesSharingStorage: function () {
    try {
      var pages = Module['__coreAiPages'];
      if (!pages) {
        pages = { id: Math.random().toString(36).slice(2) + Date.now().toString(36), seen: {} };
        Module['__coreAiPages'] = pages;
        if (typeof BroadcastChannel === 'undefined') {
          return 0;
        }

        var channel = new BroadcastChannel('coreai-persistent-data');
        pages.channel = channel;
        channel.onmessage = function (event) {
          var message = event.data;
          if (!message || typeof message['id'] !== 'string' || message['id'] === pages.id) {
            return;
          }

          if (message['kind'] === 'bye') {
            delete pages.seen[message['id']];
            return;
          }

          pages.seen[message['id']] = Date.now();
          if (message['kind'] === 'hello') {
            channel.postMessage({ 'kind': 'here', 'id': pages.id });
          }
        };
        channel.postMessage({ 'kind': 'hello', 'id': pages.id });
        setInterval(function () {
          try {
            channel.postMessage({ 'kind': 'here', 'id': pages.id });
          } catch (error) {
          }
        }, 30000);
        window.addEventListener('pagehide', function () {
          try {
            channel.postMessage({ 'kind': 'bye', 'id': pages.id });
          } catch (error) {
          }
        });
      }

      var now = Date.now();
      var count = 0;
      for (var id in pages.seen) {
        if (Object.prototype.hasOwnProperty.call(pages.seen, id)) {
          if (now - pages.seen[id] > 120000) {
            delete pages.seen[id];
          } else {
            count++;
          }
        }
      }

      return count;
    } catch (error) {
      return 0;
    }
  }
});
