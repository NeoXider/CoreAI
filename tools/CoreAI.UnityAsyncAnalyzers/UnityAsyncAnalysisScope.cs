using System;

namespace CoreAI.UnityAsyncAnalyzers;

/// <summary>
/// Path scope for <c>CAIU001</c> (CoreAiUnity Runtime/Editor, UPM paths). No Roslyn types.
/// </summary>
public static class UnityAsyncAnalysisScope
{
    /// <summary>
    /// CoreAiUnity production code: <c>Runtime/</c> and <c>Editor/</c>; excludes <c>Tests/</c> and <c>ThirdParty/</c>.
    /// </summary>
    public static bool ShouldAnalyzePath(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        string n = path!.Replace('\\', '/');

        if (Ins(n, "/Tests/"))
            return false;

        if (Ins(n, "/ThirdParty/"))
            return false;

        if (Ins(n, "/CoreAiUnity/Runtime/") || Ins(n, "/CoreAiUnity/Editor/"))
            return true;

        if (Ins(n, "com.neoxider.coreaiunity") &&
            (Ins(n, "/Runtime/") || Ins(n, "/Editor/")))
            return true;

        return false;
    }

    private static bool Ins(string haystack, string needle) =>
        haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
}
