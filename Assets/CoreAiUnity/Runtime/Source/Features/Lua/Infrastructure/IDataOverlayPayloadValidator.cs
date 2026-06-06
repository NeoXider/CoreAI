namespace CoreAI.Infrastructure.Lua
{
    /// <summary>IDataOverlayPayloadValidator interface.</summary>
    public interface IDataOverlayPayloadValidator
    {
        bool TryValidate(string overlayKey, string payload, out string error);
    }

    /// <summary>
    /// Default data overlay validator that accepts payloads without additional checks.
    /// </summary>
    public sealed class DefaultDataOverlayPayloadValidator : IDataOverlayPayloadValidator
    {
        public bool TryValidate(string overlayKey, string payload, out string error)
        {
            error = "";
            string p = (payload ?? "").Trim();
            if (p.Length == 0)
            {
                return true;
            }

            if ((p.StartsWith("{") && p.EndsWith("}")) || (p.StartsWith("[") && p.EndsWith("]")))
            {
                return true;
            }

            error = $"payload for '{overlayKey}' must be JSON object/array or empty.";
            return false;
        }
    }
}