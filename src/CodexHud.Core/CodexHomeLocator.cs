namespace CodexHud.Core;

public static class CodexHomeLocator
{
    public static string FindCodexHome()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex");
    }

    public static string FindSessionsDirectory(string? codexHome = null) =>
        Path.Combine(codexHome ?? FindCodexHome(), "sessions");
}
