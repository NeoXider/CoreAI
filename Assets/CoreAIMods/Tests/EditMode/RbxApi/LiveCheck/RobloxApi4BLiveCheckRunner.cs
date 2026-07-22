using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CoreAI.Ai;
using CoreAI.Ai.LuaCs;
using CoreAI.Infrastructure.Logging;
using CoreAI.Mods.Rbx.Instances;
using Newtonsoft.Json.Linq;

namespace CoreAI.Tests.EditMode.RobloxApi.LiveCheck
{
    /// <summary>
    /// Engine-free, NUnit-free driver for the Roblox MVP1 "does a real 4B model write working Lua
    /// against our surface" live check. Deliberately shared verbatim between the EditMode test
    /// (<c>RobloxApi4BLiveCheckEditModeTests</c>) and the out-of-Unity console harness in the
    /// scratchpad, so the two exercise the identical prompt/execute/assert path against the same
    /// production stack (<see cref="LuaCsModRuntimeFactory"/> + <see cref="LuaCsRobloxApiBindings"/>
    /// + the real one-off <c>execute_lua</c> executor). Nothing here depends on UnityEngine at
    /// runtime — the Roblox world is the in-memory <see cref="InstanceRegistry"/> with no binder,
    /// so it runs under net8 too.
    /// </summary>
    public static class RobloxApi4BLiveCheckRunner
    {
        public const string DefaultBaseUrl = "http://127.0.0.1:1234/v1";
        public const string DefaultModel = "qwen_qwen3.5-4b";

        // ---- Endpoint config (env-overridable, LM-Studio defaults) --------------------------

        public static string BaseUrl =>
            FirstNonEmpty(Environment.GetEnvironmentVariable("COREAI_TEST_BASE_URL"), DefaultBaseUrl)
                .TrimEnd('/');

        public static string Model =>
            FirstNonEmpty(Environment.GetEnvironmentVariable("COREAI_TEST_MODEL"), DefaultModel);

