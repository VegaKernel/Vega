#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;

namespace TShockAPI;

/// <summary>
/// Registration boundary for plugins that still define commands with the legacy <see cref="Command"/> type.
/// This is a source-level migration contract, not a binary-compatibility promise.
/// </summary>
public interface ILegacyCommandRegistry : IReadOnlyCollection<Command>
{
    void Register(Command command);

    bool Unregister(Command command);

    IReadOnlyList<Command> FindAll(string alias);

    Command? FindFirst(string alias);
}

internal sealed class LegacyCommandRegistry : ILegacyCommandRegistry
{
    private readonly object syncRoot = new();
    private readonly List<Command> commands = new();

    public int Count
    {
        get
        {
            lock (syncRoot)
                return commands.Count;
        }
    }

    public void Register(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (syncRoot)
            commands.Add(command);
    }

    public bool Unregister(Command command)
    {
        ArgumentNullException.ThrowIfNull(command);

        lock (syncRoot)
            return commands.Remove(command);
    }

    public IReadOnlyList<Command> FindAll(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        lock (syncRoot)
            return commands.Where(command => command.HasAlias(alias)).ToArray();
    }

    public Command? FindFirst(string alias)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(alias);

        lock (syncRoot)
            return commands.FirstOrDefault(command => command.HasAlias(alias));
    }

    public IEnumerator<Command> GetEnumerator()
    {
        Command[] snapshot;
        lock (syncRoot)
            snapshot = commands.ToArray();

        return ((IEnumerable<Command>)snapshot).GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
