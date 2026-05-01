using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace CoreAI.UnityAsyncAnalyzers;

/// <summary>
/// CoreAiUnity: <c>ConfigureAwait(false)</c> can leave the Unity sync context and break UnityWebRequest, UI, or WebGL
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ConfigureAwaitFalseInUnityCodeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "CAIU001";

    private static readonly LocalizableString Title =
        "Avoid ConfigureAwait(false) in CoreAiUnity async code";

    private static readonly LocalizableString MessageFormat =
        "Do not use ConfigureAwait(false) in CoreAiUnity; await without it or use UniTask.SwitchToMainThread before UnityEngine API";

    private static readonly LocalizableString Description =
        "UnityEngine APIs must run on the main thread. ConfigureAwait(false) often resumes on the thread pool.";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        Title,
        MessageFormat,
        "Unity",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: Description,
        helpLinkUri: "https://docs.unity3d.com/ScriptReference/Networking.UnityWebRequest.html");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext context)
    {
        if (!UnityAsyncAnalysisScope.ShouldAnalyzePath(context.Node.SyntaxTree.FilePath))
            return;

        if (context.Node is not InvocationExpressionSyntax invocation)
            return;

        if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
            return;

        if (!string.Equals(memberAccess.Name.Identifier.Text, "ConfigureAwait", StringComparison.Ordinal))
            return;

        if (invocation.ArgumentList.Arguments.Count != 1)
            return;

        ExpressionSyntax arg = invocation.ArgumentList.Arguments[0].Expression;
        if (arg is not LiteralExpressionSyntax literal ||
            !literal.Token.IsKind(SyntaxKind.FalseKeyword))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
