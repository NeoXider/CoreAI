using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEngine;

namespace CoreAI.Tests.EditMode
{
    /// <summary>
    /// Запрещает в коде, который попадает в Unity WebGL-плеер, асинхронные примитивы, которые там не
    /// работают вовсе. Зона проверки — переносимое ядро <c>Assets/CoreAI/Runtime</c> и Unity-слой
    /// <c>Assets/CoreAiUnity/Runtime</c>.
    /// <para>
    /// <b>Зачем страж, а не «помнить».</b> В WebGL нет ни пула потоков, ни <c>System.Threading.Timer</c>.
    /// Продолжение, отданное пулу (<c>RunContinuationsAsynchronously</c>, <c>Task.Run</c>), там не
    /// выполняется никогда, а <c>CancelAfter</c>/<c>Task.Delay</c> просто не срабатывают. Отказ при этом
    /// БЕЗМОЛВНЫЙ: не исключение, а вечное ожидание. Этот класс дефектов чинился в репозитории уже трижды
    /// (транспорт SSE, таймауты запроса, дренаж и per-call таймаут инструментов) и каждый раз возвращался,
    /// потому что ловить его было нечем.
    /// </para>
    /// <para>
    /// <b>Чего страж не заменяет.</b> Анализатор CAIU001 — про <c>ConfigureAwait</c>, а
    /// <see cref="CoreAiWebGlAsyncGuardEditModeTests"/> — про <c>UniTask.SwitchToThreadPool</c> и одну
    /// конкретную петлю в <c>LlmClientRegistry</c>. Ни один из них эти четыре примитива не видит.
    /// </para>
    /// <para>
    /// <b>Что нарушением не считается.</b> Комментарии и строковые литералы вырезаются перед сканом, а
    /// ветки препроцессора, недостижимые в WebGL-плеере (<c>#if UNITY_EDITOR</c>, <c>#else</c> к
    /// <c>#if UNITY_WEBGL &amp;&amp; !UNITY_EDITOR</c> и подобные), пропускаются целиком: явная
    /// платформенная развилка — это и есть правильное решение, а не нарушение.
    /// </para>
    /// <para>
    /// Исключения вносятся в <see cref="Allowlist"/> осознанно и с причиной; список заморожен, каждый
    /// новый пункт — это отдельная задача, а не строчка в словаре.
    /// </para>
    /// </summary>
    [TestFixture]
    public sealed class WebGlUnsafeAsyncPrimitivesEditModeTests
    {
        /// <summary>Примитив, мёртвый в WebGL-плеере. Ключ исключений — идентификатор, а не текст регулярки.</summary>
        private enum Primitive
        {
            PoolContinuations,
            CancelAfter,
            TaskDelay,
            TaskRun
        }

        private static readonly (Primitive Id, Regex Rx, string Why)[] Forbidden =
        {
            (Primitive.PoolContinuations,
                new Regex(@"\bRunContinuationsAsynchronously\b", RegexOptions.Compiled),
                "запрещает инлайн-возобновление, продолжение уходит в пул потоков — в WebGL его нет"),
            (Primitive.CancelAfter,
                new Regex(@"\.CancelAfter\s*\(", RegexOptions.Compiled),
                "опирается на System.Threading.Timer — в WebGL не срабатывает, дедлайн становится фикцией"),
            (Primitive.TaskDelay,
                new Regex(@"\bTask\.Delay\s*\(", RegexOptions.Compiled),
                "тот же таймер; задержку обязан планировать хост (ILlmAsyncMarshaler.DelayAsync) или UniTask.Delay"),
            (Primitive.TaskRun,
                new Regex(@"\bTask\.Run\s*\(", RegexOptions.Compiled),
                "требует пула потоков, которого в WebGL нет")
        };

        private static readonly string[] ScannedRoots =
        {
            "Assets/CoreAI/Runtime",
            "Assets/CoreAiUnity/Runtime"
        };

