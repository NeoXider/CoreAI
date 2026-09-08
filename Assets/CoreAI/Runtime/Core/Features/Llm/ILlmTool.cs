using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;

namespace CoreAI.Ai
{
    /// <summary>
    /// Defines the contract for llm tool implementations.
    /// </summary>
    public interface ILlmTool
    {
        /// <summary>Public name.</summary>
        string Name { get; }

        /// <summary>Human-readable description.</summary>
        string Description { get; }

        /// <summary>JSON schema describing tool parameters.</summary>
        string ParametersSchema { get; }

        /// <summary>True when repeated calls with the same arguments are meaningful and should not be suppressed.</summary>
        bool AllowDuplicates { get; }

        /// <summary>
        /// Per-tool override of <see cref="ICoreAISettings.DefaultToolTimeoutMs"/> for this tool's body.
        /// <c>null</c> (the default) keeps the global setting; a positive value is that tool's own budget
        /// in milliseconds; <c>0</c> or a negative value disables the per-call deadline for it entirely —
        /// the same meaning <c>0</c> already has globally.
        /// <para>
        /// Exists for tools that WAIT FOR A HUMAN — a quiz card, a drag-and-drop exercise, a confirmation
        /// prompt. Their body is idle by design for as long as the person is thinking, so a global default
        /// sized for a hung HTTP call cuts them off mid-question and the model is told the tool "timed out"
        /// while the user is still reading it. Raising the global setting instead would take that protection
        /// away from every other tool, which is exactly why the lever is per tool.
        /// </para>
        /// <para>
        /// Disabling the deadline does NOT make the turn unbounded: <see cref="ICoreAISettings.LlmRequestTimeoutSeconds"/>
        /// still cancels the request, and that cancellation reaches the tool body through the token it is
        /// invoked with. It is enforced by <c>TimeoutLlmClientDecorator</c> and, on Unity, additionally by
        /// <c>CoreAiChatService</c> through the PlayerLoop-based <c>CancelAfterSlim</c> (a managed timer is
        /// unreliable on WebGL). Both treat it as a no-progress budget on the streaming path — each yielded
        /// chunk re-arms it, and a blocked tool yields none — while <c>CoreAiChatService</c> re-arms it on
        /// tool-call events for the non-streaming path; either way the window ends a WAITING tool a full
        /// window after it started. Two cases leave no ceiling at all, and they are why a large finite value
        /// beats disabling: <c>LlmRequestTimeoutSeconds &lt;= 0</c>, and the mid-stream-abort drain in
        /// <c>ToolExecutionPolicy.CompleteStreamedTurnAsync</c>, which passes
        /// <see cref="CancellationToken.None"/> on purpose and therefore has nothing but this deadline.
        /// </para>
        /// <para>
        /// The <c>call_skill_tool</c> proxy resolves its target once before scheduling; the same target's
        /// timeout and binding are used for the entire call, including live skill catalogs.
        /// </para>
        /// </summary>
        int? ToolTimeoutMsOverride => null;

        /// <summary>
        /// True when a SUCCESSFUL call of this tool ENDS the model's turn: the agentic loop closes the
        /// turn instead of handing the tool result back for another roundtrip. Other calls already
        /// emitted in the same batch still complete under the normal ordering policy.
        /// <para>
        /// Exists for tools that hand control to a HUMAN — a quiz card, a drag-and-drop exercise, a
        /// confirmation prompt. Their result ("card shown, waiting for the student") is not material the
        /// model can continue from: given another roundtrip it writes the reaction to an answer nobody has
        /// given yet, and the student reads "Correct!" under a card they have not touched. Holding that with
        /// prompt text alone does not hold it at all, which is why the rule lives in the contract.
        /// </para>
        /// <para>
        /// Only a SUCCESSFUL call ends the turn — a failed one must still reach the model so it can recover.
        /// Prose the model produced BEFORE the call stays visible to the user; only the NEXT roundtrip is
        /// cut. A turn that ends with no visible text at all is already handled upstream (the orchestrator
        /// must not synthesize an extra chat line).
        /// </para>
        /// <para>
        /// Enforced by <c>ToolExecutionPolicy.TurnEndingToolSucceeded</c>. Direct calls use the tool's
        /// metadata; <c>call_skill_tool</c> captures the permitted inner target and applies its metadata
        /// before invocation. A later live-catalog update cannot change an in-flight call.
        /// </para>
        /// </summary>
        bool EndsTurn => false;

