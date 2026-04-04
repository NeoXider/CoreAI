using System.Text;

namespace CoreAI.Ai
{
    public static class DataOverlayVersionPromptFormatter
    {
        private const int MaxChars = 8000;

        public static string Format(string overlayKey, DataOverlayVersionRecord snapshot)
        {
            if (string.IsNullOrWhiteSpace(overlayKey))
            {
                return "";
            }

            StringBuilder sb = new(256);
            sb.Append("## Data_overlay_versioning\n");
            sb.Append("overlay_key: ").Append(overlayKey.Trim()).Append('\n');
            if (snapshot == null)
            {
                sb.Append(
                    "No saved overlays for this key yet. First successful coreai_data_apply establishes baseline.\n");
                return sb.ToString();
            }

            sb.Append("revision_count: ").Append(snapshot.History.Count).Append('\n');
            sb.Append("original_payload_baseline:\n```json\n");
            sb.Append(Clamp(snapshot.OriginalPayload)).Append("\n```\n");
            sb.Append("current_payload:\n```json\n");
            sb.Append(Clamp(snapshot.CurrentPayload)).Append("\n```\n");
            return sb.ToString();
        }

        private static string Clamp(string s)
        {
            if (string.IsNullOrEmpty(s))
            {
                return "";
            }

            if (s.Length <= MaxChars)
            {
                return s;
            }

            return s.Substring(0, MaxChars) + "\n…";
        }
    }
}