        /// <summary>
        /// Замороженные исключения: «файл + примитив → причина». Ключ — идентификатор примитива, а не
        /// текст регулярки, чтобы правка шаблона не превращала весь список в мёртвый.
        /// <para>
        /// Это НЕ индульгенция «здесь и так было»: перечисленные места унаследованы и на WebGL не
        /// проверялись. Список заморожен ради того, чтобы новый код не добавлял к ним ещё ФАЙЛОВ, а каждый
        /// пункт разбирался отдельной задачей. Убирать пункт можно только вместе с переводом файла на
        /// host-scheduled задержку либо на явную платформенную развилку.
        /// </para>
        /// <para>
        /// <b>Граница защиты.</b> Счётчика вхождений тут нет, поэтому одна запись накрывает файл целиком по
        /// данному примитиву: ещё одно такое же вхождение, добавленное в уже перечисленный файл, страж
        /// пропустит. Это осознанный размен — счётчик краснел бы на каждой правке строк выше по файлу.
        /// Гарантия стража формулируется так: новых вхождений в ЧИСТЫХ файлах он не пропустит.
        /// </para>
        /// </summary>
        private static readonly Dictionary<(string Path, Primitive Id), string> Allowlist = new()
        {
            [("Assets/CoreAI/Runtime/Core/ILlmAsyncMarshaler.cs", Primitive.TaskDelay)] =
                "здесь Task.Delay — это и есть переносимая реализация хука DelayAsync по умолчанию; " +
                "хост с кадровым циклом её переопределяет",
            [("Assets/CoreAI/Runtime/Core/Features/Llm/TimeoutLlmClientDecorator.cs", Primitive.CancelAfter)] =
                "унаследовано: таймаут LLM-запроса целиком; перевод на host-scheduled задержку — отдельная задача",
            [("Assets/CoreAI/Runtime/Core/Features/Llm/HttpClientOpenAiTransport.cs", Primitive.CancelAfter)] =
                "унаследовано: не-WebGL транспорт (в браузере работает FetchSseOpenAiTransport)",
            [("Assets/CoreAI/Runtime/Core/Features/Llm/MeaiOpenAiChatClient.cs", Primitive.TaskDelay)] =
                "унаследовано: таймаут чтения потока и пауза между ретраями, на WebGL не проверялись",
            [("Assets/CoreAI/Runtime/Core/Features/Llm/RetryingStreamingLlmClientDecorator.cs", Primitive.TaskDelay)] =
                "унаследовано: backoff между попытками стрима, на WebGL не проверялся",
            [("Assets/CoreAI/Runtime/Core/Features/Llm/LoggingLlmClientDecorator.cs", Primitive.TaskDelay)] =
                "унаследовано: backoff ретраев; WebGL-ветка там есть, но задержка в ней та же таймерная",
            [("Assets/CoreAI/Runtime/Core/Features/Llm/WaitLlmTool.cs", Primitive.TaskDelay)] =
                "унаследовано: инструмент «подожди», смысл которого и есть задержка",
            [("Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs", Primitive.PoolContinuations)] =
                "унаследовано: очередь задач оркестратора, на WebGL не проверялась",
            [("Assets/CoreAI/Runtime/Core/Features/Orchestration/QueuedAiOrchestrator.cs", Primitive.TaskDelay)] =
                "унаследовано: Task.Delay(Timeout.Infinite, ct) как ожидание отмены в той же очереди",
            [("Assets/CoreAI/Runtime/Core/Features/Orchestration/ScriptedLlmClient.cs", Primitive.TaskDelay)] =
                "тестовый двойник: Task.Delay(0) как точка уступки, в реальный плеер не попадает",
            [("Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/LlmClientRegistry.cs",
                Primitive.PoolContinuations)] =
                "унаследовано: ожидание активации клиента; тот же класс дефекта, разбирается отдельной задачей",
            [("Assets/CoreAiUnity/Runtime/Source/Features/Llm/Infrastructure/UnityWebRequestOpenAiTransport.cs",
                Primitive.PoolContinuations)] =
                "унаследовано: транспорт на UnityWebRequest, в браузере вместо него FetchSseOpenAiTransport"
        };

