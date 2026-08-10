using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class FullControlPreviewSchemaTests
{
    [Fact]
    public async Task Schema_keeps_source_identity_out_of_manifest_and_enforces_tag_grammar()
    {
        var schemaPath = Path.Combine(
            AppContext.BaseDirectory,
            "TestData",
            "full-control-module-preview.schema.json");
        await using var stream = File.OpenRead(schemaPath);
        using var schema = await JsonDocument.ParseAsync(
            stream,
            cancellationToken: TestContext.Current.CancellationToken);
        var root = schema.RootElement;

        Assert.Equal(
            "https://raw.githubusercontent.com/shirubasoft/aspire-modular-apphosts/main/schemas/full-control-module-preview.schema.json",
            root.GetProperty("$id").GetString());
        Assert.False(root.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(
            1,
            root.GetProperty("properties").GetProperty("schemaVersion").GetProperty("const").GetInt32());
        Assert.False(root.GetProperty("properties").TryGetProperty("sourceRepository", out _));
        Assert.False(root.GetProperty("properties").TryGetProperty("sourceRef", out _));

        var pattern = root
            .GetProperty("$defs")
            .GetProperty("containerTag")
            .GetProperty("pattern")
            .GetString()!;
        var regex = new Regex(pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
        Assert.Matches(regex, "feat-catalog.42");
        Assert.DoesNotMatch(regex, "feat/catalog:42");
    }
}
