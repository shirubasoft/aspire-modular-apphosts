using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ModularAppHosts;

namespace Shirubasoft.Aspire.ModularAppHosts.Tool;

internal static partial class PreviewTool
{
    private static async Task<int> ProduceAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var options = CommandOptions.Parse(arguments, ["image", "pin", "dependency"]);
        var descriptorPath = options.Required("descriptor");
        var output = options.Required("output");
        var workingDirectory = Path.GetFullPath(options.Optional("working-directory") ?? Environment.CurrentDirectory);
        var gitExecutable = options.Optional("git-executable") ?? "git";
        var appHost = options.Optional("apphost") is { } appHostPath
            ? Path.GetFullPath(appHostPath, workingDirectory)
            : null;
        var artifactsDirectory = options.Optional("artifacts-directory") is { } artifactsPath
            ? Path.GetFullPath(artifactsPath, workingDirectory)
            : null;
        var aspireExecutable = options.Optional("aspire-executable") ?? "aspire";
        var dockerExecutable = options.Optional("docker-executable") ?? "docker";
        options.EnsureOnly(
            "descriptor",
            "output",
            "working-directory",
            "git-executable",
            "apphost",
            "artifacts-directory",
            "aspire-executable",
            "docker-executable",
            "contract-version",
            "image",
            "pin",
            "dependency");

        if ((appHost is null) != (artifactsDirectory is null))
        {
            throw new PreviewToolException(
                "--apphost and --artifacts-directory must be specified together.");
        }

        if (appHost is not null && options.Many("image").Count > 0)
        {
            throw new PreviewToolException(
                "--image cannot be combined with --apphost; AppHost image outputs are discovered automatically.");
        }

        var descriptor = await ModulePreviewProducerDescriptor.LoadAsync(
            Path.GetFullPath(descriptorPath, workingDirectory),
            cancellationToken).ConfigureAwait(false);
        var producer = await new GitInspector(gitExecutable, workingDirectory)
            .InspectAsync(output, cancellationToken).ConfigureAwait(false);

        var manifest = new ModulePreviewManifest
        {
            Producer = producer
        };
        var moduleSelection = new ModulePreviewSelection
        {
            Name = descriptor.Module,
            Repository = producer.Repository,
            Commit = producer.Commit,
            Branch = producer.Branch,
            BaseRef = producer.BaseRef,
            BaseCommit = producer.BaseCommit
        };
        var moduleSelectionOverridden = false;

        foreach (var pin in options.Many("pin").Concat(options.Many("dependency")))
        {
            var selection = ParsePin(pin);
            if (string.Equals(selection.Name, descriptor.Module, StringComparison.OrdinalIgnoreCase))
            {
                if (moduleSelectionOverridden)
                {
                    throw new PreviewToolException(
                        $"Module selection '{descriptor.Module}' was specified more than once.");
                }

                moduleSelection = selection;
                moduleSelectionOverridden = true;
            }
            else
            {
                manifest.Modules.Add(selection);
            }
        }
        manifest.Modules.Add(moduleSelection);

        if (descriptor.Contract is not null)
        {
            var contractVersion = options.Optional("contract-version") ?? descriptor.Contract.Version;
            if (string.IsNullOrWhiteSpace(contractVersion))
            {
                throw new PreviewToolException(
                    "The producer descriptor declares a contract without a version. " +
                    "Supply its computed exact version with --contract-version.");
            }

            PreviewPolicyValidation.ValidatePackageVersion(contractVersion, "--contract-version");
            manifest.Contracts.Add(new ModulePreviewContractRequest
            {
                Module = descriptor.Module,
                PackageId = descriptor.Contract.PackageId,
                Version = contractVersion
            });
        }
        else if (options.Optional("contract-version") is not null)
        {
            throw new PreviewToolException(
                "--contract-version cannot be used when the producer descriptor does not declare a contract.");
        }