        /// <summary>
        /// True, когда инструмент МЕНЯЕТ общее состояние — мир, память, файлы, реестр. Мутирующие
        /// инструменты никогда не выполняются ОДНОВРЕМЕННО друг с другом внутри одного хода: политика
        /// исполнения ставит их в одну упорядоченную цепочку, а read-only инструменты продолжают идти
        /// параллельно под <see cref="ICoreAISettings.MaxParallelToolCalls"/>. Две мутации, гоняющиеся за
        /// одним хранилищем, теряют записи или читают рваное состояние, и автор инструмента не может
        /// защититься от этого изнутри тела — поэтому флаг объявляется в контракте.
        /// <para>
        /// По умолчанию <c>false</c>: необъявленный инструмент считается read-only и МОЖЕТ перекрываться с
        /// любым другим вызовом хода. Объявляйте флаг у каждого инструмента с побочным эффектом. Встроенные
        /// мутирующие инструменты (<c>memory</c>, <c>manage_mods</c>, <c>manage_skills</c>,
        /// <c>world_command</c>, <c>component_command</c>, <c>execute_lua</c>, <c>call_skill_tool</c>)
        /// политика узнаёт и по имени, поэтому хост, зарегистрировавший их под этими именами, сохраняет
        /// гарантию без правок.
        /// </para>
        /// <para>
        /// Флаг НЕ связан с подавлением эха: повторный вызов подавляется по своей per-call сигнатуре у
        /// любого инструмента с <see cref="AllowDuplicates"/> = <c>false</c>, мутирующего или нет.
        /// Разрешается ПО ИМЕНИ из списка инструментов роли, как и <see cref="ToolTimeoutMsOverride"/>.
        /// </para>
        /// </summary>
        bool IsMutating => false;
    }

    /// <summary>
    /// LLM tool that can expose itself as a single Microsoft.Extensions.AI function without reflection.
    /// The returned function must complete with a serializable result for the model; null or empty payloads
    /// are normalized by the tool execution policy into an explicit tool-result message.
    /// </summary>
    public interface IAIFunctionLlmTool : ILlmTool
    {
        /// <summary>Creates the MEAI function binding for this tool.</summary>
        AIFunction CreateAIFunction();
    }

    /// <summary>
    /// LLM tool that expands into several Microsoft.Extensions.AI functions without reflection.
    /// Each returned function must complete with a serializable result for the model; null or empty payloads
    /// are normalized by the tool execution policy into an explicit tool-result message.
    /// </summary>
    public interface IAIFunctionsLlmTool : ILlmTool
    {
        /// <summary>Creates the MEAI function bindings for this tool.</summary>
        IEnumerable<AIFunction> CreateAIFunctions();
    }

    /// <summary>
    /// LLM tool that can be invoked by a skill proxy from raw JSON arguments without using reflection.
    /// This is the preferred runtime contract for tools that live behind <c>call_skill_tool</c>.
    /// </summary>
    public interface IJsonInvocableLlmTool : ILlmTool
    {
        /// <summary>
        /// Invokes the tool with a JSON object string containing the tool arguments.
        /// The returned value is serialized and delivered back to the model as the tool result.
        /// </summary>
        Task<object> InvokeJsonAsync(string argumentsJson, CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Base class for strongly typed LLM tools.
    /// </summary>
    public abstract class LlmToolBase : ILlmTool
    {
        public abstract string Name { get; }
        public abstract string Description { get; }
        public virtual string ParametersSchema => "{}";
        public virtual bool AllowDuplicates => false;

        /// <summary>
        /// Override in a tool whose body waits for a human; see <see cref="ILlmTool.ToolTimeoutMsOverride"/>
        /// for the value meaning and for what still bounds the turn when the deadline is removed.
        /// </summary>
        public virtual int? ToolTimeoutMsOverride => null;

        /// <summary>
        /// Override in a tool that hands control to a human and therefore must be the LAST thing that
        /// happens in the turn; see <see cref="ILlmTool.EndsTurn"/> for the exact semantics and for what
        /// still reaches the model when the call fails.
        /// </summary>
        public virtual bool EndsTurn => false;

        /// <summary>
        /// Переопределите в инструменте с побочным эффектом; см. <see cref="ILlmTool.IsMutating"/> — что
        /// именно гарантирует флаг и почему он не заменяет подавление эха.
        /// </summary>
        public virtual bool IsMutating => false;

        protected static string JsonParams(params (string name, string type, bool required, string desc)[] p)
        {
            List<string> props = new();
            List<string> requiredProps = new();
            foreach ((string name, string type, bool required, string desc) in p)
            {
                props.Add($"\"{name}\":{{\"type\":\"{type}\",\"description\":\"{desc}\"}}");
                if (required)
                {
                    requiredProps.Add($"\"{name}\"");
                }
            }

            string requiredPart = requiredProps.Count > 0 ? $",\"required\":[{string.Join(",", requiredProps)}]" : "";
            return $"{{\"type\":\"object\",\"properties\":{{{string.Join(",", props)}}}{requiredPart}}}";
        }
    }
}
