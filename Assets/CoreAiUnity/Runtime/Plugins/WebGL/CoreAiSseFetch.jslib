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
    controllers: {}
  },

  CoreAi_FetchSseOpen__deps: ['$CoreAiSseFetchState'],
  CoreAi_FetchSseOpen: function (urlPtr, bodyPtr, headersPtr, timeoutSec, credentialsMode, callId, onOpenPtr, onChunkPtr, onDonePtr, onErrorPtr) {
    var url = UTF8ToString(urlPtr);
    var body = UTF8ToString(bodyPtr);
    var modeStr = UTF8ToString(credentialsMode);
    var credentials = modeStr === 'include' ? 'include' : modeStr === 'omit' ? 'omit' : 'same-origin';
    var controller = new AbortController();
    var timeoutId = setTimeout(function () { controller.abort(); }, timeoutSec * 1000);

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

    function flattenHeaders(h) {
      var out = '';
      try {
        h.forEach(function (v, k) { out += k + ':' + v + '\n'; });
      } catch (e) { /* older browsers */ }
      return out;
    }

    // Verbose lifecycle (uncomment for browser DevTools tracing):
    // console.log('[CoreAiSseFetch] open id=' + callId + ' POST ' + url +
    //             ' bodyBytes=' + body.length + ' creds=' + credentials +
    //             ' headers=' + Object.keys(headerObj).join(','));

    fetch(url, {
      method: 'POST',
      headers: headerObj,
      body: body,
      credentials: credentials,
      signal: controller.signal,
      cache: 'no-store'
    }).then(function (response) {
      clearTimeout(timeoutId);
      var status = response.status | 0;
      var hdrFlat = flattenHeaders(response.headers);
      // console.log('[CoreAiSseFetch] response id=' + callId + ' status=' + status +
      //             ' ok=' + response.ok + ' content-type=' + response.headers.get('content-type'));

      if (!response.ok) {
        response.text().then(function (errBody) {
          {{{ makeDynCall('viiii', 'onOpenPtr') }}}(callId, status, utf8(errBody || ''), utf8(hdrFlat));
          setTimeout(function () {
            {{{ makeDynCall('vi', 'onDonePtr') }}}(callId);
            delete CoreAiSseFetchState.controllers[callId];
          }, 0);
        }).catch(function () {
          {{{ makeDynCall('viiii', 'onOpenPtr') }}}(callId, status, utf8(''), utf8(hdrFlat));
          setTimeout(function () {
            {{{ makeDynCall('vi', 'onDonePtr') }}}(callId);
            delete CoreAiSseFetchState.controllers[callId];
          }, 0);
        });
        return;
      }

      // Headers received — let C# return its open result.
      {{{ makeDynCall('viiii', 'onOpenPtr') }}}(callId, status, utf8(''), utf8(hdrFlat));

      if (!response.body || typeof response.body.getReader !== 'function') {
        // Browsers without streaming body support: deliver the whole text at once.
        // C#'s SSE parser still works on a fully-buffered payload.
        response.text().then(function (txt) {
          if (txt && txt.length > 0) {
            {{{ makeDynCall('vii', 'onChunkPtr') }}}(callId, utf8(txt));
          }
          setTimeout(function () {
            {{{ makeDynCall('vi', 'onDonePtr') }}}(callId);
            delete CoreAiSseFetchState.controllers[callId];
          }, 0);
        }).catch(function (err) {
          var msg = err && err.message ? err.message : 'response.text() failed';
          {{{ makeDynCall('vii', 'onErrorPtr') }}}(callId, utf8(msg));
          delete CoreAiSseFetchState.controllers[callId];
        });
        return;
      }

      var reader = response.body.getReader();
      var decoder = new TextDecoder('utf-8');
      var chunkCount = 0;
      var totalBytes = 0;

      function pump() {
        reader.read().then(function (r) {
          if (r.value && r.value.byteLength > 0) {
            totalBytes += r.value.byteLength;
            var text = decoder.decode(r.value, { stream: true });
            if (text.length > 0) {
              chunkCount++;
              {{{ makeDynCall('vii', 'onChunkPtr') }}}(callId, utf8(text));
            }
          }

          if (r.done) {
            // Flush any partial multi-byte UTF-8 trailer.
            var tail = decoder.decode(new Uint8Array(0), { stream: false });
            if (tail && tail.length > 0) {
              chunkCount++;
              {{{ makeDynCall('vii', 'onChunkPtr') }}}(callId, utf8(tail));
            }
            // console.log('[CoreAiSseFetch] done id=' + callId +
            //             ' chunks=' + chunkCount + ' bytes=' + totalBytes);
            setTimeout(function () {
              {{{ makeDynCall('vi', 'onDonePtr') }}}(callId);
              delete CoreAiSseFetchState.controllers[callId];
            }, 0);
            return;
          }

          // Yield between reads so C# ReadAsync continuations run and the
          // browser can paint between bursts. Critical on single-threaded WebGL.
          setTimeout(pump, 0);
        }).catch(function (err) {
          clearTimeout(timeoutId);
          var msg = err && err.name === 'AbortError' ? 'Timeout' : (err && err.message ? err.message : 'fetch read error');
          console.warn('[CoreAiSseFetch] read-error id=' + callId + ' msg=' + msg);
          {{{ makeDynCall('vii', 'onErrorPtr') }}}(callId, utf8(msg));
          delete CoreAiSseFetchState.controllers[callId];
        });
      }

      // Defer the first read so C# finishes OpenSseResponseStreamAsync and
      // attaches its StreamReader before the first onChunk lands.
      setTimeout(pump, 0);
    }).catch(function (err) {
      clearTimeout(timeoutId);
      var msg = err && err.name === 'AbortError' ? 'Timeout' : (err && err.message ? err.message : 'fetch failed (CORS/network)');
      console.warn('[CoreAiSseFetch] fetch-rejected id=' + callId + ' msg=' + msg);
      {{{ makeDynCall('viiii', 'onOpenPtr') }}}(callId, 0, utf8(msg), utf8(''));
      {{{ makeDynCall('vii', 'onErrorPtr') }}}(callId, utf8(msg));
      delete CoreAiSseFetchState.controllers[callId];
    });
  },

  CoreAi_FetchSseAbort__deps: ['$CoreAiSseFetchState'],
  CoreAi_FetchSseAbort: function (callId) {
    var c = CoreAiSseFetchState.controllers[callId];
    if (c && c.abort) c.abort();
    delete CoreAiSseFetchState.controllers[callId];
  }
});
