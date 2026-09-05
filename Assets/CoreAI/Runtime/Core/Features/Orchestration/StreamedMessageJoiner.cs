using System.Text;

namespace CoreAI.Ai
{
    /// <summary>
    /// Собирает текст одного потока в связный ответ, разделяя РАЗНЫЕ реплики ассистента пустой строкой.
    /// <para>
    /// Один поток несёт несколько реплик: после каждого раунда инструментов модель говорит заново, но
    /// наружу это уезжает непрерывной чередой чанков. Пока в контракте не было
    /// <see cref="LlmStreamChunk.StartsNewMessage"/>, накопители склеивали конец одной реплики с
    /// началом другой встык, и на проде ученик читал «…Проверь себя:<b>Ход завершён — ждём ответ
    /// ученика на карточке.</b>» — двоеточие вплотную к заглавной букве.
    /// </para>
    /// <para>
    /// <b>Правило живёт здесь и только здесь.</b> Оно нужно трём накопителям сразу — оркестратору
    /// (история чата и <c>ApplyAiGameCommand</c>), панели чата (полный текст ответа) и потребителям
    /// вне CoreAI. Когда каждый нёс свою копию, копии разошлись за один день: две считали накопитель
    /// из одних пробелов «уже разделённым», третья дописывала в него пустую строку. Расхождение в
    /// таком правиле не падает тестом, а тихо меняет то, что читает ребёнок.
    /// </para>
    /// <para>
    /// Граница берётся ТОЛЬКО из признака в чанке. Угадывать её по пунктуации нельзя: реплика вправе
    /// закончиться двоеточием и вправе начаться со строчной буквы, поэтому любая эвристика ошибается
    /// в обе стороны — и делает дефект невоспроизводимым.
    /// </para>
    /// </summary>
    public static class StreamedMessageJoiner
    {
        /// <summary>Реплики разделяет пустая строка: это абзац markdown, а не просто перенос.</summary>
        public const int SeparatorNewlines = 2;

        /// <summary>
        /// Дописывает очередной кусок текста к накопителю-строке. Без признака границы ведёт себя ровно
        /// как прежняя конкатенация, поэтому подстановка безопасна на любом старом вызове.
        /// </summary>
        public static string Append(string accumulated, string text, bool startsNewMessage)
        {
            if (string.IsNullOrEmpty(text))
            {
                return accumulated ?? string.Empty;
            }

            if (string.IsNullOrEmpty(accumulated))
            {
                return text;
            }

            return startsNewMessage
                ? accumulated + SeparatorFor(accumulated) + text
                : accumulated + text;
        }

        /// <summary>Тот же контракт для накопителя-построителя.</summary>
        public static void Append(StringBuilder accumulated, LlmStreamChunk chunk)
        {
            if (accumulated == null || chunk == null || string.IsNullOrEmpty(chunk.Text))
            {
                return;
            }

            if (chunk.StartsNewMessage && accumulated.Length > 0)
            {
                // Снимок строки здесь не расточительство: граница случается раз на раунд инструментов,
                // а не на каждый чанк. Цена — одно копирование хвоста хода; выигрыш — правило
                // разделения не продублировано ещё раз под StringBuilder и не может разойтись.
                accumulated.Append(SeparatorFor(accumulated.ToString()));
            }

            accumulated.Append(chunk.Text);
        }

        /// <summary>
        /// Чего не хватает до пустой строки. Дописывается ровно недостающее: реплика, уже
        /// закончившаяся абзацем, не получает третьей пустой строки.
        /// </summary>
        private static string SeparatorFor(string accumulated) =>
            new('\n', SeparatorNewlines - TrailingNewlines(accumulated));

        /// <summary>
        /// Сколько переводов строки уже стоит в хвосте; пробелы, табы и <c>\r</c> хвост не прерывают.
        /// Накопитель без содержательного текста считается «уже разделённым» — разделитель перед
        /// первой репликой добавил бы только пустоту.
        /// </summary>
        private static int TrailingNewlines(string accumulated)
        {
            int newlines = 0;
            for (int i = accumulated.Length - 1; i >= 0; i--)
            {
                char symbol = accumulated[i];
                if (symbol == '\n')
                {
                    newlines++;
                    if (newlines >= SeparatorNewlines)
                    {
                        return newlines;
                    }

                    continue;
                }

                if (symbol == '\r' || symbol == ' ' || symbol == '\t')
                {
                    continue;
                }

                return newlines;
            }

            return SeparatorNewlines;
        }
    }
}