        [Test]
        public void WebGlReachableCode_DoesNotUseAsyncPrimitivesThatAreDeadInWebGl()
        {
            List<string> violations = new();
            int scannedFiles = 0;

            foreach (string root in ScannedRoots)
            {
                string absoluteRoot = ToAbsolute(root);
                Assert.IsTrue(Directory.Exists(absoluteRoot), $"Не найден каталог для скана: {absoluteRoot}");

                foreach (string file in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
                {
                    scannedFiles++;
                    string relative = ToRelative(file);
                    foreach (Hit hit in Scan(File.ReadAllText(file)))
                    {
                        if (Allowlist.ContainsKey((relative, hit.Id)))
                        {
                            continue;
                        }

                        violations.Add($"{relative}({hit.Line}): {hit.Text} — {hit.Why}");
                    }
                }
            }

            Assert.Greater(scannedFiles, 0, "Скан не нашёл ни одного файла — путь к проекту сломан.");
            Assert.IsEmpty(
                violations,
                "В коде, попадающем в WebGL-плеер, найдены примитивы, которые там не работают и дают " +
                "безмолвное вечное ожидание:\n" + string.Join("\n", violations));
        }

        /// <summary>
        /// Подсадное нарушение: без него поломка самого сканера (мёртвый ключ, съеденная регулярка)
        /// выглядела бы как «нарушений не найдено», то есть зелёным.
        /// </summary>
        [Test]
        public void Scanner_FindsSeededViolations_AndIgnoresCommentsStringsAndPlatformBranches()
        {
            List<Hit> seeded = Scan(SeededViolationProbe);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    Primitive.PoolContinuations, Primitive.CancelAfter, Primitive.TaskDelay, Primitive.TaskRun
                },
                seeded.ConvertAll(h => h.Id),
                "Сканер обязан находить каждый из четырёх примитивов в подсадном коде. Найдено: " +
                Describe(seeded));

            List<Hit> decoys = Scan(NonViolationProbe);
            Assert.IsEmpty(
                decoys,
                "Сканер сработал на том, что нарушением не является (комментарий, строка, UniTask, " +
                "ветка препроцессора, недостижимая в WebGL): " + Describe(decoys));

            // Нераспознанный многострочный литерал съедает перевод строки и сдвигает нумерацию, а вместе
            // с ней и карту достижимости. Обе перестановки префикса ($@ и @$) легальны в C#.
            List<Hit> afterVerbatim = Scan("var a = $@\"строка один\nстрока два\";\n" +
                                           "var b = @$\"строка три\nстрока четыре\";\nTask.Run(x);\n");
            CollectionAssert.AreEquivalent(
                new[] { 5 },
                afterVerbatim.ConvertAll(h => h.Line),
                "Нарушение после многострочных verbatim-литералов должно остаться на своей строке: " +
                Describe(afterVerbatim));
        }

        /// <summary>
        /// Вычислитель условий: приоритеты, скобки и — главное — трёхзначность. Правильность приоритетов
        /// иначе ничем не удержана, а «не знаю» под отрицанием легко выродить обратно в «ложь», и тогда
        /// страж молча ослепнет на целых регионах.
        /// </summary>
        [Test]
        public void PreprocessorEvaluator_ResolvesOnlyWhatItCanProve()
        {
            List<string> mismatches = new();
            foreach ((string condition, bool? expected) in EvaluatorCases)
            {
                bool? actual = EvaluateForWebGlPlayer(condition);
                if (actual != expected)
                {
                    mismatches.Add($"'{condition}': ожидалось {Tri(expected)}, получено {Tri(actual)}");
                }
            }

            Assert.IsEmpty(mismatches, "Вычислитель условий препроцессора разошёлся с ожиданиями:\n" +
                                       string.Join("\n", mismatches));
        }