        var producedImages = appHost is null
            ? options.Many("image").Select(ParseProducedImage)
            : await PublishAppHostImagesAsync(
                descriptor,
                appHost,
                artifactsDirectory!,
                aspireExecutable,
                dockerExecutable,
                cancellationToken).ConfigureAwait(false);
        var suppliedImages = new Dictionary<string, ProducedImage>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in producedImages)
        {
            if (!suppliedImages.TryAdd(image.Resource, image))
            {
                throw new PreviewToolException(
                    $"Image resource '{image.Resource}' was specified more than once.");
            }
        }

        foreach (var imageDescriptor in descriptor.Images)
        {
            if (!suppliedImages.Remove(imageDescriptor.Resource, out var image))
            {
                if (imageDescriptor.Required)
                {
                    throw new PreviewToolException(
                        $"Producer descriptor image '{imageDescriptor.Resource}' is required. " +
                        $"Supply --image {imageDescriptor.Resource}=<repository>@<sha256-digest>.");
                }

                continue;
            }

            if (!string.Equals(image.Repository, imageDescriptor.Repository, StringComparison.Ordinal))
            {
                throw new PreviewToolException(
                    $"Image resource '{image.Resource}' uses repository '{image.Repository}', but the " +
                    $"producer descriptor declares '{imageDescriptor.Repository}'.");
            }

            manifest.Images.Add(new ModulePreviewImageArtifact
            {
                Module = descriptor.Module,
                Resource = imageDescriptor.Resource,
                ResourceKind = Enum.Parse<ModulePreviewResourceKind>(imageDescriptor.ResourceKind, ignoreCase: true),
                Repository = image.Repository,
                Sha256 = image.Sha256
            });
        }

        if (suppliedImages.Count > 0)
        {
            throw new PreviewToolException(
                $"Image resource '{suppliedImages.Keys.Order(StringComparer.OrdinalIgnoreCase).First()}' " +
                "is not declared by the producer descriptor.");
        }

        if (manifest.Contracts.Count == 0 && manifest.Images.Count == 0)
        {
            throw new PreviewToolException(
                "A producer descriptor must offer at least one contract package or immutable image.");
        }

        var producerOwnsDescriptorModule =
            RepositoryIdentityComparer.Instance.Equals(moduleSelection.Repository, producer.Repository) &&
            string.Equals(moduleSelection.Commit, producer.Commit, StringComparison.OrdinalIgnoreCase);
        if (manifest.Contracts.Count > 0 && !producerOwnsDescriptorModule)
        {
            throw new PreviewToolException(
                $"The preview producer repository and commit " +
                $"('{producer.Repository}' at '{producer.Commit}') do not match module '{descriptor.Module}' " +
                $"('{moduleSelection.Repository}' at '{moduleSelection.Commit}'), " +
                "so the producer cannot request its contract package. Use an image-only descriptor.");
        }

        SortManifest(manifest);
        await manifest.SaveAsync(output, cancellationToken).ConfigureAwait(false);
        await Console.Out.WriteLineAsync(Path.GetFullPath(output)).ConfigureAwait(false);
        return 0;
    }

    private static async Task<IReadOnlyList<ProducedImage>> PublishAppHostImagesAsync(
        ModulePreviewProducerDescriptor descriptor,
        string appHost,
        string artifactsDirectory,
        string aspireExecutable,
        string dockerExecutable,
        CancellationToken cancellationToken)
    {
        var descriptionDirectory = Path.Combine(artifactsDirectory, "description");
        Directory.CreateDirectory(descriptionDirectory);
        var describeResult = await RunCommandAsync(
            aspireExecutable,
            [
                "do", "describe-images",
                "--apphost", appHost,
                "--output-path", descriptionDirectory,
                "--non-interactive"
            ],
            Environment.CurrentDirectory,
            standardInput: null,
            cancellationToken,
            applyTimeout: false).ConfigureAwait(false);
        EnsureSuccess(describeResult, "Aspire image description");

        var descriptionPath = Path.Combine(descriptionDirectory, "module-images.json");
        var description = await ModuleImageDescriptionDocument.LoadAsync(
            descriptionPath,
            cancellationToken).ConfigureAwait(false);
        var selectedImages = new List<ModuleImageDescription>();
        foreach (var declaredImage in descriptor.Images)
        {
            var matches = description.Images
                .Where(image =>
                    string.Equals(image.Module, descriptor.Module, StringComparison.Ordinal) &&
                    string.Equals(image.Resource, declaredImage.Resource, StringComparison.Ordinal) &&
                    image.Build is not null &&
                    image.PushReference is not null)
                .ToArray();
            if (matches.Length != 1)
            {
                throw new PreviewToolException(
                    $"Producer descriptor image '{declaredImage.Resource}' must resolve to exactly one " +
                    "AppHost image with a build publisher and push target.");
            }

            var expectedKind = Enum.Parse<ModulePreviewResourceKind>(
                declaredImage.ResourceKind,
                ignoreCase: true);
            if (matches[0].ResourceKind != expectedKind)
            {
                throw new PreviewToolException(
                    $"Producer descriptor image '{declaredImage.Resource}' declares resource kind " +
                    $"'{expectedKind}', but the AppHost describes '{matches[0].ResourceKind}'.");
            }

            selectedImages.Add(matches[0]);
        }

        var pushDirectory = Path.Combine(artifactsDirectory, "push");
        if (selectedImages.Count > 0)
        {
            Directory.CreateDirectory(pushDirectory);
            var pushSteps = selectedImages
                .Select(image => $"push-{image.EffectiveResource}")
                .ToArray();
            var pushArguments = new List<string> { "do" };
            pushArguments.AddRange(pushSteps);
            pushArguments.AddRange(
                [
                    "--apphost", appHost,
                    "--output-path", pushDirectory,
                    "--non-interactive"
                ]);
            var pushResult = await RunCommandAsync(
                aspireExecutable,
                pushArguments,
                Environment.CurrentDirectory,
                standardInput: null,
                cancellationToken,
                applyTimeout: false).ConfigureAwait(false);
            EnsureSuccess(
                pushResult,
                $"Aspire pipeline steps '{string.Join("', '", pushSteps)}'");
        }

        var producedImages = new List<ProducedImage>(selectedImages.Count);
        foreach (var image in selectedImages)
        {
            var pushReference = image.PushReference!;
            var inspectResult = await RunCommandAsync(
                dockerExecutable,
                [
                    "buildx", "imagetools", "inspect", pushReference,
                    "--format", "{{.Manifest.Digest}}"
                ],
                Environment.CurrentDirectory,
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(inspectResult, $"Inspect pushed image '{pushReference}'");
            var repository = GetImageRepository(pushReference);
            producedImages.Add(ParseProducedImage(
                $"{image.Resource}={repository}@{inspectResult.StandardOutput.Trim()}"));
        }

        return producedImages;
    }

    private static string GetImageRepository(string reference)
    {
        var digestSeparator = reference.LastIndexOf('@');
        if (digestSeparator > 0)
        {
            return reference[..digestSeparator];
        }

        var lastSlash = reference.LastIndexOf('/');
        var tagSeparator = reference.LastIndexOf(':');
        if (tagSeparator > lastSlash)
        {
            return reference[..tagSeparator];
        }

        throw new PreviewToolException(
            $"Pushed image reference '{reference}' must include a tag or digest.");
    }

    private static async Task<int> VerifyAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var options = CommandOptions.Parse(arguments);
        var manifestPath = options.Required("manifest");
        var policyPath = options.Required("policy");
        var output = options.Optional("output");
        options.EnsureOnly("manifest", "policy", "output");

        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var policy = await ModulePreviewConsumerPolicy.LoadAsync(policyPath, cancellationToken).ConfigureAwait(false);
        _ = PreviewPolicyEvaluator.Evaluate(manifest, policy);

        if (output is not null)
        {
            SortManifest(manifest);
            await manifest.SaveAsync(output, cancellationToken).ConfigureAwait(false);
            await Console.Out.WriteLineAsync(Path.GetFullPath(output)).ConfigureAwait(false);
        }
        else
        {
            await Console.Out.WriteLineAsync("verified").ConfigureAwait(false);
        }

        return 0;
    }

    private static async Task<int> MaterializeAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var options = CommandOptions.Parse(arguments, ["property"]);
        var manifestPath = Path.GetFullPath(options.Required("manifest"));
        var policyPath = Path.GetFullPath(options.Required("policy"));
        var workDirectory = Path.GetFullPath(options.Required("work-directory"));
        var packageFeed = options.Optional("package-feed") is { } feed
            ? Path.GetFullPath(feed)
            : null;
        var resolutionPath = Path.GetFullPath(options.Required("resolution"));
        var consumerRepository = options.Required("consumer-repository");
        var consumerCommit = options.Required("consumer-commit");
        var githubEnvironmentPath = options.Optional("github-env") is { } githubEnvironment
            ? Path.GetFullPath(githubEnvironment)
            : null;
        var nugetConfig = options.Optional("nuget-config") is { } config
            ? Path.GetFullPath(config)
            : null;
        var gitExecutable = options.Optional("git-executable") ?? "git";
        var githubCli = options.Optional("gh-executable") ?? "gh";
        var dotnetExecutable = options.Optional("dotnet-executable") ?? "dotnet";
        var dockerExecutable = options.Optional("docker-executable") ?? "docker";
        options.EnsureOnly(
            "manifest",
            "policy",
            "work-directory",
            "package-feed",
            "resolution",
            "consumer-repository",
            "consumer-commit",
            "github-env",
            "nuget-config",
            "git-executable",
            "gh-executable",
            "dotnet-executable",
            "docker-executable",
            "property");

        if (nugetConfig is not null && !File.Exists(nugetConfig))
        {
            throw new PreviewToolException($"NuGet configuration '{nugetConfig}' does not exist.");
        }

        var manifest = await ModulePreviewManifest.LoadAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var policy = await ModulePreviewConsumerPolicy.LoadAsync(policyPath, cancellationToken).ConfigureAwait(false);
        var evaluation = PreviewPolicyEvaluator.Evaluate(manifest, policy);
        var properties = ParseMaterializationProperties(options.Many("property"), evaluation);

        if (evaluation.Modules.Any(module => module.Contract is not null) && packageFeed is null)
        {
            throw new PreviewToolException(
                "--package-feed is required when the preview request includes a contract.");
        }

        EnsureEmptyWorkDirectory(workDirectory);
        if (packageFeed is not null)
        {
            Directory.CreateDirectory(packageFeed);
        }

        var resolution = new ModulePreviewResolution
        {
            RequestSha256 = ComputeCanonicalRequestSha256(manifest),
            Consumer = new ModulePreviewConsumerIdentity
            {
                Repository = consumerRepository,
                Commit = consumerCommit
            }
        };
        foreach (var selection in manifest.Modules)
        {
            resolution.Modules.Add(CopySelection(selection));
        }

        foreach (var module in evaluation.Modules.Where(module => module.Contract is not null))
        {
            var contract = module.Contract!;
            var contractPolicy = module.Policy.Contract
                ?? throw new PreviewToolException(
                    $"Consumer policy for module '{module.Selection.Name}' does not declare a contract.");
            MaterializedContractPackage package;
            if (contractPolicy.Published is not null)
            {
                await Console.Error.WriteLineAsync(
                    $"resolving published contract {contract.PackageId} {contract.Version} " +
                    $"from {contractPolicy.Published.Source}")
                    .ConfigureAwait(false);
                package = await ResolvePublishedContractAsync(
                    contract,
                    contractPolicy.Published,
                    workDirectory,
                    packageFeed!,
                    nugetConfig,
                    dotnetExecutable,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                var project = contractPolicy.SourceFallback.Project
                    ?? throw new PreviewToolException(
                        $"Contract '{contract.PackageId}' has no policy-owned materialization source.");
                await Console.Error.WriteLineAsync(
                    $"materializing contract {contract.PackageId} {contract.Version} from {module.Selection.Commit}")
                    .ConfigureAwait(false);
                var checkoutPath = Path.Combine(
                    workDirectory,
                    GetSafeDirectoryName(module.Selection.Name));
                await CheckoutExactCommitAsync(
                    module.Selection,
                    checkoutPath,
                    gitExecutable,
                    githubCli,
                    cancellationToken).ConfigureAwait(false);
                var projectPath = GetContainedPath(
                    checkoutPath,
                    project,
                    "contract source fallback project");
                if (!File.Exists(projectPath))
                {
                    throw new PreviewToolException(
                        $"Policy-owned contract project '{project}' does not exist " +
                        $"at commit '{module.Selection.Commit}'.");
                }
                EnsureNoSymbolicLinks(checkoutPath, project);

                package = await PackContractAsync(
                    contract,
                    contractPolicy,
                    projectPath,
                    packageFeed!,
                    nugetConfig,
                    properties,
                    dotnetExecutable,
                    cancellationToken).ConfigureAwait(false);
            }
            resolution.Contracts.Add(new ModulePreviewResolvedContract
            {
                Module = contract.Module,
                PackageId = contract.PackageId,
                Version = contract.Version,
                Sha256 = package.Sha256,
                Source = package.Source,
                PackagePath = package.Path
            });
        }

        foreach (var image in manifest.Images)
        {
            var reference = $"{image.Repository}@{image.Sha256}";
            await Console.Error.WriteLineAsync($"verifying image {reference}").ConfigureAwait(false);
            var result = await RunCommandAsync(
                dockerExecutable,
                ["buildx", "imagetools", "inspect", reference],
                workDirectory,
                standardInput: null,
                cancellationToken).ConfigureAwait(false);
            EnsureSuccess(result, $"OCI image verification for '{reference}'");
            resolution.Images.Add(CopyImage(image));
        }

        SortResolution(resolution);
        await resolution.SaveAsync(resolutionPath, cancellationToken).ConfigureAwait(false);
        if (githubEnvironmentPath is not null)
        {
            await WriteGitHubEnvironmentAsync(
                githubEnvironmentPath,
                resolutionPath,
                packageFeed,
                evaluation,
                cancellationToken).ConfigureAwait(false);
        }

        await Console.Out.WriteLineAsync(resolutionPath).ConfigureAwait(false);
        return 0;
    }

    private static Dictionary<string, string> ParseMaterializationProperties(
        IEnumerable<string> values,
        ModulePreviewPolicyEvaluation evaluation)
    {
        var allowed = evaluation.Modules
            .Where(module => module.Contract is not null)
            .SelectMany(module => module.Policy.Contract!.AllowedPackProperties)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values)
        {
            var equals = value.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new PreviewToolException(
                    $"Invalid property '{value}'. Expected <name>=<value>.");
            }

            var name = value[..equals];
            var propertyValue = value[(equals + 1)..];
            if (!allowed.Contains(name))
            {
                throw new PreviewToolException(
                    $"MSBuild property '{name}' is not allowed by the consumer preview policy.");
            }

            if (name.Equals("PackageVersion", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Version", StringComparison.OrdinalIgnoreCase))
            {
                throw new PreviewToolException(
                    $"MSBuild property '{name}' is controlled by the immutable contract request.");
            }

            if (propertyValue.Length > 2048 || propertyValue.Any(char.IsControl))
            {
                throw new PreviewToolException(
                    $"MSBuild property '{name}' must be at most 2048 characters without control characters.");
            }

            if (!properties.TryAdd(name, propertyValue))
            {
                throw new PreviewToolException($"MSBuild property '{name}' was specified more than once.");
            }
        }

        return properties;
    }

    private static void EnsureEmptyWorkDirectory(string workDirectory)
    {
        if (Directory.Exists(workDirectory) && Directory.EnumerateFileSystemEntries(workDirectory).Any())
        {
            throw new PreviewToolException(
                $"Materialization work directory '{workDirectory}' must be empty.");
        }

        Directory.CreateDirectory(workDirectory);
    }

    private static string ComputeCanonicalRequestSha256(ModulePreviewManifest manifest)
    {
        SortManifest(manifest);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ModulePreviewJson.SerializerOptions);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static ModulePreviewSelection CopySelection(ModulePreviewSelection selection) => new()
    {
        Name = selection.Name,
        Repository = selection.Repository,
        Commit = selection.Commit,
        Branch = selection.Branch,
        BaseRef = selection.BaseRef,
        BaseCommit = selection.BaseCommit
    };

    private static ModulePreviewImageArtifact CopyImage(ModulePreviewImageArtifact image) => new()
    {
        Module = image.Module,
        Resource = image.Resource,
        ResourceKind = image.ResourceKind,
        Repository = image.Repository,
        Sha256 = image.Sha256
    };

    private static async Task CheckoutExactCommitAsync(
        ModulePreviewSelection selection,
        string checkoutPath,
        string gitExecutable,
        string githubCli,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(checkoutPath);
        await RunRequiredCommandAsync(
            gitExecutable,
            ["init", "--quiet", checkoutPath],
            Path.GetDirectoryName(checkoutPath)!,
            "git init",
            cancellationToken).ConfigureAwait(false);
        await RunRequiredCommandAsync(
            gitExecutable,
            ["remote", "add", "origin", selection.Repository],
            checkoutPath,
            "git remote add",
            cancellationToken).ConfigureAwait(false);
        await RunRequiredCommandAsync(
            gitExecutable,
            GitHubGitAuthentication.ConfigureCredentialHelper(
                ["fetch", "--quiet", "--no-tags", "--depth", "1", "origin", selection.Commit],
                selection.Repository,
                githubCli),
            checkoutPath,
            "git fetch exact preview commit",
            cancellationToken).ConfigureAwait(false);
        await RunRequiredCommandAsync(
            gitExecutable,
            ["-c", "advice.detachedHead=false", "checkout", "--quiet", "--detach", "FETCH_HEAD"],
            checkoutPath,
            "git checkout exact preview commit",
            cancellationToken).ConfigureAwait(false);

        var head = await RunCommandAsync(
            gitExecutable,
            ["rev-parse", "HEAD"],
            checkoutPath,
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(head, "git rev-parse exact preview commit");
        if (!string.Equals(head.StandardOutput.Trim(), selection.Commit, StringComparison.OrdinalIgnoreCase))
        {
            throw new PreviewToolException(
                $"Exact checkout for module '{selection.Name}' resolved '{head.StandardOutput.Trim()}', " +
                $"not requested commit '{selection.Commit}'.");
        }

        var status = await RunCommandAsync(
            gitExecutable,
            ["status", "--porcelain=v1", "--untracked-files=all"],
            checkoutPath,
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(status, "git status after exact preview checkout");
        if (!string.IsNullOrWhiteSpace(status.StandardOutput))
        {
            throw new PreviewToolException(
                $"Exact checkout for module '{selection.Name}' is unexpectedly dirty.");
        }
    }

    private static async Task<MaterializedContractPackage> PackContractAsync(
        ModulePreviewContractRequest contract,
        ModulePreviewConsumerContractPolicy policy,
        string projectPath,
        string packageFeed,
        string? nugetConfig,
        IReadOnlyDictionary<string, string> properties,
        string dotnetExecutable,
        CancellationToken cancellationToken)
    {
        var packageFeedParent = Path.GetDirectoryName(packageFeed)
            ?? throw new PreviewToolException(
                $"Unable to determine the parent directory for package feed '{packageFeed}'.");
        var stagingDirectory = Path.Combine(
            packageFeedParent,
            $".module-preview-contract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        try
        {
            return await PackContractInStagingDirectoryAsync(
                contract,
                policy,
                projectPath,
                packageFeed,
                stagingDirectory,
                nugetConfig,
                properties,
                dotnetExecutable,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(stagingDirectory))
            {
                Directory.Delete(stagingDirectory, recursive: true);
            }
        }
    }

    private static async Task<MaterializedContractPackage> ResolvePublishedContractAsync(
        ModulePreviewContractRequest contract,
        ModulePreviewPublishedContractPolicy policy,
        string workDirectory,
        string packageFeed,
        string? nugetConfig,
        string dotnetExecutable,
        CancellationToken cancellationToken)
    {
        var resolutionDirectory = Path.Combine(
            workDirectory,
            $"published-{GetSafeDirectoryName(contract.Module)}");
        var packagesDirectory = Path.Combine(resolutionDirectory, "packages");
        Directory.CreateDirectory(resolutionDirectory);
        var projectPath = Path.Combine(resolutionDirectory, "ContractResolver.csproj");
        var project = $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="{{contract.PackageId}}" Version="[{{contract.Version}}]" />
              </ItemGroup>
            </Project>
            """;
        await File.WriteAllTextAsync(
            projectPath,
            project,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);

        var restoreArguments = new List<string>
        {
            "restore",
            projectPath,
            "--packages", packagesDirectory,
            "--no-cache",
            "--force-evaluate",
            "--nologo"
        };
        if (nugetConfig is not null)
        {
            restoreArguments.Add("--configfile");
            restoreArguments.Add(nugetConfig);
        }
        await RunRequiredCommandAsync(
            dotnetExecutable,
            restoreArguments,
            resolutionDirectory,
            $"resolve published contract '{contract.PackageId}'",
            cancellationToken).ConfigureAwait(false);

        var candidates = Directory.Exists(packagesDirectory)
            ? Directory.GetFiles(packagesDirectory, "*.nupkg", SearchOption.AllDirectories)
                .Where(path => PackageIdentityMatches(path, contract.PackageId, contract.Version))
                .ToArray()
            : [];
        var publishedPackage = candidates.Length switch
        {
            1 => Path.GetFullPath(candidates[0]),
            0 => throw new PreviewToolException(
                $"Published source '{policy.Source}' did not resolve package " +
                $"'{contract.PackageId}' version '{contract.Version}'."),
            _ => throw new PreviewToolException(
                $"Published source '{policy.Source}' resolved multiple matching packages for " +
                $"'{contract.PackageId}' version '{contract.Version}'.")
        };

        await VerifyPublishedPackageSourceAndContentHashAsync(
            publishedPackage,
            policy.Source,
            cancellationToken).ConfigureAwait(false);
        var sha256 = await ComputeFileSha256Async(publishedPackage, cancellationToken).ConfigureAwait(false);
        var packagePath = Path.Combine(packageFeed, Path.GetFileName(publishedPackage));
        if (File.Exists(packagePath))
        {
            var existingSha256 = await ComputeFileSha256Async(packagePath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(existingSha256, sha256, StringComparison.Ordinal))
            {
                throw new PreviewToolException(
                    $"Package feed already contains different bytes for '{contract.PackageId}' " +
                    $"version '{contract.Version}'.");
            }
        }
        else
        {
            File.Copy(publishedPackage, packagePath);
        }

        return new MaterializedContractPackage(Path.GetFullPath(packagePath), sha256, policy.Source);
    }

    private static async Task VerifyPublishedPackageSourceAndContentHashAsync(
        string packagePath,
        string expectedSource,
        CancellationToken cancellationToken)
    {
        var metadataPath = Path.Combine(
            Path.GetDirectoryName(packagePath)!,
            ".nupkg.metadata");
        if (!File.Exists(metadataPath))
        {
            throw new PreviewToolException(
                $"Published package '{Path.GetFileName(packagePath)}' has no NuGet source metadata.");
        }

        JsonDocument metadata;
        var metadataStream = new FileStream(
            metadataPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            metadata = await JsonDocument.ParseAsync(
                metadataStream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException exception)
        {
            throw new PreviewToolException(
                $"Published package '{Path.GetFileName(packagePath)}' has invalid NuGet source metadata.",
                exception);
        }
        finally
        {
            await metadataStream.DisposeAsync().ConfigureAwait(false);
        }

        using (metadata)
        {
            var actualSource = metadata.RootElement.TryGetProperty("source", out var source)
                && source.ValueKind == JsonValueKind.String
                ? source.GetString()
                : null;
            if (!string.Equals(actualSource, expectedSource, StringComparison.Ordinal))
            {
                throw new PreviewToolException(
                    $"Published package '{Path.GetFileName(packagePath)}' resolved from '{actualSource}', " +
                    $"not policy-owned source '{expectedSource}'.");
            }

            var contentHash = metadata.RootElement.TryGetProperty("contentHash", out var hash)
                && hash.ValueKind == JsonValueKind.String
                ? hash.GetString()
                : null;
            byte[] expectedHash;
            try
            {
                expectedHash = Convert.FromBase64String(contentHash ?? string.Empty);
            }
            catch (FormatException exception)
            {
                throw new PreviewToolException(
                    $"Published package '{Path.GetFileName(packagePath)}' has invalid NuGet content hash metadata.",
                    exception);
            }

            var packageStream = new FileStream(
                packagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] actualHash;
            try
            {
                actualHash = await SHA512.HashDataAsync(packageStream, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await packageStream.DisposeAsync().ConfigureAwait(false);
            }

            if (expectedHash.Length != actualHash.Length ||
                !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
            {
                throw new PreviewToolException(
                    $"Published package '{Path.GetFileName(packagePath)}' does not match its NuGet content hash.");
            }
        }
    }

    private static async Task<MaterializedContractPackage> PackContractInStagingDirectoryAsync(
        ModulePreviewContractRequest contract,
        ModulePreviewConsumerContractPolicy policy,
        string projectPath,
        string packageFeed,
        string stagingDirectory,
        string? nugetConfig,
        IReadOnlyDictionary<string, string> properties,
        string dotnetExecutable,
        CancellationToken cancellationToken)
    {
        var allowedProperties = policy.AllowedPackProperties.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var propertyArguments = properties
            .Where(property => allowedProperties.Contains(property.Key))
            .OrderBy(property => property.Key, StringComparer.OrdinalIgnoreCase)
            .Select(property => $"-p:{property.Key}={property.Value}")
            .ToArray();
        var restoreArguments = new List<string> { "restore", projectPath, "--nologo" };
        if (nugetConfig is not null)
        {
            restoreArguments.Add("--configfile");
            restoreArguments.Add(nugetConfig);
        }
        restoreArguments.AddRange(propertyArguments);
        await RunRequiredCommandAsync(
            dotnetExecutable,
            restoreArguments,
            Path.GetDirectoryName(projectPath)!,
            $"restore contract '{contract.PackageId}'",
            cancellationToken).ConfigureAwait(false);

        var packArguments = new List<string>
        {
            "pack",
            projectPath,
            "--configuration", "Release",
            "--no-restore",
            "--output", stagingDirectory,
            $"-p:PackageVersion={contract.Version}",
            $"-p:Version={contract.Version}",
            "--nologo"
        };
        packArguments.AddRange(propertyArguments);
        await RunRequiredCommandAsync(
            dotnetExecutable,
            packArguments,
            Path.GetDirectoryName(projectPath)!,
            $"pack contract '{contract.PackageId}'",
            cancellationToken).ConfigureAwait(false);

        var candidates = Directory.GetFiles(stagingDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Where(path => PackageIdentityMatches(path, contract.PackageId, contract.Version))
            .ToArray();
        var producedPackage = candidates.Length switch
        {
            1 => Path.GetFullPath(candidates[0]),
            0 => throw new PreviewToolException(
                $"Contract pack did not produce package '{contract.PackageId}' version '{contract.Version}'."),
            _ => throw new PreviewToolException(
                $"Contract pack produced multiple matching packages for '{contract.PackageId}' " +
                $"version '{contract.Version}'.")
        };

        var sha256 = await ComputeFileSha256Async(producedPackage, cancellationToken).ConfigureAwait(false);
        var packagePath = Path.Combine(packageFeed, Path.GetFileName(producedPackage));
        File.Move(producedPackage, packagePath, overwrite: true);
        return new MaterializedContractPackage(Path.GetFullPath(packagePath), sha256, Source: null);
    }

    private static bool PackageIdentityMatches(string packagePath, string packageId, string version)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var nuspecs = archive.Entries
                .Where(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (nuspecs.Length != 1)
            {
                return false;
            }

            using var stream = nuspecs[0].Open();
            var document = XDocument.Load(stream, LoadOptions.None);
            var metadata = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "metadata");
            var actualId = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "id")?.Value;
            var actualVersion = metadata?.Elements().FirstOrDefault(element => element.Name.LocalName == "version")?.Value;
            return string.Equals(actualId, packageId, StringComparison.Ordinal) &&
                string.Equals(actualVersion, version, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is InvalidDataException or XmlException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var digest = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            return Convert.ToHexStringLower(digest);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunRequiredCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string operation,
        CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            executable,
            arguments,
            workingDirectory,
            standardInput: null,
            cancellationToken).ConfigureAwait(false);
        EnsureSuccess(result, operation);
    }

    private static string GetContainedPath(string root, string relativePath, string description)
    {
        var fullRoot = Path.GetFullPath(root);
        var path = Path.GetFullPath(relativePath, fullRoot);
        var relative = Path.GetRelativePath(fullRoot, path);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new PreviewToolException($"The {description} escapes the exact module checkout.");
        }

        return path;
    }

    private static string GetSafeDirectoryName(string module)
    {
        var builder = new StringBuilder(module.Length);
        foreach (var character in module)
        {
            builder.Append(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'
                ? character
                : '-');
        }

        var suffix = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(module)))[..12];
        return $"{builder}-{suffix}";
    }

    private static void EnsureNoSymbolicLinks(string root, string relativePath)
    {
        var current = Path.GetFullPath(root);
        foreach (var segment in relativePath.Split('/'))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new PreviewToolException(
                    $"Policy-owned contract path '{relativePath}' contains a symbolic link.");
            }
        }
    }

    private static void SortResolution(ModulePreviewResolution resolution)
    {
        ReplaceContents(resolution.Modules, resolution.Modules
            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase));
        ReplaceContents(resolution.Contracts, resolution.Contracts
            .OrderBy(contract => contract.Module, StringComparer.OrdinalIgnoreCase));
        ReplaceContents(resolution.Images, resolution.Images
            .OrderBy(image => image.Module, StringComparer.OrdinalIgnoreCase)
            .ThenBy(image => image.Resource, StringComparer.OrdinalIgnoreCase));
    }

    private static async Task WriteGitHubEnvironmentAsync(
        string path,
        string resolutionPath,
        string? packageFeed,
        ModulePreviewPolicyEvaluation evaluation,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>
        {
            $"ModulePreview__Resolution={ValidateEnvironmentValue(resolutionPath, "resolution path")}"
        };
        if (evaluation.Modules.Any(module => module.Contract is not null))
        {
            lines.Add(
                $"ModulePreview__PackageFeed={ValidateEnvironmentValue(packageFeed!, "package feed")}");
        }
        foreach (var module in evaluation.Modules.Where(module => module.Contract is not null))
        {
            var environmentName = module.Policy.Contract!.VersionEnvironment;
            lines.Add(
                $"{environmentName}={ValidateEnvironmentValue(module.Contract!.Version, environmentName)}");
        }

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.AppendAllTextAsync(
            path,
            string.Join(Environment.NewLine, lines) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static string ValidateEnvironmentValue(string value, string description)
    {
        if (value.Any(character => character is '\r' or '\n'))
        {
            throw new PreviewToolException($"The {description} cannot contain a newline.");
        }

        return value;
    }

    private static ProducedImage ParseProducedImage(string value)
    {
        var equals = value.IndexOf('=', StringComparison.Ordinal);
        var at = value.LastIndexOf('@');
        if (equals <= 0 || at <= equals + 1 || at == value.Length - 1)
        {
            throw new PreviewToolException(
                $"Invalid image '{value}'. Expected <resource>=<repository>@<sha256-digest>.");
        }

        var image = new ProducedImage(value[..equals], value[(equals + 1)..at], value[(at + 1)..]);
        PreviewPolicyValidation.ValidateImageDigest(image.Sha256, $"Image '{image.Resource}' digest");
        return image;
    }

    private static void SortManifest(ModulePreviewManifest manifest)
    {
        ReplaceContents(manifest.Modules, manifest.Modules
            .OrderBy(module => module.Name, StringComparer.OrdinalIgnoreCase));
        ReplaceContents(manifest.Contracts, manifest.Contracts
            .OrderBy(contract => contract.Module, StringComparer.OrdinalIgnoreCase));
        ReplaceContents(manifest.Images, manifest.Images
            .OrderBy(image => image.Module, StringComparer.OrdinalIgnoreCase)
            .ThenBy(image => image.Resource, StringComparer.OrdinalIgnoreCase));
    }

    private static void ReplaceContents<T>(IList<T> target, IEnumerable<T> ordered)
    {
        var values = ordered.ToArray();
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private sealed record ProducedImage(string Resource, string Repository, string Sha256);

    private sealed record MaterializedContractPackage(string Path, string Sha256, string? Source);
}
