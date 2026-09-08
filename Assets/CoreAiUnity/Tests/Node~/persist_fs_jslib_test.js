'use strict';

// WHY: Node boundary proof for CoreAiPersistFs.jslib (NOT browser IndexedDB proof -- FS.syncfs,
// the scheduler, the clock, and the Emscripten pointer boundary are mocks; the queue/admission/
// cancel/delivery logic under test is the REAL jslib source loaded from disk).

const fs = require('fs');
const path = require('path');

const bridgePath = path.join(
  __dirname,
  '..',
  '..',
  'Runtime',
  'Plugins',
  'WebGL',
  'CoreAiPersistFs.jslib');

function loadBridge(options) {
  const syncImpl = options.syncImpl;
  const tasks = [];
  const state = { now: 1000 };
  let source = fs.readFileSync(bridgePath, 'utf8');
  source = source.replace(
    /\{\{\{\s*makeDynCall\('[^']*'\s*,\s*'([A-Za-z0-9_]+)'\)\s*\}\}\}/g,
    '($1)');
  const captured = {};
  const warnings = [];
  const sandbox = {
    mergeInto: (target, library) => Object.assign(captured, library),
    LibraryManager: { library: {} },
    stringToNewUTF8: (value) => value == null ? '' : String(value),
    _free: () => {},
    console: {
      log: () => {},
      warn: (...args) => warnings.push(args.map(String).join(' ')),
      error: (...args) => warnings.push(args.map(String).join(' ')),
    },
    FS: { syncfs: syncImpl },
    setTimeout: (fn) => {
      tasks.push(fn);
      return tasks.length;
    },
    Date: { now: () => state.now },
  };
  if (options.noScheduler) sandbox.setTimeout = undefined;
  const factory = new Function(...Object.keys(sandbox), source);
  factory(...Object.values(sandbox));
  globalThis.CoreAiPersistFsQueue = captured.$CoreAiPersistFsQueue;
  // WHY: Emscripten hoists `$`-prefixed library members to bare globals; mirror that here
  // so internal cross-references inside the REAL jslib resolve exactly as in a player build.
  for (const key of Object.keys(captured)) {
    globalThis[key.replace(/^\$/, '')] = captured[key];
  }
  return {
    bridge: captured,
    tasks,
    warnings,
    clock: state,
    runTasks: (max) => {
      let ran = 0;
      while (tasks.length > 0 && (max === undefined || ran < max)) {
        tasks.shift()();
        ran++;
      }
      return ran;
    },
  };
}

function manualFlush() {
  const pending = [];
  let syncCount = 0;
  const calls = [];
  const env = loadBridge({
    syncImpl: (populate, completion) => {
      syncCount++;
      pending.push(completion);
    },
  });
  const cb = (id) => (callId, succeeded, message) => calls.push([callId, succeeded, message, id]);
  return {
    ...env,
    pending,
    calls,
    cb,
    syncCount: () => syncCount,
  };
}

let failures = 0;

function check(name, condition, detail) {
  if (condition) {
    console.log('PASS ' + name);
    return;
  }

  failures++;
  console.error('FAIL ' + name + (detail ? ' -- ' + detail : ''));
}

function run() {
  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(11, t.cb('a'));
    check('does not complete before syncfs callback', t.calls.length === 0);
    t.pending.shift()(null);
    t.runTasks();
    check(
      'success callback completes matching call',
      JSON.stringify(t.calls) === JSON.stringify([[11, 1, '', 'a']]),
      JSON.stringify(t.calls));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(21, t.cb('a'));
    t.pending.shift()(new Error('quota failure'));
    t.runTasks();
    check(
      'error callback carries failure',
      t.calls.length === 1
        && t.calls[0][0] === 21
        && t.calls[0][1] === 0
        && t.calls[0][2].includes('quota failure'),
      JSON.stringify(t.calls));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(31, t.cb('a'));
    t.bridge.CoreAi_PersistFsSyncAsync(32, t.cb('a'));
    check('concurrent request queues one follow-up sync', t.syncCount() === 1);
    check(
      'flush diagnostics while coalesced',
      t.bridge.CoreAi_PersistFsPendingRequestCount() === 2
        && t.bridge.CoreAi_PersistFsPendingFlushCount() === 2,
      t.bridge.CoreAi_PersistFsPendingRequestCount()
        + '/' + t.bridge.CoreAi_PersistFsPendingFlushCount());
    t.pending.shift()(null);
    check(
      'first completion starts follow-up without early second success',
      t.syncCount() === 2 && t.calls.length === 0,
      JSON.stringify({ syncCount: t.syncCount(), calls: t.calls }));
    t.runTasks();
    check(
      'first waiter delivered after its own flush',
      t.calls.length === 1 && t.calls[0][0] === 31 && t.calls[0][1] === 1,
      JSON.stringify(t.calls));
    t.pending.shift()(null);
    t.runTasks();
    check(
      'queued completion follows its own syncfs callback',
      t.calls.length === 2 && t.calls[1][0] === 32 && t.calls[1][1] === 1,
      JSON.stringify(t.calls));
    check(
      'idle diagnostics drain to zero',
      t.bridge.CoreAi_PersistFsPendingRequestCount() === 0
        && t.bridge.CoreAi_PersistFsPendingFlushCount() === 0,
      t.bridge.CoreAi_PersistFsPendingRequestCount()
        + '/' + t.bridge.CoreAi_PersistFsPendingFlushCount());
  }

  {
    const t = manualFlush();
    let admitted = 0;
    for (let id = 1; id <= 64; id++) {
      admitted += t.bridge.CoreAi_PersistFsSyncAsync(id, t.cb('a'));
    }
    check('admits exactly 64 waiters', admitted === 64, String(admitted));
    check(
      'cap counts every retained state',
      t.bridge.CoreAi_PersistFsPendingRequestCount() === 64
        && t.bridge.CoreAi_PersistFsPendingFlushCount() === 2,
      t.bridge.CoreAi_PersistFsPendingRequestCount()
        + '/' + t.bridge.CoreAi_PersistFsPendingFlushCount());
    check(
      '65th waiter rejected while saturated',
      t.bridge.CoreAi_PersistFsSyncAsync(65, t.cb('a')) === 0,
      'admission beyond cap must return 0');
    check(
      'rejected waiter leaves retention unchanged',
      t.bridge.CoreAi_PersistFsPendingRequestCount() === 64,
      String(t.bridge.CoreAi_PersistFsPendingRequestCount()));
  }

  {
    const t = manualFlush();
    for (let id = 1; id <= 64; id++) {
      t.bridge.CoreAi_PersistFsSyncAsync(id, t.cb('a'));
    }
    t.bridge.CoreAi_PersistFsCancelWaiter(7);
    check(
      'cancel frees one slot',
      t.bridge.CoreAi_PersistFsPendingRequestCount() === 63,
      String(t.bridge.CoreAi_PersistFsPendingRequestCount()));
    check(
      'admission succeeds after cancel frees capacity',
      t.bridge.CoreAi_PersistFsSyncAsync(65, t.cb('a')) === 1,
      'cancel must free capacity');
    while (t.pending.length > 0) {
      t.pending.shift()(null);
      t.runTasks();
    }
    const delivered = t.calls.map((entry) => entry[0]).sort((a, b) => a - b);
    check(
      'cancelled waiter never delivered late',
      !delivered.includes(7) && delivered.length === 64,
      JSON.stringify(delivered));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(41, t.cb('a'));
    t.bridge.CoreAi_PersistFsSyncAsync(42, t.cb('a'));
    t.bridge.CoreAi_PersistFsCancelWaiter(41);
    check(
      'cancelling the in-flight waiter starts no overlapping flush',
      t.syncCount() === 1,
      String(t.syncCount()));
    t.pending.shift()(null);
    t.runTasks();
    t.pending.shift()(null);
    t.runTasks();
    check(
      'queued waiter still confirmed after cancel of its predecessor',
      t.calls.length === 1 && t.calls[0][0] === 42 && t.calls[0][1] === 1,
      JSON.stringify(t.calls));
    check(
      'cancel preserves single-flight chain',
      t.syncCount() === 2,
      String(t.syncCount()));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(51, t.cb('a'));
    t.bridge.CoreAi_PersistFsSyncAsync(52, t.cb('a'));
    t.pending.shift()(null);
    t.runTasks();
    check(
      'later write not confirmed by the earlier flush callback',
      t.calls.length === 1 && t.calls[0][0] === 51 && t.calls[0][1] === 1,
      JSON.stringify(t.calls));
    t.pending.shift()(new Error('second flush lost'));
    t.runTasks();
    check(
      'later write confirmed only by its own next flush',
      t.calls.length === 2
        && t.calls[1][0] === 52
        && t.calls[1][1] === 0
        && t.calls[1][2].includes('second flush lost'),
      JSON.stringify(t.calls));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSync();
    for (let n = 0; n < 200; n++) {
      t.bridge.CoreAi_PersistFsSync();
    }
    check(
      'fire-and-forget flood retains no waiter entries',
      t.bridge.CoreAi_PersistFsPendingRequestCount() === 0,
      String(t.bridge.CoreAi_PersistFsPendingRequestCount()));
    check(
      'fire-and-forget flood starts exactly one flush with dirty intent',
      t.syncCount() === 1 && t.bridge.CoreAi_PersistFsPendingFlushCount() === 2,
      t.syncCount() + '/' + t.bridge.CoreAi_PersistFsPendingFlushCount());
    t.pending.shift()(null);
    t.runTasks();
    check(
      'dirty intent runs exactly one follow-up flush',
      t.syncCount() === 2 && t.bridge.CoreAi_PersistFsPendingFlushCount() === 1,
      t.syncCount() + '/' + t.bridge.CoreAi_PersistFsPendingFlushCount());
    t.pending.shift()(null);
    t.runTasks();
    check(
      'flush chain drains back to idle',
      t.syncCount() === 2 && t.bridge.CoreAi_PersistFsPendingFlushCount() === 0,
      t.syncCount() + '/' + t.bridge.CoreAi_PersistFsPendingFlushCount());
  }

  {
    const t = manualFlush();
    for (let id = 1; id <= 10; id++) {
      t.bridge.CoreAi_PersistFsSyncAsync(id, t.cb('a'));
    }
    t.pending.shift()(null);
    t.pending.shift()(null);
    t.runTasks(1);
    check(
      'single scheduler task delivers at most 8 completions',
      t.calls.length <= 8 && t.calls.length > 0,
      String(t.calls.length));
    const afterOneTask = t.calls.length;
    t.runTasks();
    check(
      'remaining completions drain in later tasks',
      t.calls.length === 10 && afterOneTask < 10,
      afterOneTask + '/' + t.calls.length);
  }

  {
    const t = manualFlush();
    let selfEnqueued = false;
    t.bridge.CoreAi_PersistFsSyncAsync(1, (id, ok, message) => {
      t.calls.push([id, ok, message, 'first']);
      t.clock.now += 5;
      selfEnqueued = t.bridge.CoreAi_PersistFsSyncAsync(100, t.cb('b')) === 1;
    });
    t.bridge.CoreAi_PersistFsSyncAsync(2, t.cb('a'));
    t.bridge.CoreAi_PersistFsSyncAsync(3, t.cb('a'));
    t.pending.shift()(null);
    t.pending.shift()(null);
    t.runTasks(1);
    check(
      'time budget defers delivery past 2ms',
      t.calls.length === 1 && t.calls[0][0] === 1,
      JSON.stringify(t.calls));
    check('self-enqueued completion waits for a later task', selfEnqueued);
    t.pending.shift()(null);
    t.runTasks();
    check(
      'deferred and self-enqueued completions still delivered',
      JSON.stringify(t.calls.map((entry) => entry[0])) === JSON.stringify([1, 2, 3, 100]),
      JSON.stringify(t.calls));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(71, () => {
      throw new Error('listener blew up');
    });
    t.bridge.CoreAi_PersistFsSyncAsync(72, t.cb('a'));
    t.pending.shift()(null);
    t.pending.shift()(null);
    t.runTasks();
    check(
      'throwing completion does not block its neighbors',
      t.calls.length === 1 && t.calls[0][0] === 72 && t.calls[0][1] === 1,
      JSON.stringify(t.calls));
  }

  {
    let syncCount = 0;
    let failNext = true;
    const env = loadBridge({
      syncImpl: (populate, completion) => {
        syncCount++;
        if (failNext) {
          failNext = false;
          throw new Error('syncfs unavailable');
        }
      },
    });
    const calls = [];
    env.bridge.CoreAi_PersistFsSyncAsync(81, (id, ok, message) => calls.push([id, ok, message]));
    env.runTasks();
    check(
      'start throw fails the waiter instead of hanging',
      calls.length === 1 && calls[0][1] === 0,
      JSON.stringify(calls));
    check(
      'start throw releases busy exactly once with no retry',
      syncCount === 1
        && env.bridge.CoreAi_PersistFsPendingFlushCount() === 0
        && env.bridge.CoreAi_PersistFsPendingRequestCount() === 0,
      syncCount + '/' + env.bridge.CoreAi_PersistFsPendingFlushCount());
    check(
      'bridge usable again after a start throw',
      env.bridge.CoreAi_PersistFsSyncAsync(82, () => {}) === 1
        && env.bridge.CoreAi_PersistFsPendingFlushCount() === 1,
      String(env.bridge.CoreAi_PersistFsPendingFlushCount()));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSync();
    t.bridge.CoreAi_PersistFsSyncAsync(91, t.cb('after-fire-forget'));
    t.pending.shift()(null);
    t.pending.shift()(null);
    t.runTasks();
    check('fire-and-forget predecessor preserves async callback pointer',
      JSON.stringify(t.calls) === JSON.stringify([[91, 1, '', 'after-fire-forget']]),
      JSON.stringify(t.calls));
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(92, t.cb('first'));
    t.bridge.CoreAi_PersistFsSyncAsync(93, t.cb('second'));
    t.pending.shift()(null);
    t.pending.shift()(null);
    t.runTasks();
    check('each waiter invokes its own callback pointer',
      JSON.stringify(t.calls) === JSON.stringify([[92, 1, '', 'first'], [93, 1, '', 'second']]),
      JSON.stringify(t.calls));
  }

  {
    const t = manualFlush();
    let retainedInsideCallback = -1;
    let admissions = 0;
    t.bridge.CoreAi_PersistFsSyncAsync(1, (id, ok, message) => {
      t.calls.push([id, ok, message]);
      retainedInsideCallback = t.bridge.CoreAi_PersistFsPendingRequestCount();
      t.bridge.CoreAi_PersistFsCancelWaiter(2);
      for (let next = 100; next < 108; next++) {
        admissions += t.bridge.CoreAi_PersistFsSyncAsync(next, t.cb('new'));
      }
    });
    for (let id = 2; id <= 64; id++) t.bridge.CoreAi_PersistFsSyncAsync(id, t.cb('original'));
    t.pending.shift()(null);
    t.pending.shift()(null);
    t.runTasks(1);
    check('delivery diagnostics retain all callbacks that have not started',
      retainedInsideCallback === 63, String(retainedInsideCallback));
    check('callback cancellation frees only its own pending slot', admissions === 2, String(admissions));
    check('cancelling a sibling during delivery suppresses that sibling',
      !t.calls.some((entry) => entry[0] === 2), JSON.stringify(t.calls));
    while (t.pending.length > 0) t.pending.shift()(null);
    t.runTasks();
    check('delivery cancellation preserves all other admitted callbacks',
      !t.calls.some((entry) => entry[0] === 2) && t.calls.length === 65
        && t.bridge.CoreAi_PersistFsPendingRequestCount() === 0,
      String(t.calls.length));
  }

  {
    const t = manualFlush();
    check('fire-and-forget reports accepted queue intent', t.bridge.CoreAi_PersistFsSync() === 1);
    t.pending.shift()(new Error('fire-and-forget quota failure'));
    check('fire-and-forget callback failure is observable once',
      t.warnings.length === 1 && t.warnings[0].includes('fire-and-forget quota failure'),
      JSON.stringify(t.warnings));
    check('failed fire-and-forget flush releases physical flush state',
      t.bridge.CoreAi_PersistFsPendingFlushCount() === 0);
  }

  {
    const t = loadBridge({ syncImpl: () => { throw new Error('flush start rejected'); } });
    check('fire-and-forget reports synchronous flush rejection', t.bridge.CoreAi_PersistFsSync() === 0);
    check('fire-and-forget start failure is observable once',
      t.warnings.length === 1 && t.warnings[0].includes('flush start rejected'),
      JSON.stringify(t.warnings));
  }

  {
    const t = manualFlush();
    for (let id = 1; id <= 10; id++) t.bridge.CoreAi_PersistFsSyncAsync(id, t.cb('failure'));
    t.pending.shift()(null);
    t.pending.shift()(new Error('shared flush failure'));
    t.runTasks();
    check('failed batch warns once for the flush and completes every waiter',
      t.warnings.length === 1 && t.calls.filter((entry) => entry[1] === 0).length === 9,
      JSON.stringify({ warnings: t.warnings, calls: t.calls }));
  }

  {
    let flushes = 0;
    const t = loadBridge({ noScheduler: true, syncImpl: () => { flushes++; } });
    check('missing scheduler rejects admission without synchronous recursion',
      t.bridge.CoreAi_PersistFsSyncAsync(1, () => {}) === 0 && flushes === 0
        && t.bridge.CoreAi_PersistFsPendingRequestCount() === 0);
  }

  {
    const t = manualFlush();
    t.bridge.CoreAi_PersistFsSyncAsync(1, (id) => {
      t.calls.push([id]);
      t.bridge.CoreAi_PersistFsSyncAsync(3, t.cb('new'));
      t.pending.shift()(null);
    });
    t.bridge.CoreAi_PersistFsSyncAsync(2, t.cb('original'));
    t.pending.shift()(null);
    t.pending.shift()(null);
    t.runTasks(1);
    check('newly completed waiter cannot join the running delivery task',
      JSON.stringify(t.calls.map((entry) => entry[0])) === JSON.stringify([1, 2]),
      JSON.stringify(t.calls));
    t.runTasks();
    check('reentrant completion is delivered in its next scheduler task',
      JSON.stringify(t.calls.map((entry) => entry[0])) === JSON.stringify([1, 2, 3]),
      JSON.stringify(t.calls));
  }

  if (failures > 0) {
    process.exitCode = 1;
  }
}

run();
