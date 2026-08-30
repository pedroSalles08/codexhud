using CodexHud.Core;

namespace CodexHud.Core.Tests;

[TestClass]
public sealed class ContextRemainingPolicyTests
{
    private readonly CodexContextRemainingPolicy _policy = new();

    [TestMethod]
    public void MatchesObservedCurrentDesktopStatusFormula()
    {
        Assert.AreEqual(100, _policy.Calculate(0, 258_400));
        Assert.AreEqual(68, _policy.Calculate(81_478, 258_400));
        Assert.AreEqual(50, _policy.Calculate(129_200, 258_400));
        Assert.AreEqual(0, _policy.Calculate(300_000, 258_400));
    }

    [TestMethod]
    public void UsesAwayFromZeroRoundingLikeRust()
    {
        var policy = new CodexContextRemainingPolicy();
        Assert.AreEqual(51, policy.Calculate(49, 100));
        Assert.AreEqual(51, policy.Calculate(99, 200));
    }

    [TestMethod]
    public void MissingOrInvalidWindowDoesNotThrow()
    {
        Assert.IsNull(_policy.Calculate(null, 258_400));
        Assert.IsNull(_policy.Calculate(25_000, null));
        Assert.IsNull(_policy.Calculate(-1, 258_400));
        Assert.AreEqual(0, _policy.Calculate(25_000, 0));
    }
}
