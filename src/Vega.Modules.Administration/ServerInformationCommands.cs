using Vega.Contracts.Commands;
using Vega.Contracts.Messaging;

namespace Vega.Modules.Administration;

public sealed class ServerTextCommand(string name, string worldPath, string deploymentPath) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new(name, [], [], Syntax: name);

    public async ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token)
    {
        if (invocation.Arguments.Count != 0) return new(CommandExecutionStatus.InvalidUsage, "text.usage");
        try
        {
            string? content = await Task.Run(async () =>
            {
                string path = File.Exists(worldPath) ? worldPath : deploymentPath;
                if (!File.Exists(path)) return null;
                using var reader = new StreamReader(path);
                char[] buffer = new char[16385];
                int read = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (read == buffer.Length) throw new InvalidDataException("Server text exceeds 16 KiB characters.");
                return new string(buffer, 0, read);
            }, token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content)) return CommandExecutionResult.WithMessages(MessageDescriptor.Info("text.unconfigured", name)
                .WithDefaultText("No {0} text has been configured."));
            var lines = content.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length > 50 || lines.Any(line => line.Length > 512)) return CommandExecutionResult.Failed("text.too_long");
            return CommandExecutionResult.WithMessages(lines.Select(line => MessageDescriptor.Info("text.line", line).WithDefaultText("{0}")).ToArray());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return CommandExecutionResult.Failed("text.read_failed");
        }
    }
}

public sealed class ServerVersionCommand(string vegaVersion, string terrariaVersion) : ICommandHandler
{
    public CommandDescriptor Descriptor { get; } = new("version", [], [], Syntax: "version");
    public ValueTask<CommandExecutionResult> ExecuteAsync(CommandInvocation invocation, CancellationToken token) => ValueTask.FromResult(
        invocation.Arguments.Count != 0 ? new(CommandExecutionStatus.InvalidUsage, "version.usage") :
        CommandExecutionResult.WithMessages(MessageDescriptor.Info("version.info", vegaVersion, terrariaVersion)
            .WithDefaultText("Vega {0} | patched Terraria {1}.")));
}
