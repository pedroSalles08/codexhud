namespace CodexHud.Core;

public interface IContextRemainingPolicy
{
    int? Calculate(long? lastTokenUsageTotal, long? modelContextWindow);
}

/// <summary>
/// Reproduces the context percentage calculation observed in the current Codex Desktop /status.
/// Kept behind an interface because Codex may change this accounting between versions.
/// </summary>
public sealed class CodexContextRemainingPolicy : IContextRemainingPolicy
{
    public int? Calculate(long? lastTokenUsageTotal, long? modelContextWindow)
    {
        if (lastTokenUsageTotal is null || modelContextWindow is null)
        {
            return null;
        }

        if (lastTokenUsageTotal.Value < 0 || modelContextWindow.Value < 0)
        {
            return null;
        }

        var contextWindow = modelContextWindow.Value;
        if (contextWindow == 0)
        {
            return 0;
        }

        var remaining = Math.Max(contextWindow - lastTokenUsageTotal.Value, 0);
        var percentage = (double)remaining / contextWindow * 100d;

        return (int)Math.Clamp(
            Math.Round(percentage, MidpointRounding.AwayFromZero),
            0d,
            100d);
    }
}

public static class CurrentCodexContextPolicy
{
    public static IContextRemainingPolicy Create() =>
        new CodexContextRemainingPolicy();
}