        /// <summary>
        /// Разметка достижимости на уровне директив: <c>#if</c> / <c>#elif</c> / <c>#else</c> / вложенность.
        /// Ветка отбрасывается только при достоверно ложном условии — неизвестный define и <c>#else</c> к
        /// нему обязаны остаться под проверкой.
        /// </summary>
        [Test]
        public void PreprocessorReachability_DropsOnlyBranchesProvenAbsentFromWebGl()
        {
            List<string> mismatches = new();
            foreach ((string name, string source, bool expected) in ReachabilityCases)
            {
                bool actual = Scan(source).Count > 0;
                if (actual != expected)
                {
                    mismatches.Add($"{name}: ожидалось {(expected ? "видно" : "не видно")}, получено " +
                                   $"{(actual ? "видно" : "не видно")}");
                }
            }

            Assert.IsEmpty(mismatches, "Разметка достижимости разошлась с ожиданиями:\n" +
                                       string.Join("\n", mismatches));
        }

        /// <summary>
        /// Каждое исключение обязано соответствовать реально существующему совпадению. Протухший или
        /// неверно записанный пункт — это дыра, которую видно только так.
        /// </summary>
        [Test]
        public void Allowlist_HasNoStaleEntries()
        {
            List<string> stale = new();
            foreach (KeyValuePair<(string Path, Primitive Id), string> entry in Allowlist)
            {
                if (string.IsNullOrWhiteSpace(entry.Value))
                {
                    stale.Add($"{entry.Key.Path} [{entry.Key.Id}]: исключение без причины");
                    continue;
                }

                string absolute = ToAbsolute(entry.Key.Path);
                if (!File.Exists(absolute))
                {
                    stale.Add($"{entry.Key.Path} [{entry.Key.Id}]: файла больше нет");
                    continue;
                }

                if (!Scan(File.ReadAllText(absolute)).Exists(h => h.Id == entry.Key.Id))
                {
                    stale.Add($"{entry.Key.Path} [{entry.Key.Id}]: примитива в файле больше нет — убрать пункт");
                }
            }

            Assert.IsEmpty(
                stale,
                "Замороженный список исключений разошёлся с кодом:\n" + string.Join("\n", stale));
        }

        // ============ сканер ============

        private readonly struct Hit
        {
            public Hit(Primitive id, int line, string text, string why)
            {
                Id = id;
                Line = line;
                Text = text;
                Why = why;
            }

            public Primitive Id { get; }
            public int Line { get; }
            public string Text { get; }
            public string Why { get; }
        }

        /// <summary>
        /// Совпадения в коде, достижимом из WebGL-плеера: комментарии и строковые литералы вырезаны,
        /// недостижимые ветки препроцессора пропущены.
        /// </summary>
        private static List<Hit> Scan(string source)
        {
            string code = StripCommentsAndStringLiterals(source);
            int[] lineStarts = BuildLineStarts(code);
            bool[] reachable = BuildWebGlReachability(code, lineStarts);

            List<Hit> hits = new();
            foreach ((Primitive id, Regex rx, string why) in Forbidden)
            {
                foreach (Match match in rx.Matches(code))
                {
                    int line = LineOf(lineStarts, match.Index);
                    if (!reachable[line - 1])
                    {
                        continue;
                    }

                    hits.Add(new Hit(id, line, match.Value.Trim(), why));
                }
            }

            return hits;
        }

