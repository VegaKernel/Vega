using System.Reflection;
using Vega.Contracts.Players;
using Vega.Contracts.Items;
using Vega.Contracts.Modules;

namespace Vega.Modules.CustomItems;

public sealed class CustomItemsModule(ICustomItemDelivery delivery) : IVegaModule
{
	private readonly Dictionary<string, CustomItemDefinition> items =
		new(StringComparer.OrdinalIgnoreCase);
	private bool started;

	public VegaModuleDescriptor Descriptor { get; } = new(
		"Vega.Modules.CustomItems",
		typeof(CustomItemsModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
		["Vega.Contracts"]);

	public void Start() => started = true;

	public void Register(CustomItemDefinition item)
	{
		ArgumentNullException.ThrowIfNull(item);
		Validate(item);
		items[item.Name] = item;
	}

	public ValueTask<PlayerOperationResult> DeliverAsync(
		PlayerId playerId,
		string name,
		CancellationToken cancellationToken = default)
	{
		if (!started)
			throw new InvalidOperationException("The custom-items module has not been started.");

		return items.TryGetValue(name, out CustomItemDefinition? item)
			? delivery.DeliverAsync(playerId, item, cancellationToken)
			: ValueTask.FromResult(PlayerOperationResult.Failed("custom_item.not_found"));
	}

	public void Dispose()
	{
		started = false;
		items.Clear();
	}

	private static void Validate(CustomItemDefinition item)
	{
		if (string.IsNullOrWhiteSpace(item.Name))
			throw new ArgumentException("A custom item name is required.", nameof(item));
		if (item.ItemType <= 0)
			throw new ArgumentOutOfRangeException(nameof(item), "Item type must be positive.");
		if (item.Override.Stack is <= 0)
			throw new ArgumentOutOfRangeException(nameof(item), "Stack must be positive when supplied.");
	}
}
