// Node test harness for Assets/CoreAiUnity/Runtime/Plugins/WebGL/CoreAiSseFetch.jslib.
//
// The jslib is Emscripten library source: `mergeInto(LibraryManager.library, {...})` with
// `{{{ makeDynCall(...) }}}` macros. This harness loads it with those pieces stubbed so the
// bridge runs against a mocked browser `fetch`, then asserts the exact callback sequences the
// C# side (FetchSseOpenAiTransport) depends on:
//   success   -> onOpen(200) -> onChunk* -> onDone (exactly once, no onError)
//   http 4xx  -> onOpen(status, errorBody) -> onDone (no onError)
//   reject    -> onOpen(0, msg) + onError (exactly once each)
//   sync throw-> onOpen(0) + onError, and the exception must NOT escape into the caller (wasm)
//   abort     -> NO callbacks with reason 'cancelled' (C# learns via its own CancellationToken)
//   duplicate terminal events are swallowed by the finished-guard
//
// Run:  node Assets/CoreAiUnity/Tests/Node~/fetch_sse_jslib_test.js
// Exit code 0 = all pass.

'use strict';
const fs = require('fs');
const path = require('path');

const JSLIB_PATH = path.join(__dirname, '..', '..', 'Runtime', 'Plugins', 'WebGL', 'CoreAiSseFetch.jslib');

