using Vega.Contracts.Operations;

namespace Vega.Contracts.Modules;

public sealed record VegaModuleDescriptor(
	string Name,
	string Version,
	IReadOnlyList<string> Dependencies);

public interface IVegaModule : IDisposable
{
	VegaModuleDescriptor Descriptor { get; }

	void Start();
}

public interface IVegaModuleHost : IModuleOperationsCatalog, IDisposable
{
	void Start();
}
