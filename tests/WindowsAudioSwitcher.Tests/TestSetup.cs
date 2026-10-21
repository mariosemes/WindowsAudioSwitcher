using System.IO;
using System.Runtime.CompilerServices;

namespace WindowsAudioSwitcher.Tests;

internal static class TestSetup
{
    /// <summary>
    /// Runs before any test touches the app's types. Sandboxes the app so the
    /// logger and settings store write to a temp folder instead of the user's
    /// real %APPDATA%\WindowsAudioSwitcher.
    /// </summary>
#pragma warning disable CA2255 // ModuleInitializer in a test assembly is intentional.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WindowsAudioSwitcher.Tests");
        Environment.SetEnvironmentVariable(AppEnvironment.DataDirVariable, dir);
    }
}

/// <summary>A fresh temp folder per test, deleted afterwards.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "WindowsAudioSwitcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}