        /// <summary>
        /// GET /v1/models and confirm the target model id is served. Returns (available, reason).
        /// Used by the EditMode test to <c>Assert.Ignore</c> instead of failing CI when the local
        /// LM Studio endpoint is not up.
        /// </summary>
        public static async Task<(bool available, string reason)> ProbeAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(8) };
                using HttpResponseMessage response =
                    await client.GetAsync(BaseUrl + "/models", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"GET {BaseUrl}/models -> HTTP {(int)response.StatusCode}");
                }

                string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                JObject root = JObject.Parse(body);
                foreach (JToken entry in root["data"] ?? new JArray())
                {
                    if (string.Equals((string)entry["id"], Model, StringComparison.Ordinal))
                    {
                        return (true, $"{Model} served at {BaseUrl}");
                    }
                }

                return (false, $"model '{Model}' not served at {BaseUrl}");
            }
            catch (Exception ex)
            {
                return (false, $"{BaseUrl} unreachable: {ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---- The honest minimal skill excerpt handed to the 4B --------------------------------
        // WHY: kept tight on purpose — this is what a small model realistically gets in production,
        // not the whole Lua Modding skill. Execute_lua semantics + globals list + two examples.

        public const string SystemPrompt =
@"You write Lua for the CoreAI `execute_lua` tool, which runs your script once in a sandboxed
Roblox-style world and reports back the FIRST value your script `return`s (stringified).

OUTPUT RULES (obey exactly):
- Reply with ONLY one Lua code block: ```lua ... ```
- No prose, no explanation, no comments outside the block.
- To report information, `return` a string. print() output is NOT captured.

AVAILABLE GLOBALS:
- Instance.new(className)  -> creates an instance. Creatable classes include Part, Folder, Model.
- workspace, game          -> the world roots. Parent things under workspace.
- Enum                     -> e.g. Enum.Material.Neon (an EnumItem with .Value and .Name).
- Vector3, CFrame, Color3  -> datatype constructors (Vector3.new(x,y,z), etc.).

INSTANCE MEMBERS:
- inst.Name, inst.Parent, inst.ClassName
- inst.Parent = workspace            (set Parent AFTER configuring; do NOT pass a parent to Instance.new)
- inst:FindFirstChild(name), inst:GetChildren()
- inst:AddTag(tag), inst:HasTag(tag)
- inst:SetAttribute(k, v), inst:GetAttribute(k)
- inst:GetFullName(), inst:Destroy()

EXAMPLE 1 — create and tag:
```lua
local part = Instance.new(""Part"")
part.Name = ""Spawn""
part.Parent = workspace
part:AddTag(""Checkpoint"")
```

EXAMPLE 2 — report values:
```lua
return workspace:GetFullName() .. "" material="" .. tostring(Enum.Material.Grass.Value)
```";

        // ---- Scenarios ------------------------------------------------------------------------

        public sealed class Scenario
        {
            public string Id;
            public string Title;
            public string UserTask;
            /// <summary>Returns (ok, detail). Inspects the real world + executor output.</summary>
            public Func<LuaCsRobloxApiBindings, LuaTool.LuaResult, (bool ok, string detail)> Verify;
        }

        public static IReadOnlyList<Scenario> Scenarios()
        {
            return new List<Scenario>
            {
                new Scenario
                {
                    Id = "1_part_door_tagged",
                    Title = "create a Part named Door parented to workspace, tagged Interactive",
                    UserTask =
                        "Create a Part named \"Door\", parent it to workspace, " +
                        "and add the tag \"Interactive\" to it.",
                    Verify = (roblox, _) =>
                    {
                        RbxInstance workspace = Workspace(roblox);
                        RbxInstance door = workspace.FindFirstChild("Door");
                        if (door == null)
                        {
                            return (false, "no child named 'Door' under workspace");
                        }

                        if (door.ClassName != "Part")
                        {
                            return (false, $"'Door' is a {door.ClassName}, expected Part");
                        }

                        if (door.Parent != workspace)
                        {
                            return (false, "'Door' is not parented to workspace");
                        }

                        if (!door.HasTag("Interactive"))
                        {
                            return (false, "'Door' is not tagged 'Interactive'");
                        }

                        return (true, "Part 'Door' under workspace, tag 'Interactive' present");
                    }
                },
                new Scenario
                {
                    Id = "2_loot_folder_three_coins",
                    Title = "create Folder Loot in workspace containing 3 Parts Coin1..Coin3",
                    UserTask =
                        "In workspace, create a Folder named \"Loot\". Inside that folder create " +
                        "3 Parts named \"Coin1\", \"Coin2\", and \"Coin3\".",
                    Verify = (roblox, _) =>
                    {
                        RbxInstance workspace = Workspace(roblox);
                        RbxInstance loot = workspace.FindFirstChild("Loot");
                        if (loot == null)
                        {
                            return (false, "no child named 'Loot' under workspace");
                        }

                        if (loot.ClassName != "Folder")
                        {
                            return (false, $"'Loot' is a {loot.ClassName}, expected Folder");
                        }

                        foreach (string coin in new[] { "Coin1", "Coin2", "Coin3" })
                        {
                            RbxInstance c = loot.FindFirstChild(coin);
                            if (c == null)
                            {
                                return (false, $"'Loot' is missing child '{coin}'");
                            }

                            if (c.ClassName != "Part")
                            {
                                return (false, $"'{coin}' is a {c.ClassName}, expected Part");
                            }
                        }

                        int childCount = loot.GetChildren().Count;
                        return (true, $"Folder 'Loot' with Coin1..Coin3 (childCount={childCount})");
                    }
                },
                new Scenario
                {
                    Id = "3_report_fullname_and_neon",
                    Title = "return workspace full name and Enum.Material.Neon value",
                    UserTask =
                        "Return a single string that contains both the full name of workspace " +
                        "and the numeric value of Enum.Material.Neon.",
                    Verify = (_, result) =>
                    {
                        string output = result.Output ?? "";
                        bool hasWorkspace =
                            output.IndexOf("Workspace", StringComparison.OrdinalIgnoreCase) >= 0;
                        // Enum.Material.Neon.Value == 288 (RbxEnum.cs).
                        bool hasNeon = output.Contains("288");
                        if (hasWorkspace && hasNeon)
                        {
                            return (true, $"output '{output}' contains 'Workspace' and '288'");
                        }

                        return (false,
                            $"output '{output}' missing " +
                            (hasWorkspace ? "" : "'Workspace' ") + (hasNeon ? "" : "'288'"));
                    }
                }
            };
        }

        // ---- Per-scenario execution (with one error-fed retry) --------------------------------

        public sealed class Attempt
        {
            public string ModelReply;
            public string ExtractedLua;
            public bool ExecSuccess;
            public string ExecOutput;
            public string ExecError;
            public bool Verified;
            public string VerifyDetail;
        }

        public sealed class ScenarioResult
        {
            public string Id;
            public string Title;
            public string UserTask;
            public readonly List<Attempt> Attempts = new List<Attempt>();
            public bool Passed;
            public bool RetryNeeded;
            public bool RetryHelped;
        }

        /// <summary>
        /// Runs one scenario end-to-end against the REAL stack: fresh world, ask the model, extract
        /// Lua, execute through the one-off executor, verify world state. On failure, retry ONCE
        /// feeding the Lua error (or a world-mismatch note) back to the model — this is the probe of
        /// whether our RbxError "CODE | fix" format lets a 4B self-correct.
        /// </summary>
        public static async Task<ScenarioResult> RunScenarioAsync(
            Scenario scenario, CancellationToken cancellationToken = default)
        {
            ScenarioResult result = new()
            {
                Id = scenario.Id,
                Title = scenario.Title,
                UserTask = scenario.UserTask
            };

            using HttpClient client = new() { Timeout = TimeSpan.FromSeconds(240) };

            List<JObject> messages = new()
            {
                Message("system", SystemPrompt),
                Message("user", scenario.UserTask)
            };

            for (int attemptIndex = 0; attemptIndex < 2; attemptIndex++)
            {
                Attempt attempt = new();
                result.Attempts.Add(attempt);

                attempt.ModelReply =
                    await ChatAsync(client, messages, cancellationToken).ConfigureAwait(false);
                attempt.ExtractedLua = ExtractLua(attempt.ModelReply);

                // Fresh world per attempt so a retry is a clean re-attempt, not a diff against a
                // half-built tree from the first try.
                LuaCsRobloxApiBindings roblox = new();
                LuaCsModStack stack = BuildStack(roblox);

                LuaTool.LuaResult exec = await stack.ToolExecutor
                    .ExecuteAsync(attempt.ExtractedLua, cancellationToken).ConfigureAwait(false);
                attempt.ExecSuccess = exec.Success;
                attempt.ExecOutput = exec.Output;
                attempt.ExecError = exec.Error;

                (bool ok, string detail) = exec.Success
                    ? scenario.Verify(roblox, exec)
                    : (false, "execution failed before world could be inspected");
                attempt.Verified = ok;
                attempt.VerifyDetail = detail;

                if (ok)
                {
                    result.Passed = true;
                    result.RetryHelped = attemptIndex > 0;
                    return result;
                }

                if (attemptIndex == 0)
                {
                    result.RetryNeeded = true;

                    // Feed the failure back. Prefer the real Lua error (the CODE | fix text); fall
                    // back to a world-mismatch note when the script ran but built the wrong thing.
                    string feedback = !exec.Success
                        ? "Your script errored when execute_lua ran it:\n" + exec.Error +
                          "\nFix it and reply with ONLY a corrected ```lua``` block."
                        : "Your script ran without error but produced the wrong result: " + detail +
                          "\nFix it and reply with ONLY a corrected ```lua``` block.";

                    messages.Add(Message("assistant", attempt.ModelReply));
                    messages.Add(Message("user", feedback));
                }
            }

            return result;
        }

        /// <summary>
        /// Diagnostic: build the real stack and run one fixed Lua snippet through the one-off
        /// executor, bypassing the LLM. Lets a harness confirm the execution path independently of
        /// model latency. Returns the raw executor result.
        /// </summary>
        public static async Task<LuaTool.LuaResult> RunLuaOnceAsync(
            string lua, CancellationToken cancellationToken = default)
        {
            LuaCsRobloxApiBindings roblox = new();
            LuaCsModStack stack = BuildStack(roblox);
            return await stack.ToolExecutor.ExecuteAsync(lua, cancellationToken).ConfigureAwait(false);
        }

        // ---- Stack + HTTP + extraction helpers ------------------------------------------------

        private static LuaCsModStack BuildStack(LuaCsRobloxApiBindings roblox)
        {
            // WorldEdit is part of LuaCapabilities.All, which is what production grants; Instance.new
            // is only installed on the WorldEdit tier (see LuaCsRobloxApiBindings.Register).
            return LuaCsModRuntimeFactory.Create(new LuaCsModStackOptions
            {
                Logger = new SilentGameLogger(),
                Capabilities = LuaCapabilities.All,
                OneOffCapabilities = LuaCapabilities.All,
                RobloxApi = roblox
            });
        }

        private static RbxInstance Workspace(LuaCsRobloxApiBindings roblox)
        {
            return roblox.Game.FindFirstChildOfClass("Workspace");
        }

        private static async Task<string> ChatAsync(
            HttpClient client, List<JObject> messages, CancellationToken cancellationToken)
        {
            JObject request = new()
            {
                ["model"] = Model,
                ["temperature"] = 0.1,
                // WHY: qwen_qwen3.5-4b is a reasoning model — it spends tokens in reasoning_content
                // before emitting the answer in content, so a small budget truncates the code block.
                ["max_tokens"] = 4000,
                ["stream"] = false,
                ["messages"] = new JArray(messages)
            };

            using StringContent content = new(
                request.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
            using HttpResponseMessage response = await client
                .PostAsync(BaseUrl + "/chat/completions", content, cancellationToken)
                .ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"chat/completions HTTP {(int)response.StatusCode}: {Trim(body, 400)}");
            }

            JObject root = JObject.Parse(body);
            JToken message = root["choices"]?[0]?["message"];
            string text = (string)message?["content"];
            if (string.IsNullOrWhiteSpace(text))
            {
                // Some reasoning servers place the answer in reasoning_content when content is empty.
                text = (string)message?["reasoning_content"] ?? "";
            }

            return text ?? "";
        }

        /// <summary>
        /// Pulls the Lua out of the model reply: a ```lua fenced block if present, else any fenced
        /// block, else the whole reply with any &lt;think&gt; block stripped.
        /// </summary>
        public static string ExtractLua(string reply)
        {
            if (string.IsNullOrEmpty(reply))
            {
                return "";
            }

            reply = Regex.Replace(reply, "<think>.*?</think>", "",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);

            Match fenced = Regex.Match(reply, "```[ \\t]*lua[ \\t]*\\r?\\n(.*?)```",
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (fenced.Success)
            {
                return fenced.Groups[1].Value.Trim();
            }

            Match anyFence = Regex.Match(reply, "```[ \\t]*\\r?\\n?(.*?)```", RegexOptions.Singleline);
            if (anyFence.Success)
            {
                return anyFence.Groups[1].Value.Trim();
            }

            return reply.Trim();
        }

        private static JObject Message(string role, string content)
        {
            return new JObject { ["role"] = role, ["content"] = content };
        }

        private static string FirstNonEmpty(string a, string b)
        {
            return string.IsNullOrWhiteSpace(a) ? b : a.Trim();
        }

        private static string Trim(string s, int max)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= max)
            {
                return s ?? "";
            }

            return s.Substring(0, max) + "…";
        }

        /// <summary>Null-object logger so the factory's optional logger dependency is satisfied.</summary>
        private sealed class SilentGameLogger : IGameLogger
        {
            public void LogDebug(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogInfo(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogWarning(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
            public void LogError(GameLogFeature feature, string message, UnityEngine.Object context = null) { }
        }

        /// <summary>Renders a scenario result as a plain-text transcript for logs / harness stdout.</summary>
        public static string FormatTranscript(ScenarioResult r)
        {
            StringBuilder sb = new();
            sb.AppendLine($"=== Scenario {r.Id} — {(r.Passed ? "PASS" : "FAIL")} ===");
            sb.AppendLine($"Title : {r.Title}");
            sb.AppendLine($"Task  : {r.UserTask}");
            sb.AppendLine($"Retry : needed={r.RetryNeeded} helped={r.RetryHelped}");
            for (int i = 0; i < r.Attempts.Count; i++)
            {
                Attempt a = r.Attempts[i];
                sb.AppendLine($"--- attempt {i + 1} ---");
                sb.AppendLine("model lua:");
                sb.AppendLine(a.ExtractedLua);
                sb.AppendLine($"exec: success={a.ExecSuccess} output={Quote(a.ExecOutput)} " +
                              $"error={Quote(a.ExecError)}");
                sb.AppendLine($"verify: {a.Verified} — {a.VerifyDetail}");
            }

            return sb.ToString();
        }

        private static string Quote(string s)
        {
            return s == null ? "<null>" : "'" + s.Replace("\n", " ").Replace("\r", " ") + "'";
        }

        static RobloxApi4BLiveCheckRunner()
        {
            _ = CultureInfo.InvariantCulture;
        }
    }
}