        /// <summary>
        /// Убирает <c>//</c>-хвосты, <c>/* */</c> и содержимое литералов, сохраняя переводы строк (а
        /// значит и номера строк) и директивы препроцессора. Иначе страж падает на собственных
        /// комментариях вида «WHY NOT RunContinuationsAsynchronously» — то есть ровно на том файле,
        /// ради которого написан.
        /// </summary>
        private static string StripCommentsAndStringLiterals(string text)
        {
            StringBuilder code = new(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n')
                    {
                        i++;
                    }

                    continue;
                }

                if (c == '/' && i + 1 < text.Length && text[i + 1] == '*')
                {
                    i += 2;
                    while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                    {
                        if (text[i] == '\n')
                        {
                            code.Append('\n');
                        }

                        i++;
                    }

                    i = Math.Min(i + 2, text.Length);
                    continue;
                }

                if (c == '\'')
                {
                    i = SkipCharLiteral(text, i);
                    continue;
                }

                int verbatimBody = VerbatimBodyStart(text, i);
                if (verbatimBody >= 0)
                {
                    i = SkipVerbatimString(text, verbatimBody, code);
                    continue;
                }

                if (c == '"')
                {
                    i = SkipRegularString(text, i + 1);
                    continue;
                }

                code.Append(c);
                i++;
            }

