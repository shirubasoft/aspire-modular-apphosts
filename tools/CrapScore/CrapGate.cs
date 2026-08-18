using System.Globalization;

namespace CrapScore;

internal sealed record CrapGateResult(bool Passed, bool Disabled, string Message);

internal static class CrapGate
{
    public const double StopCheckingAt = 5;

    public static CrapGateResult Evaluate(double currentScore, double baseScore)
    {
        currentScore = Canonicalize(currentScore);
        baseScore = Canonicalize(baseScore);

        if (baseScore <= StopCheckingAt)
        {
            return new CrapGateResult(
                Passed: true,
                Disabled: true,
                $"CRAP reduction gate disabled: the target-branch maximum is {Format(baseScore)}, at or below 5.");
        }

        if (currentScore >= baseScore)
        {
            return new CrapGateResult(
                Passed: false,
                Disabled: false,
                $"CRAP reduction required: current maximum {Format(currentScore)} must be lower than target-branch maximum {Format(baseScore)}.");
        }

        return new CrapGateResult(
            Passed: true,
            Disabled: false,
            $"CRAP maximum reduced from {Format(baseScore)} on the target branch to {Format(currentScore)}.");
    }

    internal static double Canonicalize(double value) =>
        Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private static string Format(double value) => value.ToString("F6", CultureInfo.InvariantCulture);
}
