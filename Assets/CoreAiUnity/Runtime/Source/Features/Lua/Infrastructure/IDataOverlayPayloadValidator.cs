namespace CoreAI.Infrastructure.Lua
{
    /// <summary>Опциональная валидация payload перед coreai_data_apply.</summary>
    public interface IDataOverlayPayloadValidator
    {
        bool TryValidate(string overlayKey, string payload, out string error);
    }

    /// <summary>
    /// Дефолтная мягкая валидация: разрешаем JSON object/array и пустую строку.
    /// Для строгих правил игра может подставить свою реализацию в DI.
    /// </summary>
    public sealed class DefaultDataOverlayPayloadValidator : IDataOverlayPayloadValidator
    {
        public bool TryValidate(string overlayKey, string payload, out string error)
        {
            error = "";
            var p = (payload ?? "").Trim();
            if (p.Length == 0)
                return true;
            if ((p.StartsWith("{") && p.EndsWith("}")) || (p.StartsWith("[") && p.EndsWith("]")))
                return true;
            error = $"payload for '{overlayKey}' must be JSON object/array or empty.";
            return false;
        }
    }
}