            return code.ToString();
        }

        /// <summary>
        /// Индекс тела verbatim-литерала — <c>@"…"</c>, <c>$@"…"</c> и <c>@$"…"</c> (обе перестановки
        /// легальны) — или <c>-1</c>, если здесь литерал не начинается. Нераспознанный многострочный
        /// verbatim читался бы как обычная строка, съел бы перевод строки и сдвинул всю нумерацию.
        /// </summary>
        private static int VerbatimBodyStart(string text, int i)
        {
            if (i + 1 < text.Length && text[i] == '@' && text[i + 1] == '"')
            {
                return i + 2;
            }

            bool prefixed = i + 2 < text.Length && text[i + 2] == '"' &&
                            ((text[i] == '$' && text[i + 1] == '@') ||
                             (text[i] == '@' && text[i + 1] == '$'));
            return prefixed ? i + 3 : -1;
        }

        private static int SkipCharLiteral(string text, int start)
        {
            int i = start + 1;
            while (i < text.Length && text[i] != '\'' && text[i] != '\n')
            {
                i += text[i] == '\\' ? 2 : 1;
            }

            return Math.Min(i + 1, text.Length);
        }

        /// <summary>Пропускает <c>@"…"</c> (<c>""</c> — экранированная кавычка), сохраняя переводы строк.</summary>
        private static int SkipVerbatimString(string text, int start, StringBuilder code)
        {
            int i = start;
            while (i < text.Length)
            {
                if (text[i] == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    break;
                }

                if (text[i] == '\n')
                {
                    code.Append('\n');
                }

                i++;
            }

            return Math.Min(i + 1, text.Length);
        }

        private static int SkipRegularString(string text, int start)
        {
            int i = start;
            while (i < text.Length && text[i] != '"' && text[i] != '\n')
            {
                i += text[i] == '\\' ? 2 : 1;
            }

            return Math.Min(i + 1, text.Length);
        }

        // ============ достижимость из WebGL-плеера ============

        /// <summary>
        /// Для каждой строки: может ли она попасть в WebGL-плеер. Условия вычисляются в трёх значениях
        /// при <c>UNITY_WEBGL = true</c>, <c>UNITY_EDITOR = false</c>; любой другой символ — «не знаю»
        /// (<c>null</c>).
        /// <para>
        /// Ветка отбрасывается ТОЛЬКО когда условие достоверно ложно. Правило «неизвестный символ = true»
        /// было бы консервативным лишь для позитивного вхождения и переворачивалось бы под отрицанием:
        /// <c>#if !COREAI_LLM</c> объявлялось бы недостижимым, хотя в сборке без этого define оно
        /// компилируется, и то же самое случалось бы с <c>#else</c> к любому неизвестному условию — то
        /// есть страж получал бы слепые зоны ровно там, где запрещённые примитивы уже водятся.
        /// </para>
        /// </summary>
        private static bool[] BuildWebGlReachability(string code, int[] lineStarts)
        {
            bool[] reachable = new bool[lineStarts.Length];
            List<(bool Active, bool AnyBranchCertain)> stack = new();

            for (int lineIndex = 0; lineIndex < lineStarts.Length; lineIndex++)
            {
                string line = LineText(code, lineStarts, lineIndex).Trim();
                if (line.StartsWith("#if", StringComparison.Ordinal))
                {
                    bool? value = EvaluateForWebGlPlayer(line.Substring("#if".Length));
                    stack.Add((value != false, value == true));
                }
                else if (line.StartsWith("#elif", StringComparison.Ordinal) && stack.Count > 0)
                {
                    bool certain = stack[stack.Count - 1].AnyBranchCertain;
                    bool? value = EvaluateForWebGlPlayer(line.Substring("#elif".Length));
                    stack[stack.Count - 1] = (!certain && value != false, certain || value == true);
                }
                else if (line.StartsWith("#else", StringComparison.Ordinal) && stack.Count > 0)
                {
                    // #else недостижим только тогда, когда какая-то ветка выше взята ДОСТОВЕРНО.
                    bool certain = stack[stack.Count - 1].AnyBranchCertain;
                    stack[stack.Count - 1] = (!certain, true);
                }
                else if (line.StartsWith("#endif", StringComparison.Ordinal) && stack.Count > 0)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                bool active = true;
                foreach ((bool frameActive, bool _) in stack)
                {
                    active &= frameActive;
                }

                reachable[lineIndex] = active;
            }

            return reachable;
        }

        /// <summary>
        /// Вычисляет условие препроцессора для WebGL-плеера: <c>true</c> / <c>false</c> / <c>null</c> —
        /// «достоверно неизвестно». Поддержаны <c>!</c>, <c>&amp;&amp;</c>, <c>||</c> и скобки; всё
        /// непонятое (неизвестный символ, чужой оператор, оборванное выражение) даёт <c>null</c>, то есть
        /// ветка остаётся под проверкой.
        /// </summary>
        private static bool? EvaluateForWebGlPlayer(string condition)
        {
            List<string> tokens = Tokenize(condition);
            if (tokens == null)
            {
                return null;
            }

            int position = 0;
            bool? value = ParseOr(tokens, ref position);
            return position == tokens.Count ? value : null;
        }

        private static List<string> Tokenize(string condition)
        {
            List<string> tokens = new();
            int i = 0;
            while (i < condition.Length)
            {
                char c = condition[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '(' || c == ')' || c == '!')
                {
                    tokens.Add(c.ToString());
                    i++;
                }
                else if ((c == '&' || c == '|') && i + 1 < condition.Length && condition[i + 1] == c)
                {
                    tokens.Add(condition.Substring(i, 2));
                    i += 2;
                }
                else if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < condition.Length && (char.IsLetterOrDigit(condition[i]) || condition[i] == '_'))
                    {
                        i++;
                    }

                    tokens.Add(condition.Substring(start, i - start));
                }
                else
                {
                    // Оператор, которого мы не разбираем (==, defined(...) и т.п.): всё условие — «не знаю».
                    return null;
                }
            }

            return tokens;
        }

        private static bool? ParseOr(List<string> tokens, ref int position)
        {
            bool? value = ParseAnd(tokens, ref position);
            while (position < tokens.Count && tokens[position] == "||")
            {
                position++;
                bool? right = ParseAnd(tokens, ref position);
                value = value == true || right == true ? true
                    : value == false && right == false ? false
                    : (bool?)null;
            }

            return value;
        }

        private static bool? ParseAnd(List<string> tokens, ref int position)
        {
            bool? value = ParseUnary(tokens, ref position);
            while (position < tokens.Count && tokens[position] == "&&")
            {
                position++;
                bool? right = ParseUnary(tokens, ref position);
                value = value == false || right == false ? false
                    : value == true && right == true ? true
                    : (bool?)null;
            }

            return value;
        }

        /// <summary>Всегда съедает хотя бы один токен, поэтому разбор кривого условия завершается.</summary>
        private static bool? ParseUnary(List<string> tokens, ref int position)
        {
            if (position >= tokens.Count)
            {
                return null;
            }

            string token = tokens[position++];
            if (token == "!")
            {
                return !ParseUnary(tokens, ref position);
            }

            if (token == "(")
            {
                bool? value = ParseOr(tokens, ref position);
                if (position < tokens.Count && tokens[position] == ")")
                {
                    position++;
                    return value;
                }

                return null;
            }

            switch (token)
            {
                case "UNITY_WEBGL":
                case "true":
                    return true;
                case "UNITY_EDITOR":
                case "false":
                    return false;
                case ")":
                case "&&":
                case "||":
                    return null;
                default:
                    // Неизвестный define (COREAI_LLM, UNITY_6000_5_OR_NEWER и подобные) может быть как
                    // включён, так и выключен в WebGL-сборке — достоверного ответа нет.
                    return null;
            }
        }

        // ============ строки и пути ============

        private static int[] BuildLineStarts(string text)
        {
            List<int> starts = new() { 0 };
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    starts.Add(i + 1);
                }
            }

            return starts.ToArray();
        }

        /// <summary>Номер строки (с единицы) по смещению — двоичным поиском, а не пересчётом с начала.</summary>
        private static int LineOf(int[] lineStarts, int index)
        {
            int found = Array.BinarySearch(lineStarts, index);
            return found >= 0 ? found + 1 : ~found;
        }

        private static string LineText(string text, int[] lineStarts, int lineIndex)
        {
            int start = lineStarts[lineIndex];
            int end = lineIndex + 1 < lineStarts.Length ? lineStarts[lineIndex + 1] : text.Length;
            return text.Substring(start, end - start).TrimEnd('\n', '\r');
        }

        private static string ToAbsolute(string relative)
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            return Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        }

        private static string ToRelative(string absolute)
        {
            string assets = Application.dataPath.Replace('\\', '/');
            string normalized = absolute.Replace('\\', '/');
            return normalized.StartsWith(assets, StringComparison.Ordinal)
                ? "Assets" + normalized.Substring(assets.Length)
                : normalized;
        }

        private static string Describe(List<Hit> hits)
        {
            if (hits.Count == 0)
            {
                return "(пусто)";
            }

            return string.Join(", ", hits.ConvertAll(h => $"{h.Id}@{h.Line}:{h.Text}"));
        }

        private static string Tri(bool? value)
        {
            return value.HasValue ? value.Value.ToString() : "не знаю";
        }

        // ============ подсадной материал для самопроверки ============

        /// <summary>Условие препроцессора → достоверный ответ для WebGL-плеера (<c>null</c> = «не знаю»).</summary>
        private static readonly (string Condition, bool? Expected)[] EvaluatorCases =
        {
            ("UNITY_WEBGL", true),
            ("UNITY_EDITOR", false),
            ("!UNITY_EDITOR", true),
            ("true", true),
            ("false", false),
            ("UNITY_WEBGL && !UNITY_EDITOR", true),
            ("!UNITY_WEBGL || UNITY_EDITOR", false),

            // Неизвестный define не притворяется ни истиной, ни ложью — ни сам по себе, ни под "!".
            ("COREAI_LLM", null),
            ("!COREAI_LLM", null),

            // Реальные условия из репозитория.
            ("COREAI_HAS_LLMUNITY && !UNITY_WEBGL && COREAI_LLM", false),
            ("!COREAI_HAS_LLMUNITY || UNITY_WEBGL || !COREAI_LLM", true),

            // Скобки и трёхзначная логика.
            ("(UNITY_WEBGL || COREAI_LLM) && UNITY_EDITOR", false),
            ("UNITY_WEBGL && (UNITY_EDITOR || COREAI_LLM)", null),
            ("!(UNITY_WEBGL)", false),
            ("!(UNITY_EDITOR && COREAI_LLM)", true),

            // "&&" связывает крепче "||": плоский разбор слева направо дал бы здесь false.
            ("UNITY_WEBGL || UNITY_WEBGL && UNITY_EDITOR", true),
            ("UNITY_EDITOR && UNITY_WEBGL || UNITY_WEBGL", true),

            // Всё непонятое — «не знаю», а не молчаливая ложь.
            ("UNITY_WEBGL == 1", null),
            ("defined(UNITY_WEBGL)", null),
            ("UNITY_WEBGL &&", null),
            ("(UNITY_WEBGL", null),
            ("", null)
        };

        /// <summary>Кусок исходника → видит ли сканер спрятанный в нём <c>Task.Run(</c>.</summary>
        private static readonly (string Name, string Source, bool Expected)[] ReachabilityCases =
        {
            ("код без директив", "void M() { Task.Run(x); }\n", true),
            ("#if UNITY_EDITOR", "#if UNITY_EDITOR\nTask.Run(x);\n#endif\n", false),
            ("#else к #if UNITY_EDITOR", "#if UNITY_EDITOR\nNo();\n#else\nTask.Run(x);\n#endif\n", true),
            ("#if UNITY_WEBGL && !UNITY_EDITOR",
                "#if UNITY_WEBGL && !UNITY_EDITOR\nTask.Run(x);\n#endif\n", true),
            ("#else к WebGL-развилке",
                "#if UNITY_WEBGL && !UNITY_EDITOR\nNo();\n#else\nTask.Run(x);\n#endif\n", false),
            ("#if НЕИЗВЕСТНЫЙ", "#if COREAI_LLM\nTask.Run(x);\n#endif\n", true),
            ("#if !НЕИЗВЕСТНЫЙ", "#if !COREAI_LLM\nTask.Run(x);\n#endif\n", true),
            ("#else к #if НЕИЗВЕСТНЫЙ", "#if COREAI_LLM\nNo();\n#else\nTask.Run(x);\n#endif\n", true),
            ("#if false", "#if false\nTask.Run(x);\n#endif\n", false),
            ("#elif, взятая ветка", "#if UNITY_EDITOR\nNo();\n#elif UNITY_WEBGL\nTask.Run(x);\n#endif\n", true),
            ("#elif после достоверно взятой ветки",
                "#if UNITY_WEBGL\nNo();\n#elif COREAI_LLM\nTask.Run(x);\n#endif\n", false),
            ("#else после достоверного #elif",
                "#if UNITY_EDITOR\nNo();\n#elif UNITY_WEBGL\nNo();\n#else\nTask.Run(x);\n#endif\n", false),
            ("вложенный #if внутри достижимого",
                "#if UNITY_WEBGL\n#if UNITY_EDITOR\nTask.Run(x);\n#endif\n#endif\n", false),
            ("вложенный #if внутри недостижимого",
                "#if UNITY_EDITOR\n#if UNITY_WEBGL\nTask.Run(x);\n#endif\n#endif\n", false),
            ("код после закрытия недостижимого региона",
                "#if UNITY_EDITOR\nNo();\n#endif\nTask.Run(x);\n", true),
            ("кривое условие оставляет ветку под проверкой",
                "#if UNITY_WEBGL == 1\nTask.Run(x);\n#endif\n", true)
        };

        private const string SeededViolationProbe = @"
class Seeded
{
    void Bad()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        cts.CancelAfter(1000);
        await Task.Delay(5, token);
        Task.Run(() => Work());
    }
}
";

        private const string NonViolationProbe = @"
class NotAViolation
{
    // WHY NOT RunContinuationsAsynchronously: комментарий не код.
    /* Task.Delay( и cts.CancelAfter( внутри блочного комментария тоже. */
    private const string Message = ""Task.Run( внутри строки — тоже не код"";

    /// <summary>Ссылка <see cref=""Task.Delay(int, CancellationToken)""/> в xml-doc.</summary>
    void Ok()
    {
        UniTask.Delay(5);
#if UNITY_EDITOR
        Task.Run(() => Work());
#endif
#if UNITY_WEBGL && !UNITY_EDITOR
        Work();
#else
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        cts.CancelAfter(1000);
#endif
    }
}
";
    }
}
