using System;
using System.ComponentModel;
using Newtonsoft.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using CoreAI.Logging;

namespace CoreAI.Ai
{
    /// <summary>
    /// Host-side implementation of agent memory tool operations.
    /// </summary>
    public sealed class MemoryTool
    {
        private readonly IAgentMemoryStore _store;
        private readonly string _roleId;
        private readonly ICoreAISettings _settings;

        public MemoryTool(IAgentMemoryStore store, string roleId, ICoreAISettings settings = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _roleId = roleId ?? throw new ArgumentNullException(nameof(roleId));
            _settings = settings;
        }


        public AIFunction CreateAIFunction()
        {
            Func<string, string?, string?, string?, string?, int?, bool, CancellationToken, Task<string>> func =
                ExecuteAsync;
            AIFunctionFactoryOptions options = new()
            {
                Name = "memory",
                Description =
                    "Read, store, append, clear, or granularly edit persistent memory for agent recall across sessions."
            };
            return AIFunctionFactory.Create(func, options);
        }


        public async Task<string> ExecuteAsync(
            [Description("Action: read, write, append, clear, str_replace, insert, delete, or rename")]
            string action,
            [Description(
                "Memory content for write/append/insert, replacement fallback for str_replace/rename, or delete target fallback")]
            string? content = null,
            [Description("Exact text to replace/delete, or section/key label to rename")]
            string? old_text = null,
            [Description("Replacement text for str_replace, or new section/key label for rename")]
            string? new_text = null,
            [Description("For insert: exact anchor text; content is inserted after the anchor's line")]
            string? anchor = null,
            [Description("For insert: 1-based line number to insert before; beyond end appends")]
            int? line = null,
            [Description("For str_replace/delete: true edits all exact matches; false edits the first match")]
            bool replace_all = false,
            CancellationToken cancellationToken = default)
        {
            if (_settings?.LogToolCalls ?? CoreAISettings.LogToolCalls)
            {
                Log.Instance.Info($"[Tool Call] memory: action={action}", LogTag.Memory);
            }

            string mutationContent = string.IsNullOrEmpty(content) ? new_text : content;

            if ((_settings?.LogToolCallArguments ?? CoreAISettings.LogToolCallArguments) && mutationContent != null)
            {
                string preview = mutationContent.Length > 200 ? mutationContent.Substring(0, 200) : mutationContent;
                Log.Instance.Info($"  content: {preview}", LogTag.Memory);
            }

            if (string.IsNullOrEmpty(action))
            {
                return SerializeResult(new MemoryResult { Success = false, Error = "Action is required" });
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "read":
                        return ReadMemory();

                    case "write":
                        if (string.IsNullOrEmpty(mutationContent))
                        {
                            return SerializeResult(new MemoryResult
                                { Success = false, Error = "content or new_text is required for write action" });
                        }

                        return await SaveMutationAsync(action, mutationContent, "Memory saved", cancellationToken)
                            .ConfigureAwait(false);

                    case "append":
                        if (string.IsNullOrEmpty(mutationContent))
                        {
                            return SerializeResult(new MemoryResult
                                { Success = false, Error = "content or new_text is required for append action" });
                        }

                        return await MutateMemoryAsync(
                                action,
                                current =>
                                {
                                    if (MemoryContainsLine(current, mutationContent))
                                    {
                                        return MemoryMutationPlan.NoChange(
                                            $"Content already exists in memory for role: {_roleId}. Continue with your task.");
                                    }

                                    string next = string.IsNullOrEmpty(current)
                                        ? mutationContent
                                        : current + "\n" + mutationContent;
                                    return MemoryMutationPlan.Change(next, "Content appended");
                                },
                                cancellationToken)
                            .ConfigureAwait(false);

                    case "clear":
                        return ClearMemory();

                    case "str_replace":
                        return await ExecuteStrReplaceAsync(old_text, mutationContent, replace_all, cancellationToken)
                            .ConfigureAwait(false);

                    case "insert":
                        return await ExecuteInsertAsync(mutationContent, anchor, line, cancellationToken)
                            .ConfigureAwait(false);

                    case "delete":
                        return await ExecuteDeleteAsync(old_text ?? mutationContent, replace_all, cancellationToken)
                            .ConfigureAwait(false);

                    case "rename":
                        return await ExecuteRenameAsync(old_text, mutationContent, cancellationToken)
                            .ConfigureAwait(false);

                    default:
                        return SerializeResult(new MemoryResult
                        {
                            Success = false,
                            Error =
                                $"Unknown action: '{action}'. Valid actions: read, write, append, clear, str_replace, insert, delete, rename"
                        });
                }
            }
            catch (Exception ex)
            {
                if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
                {
                    Log.Instance.Error($"[Tool Call] memory: FAILED - {ex.Message}", LogTag.Memory);
                }

                return SerializeResult(new MemoryResult
                {
                    Success = false,
                    Error = $"Memory operation failed: {ex.Message}"
                });
            }
        }

