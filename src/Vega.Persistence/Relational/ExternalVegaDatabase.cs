using Vega.Contracts.Cluster.Events;
using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Moderation;
using Vega.Contracts.Regions;
using Vega.Contracts.Worlds;
using System.Diagnostics;

namespace Vega.Persistence.Relational;

public abstract class ExternalVegaDatabase : IVegaDatabase
{
    private readonly ExternalSchemaInitializer schemaInitializer;
    private readonly SemaphoreSlim initialization = new(1, 1);
    private int schemaVersion;

    private protected ExternalVegaDatabase(
        string provider,
        string name,
        IRelationalConnectionFactory connections,
        RelationalProvider relationalProvider,
        string schemaSql,
        string identitySchemaSql,
        string regionSchemaSql,
        string moderationSchemaSql,
        RelationalGlobalBanSql globalBanSql)
    {
        Provider = provider;
        Name = name;
        schemaInitializer = new ExternalSchemaInitializer(
            connections,
            relationalProvider,
            schemaSql,
            identitySchemaSql,
            regionSchemaSql,
            moderationSchemaSql);
        GlobalBans = new RelationalGlobalBanReplica(connections, globalBanSql, EnsureReady);
        var identities = new RelationalIdentityService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
        Accounts = identities;
        Authorization = identities;
        RoleLeases = new RelationalRoleLeaseService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
        Mutes = new RelationalMuteService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
        Regions = new RelationalRegionService(
            connections.Open,
            connections.OpenAsync,
            EnsureReady);
    }

    public string Provider { get; }

    public string Name { get; }

    public int SchemaVersion => Volatile.Read(ref schemaVersion);

    public bool IsReady => SchemaVersion > 0;

    public abstract string BackupFileName { get; }

    public IGlobalBanReplica GlobalBans { get; }

    public IAccountService Accounts { get; }

    public IAuthorizationService Authorization { get; }

    public IRoleLeaseService RoleLeases { get; }

    public IMuteService Mutes { get; }

    public IRegionService Regions { get; }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (IsReady)
        {
            return;
        }

        await initialization.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsReady)
            {
                return;
            }

            int version = await schemaInitializer
                .InitializeAsync(cancellationToken)
                .ConfigureAwait(false);
            Volatile.Write(ref schemaVersion, version);
            Authorization.SnapshotGroups();
            RoleLeases.SnapshotActive(DateTimeOffset.UtcNow);
            Mutes.SnapshotActive(DateTimeOffset.UtcNow);
            Regions.SnapshotRegions(WorldId.Primary);
        }
        finally
        {
            initialization.Release();
        }
    }

    public abstract ValueTask WriteBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default);

    protected void EnsureBackupReady() => EnsureReady();

    protected static async ValueTask RunDumpAsync(
        string executable,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string> environment,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        string destination = Path.GetFullPath(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (File.Exists(destination)) throw new IOException($"Backup destination '{destination}' already exists.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        foreach ((string key, string value) in environment) start.Environment[key] = value;
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException($"Database dump tool '{executable}' did not start.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new IOException($"Database dump tool '{executable}' is not installed or cannot be started.", ex);
        }

        Task<string> stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);
        _ = await stdoutTask.ConfigureAwait(false);
        if (process.ExitCode != 0 || !File.Exists(destination))
        {
            if (File.Exists(destination)) File.Delete(destination);
            string detail = stderr.Length > 1024 ? stderr[..1024] : stderr;
            throw new IOException($"Database dump tool '{executable}' failed with exit code {process.ExitCode}: {detail}");
        }
    }

    private void EnsureReady()
    {
        if (!IsReady)
        {
            throw new InvalidOperationException(
                $"{Name} has not completed runtime initialization.");
        }
    }
}
