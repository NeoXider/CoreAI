using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CoreAI;
using CoreAI.Ai;
using CoreAI.Infrastructure.Llm;
using CoreAI.Infrastructure.Llm.Gateway;
using CoreAI.Logging;
using Microsoft.Extensions.AI;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args.Contains("--help"))
        {
            Console.WriteLine("CoreAI: agent with a tool and a multi-file skill, no Unity.\n" +
                "Run: dotnet run --project examples/dotnet -- \"How much iron is in stock?\"\n" +
                "COREAI_ENDPOINT — URL of an OpenAI-compatible API with /v1 (required).\n" +
                "COREAI_MODEL — model id with native tool calling (required in provider mode).\n" +
                "COREAI_API_KEY — provider key; may be empty for a local server.\n" +
                "--server-managed uses COREAI_GATEWAY_TOKEN and lets the server select a missing model.\n" +
                "--allow-insecure-http explicitly enables an HTTP gateway selected by the host.\n" +
                "--help and an argument-free launch make no network requests. Ctrl+C cancels the request.");
            return 0;
        }

        bool serverManaged = args.Contains("--server-managed");
        bool allowInsecureHttp = args.Contains("--allow-insecure-http");
        string[] requestArgs = args.Where(argument => argument != "--server-managed" &&
            argument != "--allow-insecure-http").ToArray();
        if (requestArgs.Length == 0)
        {
            Console.Error.WriteLine("Provide a request or use --help.");
            return 2;
        }
        string endpoint = Environment.GetEnvironmentVariable("COREAI_ENDPOINT") ?? "";
        string model = Environment.GetEnvironmentVariable("COREAI_MODEL") ?? "";
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            (!serverManaged && string.IsNullOrWhiteSpace(model)))
        {
            Console.Error.WriteLine("Set COREAI_ENDPOINT and a provider model, or use --server-managed. See --help.");
            return 2;
        }

        if (serverManaged)
        {
            try { GatewayClientOptions.ValidateEndpoint(endpoint, allowInsecureHttp); }
            catch (ArgumentException)
            {
                Console.Error.WriteLine("Gateway mode requires HTTPS or explicitly enabled HTTP.");
                return 2;
            }
        }

        Dictionary<string, int> stock = new(StringComparer.OrdinalIgnoreCase)
        {
            ["iron"] = 12,
            ["wood"] = 30
        };
        DelegateLlmTool stockTool = new("get_stock", "Read stock by item id: iron or wood.",
            new Func<string, string>((string item) => stock.TryGetValue(item, out int count)
                ? $"{item}: {count}"
                : "Unknown item. Supported ids: iron, wood."))
        {
            AllowDuplicates = true
        };
        SkillSet inventory = SkillSet.FromTextParts("Inventory", "Read the application's supply stock.",
            new KeyValuePair<string, string>[]
            {
                new("SKILL.md", "Read references/items.md for item ids, then call get_stock. " +
                    "Use the observed count; never invent stock. This skill is read-only."),
                new("references/items.md", "iron = pig iron bars; wood = timber planks. Counts are individual units.")
            }, stockTool);
        IReadOnlyList<SkillSet> skills = new[] { inventory };
        ILlmTool[] tools = { ReadSkillLlmTool.Create(skills), CallSkillToolLlmTool.Create(skills) };
        ChatOptions options = new()
        {
            Tools = tools.Select(tool => (AITool)((IAIFunctionLlmTool)tool).CreateAIFunction()).ToList()
        };
        OpenAiHttpOptions http = new()
        {
            ApiBaseUrl = endpoint,
            ApiKey = serverManaged ? "" : Environment.GetEnvironmentVariable("COREAI_API_KEY") ?? "",
            ExecutionMode = serverManaged ? LlmExecutionMode.ServerManagedApi : LlmExecutionMode.ClientOwnedApi,
            Model = model,
            MaxTokens = 2048,
            RequestTimeoutSeconds = 60,
            LogLlmInput = false,
            LogLlmOutput = false
        };
        CoreAISettingsOptions settings = new()
        {
            MaxToolCallRoundtrips = 8,
            DefaultToolTimeoutMs = 5000,
            LogToolCallArguments = false,
            LogToolCallResults = false
        };
        IChatClient backend = serverManaged
            ? GatewayClientFactory.Create(http, new HttpClientOpenAiTransport(),
                new GatewayClientOptions { AllowInsecureHttp = allowInsecureHttp },
                authContextProvider: new EnvironmentGatewayAuthContext())
            : new MeaiOpenAiChatClient(http);
        using SmartToolCallingChatClient client = new(backend,
            NullLog.Instance, settings, false, tools, "Quartermaster");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(90));
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;
        try
        {
            ChatMessage[] messages =
            {
                new(ChatRole.System, "You are a quartermaster. Answer in the user's language. " +
                    "Read the relevant skill with read_skill before using call_skill_tool.\n" +
                    SkillSet.BuildCatalog(skills)),
                new(ChatRole.User, string.Join(" ", requestArgs))
            };
            ChatResponse response = await client.GetResponseAsync(messages, options, cancellation.Token);
            ChatMessage? answer = response.Messages.LastOrDefault(message => message.Role == ChatRole.Assistant);
            Console.WriteLine(answer?.Text ?? "The model returned no text answer.");
            Console.WriteLine($"Tool calls executed: {client.LastExecutedToolCalls.Count}");
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Request cancelled or exceeded the overall 90-second limit.");
            return 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Request failed: {exception.GetType().Name}. " +
                "Check API availability, the model id, and native tool calling support.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    /// <summary>The application owns its token source; no login or provisioning service is implied.</summary>
    private sealed class EnvironmentGatewayAuthContext : ILlmAuthContextProvider
    {
        public string GetAuthorizationHeader()
        {
            string token = Environment.GetEnvironmentVariable("COREAI_GATEWAY_TOKEN") ?? "";
            return string.IsNullOrWhiteSpace(token) ? "" : "Bearer " + token.Trim();
        }
        public string TenantId => "";
        public string UserId => "";
        public string SessionId => "";
    }
}
