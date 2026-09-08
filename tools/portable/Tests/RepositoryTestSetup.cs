using System;
using System.IO;
using NUnit.Framework;

[SetUpFixture]
public sealed class RepositoryTestSetup
{
    private string _previousDirectory;

    [OneTimeSetUp]
    public void SetRepositoryWorkingDirectory()
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "tools", "portable", "CoreAI.Core.csproj")))
        {
            directory = directory.Parent;
        }

        if (directory == null)
        {
            throw new DirectoryNotFoundException("Portable source-contract tests require the CoreAI repository checkout.");
        }

        _previousDirectory = Environment.CurrentDirectory;
        Environment.CurrentDirectory = directory.FullName;
    }

    [OneTimeTearDown]
    public void RestoreWorkingDirectory()
    {
        if (_previousDirectory != null)
        {
            Environment.CurrentDirectory = _previousDirectory;
        }
    }
}
