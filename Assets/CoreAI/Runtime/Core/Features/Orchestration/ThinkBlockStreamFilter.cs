using System;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Stateful stream filter that removes hidden <think> blocks from model output.
    /// </summary>
    public sealed class ThinkBlockStreamFilter
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";

        private readonly StringBuilder _buffer = new();
        private bool _insideThink;

        /// <summary>
        /// Показан ли уже хоть один видимый символ этого потока.
        /// <para>
        /// Одиночный <c>&lt;/think&gt;</c> без открывающего тега прячет текст перед собой только ДО
        /// первого видимого вывода: модели, которые стримят рассуждение без <c>&lt;think&gt;</c>,
        /// делают это в начале ответа. После того как текст уже на экране, прятать нечего — и
        /// удержанный остаток чанка тоже прятать нельзя, иначе результат зависит от того, где
        /// провайдер порезал поток: «Ответ: да.» + « Ещё &lt;» + «/think&gt;» терял « Ещё », а
        /// та же строка одним чанком — нет.
        /// </para>
        /// </summary>
        private bool _visibleShown;

        /// <summary>
        /// Optional sink receiving the hidden reasoning text the moment it is suppressed from the
        /// visible stream, so hosts can show a live "thinking" section instead of losing the span.
        /// Null (the default) preserves the original suppress-only behavior.
        /// </summary>
        public Action<string> ReasoningSink { get; set; }

        /// <summary>Clears buffered partial tags and exits any active hidden-thought block.</summary>
        public void Reset()
        {
            _buffer.Clear();
            _insideThink = false;
            _visibleShown = false;
        }

        /// <summary>Forwards one suppressed reasoning span to <see cref="ReasoningSink"/>, if set.</summary>
        private void EmitReasoning(string hidden)
        {
            if (!string.IsNullOrEmpty(hidden))
            {
                ReasoningSink?.Invoke(hidden);
            }
        }

        /// <summary>
        /// Processes one streaming text chunk and returns only text that is safe to display.
        /// </summary>
        /// <remarks>
        /// The filter preserves partial <c>&lt;think&gt;</c> and <c>&lt;/think&gt;</c> tags across
        /// chunk boundaries so hidden reasoning is not leaked when providers split tokens mid-tag.
        /// Удержанный хвост, который так и не стал тегом, отдаёт <see cref="Flush"/> — вызывать его
        /// в конце потока обязательно, иначе ответ «оператор сравнения: &lt;» теряет последний символ.
        /// </remarks>
        public string ProcessChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return string.Empty;
            }

            _buffer.Append(chunk);
            string buf = _buffer.ToString();
            StringBuilder visible = new();

            while (buf.Length > 0)
            {
                if (_insideThink)
                {
                    int closeIdx = buf.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0)
                    {
                        EmitReasoning(buf.Substring(0, closeIdx));
                        _insideThink = false;
                        buf = buf.Substring(closeIdx + CloseTag.Length);
                    }
                    else
                    {
                        // WHY: Keep only a possible closing-tag prefix; all other hidden text stays suppressed.
                        string keptTail = KeepTailForPossibleTag(buf, CloseTag);
                        EmitReasoning(buf.Substring(0, buf.Length - keptTail.Length));
                        _buffer.Clear();
                        _buffer.Append(keptTail);
                        return Publish(visible);
                    }
                }
                else
                {
                    int openIdx = buf.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
                    int closeIdx = buf.IndexOf(CloseTag, StringComparison.OrdinalIgnoreCase);
                    if (closeIdx >= 0 && (openIdx < 0 || closeIdx < openIdx))
                    {
                        string beforeClose = buf.Substring(0, closeIdx);
                        if (_visibleShown || visible.Length > 0)
                        {
                            // WHY: видимый текст уже пошёл ученику, значит это не рассуждение, а
                            // залётный тег: сам тег убираем, текст вокруг него — обычный ответ.
                            visible.Append(beforeClose);
                        }
                        else
                        {
                            // WHY: Some OpenAI-compatible reasoning models stream hidden thought text without
                            // the opening tag but still include </think> before the visible answer.
                            // Treat the buffered prefix as hidden and resume after the orphan close tag.
                            EmitReasoning(beforeClose);
                        }

                        buf = buf.Substring(closeIdx + CloseTag.Length);
                        continue;
                    }

                    if (openIdx >= 0)
                    {
                        if (openIdx > 0)
                        {
                            visible.Append(buf, 0, openIdx);
                        }

                        _insideThink = true;
                        buf = buf.Substring(openIdx + OpenTag.Length);
                    }
                    else
                    {
                        // WHY: Hold a possible tag until the next chunk proves whether it is real.
                        int lastLt = buf.LastIndexOf('<');
                        if (lastLt >= 0)
                        {
                            string possibleTag = buf.Substring(lastLt);
                            bool mayBecomeClose = IsPrefixOf(possibleTag, CloseTag);
                            bool mayBecomeOpen = IsPrefixOf(possibleTag, OpenTag);
                            if (mayBecomeClose && !_visibleShown && visible.Length == 0)
                            {
                                // WHY: ничего ещё не показано, и весь накопленный текст может оказаться
                                // рассуждением перед одиночным </think> — удерживаем его целиком.
                                _buffer.Clear();
                                _buffer.Append(buf);
                                return string.Empty;
                            }

                            if (mayBecomeClose || mayBecomeOpen)
                            {
                                if (lastLt > 0)
                                {
                                    visible.Append(buf, 0, lastLt);
                                }

                                _buffer.Clear();
                                _buffer.Append(possibleTag);
                                return Publish(visible);
                            }
                        }

                        visible.Append(buf);
                        buf = string.Empty;
                    }
                }
            }

            _buffer.Clear();
            return Publish(visible);
        }

        /// <summary>
        /// Returns any buffered visible tail at the end of a stream.
        /// </summary>
        /// <remarks>
        /// Хвост удерживался лишь потому, что МОГ стать тегом. Поток закончился — не стал, значит это
        /// обычный текст ответа: «2 &lt;» на конце реплики учителя Python ничем не хуже «2 &lt; 3».
        /// Прятать его как «недописанный тег» — терять законный символ ради случая, которого не было.
        /// </remarks>
        public string Flush()
        {
            if (_insideThink)
            {
                // WHY: End-of-stream inside an unclosed think block: the buffered tail is still
                // hidden reasoning (never visible), so surface it to the sink before dropping it.
                EmitReasoning(_buffer.ToString());
                _buffer.Clear();
                return string.Empty;
            }

            string tail = _buffer.ToString();
            _buffer.Clear();
            if (tail.Length > 0)
            {
                _visibleShown = true;
            }

            return tail;
        }

        /// <summary>Отдаёт накопленный видимый текст и запоминает, что ученик его уже увидел.</summary>
        private string Publish(StringBuilder visible)
        {
            if (visible.Length > 0)
            {
                _visibleShown = true;
            }

            return visible.ToString();
        }

        /// <summary>
        /// Keeps the longest suffix that may become the requested tag when the next chunk arrives.
        /// </summary>
        private static string KeepTailForPossibleTag(string buf, string tag)
        {
            int maxKeep = Math.Min(tag.Length - 1, buf.Length);
            for (int keep = maxKeep; keep > 0; keep--)
            {
                string tail = buf.Substring(buf.Length - keep);
                if (IsPrefixOf(tail, tag))
                {
                    return tail;
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// Case-insensitive ordinal prefix check for small protocol tags.
        /// </summary>
        private static bool IsPrefixOf(string candidate, string full)
        {
            if (string.IsNullOrEmpty(candidate) || candidate.Length > full.Length)
            {
                return false;
            }

            for (int i = 0; i < candidate.Length; i++)
            {
                char a = char.ToLowerInvariant(candidate[i]);
                char b = char.ToLowerInvariant(full[i]);
                if (a != b)
                {
                    return false;
                }
            }

            return true;
        }
    }
}