function loadLibrary() {
  let src = fs.readFileSync(JSLIB_PATH, 'utf8');
  // {{{ makeDynCall('sig', 'ptr') }}}(args) -> (ptr)(args): pointers are plain JS functions here.
  src = src.replace(/\{\{\{\s*makeDynCall\('[^']*'\s*,\s*'([A-Za-z0-9_]+)'\)\s*\}\}\}/g, '($1)');

  const captured = {};
  globalThis.__allocs = 0;
  globalThis.__frees = 0;
  const sandbox = {
    mergeInto: (target, lib) => Object.assign(captured, lib),
    LibraryManager: { library: {} },
    UTF8ToString: (x) => (x == null ? '' : String(x)),
    stringToNewUTF8: (s) => { globalThis.__allocs++; return s == null ? '' : String(s); },
    _free: () => { globalThis.__frees++; },
    console,
    setTimeout,
    clearTimeout,
    AbortController,
    TextDecoder,
    // The library closure captures `fetch` at load time, so route through a late-bound shim;
    // each scenario installs its mock as globalThis.fetch.
    fetch: (u, i) => globalThis.fetch(u, i),
  };
  const fn = new Function(...Object.keys(sandbox), src);
  fn(...Object.values(sandbox));

  // Emscripten resolves the $CoreAiSseFetchState dep as a bare global inside library functions.
  globalThis.CoreAiSseFetchState = captured.$CoreAiSseFetchState;
  return captured;
}

function makeCallbacks(log) {
  return {
    onOpen: (id, status, errBody, hdrs) => log.push(['open', status, String(errBody), String(hdrs)]),
    onChunk: (id, text) => log.push(['chunk', String(text)]),
    onDone: (id) => log.push(['done']),
    onError: (id, msg) => log.push(['error', String(msg)]),
  };
}

function openCall(lib, cb, { url = 'http://x/v1/chat', body = '{}', headers = 'Content-Type:application/json', timeoutSec = 0, creds = 'omit', callId = 1 } = {}) {
  lib.CoreAi_FetchSseOpen(url, body, headers, timeoutSec, creds, callId, cb.onOpen, cb.onChunk, cb.onDone, cb.onError);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

function sseResponse(chunks, { status = 200, headers = { 'content-type': 'text/event-stream' } } = {}) {
  const encoder = new TextEncoder();
  let i = 0;
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: { forEach: (f) => Object.entries(headers).forEach(([k, v]) => f(v, k)), get: (k) => headers[k.toLowerCase()] },
    body: {
      getReader: () => ({
        read: () => Promise.resolve(i < chunks.length
          ? { value: encoder.encode(chunks[i++]), done: false }
          : { value: undefined, done: true }),
      }),
    },
    text: () => Promise.resolve(chunks.join('')),
  };
}

let failures = 0;
function check(name, cond, detail) {
  if (cond) {
    console.log('  PASS ' + name);
  } else {
    failures++;
    console.error('  FAIL ' + name + (detail ? ' -- ' + detail : ''));
  }
}

async function run() {
  // --- 1. success stream ---------------------------------------------------
  {
    const lib = loadLibrary();
    const log = [];
    globalThis.fetch = () => Promise.resolve(sseResponse(['data: {"a":1}\n\n', 'data: [DONE]\n\n']));
    openCall(lib, makeCallbacks(log), { callId: 11 });
    await sleep(50);
    console.log('scenario: success stream');
    check('opens with 200', log[0] && log[0][0] === 'open' && log[0][1] === 200, JSON.stringify(log));
    check('open reports headers', log[0] && log[0][3].includes('content-type:text/event-stream'), JSON.stringify(log));
    check('delivers both chunks in order',
      log.filter((e) => e[0] === 'chunk').map((e) => e[1]).join('|') === 'data: {"a":1}\n\n|data: [DONE]\n\n',
      JSON.stringify(log));
    check('exactly one done', log.filter((e) => e[0] === 'done').length === 1, JSON.stringify(log));
    check('no error', log.every((e) => e[0] !== 'error'), JSON.stringify(log));
    check('controller cleaned up', Object.keys(CoreAiSseFetchState.controllers).length === 0);
    check('every marshaled string freed (no wasm heap leak)',
      globalThis.__allocs > 0 && globalThis.__allocs === globalThis.__frees,
      `allocs=${globalThis.__allocs} frees=${globalThis.__frees}`);
    // Late abort after normal completion must not resurrect state (abortReasons leak).
    lib.CoreAi_FetchSseAbort(11);
    await sleep(10);
    check('late abort leaves no abortReasons entry',
      Object.keys(CoreAiSseFetchState.abortReasons).length === 0,
      JSON.stringify(CoreAiSseFetchState.abortReasons));
  }

  // --- 2. http error with body ---------------------------------------------
  {
    const lib = loadLibrary();
    const log = [];
    globalThis.fetch = () => Promise.resolve(sseResponse(['{"error":{"message":"try again in 14.5s"}}'], { status: 429, headers: { 'content-type': 'application/json' } }));
    openCall(lib, makeCallbacks(log), { callId: 21 });
    await sleep(50);
    console.log('scenario: http 429 with error body');
    check('opens with 429 and body', log[0] && log[0][1] === 429 && log[0][2].includes('14.5s'), JSON.stringify(log));
    check('done after error body, no onError',
      log.filter((e) => e[0] === 'done').length === 1 && log.every((e) => e[0] !== 'error'),
      JSON.stringify(log));
  }

  // --- 3. network rejection -------------------------------------------------
  {
    const lib = loadLibrary();
    const log = [];
    globalThis.fetch = () => Promise.reject(new TypeError('Failed to fetch'));
    openCall(lib, makeCallbacks(log), { callId: 31 });
    await sleep(50);
    console.log('scenario: network rejection');
    check('open(0) with message', log.some((e) => e[0] === 'open' && e[1] === 0 && e[2].includes('Failed to fetch')), JSON.stringify(log));
    check('exactly one error', log.filter((e) => e[0] === 'error').length === 1, JSON.stringify(log));
  }

  // --- 4. synchronous fetch throw (invalid header) --------------------------
  {
    const lib = loadLibrary();
    const log = [];
    globalThis.fetch = () => { throw new TypeError('Invalid name'); };
    let escaped = false;
    console.log('scenario: synchronous fetch throw');
    try {
      openCall(lib, makeCallbacks(log), { callId: 41 });
    } catch (e) {
      escaped = true;
    }
    await sleep(20);
    check('exception does not escape into caller', !escaped);
    check('surfaces open(0) + error once',
      log.some((e) => e[0] === 'open' && e[1] === 0) && log.filter((e) => e[0] === 'error').length === 1,
      JSON.stringify(log));
  }

  // --- 5. abort mid-stream => silent (cancelled) -----------------------------
  {
    const lib = loadLibrary();
    const log = [];
    let rejectRead;
    const encoder = new TextEncoder();
    let sent = false;
    globalThis.fetch = (url, init) => Promise.resolve({
      ok: true, status: 200,
      headers: { forEach: () => {}, get: () => 'text/event-stream' },
      body: {
        getReader: () => ({
          read: () => {
            if (!sent) { sent = true; return Promise.resolve({ value: encoder.encode('data: x\n\n'), done: false }); }
            return new Promise((_, rej) => {
              rejectRead = rej;
              init.signal.addEventListener('abort', () => {
                const e = new Error('aborted'); e.name = 'AbortError'; rej(e);
              });
            });
          },
        }),
      },
    });
    openCall(lib, makeCallbacks(log), { callId: 51 });
    await sleep(30);
    lib.CoreAi_FetchSseAbort(51);
    await sleep(30);
    console.log('scenario: abort mid-stream');
    check('chunk delivered before abort', log.some((e) => e[0] === 'chunk'), JSON.stringify(log));
    check('no error/done after cancelled abort',
      log.every((e) => e[0] !== 'error') && log.every((e) => e[0] !== 'done'),
      JSON.stringify(log));
    check('state cleaned up', Object.keys(CoreAiSseFetchState.controllers).length === 0);
  }

  // --- 6. duplicate terminal events are swallowed ----------------------------
  {
    const lib = loadLibrary();
    const log = [];
    // Reader that errors twice: read rejects AND the fetch-level catch would also observe it if
    // the bridge mishandled state. Simulate by rejecting the first read with a non-abort error.
    globalThis.fetch = () => Promise.resolve({
      ok: true, status: 200,
      headers: { forEach: () => {}, get: () => 'text/event-stream' },
      body: {
        getReader: () => ({
          read: () => Promise.reject(new Error('network reset')),
        }),
      },
    });
    openCall(lib, makeCallbacks(log), { callId: 61 });
    await sleep(40);
    // Late duplicate: aborting after the error must not produce a second terminal callback.
    lib.CoreAi_FetchSseAbort(61);
    await sleep(20);
    console.log('scenario: duplicate terminal events');
    check('exactly one error total', log.filter((e) => e[0] === 'error').length === 1, JSON.stringify(log));
  }

  // --- 7. request headers reach fetch ----------------------------------------
  {
    const lib = loadLibrary();
    const log = [];
    let seenInit = null;
    globalThis.fetch = (url, init) => { seenInit = init; return Promise.resolve(sseResponse([])); };
    openCall(lib, makeCallbacks(log), {
      callId: 71,
      headers: 'Authorization:Bearer k\nContent-Type:application/json',
    });
    await sleep(30);
    console.log('scenario: request header passthrough');
    check('Authorization forwarded', seenInit && seenInit.headers.Authorization === 'Bearer k', JSON.stringify(seenInit && seenInit.headers));
    check('Content-Type forwarded', seenInit && seenInit.headers['Content-Type'] === 'application/json', JSON.stringify(seenInit && seenInit.headers));
    check('credentials omit + no-store', seenInit && seenInit.credentials === 'omit' && seenInit.cache === 'no-store');
  }

  // --- 8. timeout aborts with reason Timeout ---------------------------------
  {
    const lib = loadLibrary();
    const log = [];
    globalThis.fetch = (url, init) => new Promise((_, rej) => {
      init.signal.addEventListener('abort', () => {
        const e = new Error('aborted'); e.name = 'AbortError'; rej(e);
      });
    });
    openCall(lib, makeCallbacks(log), { callId: 81, timeoutSec: 0.05 });
    await sleep(150);
    console.log('scenario: transport timeout');
    check('surfaces Timeout as error (not silent cancel)',
      log.some((e) => e[0] === 'error' && e[1] === 'Timeout'),
      JSON.stringify(log));
  }

  // --- 9. body-inactivity watchdog (headers ok, then stall) -------------------
  {
    const lib = loadLibrary();
    const log = [];
    const encoder = new TextEncoder();
    let sent = false;
    globalThis.fetch = (url, init) => Promise.resolve({
      ok: true, status: 200,
      headers: { forEach: () => {}, get: () => 'text/event-stream' },
      body: {
        getReader: () => ({
          read: () => {
            if (!sent) { sent = true; return Promise.resolve({ value: encoder.encode('data: x\n\n'), done: false }); }
            // Stall forever; only the abort signal resolves this read.
            return new Promise((_, rej) => {
              init.signal.addEventListener('abort', () => {
                const e = new Error('aborted'); e.name = 'AbortError'; rej(e);
              });
            });
          },
        }),
      },
    });
    openCall(lib, makeCallbacks(log), { callId: 91, timeoutSec: 0.05 });
    await sleep(200);
    console.log('scenario: body-inactivity watchdog');
    check('chunk delivered before stall', log.some((e) => e[0] === 'chunk'), JSON.stringify(log));
    check('stalled body surfaces Timeout error',
      log.some((e) => e[0] === 'error' && e[1] === 'Timeout'),
      JSON.stringify(log));
    check('state cleaned up after timeout', Object.keys(CoreAiSseFetchState.controllers).length === 0);
  }

  console.log(failures === 0 ? '\nALL PASS' : `\n${failures} FAILURE(S)`);
  process.exit(failures === 0 ? 0 : 1);
}

run().catch((e) => { console.error('harness crashed:', e); process.exit(2); });
