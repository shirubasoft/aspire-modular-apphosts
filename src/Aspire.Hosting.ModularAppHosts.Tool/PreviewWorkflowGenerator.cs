using System.Text;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static partial class PreviewTool
{
    private static async Task<int> WorkflowAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        if (arguments.Length < 2 ||
            !string.Equals(arguments[0], "generate", StringComparison.Ordinal) ||
            !string.Equals(arguments[1], "producer", StringComparison.Ordinal))
        {
            throw new PreviewToolException(
                "Expected the 'preview workflow generate producer' command.");
        }

        return await GenerateProducerWorkflowAsync(
            arguments.Skip(2).ToArray(),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> GenerateProducerWorkflowAsync(
        string[] arguments,
        CancellationToken cancellationToken)
    {
        var options = CommandOptions.Parse(arguments, ["secret"], ["anonymous-registry", "force"]);
        var descriptorPath = ValidateWorkflowRepositoryPath(
            options.Required("descriptor"),
            "descriptor");
        var workingDirectory = Path.GetFullPath(
            options.Optional("working-directory") ?? Environment.CurrentDirectory);
        var appHostPath = ValidateWorkflowRepositoryPath(
            options.Required("apphost"),
            "apphost");
        var outputPath = Path.GetFullPath(options.Required("output"));
        var consumerRepository = ValidateTargetRepository(options.Required("repo"));
        var consumerWorkflow = ValidateWorkflow(options.Required("workflow"));
        var consumerRef = ValidateSimpleValue(options.Required("ref"), "ref");
        var aspireVersion = ValidateExactPackageVersion(
            options.Required("aspire-version"),
            "aspire-version");
        var toolVersion = ValidateExactPackageVersion(
            options.Required("tool-version"),
            "tool-version");
        var githubTokenSecret = ValidateGitHubName(
            options.Required("github-token-secret"),
            "github-token-secret");
        var globalJsonPath = ValidateWorkflowRepositoryPath(
            options.Optional("global-json") ?? "global.json",
            "global-json");
        var registryAuthenticationScript = options.Optional("registry-auth-script") is { } registryScript
            ? ValidateWorkflowRepositoryPath(registryScript, "registry-auth-script")
            : null;
        var packageAuthenticationScript = options.Optional("package-auth-script") is { } packageScript
            ? ValidateWorkflowRepositoryPath(packageScript, "package-auth-script")
            : null;
        var contractPublishScript = options.Optional("contract-publish-script") is { } publishScript
            ? ValidateWorkflowRepositoryPath(publishScript, "contract-publish-script")
            : null;
        var anonymousRegistry = options.Flag("anonymous-registry");
        var force = options.Flag("force");
        options.EnsureOnly(
            "descriptor",
            "working-directory",
            "apphost",
            "output",
            "repo",
            "workflow",
            "ref",
            "aspire-version",
            "tool-version",
            "github-token-secret",
            "global-json",
            "registry-auth-script",
            "package-auth-script",
            "contract-publish-script",
            "anonymous-registry",
            "force",
            "secret");

        if (anonymousRegistry == (registryAuthenticationScript is not null))
        {
            throw new PreviewToolException(
                "Specify exactly one of --registry-auth-script or --anonymous-registry.");
        }

        var descriptorAbsolutePath = Path.GetFullPath(descriptorPath, workingDirectory);
        var descriptor = await ModulePreviewProducerDescriptor.LoadAsync(
            descriptorAbsolutePath,
            cancellationToken).ConfigureAwait(false);
        if (descriptor.Contract is null &&
            (packageAuthenticationScript is not null || contractPublishScript is not null))
        {
            throw new PreviewToolException(
                "Package authentication and contract publishing scripts require a descriptor contract.");
        }

        var secretEnvironment = ParseWorkflowSecrets(options.Many("secret"));
        if (secretEnvironment.ContainsKey("GH_TOKEN"))
        {
            throw new PreviewToolException(
                "--secret cannot set GH_TOKEN; use --github-token-secret.");
        }

        var workflow = RenderProducerWorkflow(new ProducerWorkflowOptions(
            descriptorPath,
            appHostPath,
            consumerRepository,
            consumerWorkflow,
            consumerRef,
            aspireVersion,
            toolVersion,
            githubTokenSecret,
            globalJsonPath,
            registryAuthenticationScript,
            packageAuthenticationScript,
            contractPublishScript,
            descriptor.Contract?.Version,
            descriptor.Contract is not null,
            secretEnvironment));

        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        if (!force && File.Exists(outputPath))
        {
            throw new PreviewToolException(
                $"Workflow output '{outputPath}' already exists. Pass --force to replace it.");
        }

        var outputStream = new FileStream(
            outputPath,
            force ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            await outputStream.WriteAsync(
                Utf8NoBom.GetBytes(workflow),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await outputStream.DisposeAsync().ConfigureAwait(false);
        }

        await Console.Out.WriteLineAsync(outputPath).ConfigureAwait(false);
        return 0;
    }

    private static SortedDictionary<string, string> ParseWorkflowSecrets(IEnumerable<string> values)
    {
        var secrets = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            var equals = value.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals == value.Length - 1)
            {
                throw new PreviewToolException(
                    $"Invalid workflow secret mapping '{value}'. Expected <environment-name>=<secret-name>.");
            }

            var environmentName = ValidateEnvironmentName(value[..equals]);
            var secretName = ValidateGitHubName(value[(equals + 1)..], "secret");
            if (!secrets.TryAdd(environmentName, secretName))
            {
                throw new PreviewToolException(
                    $"Workflow environment variable '{environmentName}' was specified more than once.");
            }
        }

        return secrets;
    }

    private static string ValidateEnvironmentName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new PreviewToolException(
                "Workflow secret environment names must contain only ASCII letters, digits, and underscores, " +
                "and cannot start with a digit.");
        }

        return value;
    }

    private static string ValidateGitHubName(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !(char.IsAsciiLetter(value[0]) || value[0] == '_') ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
        {
            throw new PreviewToolException(
                $"--{option} must contain only ASCII letters, digits, and underscores, and cannot start with a digit.");
        }

        return value;
    }

    private static string ValidateWorkflowRepositoryPath(string value, string option)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            Path.IsPathRooted(value) ||
            value.Any(char.IsControl))
        {
            throw new PreviewToolException(
                $"--{option} must be a repository-relative path without control characters.");
        }

        var normalized = value.Replace('\\', '/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new PreviewToolException(
                $"--{option} must be a repository-relative path without '.' or '..' segments.");
        }

        return string.Join('/', segments);
    }

    private static string ValidateExactPackageVersion(string value, string option)
    {
        PreviewPolicyValidation.ValidatePackageVersion(value, $"--{option}");
        return value;
    }

    private static string RenderProducerWorkflow(ProducerWorkflowOptions options)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# Generated by: dotnet modular-apphosts preview workflow generate producer");
        builder.AppendLine("# Authentication remains producer-owned; regenerate after changing generator options.");
        builder.AppendLine("name: Module preview");
        builder.AppendLine();
        builder.AppendLine("permissions:");
        builder.AppendLine("  actions: write");
        builder.AppendLine("  contents: read");
        builder.AppendLine("  packages: write");
        builder.AppendLine();
        builder.AppendLine("on:");
        AppendWorkflowCall(builder, options);
        AppendWorkflowDispatch(builder, options);
        builder.AppendLine();
        builder.AppendLine("run-name: Module preview · ${{ inputs.source-ref || github.head_ref || github.ref_name }}");
        builder.AppendLine();
        builder.AppendLine("jobs:");
        builder.AppendLine("  preview:");
        builder.AppendLine("    runs-on: ubuntu-latest");
        builder.AppendLine("    outputs:");
        builder.AppendLine("      workflow-run-id: ${{ steps.trigger.outputs.workflow_run_id }}");
        builder.AppendLine("      workflow-run-url: ${{ steps.trigger.outputs.workflow_run_url }}");
        builder.AppendLine("    env:");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      APPHOST: {YamlString(options.AppHostPath)}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      PRODUCER_DESCRIPTOR: {YamlString(options.DescriptorPath)}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      ASPIRE_VERSION: {YamlString(options.AspireVersion)}");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      MODULAR_APPHOSTS_VERSION: {YamlString(options.ToolVersion)}");
        builder.AppendLine("      PREVIEW_ARTIFACTS_DIR: ${{ runner.temp }}/module-preview");
        builder.AppendLine("      ASPIRE_TOOL_DIR: ${{ runner.temp }}/tools/aspire");
        builder.AppendLine("      MODULAR_APPHOSTS_TOOL_DIR: ${{ runner.temp }}/tools/modular-apphosts");
        builder.AppendLine("      TOOLS_NUGET_CONFIG: ${{ runner.temp }}/modular-apphosts-tools.nuget.config");
        builder.AppendLine("      NUGET_PACKAGES: ${{ runner.temp }}/nuget-packages");
        builder.AppendLine("      Aspire__ModularAppHosts__PublishImages: 'true'");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      GH_TOKEN: ${{{{ secrets.{options.GitHubTokenSecret} }}}}");
        if (options.HasContract)
        {
            builder.AppendLine("      CONTRACT_VERSION: ${{ inputs.contract-version }}");
        }

        foreach (var secret in options.SecretEnvironment)
        {
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      {secret.Key}: ${{{{ secrets.{secret.Value} }}}}");
        }

        builder.AppendLine("    steps:");
        builder.AppendLine("      - name: Check out the pushed producer branch");
        builder.AppendLine("        uses: actions/checkout@v4");
        builder.AppendLine("        with:");
        builder.AppendLine("          ref: ${{ inputs.source-ref || github.head_ref || github.ref_name }}");
        builder.AppendLine("          fetch-depth: 0");
        builder.AppendLine();
        builder.AppendLine("      - name: Verify attached pushed branch");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          set -euo pipefail");
        builder.AppendLine("          branch=$(git symbolic-ref --quiet --short HEAD) || {");
        builder.AppendLine("            echo 'The producer checkout must be attached to a branch.' >&2");
        builder.AppendLine("            exit 1");
        builder.AppendLine("          }");
        builder.AppendLine("          commit=$(git rev-parse HEAD)");
        builder.AppendLine("          remote_commit=$(git ls-remote --heads origin \"refs/heads/$branch\" | awk '{print $1}')");
        builder.AppendLine("          if [[ -z \"$remote_commit\" || \"$remote_commit\" != \"$commit\" ]]; then");
        builder.AppendLine("            echo \"HEAD must be the pushed tip of origin branch '$branch'.\" >&2");
        builder.AppendLine("            exit 1");
        builder.AppendLine("          fi");
        builder.AppendLine();
        builder.AppendLine("      - name: Set up .NET");
        builder.AppendLine("        uses: actions/setup-dotnet@v5");
        builder.AppendLine("        with:");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"          global-json-file: {YamlString(options.GlobalJsonPath)}");
        builder.AppendLine();
        builder.AppendLine("      - name: Install isolated workflow tools");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          set -euo pipefail");
        builder.AppendLine("          mkdir -p \"$PREVIEW_ARTIFACTS_DIR\" \"$ASPIRE_TOOL_DIR\" \"$MODULAR_APPHOSTS_TOOL_DIR\"");
        builder.AppendLine("          printf '%s\\n' \\");
        builder.AppendLine("            '<?xml version=\"1.0\" encoding=\"utf-8\"?>' \\");
        builder.AppendLine("            '<configuration>' \\");
        builder.AppendLine("            '  <packageSources>' \\");
        builder.AppendLine("            '    <clear />' \\");
        builder.AppendLine("            '    <add key=\"nuget.org\" value=\"https://api.nuget.org/v3/index.json\" />' \\");
        builder.AppendLine("            '  </packageSources>' \\");
        builder.AppendLine("            '</configuration>' > \"$TOOLS_NUGET_CONFIG\"");
        builder.AppendLine("          dotnet tool install aspire.cli \\");
        builder.AppendLine("            --tool-path \"$ASPIRE_TOOL_DIR\" \\");
        builder.AppendLine("            --version \"$ASPIRE_VERSION\" \\");
        builder.AppendLine("            --configfile \"$TOOLS_NUGET_CONFIG\"");
        builder.AppendLine("          dotnet tool install Shirubasoft.Aspire.ModularAppHosts.Tool \\");
        builder.AppendLine("            --tool-path \"$MODULAR_APPHOSTS_TOOL_DIR\" \\");
        builder.AppendLine("            --version \"$MODULAR_APPHOSTS_VERSION\" \\");
        builder.AppendLine("            --configfile \"$TOOLS_NUGET_CONFIG\"");

        AppendScriptStep(builder, "Authenticate package feed", options.PackageAuthenticationScript);
        if (options.ContractPublishScript is not null)
        {
            builder.AppendLine();
            builder.AppendLine("      - name: Publish exact module contract");
            builder.AppendLine("        shell: bash");
            builder.AppendLine("        run: |");
            builder.AppendLine("          set -euo pipefail");
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"          bash \"$GITHUB_WORKSPACE/{ShellDoubleQuoted(options.ContractPublishScript)}\" \"$CONTRACT_VERSION\"");
        }

        AppendScriptStep(builder, "Authenticate image registries", options.RegistryAuthenticationScript);
        builder.AppendLine();
        builder.AppendLine("      - name: Describe module images");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          set -euo pipefail");
        builder.AppendLine("          mkdir -p \"$PREVIEW_ARTIFACTS_DIR/images\"");
        builder.AppendLine("          \"$ASPIRE_TOOL_DIR/aspire\" do describe-images \\");
        builder.AppendLine("            --apphost \"$GITHUB_WORKSPACE/$APPHOST\" \\");
        builder.AppendLine("            --output-path \"$PREVIEW_ARTIFACTS_DIR/images\" \\");
        builder.AppendLine("            --non-interactive");
        builder.AppendLine();
        builder.AppendLine("      - name: Build and push declared images");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          set -euo pipefail");
        builder.AppendLine("          image_description=\"$PREVIEW_ARTIFACTS_DIR/images/module-images.json\"");
        builder.AppendLine("          missing=$(jq -r --slurpfile descriptor \"$GITHUB_WORKSPACE/$PRODUCER_DESCRIPTOR\" '");
        builder.AppendLine("            . as $description");
        builder.AppendLine("            | $descriptor[0].images[].resource as $resource");
        builder.AppendLine("            | select([");
        builder.AppendLine("                $description.images[]");
        builder.AppendLine("                | select(.module == $descriptor[0].module");
        builder.AppendLine("                    and .resource == $resource");
        builder.AppendLine("                    and .build != null");
        builder.AppendLine("                    and .pushReference != null)");
        builder.AppendLine("              ] | length != 1)");
        builder.AppendLine("            | $resource");
        builder.AppendLine("          ' \"$image_description\")");
        builder.AppendLine("          if [[ -n \"$missing\" ]]; then");
        builder.AppendLine("            echo 'Every descriptor image must have exactly one build publisher and push target.' >&2");
        builder.AppendLine("            printf 'Missing or ambiguous resources:\\n%s\\n' \"$missing\" >&2");
        builder.AppendLine("            exit 1");
        builder.AppendLine("          fi");
        builder.AppendLine("          mapfile -t resources < <(");
        builder.AppendLine("            jq -r --slurpfile descriptor \"$GITHUB_WORKSPACE/$PRODUCER_DESCRIPTOR\" '");
        builder.AppendLine("              .images[]");
        builder.AppendLine("              | select(.module == $descriptor[0].module)");
        builder.AppendLine("              | select(.resource as $resource");
        builder.AppendLine("                  | ($descriptor[0].images | map(.resource) | index($resource)))");
        builder.AppendLine("              | select(.build != null and .pushReference != null)");
        builder.AppendLine("              | .effectiveResource");
        builder.AppendLine("            ' \"$image_description\" | sort -u");
        builder.AppendLine("          )");
        builder.AppendLine("          if (( ${#resources[@]} > 0 )); then");
        builder.AppendLine("            mkdir -p \"$PREVIEW_ARTIFACTS_DIR/push\"");
        builder.AppendLine("            \"$ASPIRE_TOOL_DIR/aspire\" do push \\");
        builder.AppendLine("              --apphost \"$GITHUB_WORKSPACE/$APPHOST\" \\");
        builder.AppendLine("              --output-path \"$PREVIEW_ARTIFACTS_DIR/push\" \\");
        builder.AppendLine("              --non-interactive \\");
        builder.AppendLine("              \"${resources[@]}\"");
        builder.AppendLine("          fi");
        builder.AppendLine();
        builder.AppendLine("      - name: Produce immutable preview request");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          set -euo pipefail");
        builder.AppendLine("          image_description=\"$PREVIEW_ARTIFACTS_DIR/images/module-images.json\"");
        builder.AppendLine("          image_args=()");
        builder.AppendLine("          while IFS=$'\\t' read -r resource push_reference; do");
        builder.AppendLine("            if [[ \"$push_reference\" == *@* ]]; then");
        builder.AppendLine("              pushed_repository=${push_reference%@*}");
        builder.AppendLine("            elif [[ \"${push_reference##*/}\" == *:* ]]; then");
        builder.AppendLine("              pushed_repository=${push_reference%:*}");
        builder.AppendLine("            else");
        builder.AppendLine("              echo \"Image '$resource' has an untagged push reference: $push_reference\" >&2");
        builder.AppendLine("              exit 1");
        builder.AppendLine("            fi");
        builder.AppendLine("            manifest=$(docker buildx imagetools inspect \\");
        builder.AppendLine("              \"$push_reference\" --format '{{json .Manifest}}')");
        builder.AppendLine("            digest=$(jq -er '.digest | select(test(\"^sha256:[0-9a-f]{64}$\"))' <<< \"$manifest\")");
        builder.AppendLine("            image_args+=(--image \"$resource=$pushed_repository@$digest\")");
        builder.AppendLine("          done < <(");
        builder.AppendLine("            jq -r --slurpfile descriptor \"$GITHUB_WORKSPACE/$PRODUCER_DESCRIPTOR\" '");
        builder.AppendLine("              .images[]");
        builder.AppendLine("              | select(.module == $descriptor[0].module)");
        builder.AppendLine("              | select(.resource as $resource");
        builder.AppendLine("                  | ($descriptor[0].images | map(.resource) | index($resource)))");
        builder.AppendLine("              | select(.build != null and .pushReference != null)");
        builder.AppendLine("              | [");
        builder.AppendLine("                  .resource,");
        builder.AppendLine("                  .pushReference");
        builder.AppendLine("                ]");
        builder.AppendLine("              | @tsv");
        builder.AppendLine("            ' \"$image_description\"");
        builder.AppendLine("          )");
        builder.AppendLine("          produce_args=(");
        builder.AppendLine("            preview produce");
        builder.AppendLine("            --descriptor \"$GITHUB_WORKSPACE/$PRODUCER_DESCRIPTOR\"");
        builder.AppendLine("            --output \"$PREVIEW_ARTIFACTS_DIR/module-preview.json\"");
        builder.AppendLine("            --working-directory \"$GITHUB_WORKSPACE\"");
        if (options.HasContract)
        {
            builder.AppendLine("            --contract-version \"$CONTRACT_VERSION\"");
        }

        builder.AppendLine("          )");
        builder.AppendLine("          \"$MODULAR_APPHOSTS_TOOL_DIR/dotnet-modular-apphosts\" \\");
        builder.AppendLine("            \"${produce_args[@]}\" \"${image_args[@]}\"");
        builder.AppendLine();
        builder.AppendLine("      - name: Trigger and wait for consumer E2E");
        builder.AppendLine("        id: trigger");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          set -euo pipefail");
        builder.AppendLine("          \"$MODULAR_APPHOSTS_TOOL_DIR/dotnet-modular-apphosts\" preview trigger \\");
        builder.AppendLine("            --manifest \"$PREVIEW_ARTIFACTS_DIR/module-preview.json\" \\");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"            --repo {ShellWord(options.ConsumerRepository)} \\");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"            --workflow {ShellWord(options.ConsumerWorkflow)} \\");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"            --ref {ShellWord(options.ConsumerRef)} \\");
        builder.AppendLine("            --wait \\");
        builder.AppendLine("            --github-output \"$GITHUB_OUTPUT\"");
        builder.AppendLine();
        builder.AppendLine("      - name: Link consumer run");
        builder.AppendLine("        if: always() && steps.trigger.outputs.workflow_run_url != ''");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          echo '### Consumer E2E' >> \"$GITHUB_STEP_SUMMARY\"");
        builder.AppendLine("          echo '[Open workflow run](${{ steps.trigger.outputs.workflow_run_url }})' >> \"$GITHUB_STEP_SUMMARY\"");
        builder.AppendLine();
        builder.AppendLine("      - name: Upload preview diagnostics");
        builder.AppendLine("        if: always()");
        builder.AppendLine("        uses: actions/upload-artifact@v4");
        builder.AppendLine("        with:");
        builder.AppendLine("          name: module-preview");
        builder.AppendLine("          path: ${{ env.PREVIEW_ARTIFACTS_DIR }}");
        builder.AppendLine("          if-no-files-found: warn");
        return builder.ToString();
    }

    private static void AppendWorkflowCall(StringBuilder builder, ProducerWorkflowOptions options)
    {
        builder.AppendLine("  workflow_call:");
        builder.AppendLine("    inputs:");
        AppendWorkflowInputs(builder, options, indentation: "      ");
        var secrets = GetDeclaredSecrets(options);
        if (secrets.Length > 0)
        {
            builder.AppendLine("    secrets:");
            foreach (var secret in secrets)
            {
                builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      {secret}:");
                builder.AppendLine("        required: true");
            }
        }

        builder.AppendLine("    outputs:");
        builder.AppendLine("      workflow-run-id:");
        builder.AppendLine("        description: Consumer workflow run ID");
        builder.AppendLine("        value: ${{ jobs.preview.outputs.workflow-run-id }}");
        builder.AppendLine("      workflow-run-url:");
        builder.AppendLine("        description: Consumer workflow run URL");
        builder.AppendLine("        value: ${{ jobs.preview.outputs.workflow-run-url }}");
    }

    private static void AppendWorkflowDispatch(StringBuilder builder, ProducerWorkflowOptions options)
    {
        builder.AppendLine("  workflow_dispatch:");
        builder.AppendLine("    inputs:");
        AppendWorkflowInputs(builder, options, indentation: "      ");
    }

    private static void AppendWorkflowInputs(
        StringBuilder builder,
        ProducerWorkflowOptions options,
        string indentation)
    {
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}source-ref:");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  description: Pushed producer branch to check out");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  required: false");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  type: string");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  default: ''");
        if (!options.HasContract)
        {
            return;
        }

        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}contract-version:");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  description: Exact contract package version published for this preview");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  required: true");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  type: string");
        if (!string.IsNullOrWhiteSpace(options.DescriptorContractVersion))
        {
            builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"{indentation}  default: {YamlString(options.DescriptorContractVersion)}");
        }
    }

    private static string[] GetDeclaredSecrets(ProducerWorkflowOptions options)
    {
        var secrets = new SortedSet<string>(StringComparer.Ordinal);
        if (!string.Equals(options.GitHubTokenSecret, "GITHUB_TOKEN", StringComparison.Ordinal))
        {
            secrets.Add(options.GitHubTokenSecret);
        }

        foreach (var secret in options.SecretEnvironment.Values)
        {
            if (!string.Equals(secret, "GITHUB_TOKEN", StringComparison.Ordinal))
            {
                secrets.Add(secret);
            }
        }

        return secrets.ToArray();
    }

    private static void AppendScriptStep(StringBuilder builder, string name, string? repositoryPath)
    {
        if (repositoryPath is null)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"      - name: {name}");
        builder.AppendLine("        shell: bash");
        builder.AppendLine("        run: |");
        builder.AppendLine("          set -euo pipefail");
        builder.AppendLine(System.Globalization.CultureInfo.InvariantCulture, $"          bash \"$GITHUB_WORKSPACE/{ShellDoubleQuoted(repositoryPath)}\"");
    }

    private static string YamlString(string value) => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static string ShellDoubleQuoted(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("$", "\\$", StringComparison.Ordinal)
            .Replace("`", "\\`", StringComparison.Ordinal);

    private static string ShellWord(string value) => $"'{value.Replace("'", "'\\''", StringComparison.Ordinal)}'";

    private sealed record ProducerWorkflowOptions(
        string DescriptorPath,
        string AppHostPath,
        string ConsumerRepository,
        string ConsumerWorkflow,
        string ConsumerRef,
        string AspireVersion,
        string ToolVersion,
        string GitHubTokenSecret,
        string GlobalJsonPath,
        string? RegistryAuthenticationScript,
        string? PackageAuthenticationScript,
        string? ContractPublishScript,
        string? DescriptorContractVersion,
        bool HasContract,
        IReadOnlyDictionary<string, string> SecretEnvironment);
}
