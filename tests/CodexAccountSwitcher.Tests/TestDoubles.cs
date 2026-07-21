using CodexAccountSwitcher.Models;

namespace CodexAccountSwitcher.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "CodexAccountSwitcher.Tests", Guid.NewGuid().ToString("N"));

    public TemporaryDirectory() => Directory.CreateDirectory(Path);

    public void Write(string relativePath, string content)
    {
        var fullPath = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, content);
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}

internal sealed class TemporaryFile : IDisposable
{
    public string Path { get; }

    private TemporaryFile(string content)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"cas-{Guid.NewGuid():N}.json");
        File.WriteAllText(Path, content);
    }

    public static TemporaryFile Json(string content) => new(content);

    public void Dispose()
    {
        if (File.Exists(Path)) File.Delete(Path);
    }
}

internal static class Accounts
{
    public static AccountRecord Record(string key, string email, string alias = "") =>
        new(key, "acct", "user", email, alias, null, "plus", "chatgpt");
}
