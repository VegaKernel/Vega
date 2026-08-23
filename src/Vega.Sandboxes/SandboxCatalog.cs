using System.Security.Cryptography;
using System.Text.Json;
using Vega.Contracts.Sandboxes;
using Vega.Contracts.Worlds;

namespace Vega.Sandboxes;

internal sealed class SandboxCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
    private readonly string path;
    private readonly Dictionary<WorldId, SandboxDescriptor> descriptors;

    public SandboxCatalog(string path)
    {
        this.path = Path.GetFullPath(path);
        descriptors = Load(this.path).ToDictionary(static descriptor => descriptor.WorldId);
    }

    public IReadOnlyList<SandboxDescriptor> Snapshot() => descriptors.Values
        .OrderBy(static descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    public bool TryGet(WorldId worldId, out SandboxDescriptor? descriptor) =>
        descriptors.TryGetValue(worldId, out descriptor);

    public void Add(SandboxDescriptor descriptor)
    {
        descriptors.Add(descriptor.WorldId, descriptor);
        Save();
    }

    public void Replace(SandboxDescriptor descriptor)
    {
        descriptors[descriptor.WorldId] = descriptor;
        Save();
    }

    public bool Remove(WorldId worldId)
    {
        if (!descriptors.Remove(worldId))
        {
            return false;
        }

        Save();
        return true;
    }

    private static IReadOnlyList<SandboxDescriptor> Load(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<CatalogEntry[]>(File.ReadAllText(path), JsonOptions) ?? [])
                .Select(static entry => entry.ToDescriptor())
                .ToArray();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Sandbox catalog '{path}' is invalid: {ex.Message}", ex);
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + ".tmp";
        File.WriteAllText(
            temporary,
            JsonSerializer.Serialize(Snapshot().Select(CatalogEntry.From).ToArray(), JsonOptions));
        File.Move(temporary, path, true);
    }

    private sealed record CatalogEntry(
        string WorldId,
        string DisplayName,
        string SourceWorldPath,
        string InstanceWorldPath,
        SandboxPersistenceMode Persistence,
        int PlayerCapacity,
        SandboxAccessMode AccessMode,
        string? AccessPermission,
        string[] AllowedAccounts,
        TimeSpan? IdleTimeToLive,
        SandboxSscMode SscMode,
        string[] EnabledModules,
        int ListenerPort,
        long Revision,
        DateTimeOffset CreatedAt,
        string TemplateRevision)
    {
        public static CatalogEntry From(SandboxDescriptor descriptor) => new(
            descriptor.WorldId.Value,
            descriptor.DisplayName,
            descriptor.SourceWorldPath,
            descriptor.InstanceWorldPath,
            descriptor.Persistence,
            descriptor.PlayerCapacity,
            descriptor.Access.Mode,
            descriptor.Access.Permission,
            descriptor.Access.AllowedAccounts?.ToArray() ?? [],
            descriptor.IdleTimeToLive,
            descriptor.SscMode,
            descriptor.EnabledModules.ToArray(),
            descriptor.ListenerPort,
            descriptor.Revision,
            descriptor.CreatedAt,
            descriptor.TemplateRevision);

        public SandboxDescriptor ToDescriptor() => new(
            new WorldId(WorldId),
            DisplayName,
            SourceWorldPath,
            InstanceWorldPath,
            Persistence,
            PlayerCapacity,
            new SandboxAccessPolicy(
                AccessMode,
                AccessPermission,
                new HashSet<string>(AllowedAccounts ?? [], StringComparer.OrdinalIgnoreCase)),
            IdleTimeToLive,
            SscMode,
            new HashSet<string>(EnabledModules ?? [], StringComparer.OrdinalIgnoreCase),
            ListenerPort,
            Revision,
            CreatedAt,
            TemplateRevision);
    }
}

internal sealed class SandboxMaterializer(string instancesRoot, int maximumInstanceMegabytes)
{
    private readonly string root = EnsureRoot(instancesRoot);
    private readonly long maximumBytes = checked((long)maximumInstanceMegabytes * 1024 * 1024);

    public string InstancePath(WorldId worldId) =>
        Path.Combine(root, worldId.Value, "world.wld");

    public async ValueTask<string> MaterializeAsync(
        WorldId worldId,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ValidateWorldId(worldId);
        string source = ValidateSource(sourcePath);
        string target = InstancePath(worldId);
        if (File.Exists(target))
        {
            return target;
        }

        await PromoteCopyAsync(source, target, cancellationToken).ConfigureAwait(false);
        return target;
    }

    public async ValueTask ResetAsync(
        SandboxDescriptor descriptor,
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (descriptor.Persistence == SandboxPersistenceMode.External)
        {
            throw new InvalidOperationException("Externally managed worlds cannot be reset by Vega.");
        }

        string source = ValidateSource(sourcePath);
        string target = ValidateTarget(descriptor.InstanceWorldPath);
        await PromoteCopyAsync(source, target, cancellationToken).ConfigureAwait(false);
    }

    public void Destroy(SandboxDescriptor descriptor)
    {
        if (descriptor.Persistence == SandboxPersistenceMode.External)
        {
            return;
        }

        string target = ValidateTarget(descriptor.InstanceWorldPath);
        string directory = Path.GetDirectoryName(target)!;
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, true);
        }
    }

    public static string ComputeRevision(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private async ValueTask PromoteCopyAsync(string source, string target, CancellationToken cancellationToken)
    {
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length > maximumBytes)
        {
            throw new InvalidDataException(
                $"World image is {sourceInfo.Length} bytes and exceeds the {maximumBytes}-byte sandbox limit.");
        }

        string targetRoot = Path.GetPathRoot(Path.GetFullPath(target))!;
        long requiredFreeSpace = checked(sourceInfo.Length + 64L * 1024 * 1024);
        if (new DriveInfo(targetRoot).AvailableFreeSpace < requiredFreeSpace)
        {
            throw new IOException(
                $"Insufficient free space to stage sandbox world; {requiredFreeSpace} bytes are required.");
        }

        string directory = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(directory);
        string staging = Path.Combine(directory, $".{Path.GetFileName(target)}.{Guid.NewGuid():N}.staging");
        string backup = target + ".previous";
        try
        {
            await using (FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, true))
            await using (FileStream output = new(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024, true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (File.Exists(target))
            {
                File.Replace(staging, target, backup, true);
            }
            else
            {
                File.Move(staging, target);
            }
        }
        finally
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private string ValidateTarget(string path)
    {
        string resolved = Path.GetFullPath(path);
        string prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Sandbox instance path escapes the managed instance root.");
        }

        return resolved;
    }

    private static string ValidateSource(string path)
    {
        string resolved = Path.GetFullPath(path);
        if (!File.Exists(resolved) || !string.Equals(Path.GetExtension(resolved), ".wld", StringComparison.OrdinalIgnoreCase))
        {
            throw new FileNotFoundException("Sandbox source must be an existing .wld file.", resolved);
        }

        return resolved;
    }

    private static string EnsureRoot(string path)
    {
        string resolved = Path.GetFullPath(path);
        Directory.CreateDirectory(resolved);
        return resolved;
    }

    internal static void ValidateWorldId(WorldId worldId)
    {
        if (worldId == WorldId.Primary || string.IsNullOrWhiteSpace(worldId.Value) || worldId.Value.Length > 64 ||
            worldId.Value.Any(static value => !char.IsAsciiLetterOrDigit(value) && value is not '-' and not '_'))
        {
            throw new ArgumentException("Sandbox WorldId must contain 1-64 ASCII letters, digits, '-' or '_' and cannot be 'primary'.");
        }
    }
}
