using TShockNG.Contracts.Accounts;
using TShockNG.Contracts.Authorization;
using TShockNG.Contracts.Bans;
using TShockNG.Contracts.Commands;
using TShockNG.Contracts.Localization;
using TShockNG.Contracts.Networking;
using TShockNG.Contracts.Players;
using TShockNG.Contracts.Regions;
using TShockNG.Contracts.Updates;
using TShockNG.Contracts.Worlds;

namespace TShockNG.Contracts.Runtime;

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
