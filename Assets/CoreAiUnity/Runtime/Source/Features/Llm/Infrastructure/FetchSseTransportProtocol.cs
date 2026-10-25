using System;
using System.Collections.Generic;
using System.Text;
using CoreAI.Ai;

namespace CoreAI.Infrastructure.Llm
{
    /// <summary>
    /// Pure protocol helpers shared with <c>FetchSseOpenAiTransport</c> (the WebGL fetch/SSE bridge).
    /// The transport itself only compiles for the WebGL player, so everything unit-testable lives
    /// here, guard-free: the C# side of the bridge wire format (newline-flattened header strings
    /// crossing the jslib boundary in both directions) and the cancel-message contract.
    /// </summary>
    internal static class FetchSseTransportProtocol
    {
        /// <summary>
        /// Flattens request headers into the "Name:value\n..." string the jslib bridge parses.
        /// Header names/values are sanitized (CR/LF stripped) so one bad value can neither break
        /// the line format nor make the browser's fetch throw synchronously on an invalid header.
        /// Always guarantees a Content-Type: without an explicit one the browser sends
        /// text/plain;charset=UTF-8, which some OpenAI-compatible servers tolerate (Groq) and
        /// others hard-reset ("Failed to fetch" from LM Studio's Express server).
        /// </summary>
        public static string BuildHeaderString(IReadOnlyList<KeyValuePair<string, string>> headers)
        {
            StringBuilder sb = new();
            bool hasContentType = false;
            if (headers != null)
            {
                foreach (KeyValuePair<string, string> h in headers)
                {
                    string name = SanitizeHeaderToken(h.Key);
                    if (name.Length == 0)
                    {
                        continue;
                    }

                    if (string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase))
                    {
                        hasContentType = true;
                    }

                    sb.Append(name).Append(':').Append(SanitizeHeaderToken(h.Value)).Append('\n');
                }
            }

            if (!hasContentType)
            {
                sb.Append("Content-Type:application/json\n");
            }

            return sb.ToString().TrimEnd('\n');
        }

        /// <summary>
        /// Parses the "name:value\n..." response-header string produced by the jslib bridge into a
        /// case-insensitive multimap. Malformed lines (no colon, empty name) are skipped.
        /// </summary>
        public static IReadOnlyDictionary<string, IEnumerable<string>> ParseFlatHeaders(string flat)
        {
            Dictionary<string, IEnumerable<string>> map = new(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(flat))
            {
                return map;
            }

            foreach (string line in flat.Split('\n'))
            {
                int idx = line.IndexOf(':');
                if (idx <= 0)
                {
                    continue;
                }

                string name = line.Substring(0, idx).Trim();
                string value = line.Substring(idx + 1).Trim();
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (map.TryGetValue(name, out IEnumerable<string> existing))
                {
                    List<string> list = existing as List<string> ?? new List<string>(existing);
                    list.Add(value);
                    map[name] = list;
                }
                else
                {
                    map[name] = new List<string> { value };
                }
            }

            return map;
        }

        /// <summary>
        /// Whether a bridge error message means "consumer cancelled" (mapped to
        /// <see cref="OperationCanceledException"/>) rather than a real transport failure.
        /// Must accept both spellings used by the jslib ("cancelled") and any host code ("canceled").
        /// </summary>
        public static bool IsCancelledMessage(string message)
        {
            return string.Equals(message, "cancelled", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(message, "canceled", StringComparison.OrdinalIgnoreCase);
        }

        internal static Exception CreateTransportException(string message)
        {
            if (IsCancelledMessage(message)) return new OperationCanceledException();
            if (string.Equals(message, "Timeout", StringComparison.OrdinalIgnoreCase))
                return new LlmClientException("SSE transport total timeout expired.", LlmErrorCode.Timeout);
            if (string.Equals(message, "BacklogOverflow", StringComparison.Ordinal))
                return new LlmClientException("SSE transport buffer capacity exceeded.", LlmErrorCode.BackendUnavailable);
            return new LlmClientException("SSE browser transport failed.", LlmErrorCode.BackendUnavailable);
        }

        private static string SanitizeHeaderToken(string raw)
        {
            if (string.IsNullOrEmpty(raw))
            {
                return "";
            }

            return raw.Replace("\r", "").Replace("\n", "").Trim();
        }
    }

    internal sealed class FetchSseBacklog
    {
        internal const int DefaultMaxBufferedBytes = 1024 * 1024;
        internal const int DefaultMaxBufferedChunks = 1024;
        private readonly object _gate = new object();
        private readonly Queue<byte[]> _chunks = new Queue<byte[]>();
        private readonly int _maxBytes;
        private readonly int _maxChunks;
        private byte[] _current;
        private int _position;
        private int _bufferedBytes;
        private int _bufferedChunks;

        internal FetchSseBacklog(int maxBytes, int maxChunks)
        {
            if (maxBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxBytes));
            if (maxChunks <= 0) throw new ArgumentOutOfRangeException(nameof(maxChunks));
            _maxBytes = maxBytes;
            _maxChunks = maxChunks;
        }

        internal int BufferedBytes { get { lock (_gate) return _bufferedBytes; } }
        internal int BufferedChunks { get { lock (_gate) return _bufferedChunks; } }

        internal bool TryEnqueue(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return true;
            lock (_gate)
            {
                int byteCount = Encoding.UTF8.GetByteCount(chunk);
                if (byteCount > _maxBytes - _bufferedBytes || _bufferedChunks >= _maxChunks) return false;
                _chunks.Enqueue(Encoding.UTF8.GetBytes(chunk));
                _bufferedBytes += byteCount;
                _bufferedChunks++;
                return true;
            }
        }

        internal int Read(byte[] buffer, int offset, int count, out int acknowledgedBytes)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset > buffer.Length - count)
                throw new ArgumentOutOfRangeException(nameof(count));
            acknowledgedBytes = 0;
            if (count == 0) return 0;
            lock (_gate)
            {
                if (_current == null)
                {
                    if (_chunks.Count == 0) return 0;
                    _current = _chunks.Dequeue();
                    _position = 0;
                }

                int copied = Math.Min(count, _current.Length - _position);
                Array.Copy(_current, _position, buffer, offset, copied);
                _position += copied;
                if (_position == _current.Length)
                {
                    acknowledgedBytes = _current.Length;
                    _bufferedBytes -= _current.Length;
                    _bufferedChunks--;
                    _current = null;
                    _position = 0;
                }

                return copied;
            }
        }

        internal void Clear()
        {
            lock (_gate)
            {
                _chunks.Clear();
                _current = null;
                _position = 0;
                _bufferedBytes = 0;
                _bufferedChunks = 0;
            }
        }
    }
}
