namespace Vega.Contracts.Extensions;

public readonly record struct ExtensionStateScopeId
{
	public ExtensionStateScopeId(string kind, string value)
	{
		ArgumentException.ThrowIfNullOrWhiteSpace(kind);
		ArgumentException.ThrowIfNullOrWhiteSpace(value);
		Kind = kind;
		Value = value;
	}

	public string Kind { get; }

	public string Value { get; }

	public override string ToString() => $"{Kind}:{Value}";
}

public readonly record struct ExtensionStateKey<T>
{
    public ExtensionStateKey(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public string Name { get; }
}

public interface IExtensionStateScope : IDisposable
{
    ExtensionStateScopeId Id { get; }

    T GetOrCreate<T>(ExtensionStateKey<T> key, Func<T> factory);

    bool TryGet<T>(ExtensionStateKey<T> key, out T? value);

    void Set<T>(ExtensionStateKey<T> key, T value);

    bool Remove<T>(ExtensionStateKey<T> key);
}

public interface IExtensionStateStore : IDisposable
{
    IExtensionStateScope CreateScope(ExtensionStateScopeId id);

    bool TryGetScope(ExtensionStateScopeId id, out IExtensionStateScope? scope);

    bool RemoveScope(ExtensionStateScopeId id);
}
