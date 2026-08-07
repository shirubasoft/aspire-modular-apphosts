using System.Text.Json;
using YamlDotNet.Serialization;
using Xunit;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool.Tests;

public sealed class PreviewWorkflowGeneratorTests
{
    private const string SettingsSchema =
        "https://raw.githubusercontent.com/shirubasoft/aspire-modular-apphosts/main/schemas/preview-workflow-settings.schema.json";

    [Fact]
    public async Task Producer_generation_matches_golden_file_and_emits_valid_yaml()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"modular-apphosts-workflow-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outputDirectory);
        try
        {
            var outputPath = Path.Combine(outputDirectory, "producer.yml");
            string[] arguments =
                [
                    "preview", "workflow", "generate", "producer",
                    "--descriptor", "module-preview.producer.json",
                    "--working-directory", Path.Combine(AppContext.BaseDirectory, "TestData"),
                    "--apphost", "src/Sample.AppHost/Sample.AppHost.csproj",
                    "--output", outputPath,
                    "--repo", "example/consumer-tests",
                    "--workflow", "e2e.yml",
                    "--ref", "main",
                    "--aspire-version", "13.4.6",
                    "--tool-version", "4.4.0",
                    "--github-token-secret", "PREVIEW_GITHUB_TOKEN",
                    "--registry-auth-script", ".github/scripts/registry-login.sh",
                    "--package-auth-script", ".github/scripts/package-login.sh",
                    "--contract-publish-script", ".github/scripts/publish-contract.sh",
                    "--secret", "REGISTRY_TOKEN=CONTAINER_REGISTRY_TOKEN",
                    "--secret", "PACKAGE_TOKEN=PACKAGE_FEED_TOKEN"
                ];
            var exitCode = await PreviewTool.RunAsync(
                arguments,
                cancellationToken);

            Assert.Equal(0, exitCode);
            var actual = await File.ReadAllTextAsync(outputPath, cancellationToken);
            var expected = await File.ReadAllTextAsync(
                Path.Combine(AppContext.BaseDirectory, "TestData", "producer-workflow.yml"),
                cancellationToken);
            Assert.Equal(NormalizeLineEndings(expected), NormalizeLineEndings(actual));
            Assert.Contains(
                "preview export",
                actual,
                StringComparison.Ordinal);
            Assert.Contains(
                "dotnet new nugetconfig",
                actual,
                StringComparison.Ordinal);
            Assert.Contains(
                "--apphost \"$GITHUB_WORKSPACE/$APPHOST\"",
                actual,
                StringComparison.Ordinal);
            Assert.Contains(
                "gh workflow run",
                actual,
                StringComparison.Ordinal);
            Assert.DoesNotContain("jq", actual, StringComparison.Ordinal);

            var parsed = new DeserializerBuilder().Build()
                .Deserialize<Dictionary<object, object>>(actual);
            Assert.Contains("on", parsed.Keys.Cast<string>());
            Assert.Contains("jobs", parsed.Keys.Cast<string>());

            Assert.Equal(1, await PreviewTool.RunAsync(arguments, cancellationToken));
            Assert.Equal(actual, await File.ReadAllTextAsync(outputPath, cancellationToken));
            Assert.Equal(0, await PreviewTool.RunAsync(
                [.. arguments, "--force"],
                cancellationToken));
            Assert.Equal(actual, await File.ReadAllTextAsync(outputPath, cancellationToken));
        }
        finally
        {
            Directory.Delete(outputDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("preview", "workflow")]
    [InlineData("preview", "workflow", "generate")]
    [InlineData("preview", "workflow", "generate", "consumer")]
    public async Task Workflow_command_rejects_incomplete_or_unknown_shapes(params string[] arguments)
    {
        var exitCode = await PreviewTool.RunAsync(
            arguments,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Producer_generation_requires_an_explicit_registry_authentication_choice()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid():N}.yml");
        try
        {
            var exitCode = await PreviewTool.RunAsync(
                [
                    "preview", "workflow", "generate", "producer",
                    "--descriptor", "module-preview.producer.json",
                    "--working-directory", Path.Combine(AppContext.BaseDirectory, "TestData"),
                    "--apphost", "src/Sample.AppHost/Sample.AppHost.csproj",
                    "--output", outputPath,
                    "--repo", "example/consumer-tests",
                    "--workflow", "e2e.yml",
                    "--ref", "main",
                    "--aspire-version", "13.4.6",
                    "--tool-version", "4.4.0",
                    "--github-token-secret", "PREVIEW_GITHUB_TOKEN"
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Producer_generation_applies_strict_workflow_settings_at_stable_phases()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var outputPath = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid():N}.yml");
        try
        {
            var exitCode = await PreviewTool.RunAsync(
                [
                    .. ProducerArguments(outputPath),
                    "--settings", "producer-workflow.settings.json"
                ],
                cancellationToken);

            Assert.Equal(0, exitCode);
            var workflow = await File.ReadAllTextAsync(outputPath, cancellationToken);
            Assert.Contains("group: 'sample-runners'", workflow, StringComparison.Ordinal);
            Assert.Contains("- 'self-hosted'", workflow, StringComparison.Ordinal);
            Assert.Contains("dotnet-version: '10.0.x'", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("global-json-file:", workflow, StringComparison.Ordinal);
            Assert.Contains("token: '${{ steps.app-token.outputs.token }}'", workflow, StringComparison.Ordinal);
            Assert.Contains("uses: 'actions/create-github-app-token@v2'", workflow, StringComparison.Ordinal);
            Assert.Contains("working-directory: '${{ github.workspace }}'", workflow, StringComparison.Ordinal);
            Assert.Contains("'app-id': '${{ vars.APP_ID }}'", workflow, StringComparison.Ordinal);
            Assert.True(
                workflow.IndexOf("'app-id': '${{ vars.APP_ID }}'", StringComparison.Ordinal) <
                workflow.IndexOf("'private-key': '${{ secrets.APP_PRIVATE_KEY }}'", StringComparison.Ordinal));
            Assert.True(
                workflow.IndexOf("Create repository token", StringComparison.Ordinal) <
                workflow.IndexOf("Check out the pushed producer branch", StringComparison.Ordinal));
            Assert.True(
                workflow.IndexOf("Check out the pushed producer branch", StringComparison.Ordinal) <
                workflow.IndexOf("Prepare local package feed", StringComparison.Ordinal));
            Assert.True(
                workflow.IndexOf("Verify attached pushed branch", StringComparison.Ordinal) <
                workflow.IndexOf("Authenticate package source", StringComparison.Ordinal));
            Assert.True(
                workflow.IndexOf("Authenticate package source", StringComparison.Ordinal) <
                workflow.IndexOf("Produce immutable preview request", StringComparison.Ordinal));
            Assert.True(
                workflow.IndexOf("Produce immutable preview request", StringComparison.Ordinal) <
                workflow.IndexOf("Record preview request", StringComparison.Ordinal));
            Assert.True(
                workflow.IndexOf("Record preview request", StringComparison.Ordinal) <
                workflow.IndexOf("Trigger and wait for consumer E2E", StringComparison.Ordinal));

            var parsed = new DeserializerBuilder().Build()
                .Deserialize<Dictionary<object, object>>(workflow);
            Assert.Contains("jobs", parsed.Keys.Cast<string>());
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Producer_generation_supports_runner_labels_and_skipping_dotnet_setup()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testDataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData");
        var settingsFileName = $"temporary-{Guid.NewGuid():N}.settings.json";
        var settingsPath = Path.Combine(testDataDirectory, settingsFileName);
        var outputPath = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid():N}.yml");
        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                $$"""
                {
                  "$schema": "{{SettingsSchema}}",
                  "runsOn": ["self-hosted", "linux", "x64"],
                  "dotnet": { "skip": true }
                }
                """,
                cancellationToken);

            var exitCode = await PreviewTool.RunAsync(
                [
                    .. ProducerArguments(outputPath),
                    "--settings", settingsFileName
                ],
                cancellationToken);

            Assert.Equal(0, exitCode);

            var workflow = await File.ReadAllTextAsync(outputPath, cancellationToken);
            Assert.Contains("      - 'self-hosted'", workflow, StringComparison.Ordinal);
            Assert.Contains("      - 'x64'", workflow, StringComparison.Ordinal);
            Assert.DoesNotContain("actions/setup-dotnet", workflow, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(settingsPath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Producer_generation_supports_string_runner_and_global_json_settings()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testDataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData");
        var settingsFileName = $"temporary-{Guid.NewGuid():N}.settings.json";
        var settingsPath = Path.Combine(testDataDirectory, settingsFileName);
        var outputPath = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid():N}.yml");
        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                $$"""
                {
                  "$schema": "{{SettingsSchema}}",
                  "runsOn": "windows-latest",
                  "dotnet": { "globalJson": "eng/global.json" }
                }
                """,
                cancellationToken);

            string[] arguments =
            [
                .. ProducerArguments(outputPath),
                "--settings", settingsFileName
            ];
            var exitCode = await PreviewTool.RunAsync(arguments, cancellationToken);

            Assert.Equal(0, exitCode);
            var workflow = await File.ReadAllTextAsync(outputPath, cancellationToken);
            Assert.Contains("runs-on: 'windows-latest'", workflow, StringComparison.Ordinal);
            Assert.Contains("global-json-file: 'eng/global.json'", workflow, StringComparison.Ordinal);

            File.Delete(outputPath);
            Assert.Equal(
                1,
                await PreviewTool.RunAsync(
                    [.. arguments, "--global-json", "global.json"],
                    cancellationToken));
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(settingsPath);
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task Producer_generation_quotes_yaml_resolver_words_in_step_ids_and_mapping_keys()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testDataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData");
        var settingsFileName = $"temporary-{Guid.NewGuid():N}.settings.json";
        var settingsPath = Path.Combine(testDataDirectory, settingsFileName);
        var outputPath = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid():N}.yml");
        try
        {
            await File.WriteAllTextAsync(
                settingsPath,
                $$"""
                {
                  "$schema": "{{SettingsSchema}}",
                  "runsOn": "ubuntu-latest",
                  "dotnet": { "skip": true },
                  "steps": {
                    "beforeTrigger": [
                      {
                        "id": "true",
                        "uses": "example/action@v1",
                        "with": { "on": "enabled" }
                      }
                    ]
                  }
                }
                """,
                cancellationToken);

            var exitCode = await PreviewTool.RunAsync(
                [.. ProducerArguments(outputPath), "--settings", settingsFileName],
                cancellationToken);

            Assert.Equal(0, exitCode);
            var workflow = await File.ReadAllTextAsync(outputPath, cancellationToken);
            Assert.Contains("id: 'true'", workflow, StringComparison.Ordinal);
            Assert.Contains("'on': 'enabled'", workflow, StringComparison.Ordinal);
            _ = new DeserializerBuilder().Build()
                .Deserialize<Dictionary<object, object>>(workflow);
        }
        finally
        {
            File.Delete(settingsPath);
            File.Delete(outputPath);
        }
    }

    public static TheoryData<string> InvalidSettings => new()
    {
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"version":"10.0.x"},"unknown":true}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"version":"10.0.x","skip":true}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":[],"dotnet":{"skip":true}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"checkout":{"token":"plain-token"}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"uses":"action@v1","run":"echo bad"}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"id":"trigger","run":"echo bad"}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"id":"duplicate","run":"echo one"}],"beforeTrigger":[{"id":"DUPLICATE","run":"echo two"}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"uses":"action@v1","shell":"bash"}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"run":"echo bad","with":{"value":"bad"}}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"   ","dotnet":{"skip":true}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"name":"\t","run":"echo ok"}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"run":" \n\t"}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","dotnet":{"skip":true},"steps":{"beforeCheckout":[{"run":"echo \u0001"}]}}"""),
        WithSettingsSchema("""{"$schema":"__SCHEMA__","runsOn":"ubuntu-latest","runsOn":"windows-latest","dotnet":{"skip":true}}""")
    };

    [Fact]
    public async Task Workflow_settings_schema_scalar_patterns_match_runtime_validation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "TestData", "preview-workflow-settings.schema.json");
        using var schema = JsonDocument.Parse(await File.ReadAllBytesAsync(schemaPath, cancellationToken));
        var definitions = schema.RootElement.GetProperty("$defs");
        var scalarPattern = definitions.GetProperty("nonEmptySingleLine").GetProperty("pattern").GetString()!;
        var runPattern = definitions.GetProperty("step").GetProperty("properties")
            .GetProperty("run").GetProperty("pattern").GetString()!;
        var tokenPattern = schema.RootElement.GetProperty("properties").GetProperty("checkout")
            .GetProperty("properties").GetProperty("token").GetProperty("pattern").GetString()!;

        Assert.Matches(scalarPattern, "ubuntu-latest");
        Assert.DoesNotMatch(scalarPattern, "   ");
        Assert.DoesNotMatch(scalarPattern, "line\tvalue");
        Assert.DoesNotMatch(scalarPattern, "line\u0085value");
        Assert.Matches(runPattern, "echo first\necho second\t# comment");
        Assert.DoesNotMatch(runPattern, " \n\t");
        Assert.DoesNotMatch(runPattern, "echo \u0001");
        Assert.DoesNotMatch(runPattern, "echo \u0085");
        Assert.Matches(tokenPattern, "${{ steps.app-token.outputs.token }}");
        Assert.DoesNotMatch(tokenPattern, "${{ steps.app-token.outputs.\ttoken }}");
        Assert.DoesNotMatch(tokenPattern, "${{ steps.app-token.outputs.\u0085token }}");
    }

    [Theory]
    [MemberData(nameof(InvalidSettings))]
    public async Task Producer_generation_rejects_invalid_workflow_settings(string settings)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var testDataDirectory = Path.Combine(AppContext.BaseDirectory, "TestData");
        var settingsFileName = $"invalid-{Guid.NewGuid():N}.settings.json";
        var settingsPath = Path.Combine(testDataDirectory, settingsFileName);
        var outputPath = Path.Combine(Path.GetTempPath(), $"workflow-{Guid.NewGuid():N}.yml");
        try
        {
            await File.WriteAllTextAsync(settingsPath, settings, cancellationToken);
            var exitCode = await PreviewTool.RunAsync(
                [
                    .. ProducerArguments(outputPath),
                    "--settings", settingsFileName
                ],
                cancellationToken);

            Assert.Equal(1, exitCode);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            File.Delete(settingsPath);
            File.Delete(outputPath);
        }
    }

    private static string[] ProducerArguments(string outputPath) =>
    [
        "preview", "workflow", "generate", "producer",
        "--descriptor", "module-preview.producer.json",
        "--working-directory", Path.Combine(AppContext.BaseDirectory, "TestData"),
        "--apphost", "src/Sample.AppHost/Sample.AppHost.csproj",
        "--output", outputPath,
        "--repo", "example/consumer-tests",
        "--workflow", "e2e.yml",
        "--ref", "main",
        "--aspire-version", "13.4.6",
        "--tool-version", "4.4.0",
        "--github-token-secret", "PREVIEW_GITHUB_TOKEN",
        "--anonymous-registry"
    ];

    private static string WithSettingsSchema(string value) =>
        value.Replace("__SCHEMA__", SettingsSchema, StringComparison.Ordinal);

    private static string NormalizeLineEndings(string value) =>
        $"{value.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n')}\n";
}
