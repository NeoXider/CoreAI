mergeInto(LibraryManager.library, {
  CoreAi_FetchSseOpen: function (urlPtr, bodyPtr, headersPtr, timeoutSec, credentialsMode, callId, onChunkPtr, onDonePtr, onErrorPtr) {
    const url = UTF8ToString(urlPtr);
    const body = UTF8ToString(bodyPtr);
    const credentials = UTF8ToString(credentialsMode) === 'include' ? 'include' : 'same-origin';
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), timeoutSec * 1000);

    const headerPairs = [];
    let hdrStr = UTF8ToString(headersPtr);
    if (hdrStr) {
      hdrStr.split('\n').forEach(pair => {
        const idx = pair.indexOf(':');
        if (idx > 0) headerPairs.push(pair.substring(0, idx).trim(), pair.substring(idx + 1).trim());
      });
    }

    const headerObj = {};
    for (let i = 0; i < headerPairs.length; i += 2) {
      headerObj[headerPairs[i]] = headerPairs[i + 1];
    }

    fetch(url, {
      method: 'POST',
      headers: headerObj,
      body: body,
      credentials: credentials,
      signal: controller.signal
    }).then(response => {
      clearTimeout(timeoutId);
      if (!response.ok) {
        const errText = `HTTP ${response.status} ${response.statusText}`;
        {{{ MakeDynCall('vii', 'onErrorPtr') }}}(callId, stringToNewUTF8(errText));
        return;
      }

      const reader = response.body.getReader();
      const decoder = new TextDecoder('utf-8');
      let buffer = '';

      function read() {
        reader.read().then(({ done, value }) => {
          if (done) {
            if (buffer.trim()) {
              {{{ MakeDynCall('vii', 'onChunkPtr') }}}(callId, stringToNewUTF8(buffer));
            }
            {{{ MakeDynCall('vi', 'onDonePtr') }}}(callId);
            return;
          }

          buffer += decoder.decode(value, { stream: true });
          let lines = buffer.split('\n');
          buffer = lines.pop();

          lines.forEach(line => {
            if (line.startsWith('data:')) {
              let data = line.substring(5).trim();
              if (data === '[DONE]') return;
              try {
                const json = JSON.parse(data);
                const delta = json?.choices?.[0]?.delta?.content;
                if (delta) {
                  {{{ MakeDynCall('vii', 'onChunkPtr') }}}(callId, stringToNewUTF8(delta));
                }
              } catch (e) { /* ignore parse errors */ }
            }
          });

          read();
        }).catch(err => {
          clearTimeout(timeoutId);
          const msg = err.name === 'AbortError' ? 'Timeout' : err.message;
          {{{ MakeDynCall('vii', 'onErrorPtr') }}}(callId, stringToNewUTF8(msg));
        });
      }

      read();
    }).catch(err => {
      clearTimeout(timeoutId);
      {{{ MakeDynCall('vii', 'onErrorPtr') }}}(callId, stringToNewUTF8(err.message));
    });

    return controller; // Return controller for abort
  },

  CoreAi_FetchSseAbort: function (controller) {
    if (controller && controller.abort) controller.abort();
  }
});
