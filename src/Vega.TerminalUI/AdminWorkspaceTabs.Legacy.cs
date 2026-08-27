using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

/// <summary>
/// Temporary embedded-dashboard bridge. The snapshot is intentionally ignored: dedicated
/// Phase 27 workspaces now query their own projections through IVegaAdminClient. Remove this
/// extension when workspace 1 is migrated away from the legacy Operations dashboard.
/// </summary>
internal static class AdminWorkspaceTabsLegacyExtensions
{
    public static void Update(this AdminWorkspaceTabs workspaces, OperationsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(workspaces);
        ArgumentNullException.ThrowIfNull(snapshot);
        workspaces.Pulse();
    }
}
