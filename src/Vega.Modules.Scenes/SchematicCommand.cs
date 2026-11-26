using TerraRuntime.Schematics;
using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;
using Vega.Contracts.Threading;

namespace Vega.Modules.Scenes;

internal static class SchematicOperations
{
    // ponytail: one in-flight file operation per world process bounds retained detached
    // snapshots; add a byte-budgeted lane only if concurrent import demand warrants it.
    public static readonly SemaphoreSlim Gate = new(1, 1);

    public static string? ResolveFile(string name)
    {
        if (name.EndsWith(SchematicFile.Extension, StringComparison.OrdinalIgnoreCase))
            name = name[..^SchematicFile.Extension.Length];
        return name.Length is >= 1 and <= 100 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? Path.GetFullPath(Path.Combine("schematics", name + SchematicFile.Extension)) : null;
    }
}

public sealed class SchematicCommand(SchematicSnapshotService snapshots) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("schematic", [], ["vega.scenes.manage"],
        Syntax: "schematic inspect <file> | schematic export <file> <x> <y> <width> <height> | schematic place <file> <x> <y>");

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken cancellationToken)
    {
        var args = invocation.Arguments;
        if (args.Count < 2 || SchematicOperations.ResolveFile(args[1]) is not { } path) return Failure(Descriptor.Syntax);
        if (args[0].Equals("place", StringComparison.OrdinalIgnoreCase))
        {
            if (!invocation.Principal.IsServer) return Failure("Native placement is restricted to the isolated sandbox console.");
            if (args.Count != 4 || !int.TryParse(args[2], out int placementX) || !int.TryParse(args[3], out int placementY)) return Failure(Descriptor.Syntax);
            var result = await snapshots.PlaceFileAsync(path, placementX, placementY, cancellationToken).ConfigureAwait(false);
            return result.Status == Vega.Contracts.Worlds.WorldSchematicPlacementStatus.Applied
                ? Info($"Schematic placed with {result.NpcsPlaced} NPCs.") : Failure(result.Error ?? result.Status.ToString());
        }
        bool inspect = args.Count == 2 && args[0].Equals("inspect", StringComparison.OrdinalIgnoreCase);
        SchematicBounds bounds = default;
        if (!inspect)
        {
            if (args.Count != 6 || !args[0].Equals("export", StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(args[2], out int x) || !int.TryParse(args[3], out int y) ||
                !int.TryParse(args[4], out int w) || !int.TryParse(args[5], out int h)) return Failure(Descriptor.Syntax);
            bounds = new(x, y, w, h);
        }
        if (!SchematicOperations.Gate.Wait(0)) return Failure("Another schematic operation is in progress.");
        try
        {
            SchematicDocument document;
            if (inspect) document = await Task.Run(() => SchematicFile.Load(path), cancellationToken).ConfigureAwait(false);
            else
            {
                bounds.Validate();
                if (File.Exists(path)) return Failure("Schematic already exists; choose a new filename.");
                if (await snapshots.CaptureDocumentAsync(bounds, cancellationToken).ConfigureAwait(false) is not { } captured)
                    return Failure("Game thread is unavailable.");
                document = captured;
                await Task.Run(() =>
                {
                    string staged = path + $".{Guid.NewGuid():N}.staging";
                    try
                    {
                        SchematicFile.Save(staged, document);
                        cancellationToken.ThrowIfCancellationRequested();
                        File.Move(staged, path); // Never replace a file created since admission.
                    }
                    finally { if (File.Exists(staged)) File.Delete(staged); }
                }, cancellationToken).ConfigureAwait(false);
            }
            return Info($"{Path.GetFileName(path)}: {document.Width}x{document.Height}, content={document.ContentVersion}, " +
                $"chests={document.Chests.Length}, signs={document.Signs.Length}, entities={document.TileEntities.Length}, " +
                $"NPCs={document.Npcs.Length}, world items={document.WorldItems.Length}, markers={document.Markers.Length}.");
        }
        catch (SchematicFormatException ex) { return Failure(ex.Message); }
        catch (IOException ex) { return Failure(ex.Message); }
        catch (UnauthorizedAccessException) { return Failure("Schematic file is inaccessible."); }
        catch (ArgumentException ex) { return Failure(ex.Message); }
        catch (OverflowException) { return Failure("Schematic dimensions exceed the supported range."); }
        finally { SchematicOperations.Gate.Release(); }
    }

    private static CommandExecutionResult Info(string text) => CommandExecutionResult.WithMessages(MessageDescriptor.Info("command.schematic.info").WithDefaultText(text));
    private static CommandExecutionResult Failure(string text) => CommandExecutionResult.Failed("command.schematic.failed", MessageDescriptor.Error("command.schematic.failed").WithDefaultText(text));
}
