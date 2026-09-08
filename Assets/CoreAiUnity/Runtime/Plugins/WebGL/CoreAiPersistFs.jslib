mergeInto(LibraryManager.library, {
  $CoreAiPersistFsQueue: {
    busy: false,
    active: [],
    queued: [],
    ready: [],
    delivering: [],
    nextFlush: false,
    scheduled: false,
    maxWaiters: 64,
    maxDeliveriesPerTask: 8,
    deliveryBudgetMs: 2
  },

  $CoreAiPersistFsRetainedCount__deps: ['$CoreAiPersistFsQueue'],
  $CoreAiPersistFsRetainedCount: function () {
    var q = CoreAiPersistFsQueue;
    return q.active.length + q.queued.length + q.ready.length + q.delivering.length;
  },

  $CoreAiPersistFsScheduleDelivery__deps: ['$CoreAiPersistFsQueue'],
  $CoreAiPersistFsScheduleDelivery: function () {
    var q = CoreAiPersistFsQueue;
    if (q.scheduled || q.ready.length === 0) return;
    if (typeof setTimeout !== 'function') {
      console.warn('[CoreAiPersistFs] completion scheduler is unavailable');
      return;
    }
    q.scheduled = true;
    function task() {
      q.scheduled = false;
      var startMs = typeof Date.now === 'function' ? Date.now() : 0;
      // WHY: Snapshot the head so completions enqueued during delivery run in a later task.
      var batchSize = Math.min(q.ready.length, q.maxDeliveriesPerTask);
      q.delivering = q.ready.splice(0, batchSize);
      var index = 0;
      while (q.delivering.length > 0) {
        if (index > 0 && typeof Date.now === 'function'
          && (Date.now() - startMs) >= q.deliveryBudgetMs) {
          break;
        }
        // WHY: Pending siblings remain state-owned and cancellable until their callbacks begin.
        var request = q.delivering.shift();
        index++;
        try {
          request.deliver();
        } catch (callbackError) {
          var message = callbackError && callbackError.message
            ? callbackError.message
            : String(callbackError);
          console.warn('[CoreAiPersistFs] completion delivery failed:', message);
        }
      }
      q.ready = q.delivering.concat(q.ready);
      q.delivering = [];
      if (q.ready.length > 0) {
        CoreAiPersistFsScheduleDelivery();
      }
    }
    setTimeout(task, 0);
  },

  $CoreAiPersistFsEnqueue__deps: [
    '$CoreAiPersistFsQueue',
    '$CoreAiPersistFsRetainedCount',
    '$CoreAiPersistFsScheduleDelivery',
    'free'
  ],
  $CoreAiPersistFsEnqueue: function (callId, onCompletionPtr) {
    var q = CoreAiPersistFsQueue;
    // WHY: Never replace asynchronous dispatch with a recursive synchronous drain.
    if (typeof setTimeout !== 'function') return false;

    function messageOf(error) {
      if (!error) return '';
      return error.message ? error.message : String(error);
    }

    function makeDelivery(requestToComplete, succeeded, message) {
      var capturedPtr = requestToComplete.onCompletionPtr;
      var capturedId = requestToComplete.callId;
      return function () {
        if (!capturedPtr) return;
        var errorPtr = stringToNewUTF8(message || '');
        try {
          {{{ makeDynCall('viii', 'capturedPtr') }}}(
            capturedId,
            succeeded ? 1 : 0,
            errorPtr);
        } catch (callbackError) {
          console.warn('[CoreAiPersistFs] completion callback failed:', messageOf(callbackError));
        } finally {
          _free(errorPtr);
        }
      };
    }

    function failBatchToReady(batch, message) {
      for (var index = 0; index < batch.length; index++) {
        q.ready.push({
          requestCallId: batch[index].callId,
          deliver: makeDelivery(batch[index], false, message)
        });
      }
      CoreAiPersistFsScheduleDelivery();
    }

    function onFlushCallback(succeeded, message) {
      if (!succeeded) {
        console.warn('[CoreAiPersistFs] IndexedDB flush failed:', message);
      }
      // WHY: Snapshot the active batch at flush start semantics: anything admitted after the
      // flush started sits in queued and needs the next flush, never this callback.
      var completed = q.active;
      q.active = [];
      for (var index = 0; index < completed.length; index++) {
        q.ready.push({
          requestCallId: completed[index].callId,
          deliver: makeDelivery(completed[index], succeeded, message)
        });
      }
      if (q.nextFlush || q.queued.length > 0) {
        q.active = q.queued;
        q.queued = [];
        q.nextFlush = false;
        startActive();
      } else {
        // WHY: Busy releases only here (real callback) or on a start throw below.
        q.busy = false;
      }
      CoreAiPersistFsScheduleDelivery();
    }

    function startActive() {
      try {
        if (typeof FS === 'undefined' || typeof FS.syncfs !== 'function') {
          throw new Error('FS.syncfs is unavailable');
        }

        FS.syncfs(false, function (error) {
          if (error) {
            onFlushCallback(false, messageOf(error));
          } else {
            onFlushCallback(true, '');
          }
        });
        return true;
      } catch (error) {
        // WHY: Release busy exactly once and surface the failure; never retry in a loop.
        var failed = q.active;
        q.active = [];
        q.busy = false;
        console.warn('[CoreAiPersistFs] IndexedDB flush failed:', messageOf(error));
        failBatchToReady(failed, messageOf(error));
        return false;
      }
    }

    if (onCompletionPtr) {
      if (CoreAiPersistFsRetainedCount() >= q.maxWaiters) {
        return false;
      }
      var request = {
        callId: callId,
        onCompletionPtr: onCompletionPtr
      };
      if (q.busy) {
        q.queued.push(request);
        q.nextFlush = true;
        return true;
      }
      q.busy = true;
      q.active = [request];
      startActive();
      return true;
    }

    // WHY: Fire-and-forget carries dirty intent only; it never allocates a waiter entry.
    if (q.busy) {
      q.nextFlush = true;
      return true;
    }
    q.busy = true;
    q.active = [];
    return startActive();
  },

  $CoreAiPersistFsCancelWaiter__deps: ['$CoreAiPersistFsQueue'],
  $CoreAiPersistFsCancelWaiter: function (callId) {
    var q = CoreAiPersistFsQueue;
    function removeFrom(list) {
      for (var index = 0; index < list.length; index++) {
        var entry = list[index];
        var id = entry.callId !== undefined ? entry.callId : entry.requestCallId;
        if (id === callId) {
          list.splice(index, 1);
          return true;
        }
      }
      return false;
    }
    // WHY: Cancellation only drops the waiter. It never aborts the in-flight FS flush,
    // never releases busy while its callback is outstanding, and never clears dirty intent.
    if (removeFrom(q.queued)) return;
    if (removeFrom(q.active)) return;
    if (removeFrom(q.ready)) return;
    removeFrom(q.delivering);
  },

  CoreAi_PersistFsSync__deps: ['$CoreAiPersistFsEnqueue'],
  CoreAi_PersistFsSync: function () {
    return CoreAiPersistFsEnqueue(0, 0) ? 1 : 0;
  },

  CoreAi_PersistFsSyncAsync__deps: ['$CoreAiPersistFsEnqueue'],
  CoreAi_PersistFsSyncAsync: function (callId, onCompletionPtr) {
    return CoreAiPersistFsEnqueue(callId, onCompletionPtr) ? 1 : 0;
  },

  CoreAi_PersistFsCancelWaiter__deps: ['$CoreAiPersistFsCancelWaiter'],
  CoreAi_PersistFsCancelWaiter: function (callId) {
    CoreAiPersistFsCancelWaiter(callId);
  },

  CoreAi_PersistFsPendingRequestCount__deps: ['$CoreAiPersistFsRetainedCount'],
  CoreAi_PersistFsPendingRequestCount: function () {
    return CoreAiPersistFsRetainedCount();
  },

  CoreAi_PersistFsPendingFlushCount__deps: ['$CoreAiPersistFsQueue'],
  CoreAi_PersistFsPendingFlushCount: function () {
    var q = CoreAiPersistFsQueue;
    return (q.busy ? 1 : 0) + ((q.nextFlush || q.queued.length > 0) ? 1 : 0);
  }
});
