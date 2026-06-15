using System;
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
                Description = "Store, append, clear, or granularly edit persistent memory for agent recall across sessions."
            };
            return AIFunctionFactory.Create(func, options);
        }


        // generating an unnecessary state machine. Returns Task.FromResult directly.
        public Task<string> ExecuteAsync(
            string action,
            string? content = null,
            string? old_text = null,
            string? new_text = null,
            string? anchor = null,
            int? line = null,
            bool replace_all = false,
            CancellationToken cancellationToken = default)
        {
            if (_settings?.LogToolCalls ?? CoreAISettings.LogToolCalls)
            {
                Log.Instance.Info($"[Tool Call] memory: action={action}", LogTag.Memory);
            }

            if ((_settings?.LogToolCallArguments ?? CoreAISettings.LogToolCallArguments) && content != null)
            {
                string preview = content.Length > 200 ? content.Substring(0, 200) : content;
                Log.Instance.Info($"  content: {preview}", LogTag.Memory);
            }

            if (string.IsNullOrEmpty(action))
            {
                return Task.FromResult(SerializeResult(new MemoryResult
                    { Success = false, Error = "Action is required" }));
            }

            action = action.Trim().ToLowerInvariant();

            try
            {
                switch (action)
                {
                    case "write":
                        if (string.IsNullOrEmpty(content))
                        {
                            return Task.FromResult(SerializeResult(new MemoryResult
                                { Success = false, Error = "Content is required for write action" }));
                        }

                        return Task.FromResult(SaveMutation(action, content, "Memory saved"));

                    case "append":
                        if (string.IsNullOrEmpty(content))
                        {
                            return Task.FromResult(SerializeResult(new MemoryResult
                                { Success = false, Error = "Content is required for append action" }));
                        }

                        string currentState = LoadMemory(out _);

                        if (currentState.Contains(content, StringComparison.OrdinalIgnoreCase))
                        {
                            return Task.FromResult(SerializeResult(new MemoryResult
                            {
                                Success = true,
                                Message =
                                    $"Content already exists in memory for role: {_roleId}. Continue with your task."
                            }));
                        }

                        string newMemory = string.IsNullOrEmpty(currentState)
                            ? content
                            : currentState + "\n" + content;

                        return Task.FromResult(SaveMutation(action, newMemory, "Content appended"));

                    case "clear":
                        return Task.FromResult(SaveMutation(action, "", "Memory cleared"));

                    case "str_replace":
                        return Task.FromResult(ExecuteStrReplace(old_text, new_text ?? content, replace_all));

                    case "insert":
                        return Task.FromResult(ExecuteInsert(content, anchor, line));

                    case "delete":
                        return Task.FromResult(ExecuteDelete(old_text ?? content, replace_all));

                    case "rename":
                        return Task.FromResult(ExecuteRename(old_text, new_text ?? content));

                    default:
                        return Task.FromResult(SerializeResult(new MemoryResult
                        {
                            Success = false,
                            Error =
                                $"Unknown action: '{action}'. Valid actions: write, append, clear, str_replace, insert, delete, rename"
                        }));
                }
            }
            catch (Exception ex)
            {
                if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
                {
                    Log.Instance.Error($"[Tool Call] memory: FAILED - {ex.Message}", LogTag.Memory);
                }

                return Task.FromResult(SerializeResult(new MemoryResult
                {
                    Success = false,
                    Error = $"Memory operation failed: {ex.Message}"
                }));
            }
        }

        private string ExecuteStrReplace(string oldText, string newText, bool replaceAll)
        {
            if (string.IsNullOrEmpty(oldText))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "old_text is required for str_replace action" });
            }

            if (newText == null)
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "new_text or content is required for str_replace action" });
            }

            string current = LoadMemory(out _);
            if (!current.Contains(oldText, StringComparison.Ordinal))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "old_text was not found in memory" });
            }

            string next = replaceAll
                ? current.Replace(oldText, newText)
                : ReplaceFirst(current, oldText, newText);
            return SaveMutation("str_replace", next, replaceAll ? "Replaced all exact matches" : "Replaced first exact match");
        }

        private string ExecuteInsert(string content, string anchor, int? line)
        {
            if (string.IsNullOrEmpty(content))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "Content is required for insert action" });
            }

            string current = LoadMemory(out _);
            if (!TryInsert(current, content, anchor, line, out string next, out string error))
            {
                return SerializeResult(new MemoryResult { Success = false, Error = error });
            }

            return SaveMutation("insert", next, line.HasValue ? $"Inserted before line {line.Value}" : "Inserted content");
        }

        private string ExecuteDelete(string target, bool deleteAll)
        {
            if (string.IsNullOrEmpty(target))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "old_text or content is required for delete action" });
            }

            string current = LoadMemory(out _);
            if (!current.Contains(target, StringComparison.Ordinal))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "Delete target was not found in memory" });
            }

            string next = deleteAll
                ? current.Replace(target, "")
                : ReplaceFirst(current, target, "");
            return SaveMutation("delete", next, deleteAll ? "Deleted all exact matches" : "Deleted first exact match");
        }

        private string ExecuteRename(string oldLabel, string newLabel)
        {
            if (string.IsNullOrEmpty(oldLabel))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "old_text is required for rename action" });
            }

            if (string.IsNullOrEmpty(newLabel))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "new_text or content is required for rename action" });
            }

            string current = LoadMemory(out _);
            if (!TryRenameLabel(current, oldLabel.Trim(), newLabel.Trim(), out string next))
            {
                return SerializeResult(new MemoryResult
                    { Success = false, Error = "Rename label was not found in memory" });
            }

            return SaveMutation("rename", next, $"Renamed label '{oldLabel.Trim()}' to '{newLabel.Trim()}'");
        }

        private string SaveMutation(string action, string nextMemory, string messagePrefix)
        {
            string previous = LoadMemory(out AgentMemoryState state);
            state.Memory = nextMemory ?? "";
            AgentMemoryVersionSnapshot snapshot = state.RecordVersion(action, state.Memory,
                CreateMutationNote(previous, state.Memory));
            _store.Save(_roleId, state);

            if (_settings?.LogToolCallResults ?? CoreAISettings.LogToolCallResults)
            {
                Log.Instance.Info($"[Tool Call] memory: SUCCESS - {messagePrefix} for {_roleId}",
                    LogTag.Memory);
            }

            return SerializeResult(new MemoryResult
            {
                Success = true,
                Message = $"DONE: {messagePrefix} for {_roleId}.",
                Version = snapshot.Version,
                MemoryLength = state.Memory.Length
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
        }
    }
}
