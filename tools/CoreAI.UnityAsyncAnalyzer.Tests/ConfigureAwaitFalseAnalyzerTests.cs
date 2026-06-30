using System.Collections.Immutable;
using System.Linq;
using System.Threading.Tasks;
using CoreAI.UnityAsyncAnalyzers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace CoreAI.UnityAsyncAnalyzer.Tests;

/// <summary>
/// End-to-end checks that CAIU001 actually fires on <c>ConfigureAwait(false)</c>
/// inside an in-scope CoreAiUnity path, and stays silent out of scope. This guards
/// against the committed analyzer DLL drifting from source.
/// </summary>
public sealed class ConfigureAwaitFalseAnalyzerTests
{
    private const string Source = @"
using System.Threading.Tasks;

public class C
{
    public async Task M()
    {
        await Task.Delay(1).ConfigureAwait(false);
    }
}
";

    private static async Task<ImmutableArray<Diagnostic>> RunAsync(string filePath)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(Source, path: filePath);

        var compilation = CSharpCompilation.Create(
            "TestAsm",
            new[] { tree },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Task).Assembly.Location),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create<DiagnosticAnalyzer>(new ConfigureAwaitFalseInUnityCodeAnalyzer()));

        ImmutableArray<Diagnostic> diags = await withAnalyzers.GetAnalyzerDiagnosticsAsync();
        return diags;
    }

    [Fact]
    public async Task Fires_in_CoreAiUnity_runtime_path()
    {
        ImmutableArray<Diagnostic> diags =
            await RunAsync(@"D:/CoreAI/Assets/CoreAiUnity/Runtime/Source/Http.cs");

        Assert.Contains(diags, d => d.Id == ConfigureAwaitFalseInUnityCodeAnalyzer.DiagnosticId);
    }

    [Fact]
    public async Task Silent_outside_scope()
    {
        ImmutableArray<Diagnostic> diags =
            await RunAsync(@"D:/CoreAI/Assets/CoreAI/Runtime/Core/Http.cs");

        Assert.DoesNotContain(diags, d => d.Id == ConfigureAwaitFalseInUnityCodeAnalyzer.DiagnosticId);
    }
}
