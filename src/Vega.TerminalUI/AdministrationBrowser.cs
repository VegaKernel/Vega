using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Contracts.Operations;

namespace Vega.TerminalUI;

internal static class AdministrationBrowser
{
    private const string SchemeName = "VegaDark";

    public static void Show(IApplication app, AdministrationOperationsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(snapshot);

        using Dialog dialog = new()
        {
            Title = $"Administration · cached {snapshot.SampledAt:HH:mm:ss}",
            Width = Dim.Percent(92),
            Height = Dim.Percent(85),
            SchemeName = SchemeName
        };
        var searchLabel = new Label
        {
            Text = "Search",
            X = 1,
            Y = 1
        };
        var search = new TextField
        {
            X = 9,
            Y = 1,
            Width = Dim.Fill(2),
            Text = string.Empty,
            SchemeName = SchemeName
        };
        var status = new Label
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(1),
            SchemeName = SchemeName
        };
        var rows = new ListView
        {
            X = 1,
            Y = 4,
            Width = Dim.Fill(1),
            Height = Dim.Fill(1),
            SchemeName = SchemeName,
            ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar
        };
        string[] allRows = AdministrationBrowserRows.Build(snapshot);

        void ApplyFilter()
        {
            string[] filtered = AdministrationBrowserRows.Filter(allRows, search.Text);
            rows.SetSource(new ObservableCollection<string>(filtered));
            status.Text = $"{filtered.Length}/{allRows.Length} rows · {snapshot.Groups.Count} groups · {snapshot.Accounts.Count} accounts · type to filter";
        }

        search.TextChanged += (_, _) => ApplyFilter();
        var close = new Button { Text = "_Close" };
        close.Accepted += (_, _) => app.RequestStop(dialog);
        dialog.Buttons = [close];
        dialog.Add(searchLabel, search, status, rows);
        ApplyFilter();
        search.SetFocus();
        app.Run(dialog);
    }
}

internal static class AdministrationBrowserRows
{
    public static string[] Build(AdministrationOperationsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rows = new List<string>
        {
            "[GROUPS]"
        };

        foreach (PermissionGroupOperationsSnapshot group in snapshot.Groups)
        {
            rows.Add($"GROUP   {group.Name} <- {group.ParentName ?? "<root>"} · accounts {group.AccountCount} · r{group.Revision}");
            rows.AddRange(group.GrantedPermissions.Select(permission => $"  GRANT {group.Name} · {permission}"));
            rows.AddRange(group.DeniedPermissions.Select(permission => $"  DENY  {group.Name} · {permission}"));
        }

        rows.Add("[ACCOUNTS]");
        foreach (AccountOperationsSnapshot account in snapshot.Accounts)
        {
            rows.Add(
                $"ACCOUNT {account.Name} · id {account.Id.Value} · group {account.GroupName} · " +
                $"seen {account.LastSeenUtc?.ToString("u") ?? "never"} · culture {account.Culture ?? "default"}");
            foreach (PossibleAccountOperationsSnapshot link in account.PossibleAccounts)
            {
                string evidence = link.SharedClientUuid && link.SharedIpAddress
                    ? "UUID+IP"
                    : link.SharedClientUuid
                        ? "UUID"
                        : link.SharedIpAddress
                            ? "IP"
                            : "signal";
                rows.Add(
                    $"  LINK  {account.Name} ~ {link.AccountName} · {evidence} · " +
                    $"signals {link.SharedSignals} · matched {link.LastMatchedAt:u}");
            }
        }

        return rows.ToArray();
    }

    public static string[] Filter(IReadOnlyList<string> rows, string? query)
    {
        ArgumentNullException.ThrowIfNull(rows);
        string[] terms = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return rows.ToArray();
        }

        return rows
            .Where(row => terms.All(term => row.Contains(term, StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }
}
