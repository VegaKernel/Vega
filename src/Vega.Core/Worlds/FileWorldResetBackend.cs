using Vega.Contracts.Worlds;

namespace Vega.Core.Worlds;

public sealed class FileWorldResetBackend : IWorldResetBackend
{
    public string Name => "world-file";

    public ValueTask ValidateAsync(
        WorldResetPlan plan,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string current = Path.GetFullPath(plan.CurrentWorldPath);
        string replacement = Path.GetFullPath(plan.ReplacementSourcePath);
        if (!File.Exists(current))
        {
            throw new FileNotFoundException("Active world file does not exist.", current);
        }

        if (!File.Exists(replacement))
        {
            throw new FileNotFoundException("Replacement world file does not exist.", replacement);
        }

        if (!string.Equals(Path.GetExtension(current), ".wld", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(replacement), ".wld", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("World reset accepts only .wld files.");
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask<IPreparedWorldReset> PrepareAsync(
        WorldResetPlan plan,
        CancellationToken cancellationToken = default)
    {
        string current = Path.GetFullPath(plan.CurrentWorldPath);
        string staging = current + $".reset-{plan.OperationId:N}.tmp";
        await using (FileStream source = new(
                         Path.GetFullPath(plan.ReplacementSourcePath),
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (FileStream destination = new(
                         staging,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         128 * 1024,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new PreparedFileReset(current, staging);
    }

    private sealed class PreparedFileReset(
        string current,
        string staging) : IPreparedWorldReset
    {
        private readonly string pending = current + ".pending-reset";
        private bool committed;

        public ValueTask CommitAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(pending))
            {
                throw new IOException("Another world reset is already pending restart.");
            }

            File.Move(staging, pending, false);
            committed = true;

            return ValueTask.CompletedTask;
        }

        public ValueTask RollbackAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (committed && File.Exists(pending))
            {
                File.Move(pending, staging, true);
                committed = false;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }

            return ValueTask.CompletedTask;
        }
    }

    public static string? ApplyPending(string worldPath)
    {
        string current = Path.GetFullPath(worldPath);
        string pending = current + ".pending-reset";
        if (!File.Exists(pending))
        {
            return null;
        }

        if (!File.Exists(current))
        {
            File.Move(pending, current, false);
            return null;
        }

        string backup = current + $".pre-reset-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.bak";
        File.Replace(pending, current, backup, true);
        return backup;
    }
}
