namespace Spire.MultiRepo.E2E.Support;

internal static class E2ERedactor
{
    public const string DummyUserName = "e2e-user";
    public const string DummyPassword = "e2e-password";
    public const string DummyQueryToken = "e2e-query-token";
    public const string DummyFragment = "e2e-fragment";

    public static IReadOnlyList<string> SensitiveValues { get; } =
        [DummyUserName, DummyPassword, DummyQueryToken, DummyFragment];

    public static string Redact(string value) => value
        .Replace(DummyUserName, "[REDACTED]", StringComparison.Ordinal)
        .Replace(DummyPassword, "[REDACTED]", StringComparison.Ordinal)
        .Replace(DummyQueryToken, "[REDACTED]", StringComparison.Ordinal)
        .Replace(DummyFragment, "[REDACTED]", StringComparison.Ordinal);
}
