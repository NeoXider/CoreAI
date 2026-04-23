using System;
using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// State machine для фильтрации <c>&lt;think&gt;...&lt;/think&gt;</c> блоков
    /// в стриминговом потоке LLM. Учитывает, что тег может быть разбит
    /// между несколькими чанками.
    /// <para>
    /// Использование: создайте один экземпляр на запрос, вызывайте
    /// <see cref="ProcessChunk"/> для каждого входящего чанка.
    /// После окончания стрима вызовите <see cref="Flush"/>, чтобы получить
    /// возможно забуферизованный хвост (например, если модель оборвала
    /// ответ внутри <c>&lt;think&gt;</c>).
    /// </para>
    /// </summary>
    public sealed class ThinkBlockStreamFilter
    {
        private const string OpenTag = "<think>";
        private const string CloseTag = "</think>";

        private readonly StringBuilder _buffer = new();
        private bool _insideThink;

        /// <summary>Сбросить состояние фильтра для переиспользования.</summary>
        public void Reset()
        {
            _buffer.Clear();
            _insideThink = false;
        }

        /// <summary>
        /// Обработать очередной чанк и вернуть видимую часть (может быть пустой,
        /// если чанк целиком внутри think-блока или буферизуется как
        /// неполный тег).
        /// </summary>
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
                        _insideThink = false;
                        buf = buf.Substring(closeIdx + CloseTag.Length);
                    }
                    else
                    {
                        // Остаёмся внутри think-блока — возможно </think> ещё не пришёл.
                        // Храним минимально необходимый хвост для детекта закрывающего тега.
                        _buffer.Clear();
                        _buffer.Append(KeepTailForPossibleTag(buf, CloseTag));
                        return visible.ToString();
                    }
                }
                else
                {
                    int openIdx = buf.IndexOf(OpenTag, StringComparison.OrdinalIgnoreCase);
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
                        // Нет открывающего тега целиком — проверяем, не начало ли это неполного <think>.
                        int lastLt = buf.LastIndexOf('<');
                        if (lastLt >= 0)
                        {
                            string possibleTag = buf.Substring(lastLt);
                            if (IsPrefixOf(possibleTag, OpenTag))
                            {
                                if (lastLt > 0)
                                {
                                    visible.Append(buf, 0, lastLt);
                                }

                                _buffer.Clear();
                                _buffer.Append(possibleTag);
                                return visible.ToString();
                            }
                        }

                        visible.Append(buf);
                        buf = string.Empty;
                    }
                }
            }

            _buffer.Clear();
            return visible.ToString();
        }

        /// <summary>
        /// Вызвать после завершения стрима. Возвращает оставшийся буфер,
        /// если модель оборвала ответ вне think-блока (например, неполный тег
        /// был последним чанком). Содержимое внутри незакрытого think-блока
        /// не возвращается.
        /// </summary>
        public string Flush()
        {
            if (_insideThink)
            {
                _buffer.Clear();
                return string.Empty;
            }

            if (_buffer.Length == 0)
            {
                return string.Empty;
            }

            string tail = _buffer.ToString();
            _buffer.Clear();

            // Если хвост — это начало <think>, значит тег не завершился
            // и пришёл мусорный префикс; скрываем его.
            return IsPrefixOf(tail, OpenTag) ? string.Empty : tail;
        }

        /// <summary>
        /// Оставить в буфере минимальный хвост, который может быть началом
        /// искомого тега (например, "<" или "&lt;/th" для &lt;/think&gt;).
        /// Всё, что гарантированно не может быть префиксом тега, «сливается»
        /// обратно в think-блок (т.е. отбрасывается).
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
        /// Проверка: является ли <paramref name="candidate"/> префиксом
        /// <paramref name="full"/> (регистронезависимо).
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
