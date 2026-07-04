// Browser-side SSE bridge for Unity WebGL.
//
// Design rule: this layer is a **byte pipe**. It does not parse SSE, JSON, or
// OpenAI deltas. It forwards the raw response body bytes (decoded as UTF-8
// text fragments) straight to C#, where the existing OpenAI SSE parser owns
// all framing, [DONE] handling, tool_calls, role, finish_reason, etc.
//
// Why: a JS-side parser that only extracts delta.content silently drops
// tool_calls and anything else, which presents as "deltas=1 then done" when
// the model invokes a tool. Keeping the JS side dumb avoids that failure mode
// and matches the canonical pattern used by Microsoft.SignalR's WebGL fetch
// transport and other production browser-fetch SSE bridges.

mergeInto(LibraryManager.library, {
  $CoreAiSseFetchState: {
    controllers: {},
    abortReasons: {}
  },

  CoreAi_FetchSseOpen__deps: ['$CoreAiSseFetchState', 'free'],
  CoreAi_FetchSseOpen: function (urlPtr, bodyPtr, headersPtr, timeoutSec, credentialsMode, callId, onOpenPtr, onChunkPtr, onDonePtr, onErrorPtr) {
    var url = UTF8ToString(urlPtr);
    var body = UTF8ToString(bodyPtr);
    var modeStr = UTF8ToString(credentialsMode);
    var credentials = modeStr === 'include' ? 'include' : modeStr === 'omit' ? 'omit' : 'same-origin';
    var controller = new AbortController();
    var timeoutId = timeoutSec > 0
      ? setTimeout(function () {
          CoreAiSseFetchState.abortReasons[callId] = 'Timeout';
          controller.abort();
        }, timeoutSec * 1000)
      : 0;
    // Rolling inactivity watchdog for the BODY phase: the header timeout above is cleared once
    // the response arrives, so without this a server that sends headers and then stalls mid-body
    // would hang the stream forever (C#'s Task.Delay-based idle timeout is not trusted on all
    // WebGL builds). Re-armed on every delivered read; keep-alive comments count as activity.
    var idleId = 0;
    function armIdleWatchdog() {
      if (timeoutSec <= 0) return;
      if (idleId) clearTimeout(idleId);
      idleId = setTimeout(function () {
        CoreAiSseFetchState.abortReasons[callId] = 'Timeout';
        controller.abort();
      }, timeoutSec * 1000);
    }

    CoreAiSseFetchState.controllers[callId] = controller;

    var headerObj = {};
    var hdrStr = UTF8ToString(headersPtr);
    if (hdrStr) {
      hdrStr.split('\n').forEach(function (pair) {
        var idx = pair.indexOf(':');
        if (idx > 0) headerObj[pair.substring(0, idx).trim()] = pair.substring(idx + 1).trim();
      });
    }

    function utf8(s) { return stringToNewUTF8(s == null ? '' : s); }
    // C# copies every marshaled string out synchronously (Marshal.PtrToStringUTF8 inside the
    // dynCall), so the temp allocation must be freed right after the call returns. Without the
    // _free, every chunk of every streamed response leaked on the wasm heap until tab OOM.
    function freeUtf8(p) { try { _free(p); } catch (e) { /* never throw into wasm */ } }

    function callOpen(status, errBody, hdrFlat, label) {
      var pBody = utf8(errBody || '');
      var pHdr = utf8(hdrFlat || '');
      try {
        {{{ makeDynCall('viiii', 'onOpenPtr') }}}(callId, status, pBody, pHdr);
        return true;
      } catch (callbackErr) {
        console.warn('[CoreAiSseFetch] ' + label + ' open callback failed id=' + callId, callbackErr);
        return false;
      } finally {
        freeUtf8(pBody);
        freeUtf8(pHdr);
      }
    }

    function callChunk(text, label) {
      var pText = utf8(text || '');
      try {
        {{{ makeDynCall('vii', 'onChunkPtr') }}}(callId, pText);
        return true;
      } catch (callbackErr) {
        console.warn('[CoreAiSseFetch] ' + label + ' chunk callback failed id=' + callId, callbackErr);
        return false;
      } finally {
        freeUtf8(pText);
      }
    }

    function callDone(label) {
      try {
        {{{ makeDynCall('vi', 'onDonePtr') }}}(callId);
        return true;
      } catch (callbackErr) {
        console.warn('[CoreAiSseFetch] ' + label + ' done callback failed id=' + callId, callbackErr);
        return false;
      }
    }

    function callError(msg, label) {
      var pMsg = utf8(msg || '');
      try {
        {{{ makeDynCall('vii', 'onErrorPtr') }}}(callId, pMsg);
        return true;
      } catch (callbackErr) {
        console.warn('[CoreAiSseFetch] ' + label + ' error callback failed id=' + callId, callbackErr);
        return false;
      } finally {
        freeUtf8(pMsg);
      }
    }

    function flattenHeaders(h) {
      var out = '';
      try {
        h.forEach(function (v, k) { out += k + ':' + v + '\n'; });
      } catch (e) { /* older browsers */ }
      return out;
    }

    // Terminal-state guard: once the call has reported done/error, later callbacks are dropped so
    // C# never sees open-after-done or a second error for the same callId (double rejection paths
    // exist: reader.read() catch AND fetch() catch can both observe the same AbortError).
    var finished = false;
    function finish(label) {
      if (finished) {
        return false;
      }
      finished = true;
      if (idleId) clearTimeout(idleId);
      delete CoreAiSseFetchState.abortReasons[callId];
      delete CoreAiSseFetchState.controllers[callId];
      return true;
    }

    // Verbose lifecycle (uncomment for browser DevTools tracing):
    // console.log('[CoreAiSseFetch] open id=' + callId + ' POST ' + url +
    //             ' bodyBytes=' + body.length + ' creds=' + credentials +
    //             ' headers=' + Object.keys(headerObj).join(','));

    var fetchPromise;
    try {
      fetchPromise = fetch(url, {
        method: 'POST',
        headers: headerObj,
        body: body,
        credentials: credentials,
        signal: controller.signal,
        cache: 'no-store'
      });
    } catch (syncErr) {
      // fetch() throws SYNCHRONOUSLY on invalid header names/values or a malformed URL. Without
      // this catch the JS exception escapes straight into the wasm caller and kills the call
      // stack instead of surfacing as a normal typed transport error.
      if (timeoutId) clearTimeout(timeoutId);
      var syncMsg = syncErr && syncErr.message ? syncErr.message : 'fetch failed (invalid request)';
      console.warn('[CoreAiSseFetch] fetch-sync-throw id=' + callId + ' msg=' + syncMsg);
      if (finish('fetch-sync-throw')) {
        callOpen(0, syncMsg, '', 'fetch-sync-throw');
        callError(syncMsg, 'fetch-sync-throw');
      }
      return;
    }

    fetchPromise.then(function (response) {
      if (timeoutId) clearTimeout(timeoutId);
      var status = response.status | 0;
      var hdrFlat = flattenHeaders(response.headers);
      // console.log('[CoreAiSseFetch] response id=' + callId + ' status=' + status +
      //             ' ok=' + response.ok + ' content-type=' + response.headers.get('content-type'));

      if (!response.ok) {
        response.text().then(function (errBody) {
          if (!finish('http-error')) return;
          callOpen(status, errBody || '', hdrFlat, 'http-error');
          setTimeout(function () { callDone('http-error'); }, 0);
        }).catch(function () {
          if (!finish('http-error-body')) return;
          callOpen(status, '', hdrFlat, 'http-error-body');
          setTimeout(function () { callDone('http-error-body'); }, 0);
        });
        return;
      }

      // Headers received - let C# return its open result.
      callOpen(status, '', hdrFlat, 'response');

      if (!response.body || typeof response.body.getReader !== 'function') {
        // Browsers without streaming body support: deliver the whole text at once.
        // C#'s SSE parser still works on a fully-buffered payload.
        response.text().then(function (txt) {
          if (!finish('buffered-response')) return;
          if (txt && txt.length > 0) {
            callChunk(txt, 'buffered-response');
          }
          setTimeout(function () { callDone('buffered-response'); }, 0);
        }).catch(function (err) {
          if (!finish('buffered-response-error')) return;
          var msg = err && err.message ? err.message : 'response.text() failed';
          callError(msg, 'buffered-response');
        });
        return;
      }

      var reader = response.body.getReader();
      var decoder = new TextDecoder('utf-8');
      var chunkCount = 0;
      var totalBytes = 0;

      function pump() {
        reader.read().then(function (r) {
          armIdleWatchdog();
          if (r.value && r.value.byteLength > 0) {
            totalBytes += r.value.byteLength;
            var text = decoder.decode(r.value, { stream: true });
            if (text.length > 0) {
              chunkCount++;
              callChunk(text, 'stream');
            }
          }

          if (r.done) {
            // Flush any partial multi-byte UTF-8 trailer.
            var tail = decoder.decode(new Uint8Array(0), { stream: false });
            if (tail && tail.length > 0) {
              chunkCount++;
              callChunk(tail, 'stream-tail');
            }
            // console.log('[CoreAiSseFetch] done id=' + callId +
            //             ' chunks=' + chunkCount + ' bytes=' + totalBytes);
            if (!finish('stream-done')) return;
            setTimeout(function () { callDone('stream'); }, 0);
            return;
          }

          // Yield between reads so C# ReadAsync continuations run and the
          // browser can paint between bursts. Critical on single-threaded WebGL.
          setTimeout(pump, 0);
        }).catch(function (err) {
          if (timeoutId) clearTimeout(timeoutId);
          var reason = CoreAiSseFetchState.abortReasons[callId];
          var msg = err && err.name === 'AbortError'
            ? (reason || 'cancelled')
            : (err && err.message ? err.message : 'fetch read error');
          if (!finish('read-error')) return;
          if (msg !== 'cancelled') console.warn('[CoreAiSseFetch] read-error id=' + callId + ' msg=' + msg);
          if (msg !== 'cancelled') {
            callError(msg, 'read-error');
          }
        });
      }

      // Defer the first read so C# finishes OpenSseResponseStreamAsync and
      // attaches its StreamReader before the first onChunk lands.
      armIdleWatchdog();
      setTimeout(pump, 0);
    }).catch(function (err) {
      if (timeoutId) clearTimeout(timeoutId);
      var reason = CoreAiSseFetchState.abortReasons[callId];
      var msg = err && err.name === 'AbortError'
        ? (reason || 'cancelled')
        : (err && err.message ? err.message : 'fetch failed (CORS/network)');
      if (!finish('fetch-rejected')) return;
      if (msg !== 'cancelled') console.warn('[CoreAiSseFetch] fetch-rejected id=' + callId + ' msg=' + msg);
      if (msg !== 'cancelled') {
        callOpen(0, msg, '', 'fetch-rejected');
        callError(msg, 'fetch-rejected');
      }
    });
  },

  CoreAi_FetchSseSelfTest__deps: ['$CoreAiSseFetchState', 'free'],
  CoreAi_FetchSseSelfTest: function (callId, payloadPtr, onChunkPtr, onDonePtr, onErrorPtr) {
    var payload = UTF8ToString(payloadPtr);

    function utf8(s) { return stringToNewUTF8(s == null ? '' : s); }
    function freeUtf8(p) { try { _free(p); } catch (e) { /* never throw into wasm */ } }

    function callChunk(text) {
      var pText = utf8(text || '');
      try {
        {{{ makeDynCall('vii', 'onChunkPtr') }}}(callId, pText);
        return true;
      } catch (callbackErr) {
        console.warn('[CoreAiSseFetch] self-test chunk callback failed id=' + callId, callbackErr);
        return false;
      } finally {
        freeUtf8(pText);
      }
    }

    function callDone() {
      try {
        {{{ makeDynCall('vi', 'onDonePtr') }}}(callId);
        return true;
      } catch (callbackErr) {
        console.warn('[CoreAiSseFetch] self-test done callback failed id=' + callId, callbackErr);
        return false;
      }
    }

    function callError(msg) {
      var pMsg = utf8(msg || '');
      try {
        {{{ makeDynCall('vii', 'onErrorPtr') }}}(callId, pMsg);
      } catch (callbackErr) {
        console.warn('[CoreAiSseFetch] self-test error callback failed id=' + callId, callbackErr);
      } finally {
        freeUtf8(pMsg);
      }
    }

    setTimeout(function () {
      if (!callChunk(payload)) {
        callError('self-test chunk callback failed');
        return;
      }

      setTimeout(function () {
        if (!callDone()) {
          callError('self-test done callback failed');
        }
      }, 0);
    }, 0);
  },

  CoreAi_FetchSseAbort__deps: ['$CoreAiSseFetchState'],
  CoreAi_FetchSseAbort: function (callId) {
    var c = CoreAiSseFetchState.controllers[callId];
    if (!c) {
      // Call already finished (finish() removed it): recording a reason here would leak one
      // abortReasons entry per completed request, since no catch path runs again to delete it.
      return;
    }
    CoreAiSseFetchState.abortReasons[callId] = 'cancelled';
    setTimeout(function () {
      try {
        if (c && c.abort) c.abort();
      } catch (err) {
        console.warn('[CoreAiSseFetch] abort failed id=' + callId, err);
      }
    }, 0);
  }
});
