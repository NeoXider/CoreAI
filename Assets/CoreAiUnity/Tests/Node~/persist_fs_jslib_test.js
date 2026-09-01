'use strict';

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

function loadBridge(syncfs) {
  let source = fs.readFileSync(bridgePath, 'utf8');
  source = source.replace(
    /\{\{\{\s*makeDynCall\('[^']*'\s*,\s*'([A-Za-z0-9_]+)'\)\s*\}\}\}/g,
    '($1)');
  const captured = {};
  const sandbox = {
    mergeInto: (target, library) => Object.assign(captured, library),
    LibraryManager: { library: {} },
    stringToNewUTF8: (value) => value == null ? '' : String(value),
    _free: () => {},
    console,
    FS: { syncfs },
  };
  const factory = new Function(...Object.keys(sandbox), source);
  factory(...Object.values(sandbox));
  globalThis.CoreAiPersistFsQueue = captured.$CoreAiPersistFsQueue;
  globalThis.CoreAiPersistFsEnqueue = captured.$CoreAiPersistFsEnqueue;
  return captured;
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
    const pending = [];
    const calls = [];
    const bridge = loadBridge((populate, completion) => pending.push(completion));
    bridge.CoreAi_PersistFsSyncAsync(
      11,
      (id, succeeded, message) => calls.push([id, succeeded, message]));
    check('does not complete before syncfs callback', calls.length === 0);
    pending.shift()(null);
    check(
      'success callback completes matching call',
      JSON.stringify(calls) === JSON.stringify([[11, 1, '']]),
      JSON.stringify(calls));
  }

  {
    const pending = [];
    const calls = [];
    const bridge = loadBridge((populate, completion) => pending.push(completion));
    bridge.CoreAi_PersistFsSyncAsync(
      21,
      (id, succeeded, message) => calls.push([id, succeeded, message]));
    pending.shift()(new Error('quota failure'));
    check(
      'error callback carries failure',
      calls.length === 1
        && calls[0][0] === 21
        && calls[0][1] === 0
        && calls[0][2].includes('quota failure'),
      JSON.stringify(calls));
  }

  {
    const pending = [];
    const calls = [];
    let syncCount = 0;
    const bridge = loadBridge((populate, completion) => {
      syncCount++;
      pending.push(completion);
    });
    bridge.CoreAi_PersistFsSyncAsync(
      31,
      (id, succeeded) => calls.push([id, succeeded]));
    bridge.CoreAi_PersistFsSyncAsync(
      32,
      (id, succeeded) => calls.push([id, succeeded]));
    check('concurrent request queues one follow-up sync', syncCount === 1);
    pending.shift()(null);
    check(
      'first completion starts follow-up without early second success',
      syncCount === 2 && calls.length === 1 && calls[0][0] === 31,
      JSON.stringify({ syncCount, calls }));
    pending.shift()(null);
    check(
      'queued completion follows its own syncfs callback',
      JSON.stringify(calls) === JSON.stringify([[31, 1], [32, 1]]),
      JSON.stringify(calls));
  }

  if (failures > 0) {
    process.exitCode = 1;
  }
}

run();
