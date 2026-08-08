namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal sealed class RepositoryIdentityComparer : IEqualityComparer<string>
{
    public static RepositoryIdentityComparer Instance { get; } = new();

    private RepositoryIdentityComparer()
    {
    }

    public bool Equals(string? first, string? second)
    {
        if (ReferenceEquals(first, second))
        {
            return true;
        }

        if (first is null || second is null ||
            !Uri.TryCreate(first, UriKind.Absolute, out var firstUri) ||
            !Uri.TryCreate(second, UriKind.Absolute, out var secondUri))
        {
            return false;
        }

        return string.Equals(firstUri.Scheme, secondUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(firstUri.Host, secondUri.Host, StringComparison.OrdinalIgnoreCase) &&
            firstUri.Port == secondUri.Port &&
            string.Equals(
                GetRepositoryPath(firstUri),
                GetRepositoryPath(secondUri),
                IsGitHub(firstUri) && IsGitHub(secondUri)
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal);
    }

    public int GetHashCode(string repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        if (!Uri.TryCreate(repository, UriKind.Absolute, out var uri))
        {
            return StringComparer.Ordinal.GetHashCode(repository);
        }

        var hash = new HashCode();
        hash.Add(uri.Scheme, StringComparer.OrdinalIgnoreCase);
        hash.Add(uri.Host, StringComparer.OrdinalIgnoreCase);
        hash.Add(uri.Port);
        hash.Add(
            GetRepositoryPath(uri),
            IsGitHub(uri) ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return hash.ToHashCode();
    }

    private static bool IsGitHub(Uri repository) =>
        string.Equals(repository.Host, "github.com", StringComparison.OrdinalIgnoreCase);

    private static string GetRepositoryPath(Uri repository) =>
        repository.GetComponents(UriComponents.Path, UriFormat.Unescaped).Trim('/');
}
