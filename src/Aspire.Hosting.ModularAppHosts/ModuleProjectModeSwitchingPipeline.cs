#pragma warning disable ASPIREPIPELINES001
#pragma warning disable ASPIREUSERSECRETS001

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.Pipelines;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

internal enum ModuleProjectModeSwitchValue
{
    Configured,
    Project,
    Container
}

internal sealed class ModuleProjectModeSwitchState
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public ModuleProjectModeSwitchValue? Mode { get; set; }

    public IDictionary<string, ModuleProjectModeSwitchValue> Resources { get; set; } =
        new Dictionary<string, ModuleProjectModeSwitchValue>(StringComparer.OrdinalIgnoreCase);
}

internal sealed class ModuleProjectModeSwitchStore(IUserSecretsManager userSecretsManager)
{
    internal const string SecretName = "Aspire:ModularAppHosts:ModeSwitch";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public bool IsAvailable => userSecretsManager.IsAvailable;

    public string FilePath => userSecretsManager.FilePath;

    public ModuleProjectModeSwitchState Read()
    {
        if (!IsAvailable || !File.Exists(FilePath))
        {
            return new ModuleProjectModeSwitchState();
        }

        string? json;
        try
        {
            json = new ConfigurationBuilder()
                .AddJsonFile(FilePath, optional: true)
                .Build()[SecretName];
        }
        catch (Exception exception) when (exception is InvalidDataException or FormatException)
        {
            throw CreateInvalidStateException("the user-secrets file is not valid JSON", exception);
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            return new ModuleProjectModeSwitchState();
        }

        try
        {
            var state = JsonSerializer.Deserialize<ModuleProjectModeSwitchState>(json, SerializerOptions)
                ?? throw CreateInvalidStateException("the value is JSON null");
            Validate(state);
            state.Resources = new Dictionary<string, ModuleProjectModeSwitchValue>(
                state.Resources,
                StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (JsonException exception)
        {
            throw CreateInvalidStateException(
                "the value is not a valid versioned mode-switch document",
                exception);
        }
    }

    public void Write(ModuleProjectModeSwitchState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        EnsureAvailable();
        Validate(state);
        var json = JsonSerializer.Serialize(state, SerializerOptions);
        if (!userSecretsManager.TrySetSecret(SecretName, json))
        {
            throw new InvalidOperationException(
                $"Could not save module project mode state to user secret '{SecretName}'.");
        }

        var persisted = Read();
        if (persisted.Mode != state.Mode || !persisted.Resources.OrderBy(pair => pair.Key).SequenceEqual(
                state.Resources.OrderBy(pair => pair.Key)))
        {
            throw new InvalidOperationException(
                $"Module project mode state in user secret '{SecretName}' could not be verified after saving.");
        }
    }

    public void Delete()
    {
        EnsureAvailable();
        if (!userSecretsManager.TryDeleteSecret(SecretName))
        {
            throw new InvalidOperationException(
                $"Could not delete module project mode user secret '{SecretName}'.");
        }

        if (Read() is not { Mode: null, Resources.Count: 0 })
        {
            throw new InvalidOperationException(
                $"Module project mode user secret '{SecretName}' still exists after deletion.");
        }
    }

    private void EnsureAvailable()
    {
        if (IsAvailable)
        {
            return;
        }

        throw new InvalidOperationException(
            "Module project mode switching requires an AppHost UserSecretsId. Run " +
            "'dotnet user-secrets init --project <apphost-project>' once, then retry the Aspire pipeline step.");
    }

    private static void Validate(ModuleProjectModeSwitchState state)
    {
        if (state.Version != ModuleProjectModeSwitchState.CurrentVersion)
        {
            throw CreateInvalidStateException(
                $"schema version '{state.Version}' is not supported; expected " +
                $"'{ModuleProjectModeSwitchState.CurrentVersion}'");
        }

        if (state.Mode == ModuleProjectModeSwitchValue.Configured)
        {
            throw CreateInvalidStateException("the global mode cannot be Configured; delete the secret to reset it");
        }

        if (state.Resources is null)
        {
            throw CreateInvalidStateException("the resources object is null");
        }

        foreach (var (resourceName, mode) in state.Resources)
        {
            if (string.IsNullOrWhiteSpace(resourceName))
            {
                throw CreateInvalidStateException("a resource override has an empty resource name");
            }

            if (!Enum.IsDefined(mode))
            {
                throw CreateInvalidStateException(
                    $"resource '{resourceName}' has unsupported mode value '{mode}'");
            }
        }
    }

    private static InvalidOperationException CreateInvalidStateException(
        string reason,
        Exception? innerException = null) =>
        new(
            $"Module project mode user secret '{SecretName}' is invalid because {reason}. " +
            "Run 'aspire do use-configured-modes' to remove it, or replace it with Project/Container modes.",
            innerException);
}

internal sealed class ModuleProjectModeSwitchingPipeline
{
    internal const string UseProjectsStepName = "use-projects";
    internal const string UseContainersStepName = "use-containers";
    internal const string UseConfiguredModesStepName = "use-configured-modes";

    private static readonly Action<ILogger, Exception?> LogRestartRequired =
        LoggerMessage.Define(
            LogLevel.Information,
            new EventId(1, nameof(LogRestartRequired)),
            "Saved module project mode selection. Restart the AppHost for the new resource model to take effect.");

    private readonly IDistributedApplicationBuilder _builder;
    private readonly ModuleProjectModeSwitchStore _store;
    private readonly HashSet<string> _resourceNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModuleProjectModeSwitchState _state;

    public ModuleProjectModeSwitchingPipeline(IDistributedApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _builder = builder;
        _store = new ModuleProjectModeSwitchStore(builder.UserSecretsManager);
        _state = builder.ExecutionContext.IsRunMode
            ? _store.Read()
            : new ModuleProjectModeSwitchState();
        AddGlobalSteps();
    }

    public ModuleProjectMode Resolve(
        string effectiveResourceName,
        ModuleProjectMode configuredMode,
        bool imported)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveResourceName);
        if (_builder.ExecutionContext.IsRunMode &&
            _state.Resources.TryGetValue(effectiveResourceName, out var resourceMode))
        {
            if (resourceMode != ModuleProjectModeSwitchValue.Configured)
            {
                return ToProjectMode(resourceMode);
            }

            return ResolveConfigured(configuredMode, imported);
        }

        if (_builder.ExecutionContext.IsRunMode && _state.Mode is { } globalMode)
        {
            return ToProjectMode(globalMode);
        }

        return ResolveConfigured(configuredMode, imported);
    }

