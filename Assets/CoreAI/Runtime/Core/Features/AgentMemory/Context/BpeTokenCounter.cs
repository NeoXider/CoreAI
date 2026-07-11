using System;
using System.Collections.Generic;
using System.IO;

namespace CoreAI.Ai
{
    /// <summary>
    /// Real byte-level BPE token counter for OpenAI-family models, with automatic graceful fallback
    /// to a heuristic estimator. When the model maps to a known encoding (cl100k_base / o200k_base)
    /// AND the corresponding tiktoken rank data can be loaded, this returns exact token counts;
    /// otherwise it delegates to an <see cref="ITokenEstimator"/> (the calibrating estimator).
    /// </summary>
    /// <remarks>
    /// <para><b>Activating real BPE.</b> The merge-rank tables are ~100k entries and cannot be
    /// hand-written, so they are loaded at runtime through an <see cref="IBpeRanksProvider"/>. The
    /// host supplies the data; the portable core never references Unity APIs.</para>
    ///
    /// <para><b>Expected data files (one manual step for the maintainer).</b> Drop the official
    /// tiktoken rank files into the host's resource location and have the provider stream them:</para>
    /// <list type="bullet">
    ///   <item><description><c>cl100k_base.tiktoken</c> — for gpt-4 / gpt-3.5 / text-embedding-3 / ada-002</description></item>
    ///   <item><description><c>o200k_base.tiktoken</c> — for gpt-4o / gpt-4.1 / o1 / o3 / o4 / gpt-5</description></item>
    /// </list>
    /// <para>For the bundled Unity host, the recommended path is
    /// <c>Assets/CoreAI/Runtime/Resources/Tokenizers/cl100k_base.tiktoken.bytes</c> and
    /// <c>...o200k_base.tiktoken.bytes</c> (the <c>.bytes</c> suffix makes Unity import them as
    /// <c>TextAsset</c>), loaded via <c>Resources.Load&lt;TextAsset&gt;("Tokenizers/cl100k_base.tiktoken")</c>
    /// in a Unity-side <see cref="IBpeRanksProvider"/> adapter that wraps the bytes in a
    /// <see cref="MemoryStream"/>. <b>These data files are NOT bundled in this repo and must be added
    /// to activate real BPE.</b> Until then every call falls back to the estimator — counts stay
    /// reasonable, never wrong-by-throwing.</para>
    ///
    /// <para><b>File format.</b> Standard tiktoken: one line per merge, "&lt;base64 token bytes&gt;
    /// &lt;integer rank&gt;". See <see cref="IBpeRanksProvider"/>.</para>
    ///
    /// <para><b>Fallback conditions</b> (all automatic, never throw): unknown/empty model name;
    /// no ranks provider data for the encoding; corrupt/partial data; regex or IO failure on an
    /// AOT/WebGL platform. Encoders are loaded lazily and cached per encoding; a failed load is
    /// remembered so it is not retried on every call.</para>
    /// </remarks>
    public sealed class BpeTokenCounter : ITokenCounter
    {
        private readonly IBpeRanksProvider _ranksProvider;
        private readonly ITokenEstimator _fallback;
        private readonly IReadOnlyDictionary<string, int> _specialTokens;
        private readonly object _lock = new();

        // null value = load attempted and failed; missing key = not yet attempted.
        private readonly Dictionary<BpeEncoding, BpeEncoder> _encoders = new();

        /// <param name="ranksProvider">
        /// Host adapter that streams tiktoken rank files. Null disables real BPE (always fallback).
        /// </param>
        /// <param name="fallback">
        /// Heuristic estimator used whenever real BPE is unavailable. Defaults to a fresh
        /// <see cref="CalibratingTokenEstimator"/> so the universal estimator stays the fallback.
        /// </param>
        /// <param name="specialTokens">
        /// Optional override of special tokens (defaults to the standard &lt;|endoftext|&gt;).
        /// </param>
        public BpeTokenCounter(
            IBpeRanksProvider ranksProvider = null,
            ITokenEstimator fallback = null,
            IReadOnlyDictionary<string, int> specialTokens = null)
        {
            _ranksProvider = ranksProvider ?? NullBpeRanksProvider.Instance;
            _fallback = fallback ?? new CalibratingTokenEstimator();
            _specialTokens = specialTokens;
        }

        /// <summary>The estimator this counter falls back to (exposed for diagnostics/tests).</summary>
        public ITokenEstimator Fallback => _fallback;

        /// <inheritdoc />
        public int CountTokens(string text, string modelName)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            BpeEncoding encoding = BpeEncodingResolver.Resolve(modelName);
            if (encoding == BpeEncoding.Unknown)
            {
                return _fallback.EstimateText(text);
            }

            BpeEncoder encoder = GetEncoder(encoding);
            if (encoder == null)
            {
                return _fallback.EstimateText(text);
            }

            try
            {
                return encoder.CountTokens(text);
            }
            catch
            {
                // Defensive: any unexpected runtime failure still yields a usable count.
                return _fallback.EstimateText(text);
            }
        }

        private BpeEncoder GetEncoder(BpeEncoding encoding)
        {
            lock (_lock)
            {
                if (_encoders.TryGetValue(encoding, out BpeEncoder cached))
                {
                    return cached; // may be null = known-failed load
                }

                BpeEncoder loaded = LoadEncoder(encoding);
                _encoders[encoding] = loaded;
                return loaded;
            }
        }

        private BpeEncoder LoadEncoder(BpeEncoding encoding)
        {
            Stream stream = null;
            try
            {
                stream = _ranksProvider.OpenRanks(encoding);
                if (stream == null)
                {
                    return null;
                }

                return BpeEncoder.TryLoad(encoding, stream, _specialTokens);
            }
            catch
            {
                return null;
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }
}
