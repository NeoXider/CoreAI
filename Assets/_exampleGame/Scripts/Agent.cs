using CoreAI.Ai;
using Neo;
using UnityEngine;

public class Agent : MonoBehaviour
{
    private AgentConfig _agentWithTools;
    private AgentConfig _agentChatOnly;

    [Header("Тестовые сообщения")]
    [TextArea(2, 5)]
    public string MessageWithTools = "Привет! Запомни, что мой любимый цвет красный.";

    [TextArea(2, 5)]
    public string MessageChatOnly = "Сделай текст жирным: Я очень рад тебя видеть!";
    
    void Start()
    {
        // 1. Агент с инструментами (проверяем, что может вызвать инструмент и вернуть чистый текст)
        _agentWithTools = new AgentBuilder("агент_инструменты")
            .WithMode(AgentMode.ToolsAndChat)
            .WithMemory()
            .WithSystemPrompt("Если просят что-то запомнить, вызови инструмент с именем 'memory'. Пример: ```json\n{\"name\": \"memory\", \"arguments\": {\"data\": \"твой текст\"}}\n```\nИначе отвечай обычным текстом, БЕЗ JSON.")
            .Build();

        // 2. Агент без инструментов (чистый чат, проверяем запрет Markdown)
        _agentChatOnly = new AgentBuilder("агент_просто_чат")
            .WithMode(AgentMode.ChatOnly)
            .WithSystemPrompt("Ты обычный собеседник. Отвечай коротко и ясно.")
            .Build();

        _agentWithTools.ApplyToPolicy(CoreAIAgent.Policy);
        _agentChatOnly.ApplyToPolicy(CoreAIAgent.Policy);
    }

    [Button]
    public void AskWithTools()
    {
        Debug.Log($"<color=cyan>[TEST]</color> Отправляем агенту С ИНСТРУМЕНТАМИ: {MessageWithTools}");
        _agentWithTools.Ask(MessageWithTools, (s) => Debug.Log($"<color=green>[С ИНСТРУМЕНТАМИ]</color> Ответ: {s}"));
    }

    [Button]
    public void AskChatOnly()
    {
        Debug.Log($"<color=cyan>[TEST]</color> Отправляем агенту ПРОСТО ЧАТ: {MessageChatOnly}");
        _agentChatOnly.Ask(MessageChatOnly, (s) => Debug.Log($"<color=yellow>[ПРОСТО ЧАТ]</color> Ответ: {s}"));
    }

    [Button]
    public void ClearMemory()
    {
        _agentWithTools.ClearMemory();
        _agentChatOnly.ClearMemory();
        Debug.Log("<color=cyan>[TEST]</color> Память агентов полностью очищена!");
    }
}
