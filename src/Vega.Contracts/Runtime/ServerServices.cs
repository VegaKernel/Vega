using Vega.Contracts.Accounts;
using Vega.Contracts.Authorization;
using Vega.Contracts.Bans;
using Vega.Contracts.Commands;
using Vega.Contracts.Localization;
using Vega.Contracts.Networking;
using Vega.Contracts.Players;
using Vega.Contracts.Regions;
using Vega.Contracts.Updates;
using Vega.Contracts.Worlds;

namespace Vega.Contracts.Runtime;

/// <summary>
/// Explicit composition bundle passed only across the launcher/integration boundary.
/// Domain code should depend on the individual interfaces rather than this bundle.
/// </summary>
public sealed record ServerServices(
    IAccountService Accounts,
    IAuthorizationService Authorization,
    IBanService Bans,
    ICommandDispatcher Commands,
    IIpMetadataProviderFactory IpMetadata,
    IPlayerCultureService Cultures,
    IPlayerDirectory Players,
    IPlayerOperations PlayerOperations,
    IPlayerMessenger PlayerMessenger,
    IRegionService Regions,
    IUpdateChecker Updates,
    IWorldService World);
