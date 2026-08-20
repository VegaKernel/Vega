using System.Reflection;
using Multiplicity.Packets.Views;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Extensions;
using Vega.Contracts.Modules;

namespace Vega.Modules.Pvp;

public sealed class PvpModule(
	IExtensionStateStore extensionState,
	IPlayerSessionLifecycle lifecycle,
	IPlayerDirectory players,
	IPlayerCombatDirectory combat) : IVegaModule
{
	private PvpSessionStateService? sessions;

	public VegaModuleDescriptor Descriptor { get; } = new(
		"Vega.Modules.Pvp",
		typeof(PvpModule).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown",
		["Vega.Contracts", "TZ.Multiplicity"]);

	public IPacketProcessor<PacketView> PacketProcessor { get; private set; } =
		new InactivePacketProcessor();

	public void Start()
	{
		if (sessions is not null)
		{
			return;
		}

		sessions = new PvpSessionStateService(extensionState, lifecycle);
		PacketProcessor = new PvpPacketPolicy(players, combat, sessions);
	}

	public void Dispose()
	{
		sessions?.Dispose();
		sessions = null;
		PacketProcessor = new InactivePacketProcessor();
	}

	private sealed class InactivePacketProcessor : IPacketProcessor<PacketView>
	{
		public PacketProcessingDecision Process(scoped in PacketProcessingContext<PacketView> context) =>
			throw new InvalidOperationException("The PvP module has not been started.");
	}
}
