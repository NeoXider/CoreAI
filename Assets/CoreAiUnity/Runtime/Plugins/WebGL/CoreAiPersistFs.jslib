mergeInto(LibraryManager.library, {
  $CoreAiPersistFsQueue: {
    pending: false,
    active: [],
    queued: []
  },

  $CoreAiPersistFsEnqueue__deps: ['$CoreAiPersistFsQueue', 'free'],
  $CoreAiPersistFsEnqueue: function (callId, onCompletionPtr) {
    var q = CoreAiPersistFsQueue;
    var request = {
      callId: callId,
      onCompletionPtr: onCompletionPtr
    };

    function messageOf(error) {
      if (!error) return '';
      return error.message ? error.message : String(error);
    }

    function complete(requestToComplete, succeeded, message) {
      if (!requestToComplete.onCompletionPtr) return;
      var onCompletionPtr = requestToComplete.onCompletionPtr;
      var errorPtr = stringToNewUTF8(message || '');
      try {
        {{{ makeDynCall('viii', 'onCompletionPtr') }}}(
          requestToComplete.callId,
          succeeded ? 1 : 0,
          errorPtr);
      } catch (callbackError) {
        console.warn('[CoreAiPersistFs] completion callback failed:', messageOf(callbackError));
      } finally {
        _free(errorPtr);
      }
    }

    function finishActive(succeeded, message) {
      var completed = q.active;
      q.active = [];
      for (var index = 0; index < completed.length; index++) {
        complete(completed[index], succeeded, message);
      }

      if (q.queued.length > 0) {
        q.active = q.queued;
        q.queued = [];
        startActive();
      } else {
        q.pending = false;
      }
    }

    function startActive() {
      try {
        if (typeof FS === 'undefined' || typeof FS.syncfs !== 'function') {
          finishActive(false, 'FS.syncfs is unavailable');
          return;
        }

        FS.syncfs(false, function (error) {
          if (error) {
            finishActive(false, messageOf(error));
          } else {
            finishActive(true, '');
          }
        });
      } catch (error) {
        finishActive(false, messageOf(error));
      }
    }

    if (q.pending) {
      q.queued.push(request);
      return;
    }

    q.pending = true;
    q.active = [request];
    startActive();
  },

  CoreAi_PersistFsSync__deps: ['$CoreAiPersistFsEnqueue'],
  CoreAi_PersistFsSync: function () {
    CoreAiPersistFsEnqueue(0, 0);
  },

  CoreAi_PersistFsSyncAsync__deps: ['$CoreAiPersistFsEnqueue'],
  CoreAi_PersistFsSyncAsync: function (callId, onCompletionPtr) {
    CoreAiPersistFsEnqueue(callId, onCompletionPtr);
  }
});
