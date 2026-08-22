using System.Reflection;
using System.Text.Json;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Contracts.Worlds;

namespace Vega.Modules.Chests;

public enum ChestTemplateMode
{
    Infinite,
    Regenerate
}

public sealed record ChestTemplate(
    ChestIdentity Identity,
    ChestTemplateMode Mode,
    int DelaySeconds,
    string Name,
    IReadOnlyList<ChestItemSnapshot> Items);

public sealed class ChestPolicyModule : IVegaModule, IChestInteractionPolicy
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly IChestService chests;
    private readonly IOperationsLogger logger;
    private readonly string path;
    private readonly object sync = new();
    private readonly Dictionary<ChestIdentity, ChestTemplate> templates = [];
    private readonly Dictionary<ChestIdentity, CancellationTokenSource> pending = [];
    private CancellationTokenSource? shutdown;

    public ChestPolicyModule(IChestService chests, IOperationsLogger logger, string path)
    {
        this.chests = chests ?? throw new ArgumentNullException(nameof(chests));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));
    }

    public VegaModuleDescriptor Descriptor { get; } = new(
        "Vega.Modules.Chests",
        typeof(ChestPolicyModule).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
        ["Vega.Contracts"]);

    public void Start()
    {
        shutdown = new CancellationTokenSource();
        Load();
    }

    public IReadOnlyList<ChestTemplate> SnapshotTemplates()
    {
        lock (sync)
        {
            return Array.AsReadOnly(templates.Values
                .OrderBy(static template => template.Identity.WorldId.Value, StringComparer.Ordinal)
                .ThenBy(static template => template.Identity.X)
                .ThenBy(static template => template.Identity.Y)
                .ToArray());
        }
    }

    public async ValueTask<bool> CaptureTemplateAsync(
        ChestIdentity identity,
        ChestTemplateMode mode,
        int delaySeconds,
        CancellationToken cancellationToken = default)
    {
        if (delaySeconds is < 0 or > 86400)
        {
            return false;
        }

        ChestSnapshot? snapshot = await chests.CaptureAsync(identity, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return false;
        }

        var template = new ChestTemplate(
            identity,
            mode,
            delaySeconds,
            snapshot.Name,
            snapshot.Items.Where(static item => item.ItemType != 0 && item.Stack > 0).ToArray());
        lock (sync)
        {
            templates[identity] = template;
            Save();
        }

        return true;
    }

    public bool RemoveTemplate(ChestIdentity identity)
    {
        lock (sync)
        {
            if (!templates.Remove(identity))
            {
                return false;
            }

            if (pending.Remove(identity, out CancellationTokenSource? cancellation))
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }

            Save();
            return true;
        }
    }

    public async ValueTask ObserveAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ChestTemplate? template;
        CancellationToken token;
        lock (sync)
        {
            if (!templates.TryGetValue(identity, out template) || shutdown is null)
            {
                return;
            }

            if (pending.Remove(identity, out CancellationTokenSource? previous))
            {
                previous.Cancel();
                previous.Dispose();
            }

            var linked = CancellationTokenSource.CreateLinkedTokenSource(
                shutdown.Token,
                cancellationToken);
            pending[identity] = linked;
            token = linked.Token;
        }

        try
        {
            if (template.Mode == ChestTemplateMode.Regenerate && template.DelaySeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(template.DelaySeconds), token).ConfigureAwait(false);
            }

            await RestoreAsync(template, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            lock (sync)
            {
                if (pending.TryGetValue(identity, out CancellationTokenSource? current) && current.Token == token)
                {
                    pending.Remove(identity);
                    current.Dispose();
                }
            }
        }
    }

    public async ValueTask<bool> RestoreNowAsync(
        ChestIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ChestTemplate? template;
        lock (sync)
        {
            templates.TryGetValue(identity, out template);
        }

        return template is not null && await RestoreAsync(template, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        lock (sync)
        {
            shutdown?.Cancel();
            foreach (CancellationTokenSource cancellation in pending.Values)
            {
                cancellation.Cancel();
                cancellation.Dispose();
            }

            pending.Clear();
            shutdown?.Dispose();
            shutdown = null;
        }
    }

    private async ValueTask<bool> RestoreAsync(
        ChestTemplate template,
        CancellationToken cancellationToken)
    {
        ChestSnapshot? current = await chests
            .CaptureAsync(template.Identity, cancellationToken)
            .ConfigureAwait(false);
        if (current is null)
        {
            return false;
        }

        ChestMutationResult result = await chests.ReplaceAsync(
            new ChestMutationRequest(
                template.Identity,
                current.Revision,
                template.Items,
                Descriptor.Name,
                Name: template.Name),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
        {
            logger.Log(
                OperationsLogLevel.Warning,
                "chests",
                $"Chest restore failed for {template.Identity}: {result.ErrorCode}.");
        }

        return result.IsSuccess;
    }

    private void Load()
    {
        if (!File.Exists(path))
        {
            return;
        }

        ChestTemplate[] loaded = JsonSerializer.Deserialize<ChestTemplate[]>(File.ReadAllText(path), JsonOptions) ?? [];
        lock (sync)
        {
            templates.Clear();
            foreach (ChestTemplate template in loaded)
            {
                templates[template.Identity] = template;
            }
        }
    }

    private void Save()
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(templates.Values.ToArray(), JsonOptions));
        File.Move(temporary, path, true);
    }
}
