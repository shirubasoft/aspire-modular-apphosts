using Xunit;

namespace CrapScore.Tests;

public sealed class CrapGateTests
{
    [Fact]
    public void Evaluate_requires_any_strict_reduction()
    {
        var result = CrapGate.Evaluate(100, baseScore: 100);

        Assert.False(result.Passed);
        Assert.Contains("must be lower", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_rejects_floating_point_noise_as_a_reduction()
    {
        var result = CrapGate.Evaluate(283.69599999999997, baseScore: 283.696);

        Assert.False(result.Passed);
    }

    [Fact]
    public void Evaluate_accepts_any_measurable_reduction()
    {
        var result = CrapGate.Evaluate(99.999, baseScore: 100);

        Assert.True(result.Passed);
        Assert.False(result.Disabled);
    }

    [Fact]
    public void Evaluate_stops_checking_after_target_branch_reaches_five()
    {
        var result = CrapGate.Evaluate(6, baseScore: 5);

        Assert.True(result.Passed);
        Assert.True(result.Disabled);
    }
}