        private string ReadMemory()
        {
            string current = LoadMemory(out AgentMemoryState state);
            int latestVersion = 0;
            AgentMemoryVersionSnapshot[] versions = state?.Versions;
            if (versions != null)
            {
                for (int i = 0; i < versions.Length; i++)
                {
                    if (versions[i] != null && versions[i].Version > latestVersion)
                    {
                        latestVersion = versions[i].Version;
                    }
                }
            }

            if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
            {
                Log.Instance.Info($"[Tool Call] memory: SUCCESS - Memory read for {_roleId}",
                    LogTag.Memory);
            }

            return SerializeResult(new MemoryResult
            {
                Success = true,
                Message = string.IsNullOrEmpty(current)
                    ? $"DONE: Memory is empty for {_roleId}."
                    : $"DONE: Memory read for {_roleId}.",
                Version = latestVersion,
                MemoryLength = current.Length,
                Memory = current
            });
        }

        private Task<string> ExecuteStrReplaceAsync(string oldText, string newText, bool replaceAll,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(oldText))
            {
                return Task.FromResult(SerializeResult(new MemoryResult
                    { Success = false, Error = "old_text is required for str_replace action" }));
            }

            if (newText == null)
            {
                return Task.FromResult(SerializeResult(new MemoryResult
                    { Success = false, Error = "new_text or content is required for str_replace action" }));
            }