    public void RegisterProject(string effectiveResourceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(effectiveResourceName);
        if (!_resourceNames.Add(effectiveResourceName))
        {
            return;
        }

        AddStep(
            $"use-project-{effectiveResourceName}",
            $"Runs module project {effectiveResourceName} directly after the next AppHost start.",
            () => SetResourceMode(effectiveResourceName, ModuleProjectModeSwitchValue.Project));
        AddStep(
            $"use-container-{effectiveResourceName}",
            $"Runs module project {effectiveResourceName} as a container after the next AppHost start.",
            () => SetResourceMode(effectiveResourceName, ModuleProjectModeSwitchValue.Container));
        AddStep(
            $"use-configured-{effectiveResourceName}",
            $"Restores configured mode selection for module project {effectiveResourceName}.",
            () => SetResourceMode(effectiveResourceName, ModuleProjectModeSwitchValue.Configured));
    }

    private void AddGlobalSteps()
    {
        AddStep(
            UseProjectsStepName,
            "Runs every module project directly after the next AppHost start.",
            () => SetGlobalMode(ModuleProjectModeSwitchValue.Project));
        AddStep(
            UseContainersStepName,
            "Runs every module project as a container after the next AppHost start.",
            () => SetGlobalMode(ModuleProjectModeSwitchValue.Container));
        AddStep(
            UseConfiguredModesStepName,
            "Removes developer-local module project mode overrides.",
            _store.Delete);
    }

    private void SetGlobalMode(ModuleProjectModeSwitchValue mode)
    {
        _store.Write(new ModuleProjectModeSwitchState { Mode = mode });
    }

    private void SetResourceMode(string resourceName, ModuleProjectModeSwitchValue mode)
    {
        var state = _store.Read();
        if (mode == ModuleProjectModeSwitchValue.Configured && state.Mode is null)
        {
            state.Resources.Remove(resourceName);
        }
        else
        {
            state.Resources[resourceName] = mode;
        }

        if (state.Mode is null && state.Resources.Count == 0)
        {
            _store.Delete();
            return;
        }

        _store.Write(state);
    }

    private void AddStep(string name, string description, Action action)
    {
        _builder.Pipeline.AddStep(new PipelineStep
        {
            Name = name,
            Description = description,
            Action = context =>
            {
                action();
                LogRestartRequired(context.Logger, null);
                return Task.CompletedTask;
            }
        });
    }

    private static ModuleProjectMode ToProjectMode(ModuleProjectModeSwitchValue mode) => mode switch
    {
        ModuleProjectModeSwitchValue.Project => ModuleProjectMode.Project,
        ModuleProjectModeSwitchValue.Container => ModuleProjectMode.Container,
        _ => throw new InvalidOperationException($"Mode switch value '{mode}' is not a concrete project mode.")
    };

    private static ModuleProjectMode ResolveConfigured(ModuleProjectMode configuredMode, bool imported) =>
        configuredMode == ModuleProjectMode.Auto
            ? imported ? ModuleProjectMode.Container : ModuleProjectMode.Project
            : configuredMode;
}
