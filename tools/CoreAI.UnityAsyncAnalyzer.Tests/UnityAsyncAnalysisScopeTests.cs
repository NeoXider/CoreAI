using CoreAI.UnityAsyncAnalyzers;
using Xunit;

namespace CoreAI.UnityAsyncAnalyzer.Tests;

public sealed class UnityAsyncAnalysisScopeTests
{
    [Theory]
    [InlineData(@"D:/CoreAI/Assets/CoreAiUnity/Runtime/Source/X.cs")]
    [InlineData(@"D:\CoreAI\Assets\CoreAiUnity\Editor\CoreAI.Editor\Y.cs")]
    public void True_for_runtime_and_editor(string path) =>
        Assert.True(UnityAsyncAnalysisScope.ShouldAnalyzePath(path));

    [Theory]
    [InlineData(@"D:/CoreAI/Assets/CoreAiUnity/Tests/EditMode/T.cs")]
    [InlineData(@"D:/p/Library/PackageCache/com.neoxider.coreaiunity@ab/Tests/Foo.cs")]
    public void False_under_Tests(string path) =>
        Assert.False(UnityAsyncAnalysisScope.ShouldAnalyzePath(path));

    [Fact]
    public void True_for_upm_package_cache_runtime() =>
        Assert.True(UnityAsyncAnalysisScope.ShouldAnalyzePath(
            @"D:/p/Library/PackageCache/com.neoxider.coreaiunity@ab/Runtime/Source/Http.cs"));

    [Fact]
    public void False_for_null() =>
        Assert.False(UnityAsyncAnalysisScope.ShouldAnalyzePath(null));
}