            return MutateMemoryAsync(
                "str_replace",
                current =>
                {
                    if (!current.Contains(oldText, StringComparison.Ordinal))
                    {
                        return MemoryMutationPlan.Fail("old_text was not found in memory");
                    }

                    string next = replaceAll
                        ? current.Replace(oldText, newText)
                        : ReplaceFirst(current, oldText, newText);
                    return MemoryMutationPlan.Change(next,
                        replaceAll ? "Replaced all exact matches" : "Replaced first exact match");
                },
                cancellationToken);
        }

        private Task<string> ExecuteInsertAsync(string content, string anchor, int? line,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(content))
            {
                return Task.FromResult(SerializeResult(new MemoryResult
                    { Success = false, Error = "Content is required for insert action" }));
            }

            return MutateMemoryAsync(
                "insert",
                current =>
                {
                    if (!TryInsert(current, content, anchor, line, out string next, out string error))
                    {
                        return MemoryMutationPlan.Fail(error);
                    }

                    return MemoryMutationPlan.Change(next,
                        line.HasValue ? $"Inserted before line {line.Value}" : "Inserted content");
                },
                cancellationToken);
        }

        private Task<string> ExecuteDeleteAsync(string target, bool deleteAll, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(target))
            {
                return Task.FromResult(SerializeResult(new MemoryResult
                    { Success = false, Error = "old_text or content is required for delete action" }));
            }

            return MutateMemoryAsync(
                "delete",
                current =>
                {
                    if (!current.Contains(target, StringComparison.Ordinal))
                    {
                        return MemoryMutationPlan.Fail("Delete target was not found in memory");
                    }

                    string next = deleteAll
                        ? current.Replace(target, "")
                        : ReplaceFirst(current, target, "");
                    return MemoryMutationPlan.Change(next,
                        deleteAll ? "Deleted all exact matches" : "Deleted first exact match");
                },
                cancellationToken);
        }

        private Task<string> ExecuteRenameAsync(string oldLabel, string newLabel,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(oldLabel))
            {
                return Task.FromResult(SerializeResult(new MemoryResult
                    { Success = false, Error = "old_text is required for rename action" }));
            }

            if (string.IsNullOrEmpty(newLabel))
            {
                return Task.FromResult(SerializeResult(new MemoryResult
                    { Success = false, Error = "new_text or content is required for rename action" }));
            }

            return MutateMemoryAsync(
                "rename",
                current =>
                {
                    if (!TryRenameLabel(current, oldLabel.Trim(), newLabel.Trim(), out string next))
                    {
                        return MemoryMutationPlan.Fail("Rename label was not found in memory");
                    }

                    return MemoryMutationPlan.Change(next,
                        $"Renamed label '{oldLabel.Trim()}' to '{newLabel.Trim()}'");
                },
                cancellationToken);
        }

        /// <summary>
        /// True when <paramref name="content"/> already exists as a whole trimmed line in the memory
        /// document. Replaces a former case-insensitive substring check that silently dropped a short fact
        /// which happened to be a substring of unrelated existing text (e.g. "apple" inside "pineapples").
        /// </summary>
        private static bool MemoryContainsLine(string memory, string content)
        {
            if (string.IsNullOrEmpty(memory) || string.IsNullOrEmpty(content))
            {
                return false;
            }

            string target = content.Trim();
            foreach (string line in memory.Split('\n'))
            {
                if (string.Equals(line.Trim(), target, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private Task<string> SaveMutationAsync(string action, string nextMemory, string messagePrefix,
            CancellationToken cancellationToken)
        {
            return MutateMemoryAsync(action, _ => MemoryMutationPlan.Change(nextMemory, messagePrefix),
                cancellationToken);
        }

        private Task<string> MutateMemoryAsync(string action, Func<string, MemoryMutationPlan> planner,
            CancellationToken cancellationToken)
        {
            return _store.MutateAsync(
                _roleId,
                state =>
                {
                    string previous = state.Memory ?? "";
                    MemoryMutationPlan plan = planner(previous);
                    if (!plan.Success)
                    {
                        return SerializeResult(new MemoryResult { Success = false, Error = plan.Error });
                    }

                    if (!plan.Changed)
                    {
                        return SerializeResult(new MemoryResult
                        {
                            Success = true,
                            Message = plan.Message,
                            MemoryLength = previous.Length
                        });
                    }

                    state.Memory = plan.NextMemory ?? "";
                    AgentMemoryVersionSnapshot snapshot = state.RecordVersion(action, state.Memory,
                        CreateMutationNote(previous, state.Memory));

                    if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
                    {
                        Log.Instance.Info($"[Tool Call] memory: SUCCESS - {plan.MessagePrefix} for {_roleId}",
                            LogTag.Memory);
                    }

                    return SerializeResult(new MemoryResult
                    {
                        Success = true,
                        Message = $"DONE: {plan.MessagePrefix} for {_roleId}.",
                        Version = snapshot.Version,
                        MemoryLength = state.Memory.Length
                    });
                },
                cancellationToken);
        }

        private sealed class MemoryMutationPlan
        {
            private MemoryMutationPlan(bool success, bool changed, string nextMemory, string messagePrefix,
                string message, string error)
            {
                Success = success;
                Changed = changed;
                NextMemory = nextMemory;
                MessagePrefix = messagePrefix;
                Message = message;
                Error = error;
            }

            public bool Success { get; }
            public bool Changed { get; }
            public string NextMemory { get; }
            public string MessagePrefix { get; }
            public string Message { get; }
            public string Error { get; }

            public static MemoryMutationPlan Change(string nextMemory, string messagePrefix)
            {
                return new MemoryMutationPlan(true, true, nextMemory, messagePrefix, null, null);
            }

            public static MemoryMutationPlan NoChange(string message)
            {
                return new MemoryMutationPlan(true, false, null, null, message, null);
            }

            public static MemoryMutationPlan Fail(string error)
            {
                return new MemoryMutationPlan(false, false, null, null, null, error);
            }
        }

        private string ClearMemory()
        {
            // Clear is destructive and not version-tracked because it removes the role key.
            // Undoable clear would need a separate restore feature.
            _store.Clear(_roleId);

            if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
            {
                Log.Instance.Info($"[Tool Call] memory: SUCCESS - Memory cleared for {_roleId}",
                    LogTag.Memory);
            }

            return SerializeResult(new MemoryResult
            {
                Success = true,
                Message = $"DONE: Memory cleared for {_roleId}.",
                MemoryLength = 0
            });
        }

        private string LoadMemory(out AgentMemoryState state)
        {
            if (!_store.TryLoad(_roleId, out state) || state == null)
            {
                state = new AgentMemoryState();
            }

            return state.Memory ?? "";
        }

        private static string ReplaceFirst(string value, string oldText, string newText)
        {
            int index = value.IndexOf(oldText, StringComparison.Ordinal);
            return index < 0
                ? value
                : value.Substring(0, index) + newText + value.Substring(index + oldText.Length);
        }

        private static bool TryInsert(string current, string content, string anchor, int? line, out string next,
            out string error)
        {
            next = current ?? "";
            error = null;

            if (line.HasValue)
            {
                if (line.Value <= 0)
                {
                    error = "line must be a 1-based positive line number";
                    return false;
                }

                next = InsertBeforeLine(next, content, line.Value);
                return true;
            }

            if (!string.IsNullOrEmpty(anchor))
            {
                int anchorIndex = next.IndexOf(anchor, StringComparison.Ordinal);
                if (anchorIndex < 0)
                {
                    error = "anchor was not found in memory";
                    return false;
                }

                int lineEnd = next.IndexOf('\n', anchorIndex);
                if (lineEnd < 0)
                {
                    next = AppendWithNewLine(next, content);
                    return true;
                }

                string block = content.EndsWith("\n", StringComparison.Ordinal) ? content : content + "\n";
                next = next.Insert(lineEnd + 1, block);
                return true;
            }

            next = AppendWithNewLine(next, content);
            return true;
        }

        private static string InsertBeforeLine(string current, string content, int oneBasedLine)
        {
            if (string.IsNullOrEmpty(current))
            {
                return content;
            }

            int targetIndex = oneBasedLine - 1;
            int currentLine = 0;
            int position = 0;
            while (currentLine < targetIndex && position < current.Length)
            {
                int nextBreak = current.IndexOf('\n', position);
                if (nextBreak < 0)
                {
                    return AppendWithNewLine(current, content);
                }

                position = nextBreak + 1;
                currentLine++;
            }

            string block = content.EndsWith("\n", StringComparison.Ordinal) ? content : content + "\n";
            return current.Insert(position, block);
        }

        private static string AppendWithNewLine(string current, string content)
        {
            if (string.IsNullOrEmpty(current))
            {
                return content;
            }

            return current.EndsWith("\n", StringComparison.Ordinal)
                ? current + content
                : current + "\n" + content;
        }

        private static bool TryRenameLabel(string current, string oldLabel, string newLabel, out string next)
        {
            next = current ?? "";
            string[] lines = next.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string suffix = "";
                if (line.EndsWith("\r", StringComparison.Ordinal))
                {
                    line = line.Substring(0, line.Length - 1);
                    suffix = "\r";
                }

                int labelStart = FindLabelStart(line);
                if (labelStart < 0 ||
                    line.Length < labelStart + oldLabel.Length + 1 ||
                    !string.Equals(line.Substring(labelStart, oldLabel.Length), oldLabel,
                        StringComparison.Ordinal) ||
                    line[labelStart + oldLabel.Length] != ':')
                {
                    continue;
                }

                lines[i] = line.Substring(0, labelStart) + newLabel +
                           line.Substring(labelStart + oldLabel.Length) + suffix;
                next = string.Join("\n", lines);
                return true;
            }

            return false;
        }

        private static int FindLabelStart(string line)
        {
            int index = 0;
            while (index < line.Length && char.IsWhiteSpace(line[index]))
            {
                index++;
            }

            int hashStart = index;
            while (index < line.Length && line[index] == '#')
            {
                index++;
            }

            if (index > hashStart)
            {
                while (index < line.Length && line[index] == ' ')
                {
                    index++;
                }
            }

            return index < line.Length ? index : -1;
        }

        private static string CreateMutationNote(string previous, string next)
        {
            int before = previous?.Length ?? 0;
            int after = next?.Length ?? 0;
            int delta = after - before;
            return $"chars {before}->{after} ({(delta >= 0 ? "+" : "")}{delta})";
        }

        private static string SerializeResult(MemoryResult result)
        {
            return JsonConvert.SerializeObject(result);
        }


        public sealed class MemoryResult
        {
            public bool Success { get; set; }
            public string Message { get; set; }
            public string Error { get; set; }
            public int Version { get; set; }
            public int MemoryLength { get; set; }
            public string Memory { get; set; }
        }
    }
}
