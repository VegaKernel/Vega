using System.Collections.ObjectModel;
using Terminal.Gui.Editor;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;
using Vega.Admin.Client;
using Vega.Admin.Contracts;

namespace Vega.TerminalUI;

/// <summary>
/// Terminal.Gui v2 editor for explicitly exposed managed plugin configurations. The workspace
/// never deals in filesystem paths: every read/write goes through IVegaAdminClient and uses the
/// server-owned revision, redaction and typed-validation rules.
/// </summary>
internal sealed class AdminConfigurationWorkspace
{
    private const string SchemeName = "VegaDark";
    private readonly IVegaAdminClient adminClient;
    private readonly View root = new() { SchemeName = SchemeName };
    private readonly ListView documents = CreateList();
    private readonly FrameView editorFrame = new()
    {
        Title = "Configuration",
        SchemeName = SchemeName
    };
    private readonly Editor editor = new()
    {
        SchemeName = SchemeName,
        ReadOnly = true,
        ConvertTabsToSpaces = true,
        IndentationSize = 2,
        WordWrap = false
    };
    private readonly Button refreshButton = new() { Text = "_Refresh" };
    private readonly Button reloadButton = new() { Text = "R_eload" };
    private readonly Button saveButton = new() { Text = "_Save" };
    private readonly Label status = new()
    {
        SchemeName = SchemeName,
        Text = "Managed configurations are hidden unless explicitly exposed by server policy."
    };
    private IReadOnlyList<AdminConfigurationDescriptor> descriptors = Array.Empty<AdminConfigurationDescriptor>();
    private AdminConfigurationDocument? currentDocument;
    private Task<IReadOnlyList<AdminConfigurationDescriptor>>? catalogCapture;
    private Task<ConfigurationReadCompletion>? documentCapture;
    private Task<ConfigurationMutationCompletion>? mutationCapture;
    private DateTimeOffset nextCatalogRefreshAt = DateTimeOffset.MinValue;
    private AdminConfigurationKey? requestedDocument;
    private bool forceDocumentRefresh;
    private bool suppressSelectionChanged;

    public AdminConfigurationWorkspace(IVegaAdminClient adminClient)
    {
        this.adminClient = adminClient ?? throw new ArgumentNullException(nameof(adminClient));

        var listFrame = new FrameView
        {
            Title = "Exposed configurations",
            X = 0,
            Y = 0,
            Width = Dim.Percent(34),
            Height = Dim.Fill(3),
            SchemeName = SchemeName
        };
        documents.X = 0;
        documents.Y = 0;
        documents.Width = Dim.Fill();
        documents.Height = Dim.Fill();
        listFrame.Add(documents);

        editorFrame.X = Pos.Right(listFrame);
        editorFrame.Y = 0;
        editorFrame.Width = Dim.Fill();
        editorFrame.Height = Dim.Fill(3);
        editor.X = 0;
        editor.Y = 0;
        editor.Width = Dim.Fill();
        editor.Height = Dim.Fill();
        editorFrame.Add(editor);

        var actionBar = new View
        {
            X = 0,
            Y = Pos.AnchorEnd(3),
            Width = Dim.Fill(),
            Height = 3,
            SchemeName = SchemeName
        };
        refreshButton.X = 0;
        refreshButton.Y = 0;
        reloadButton.X = Pos.Right(refreshButton) + 1;
        reloadButton.Y = 0;
        saveButton.X = Pos.Right(reloadButton) + 1;
        saveButton.Y = 0;
        status.X = 0;
        status.Y = 1;
        status.Width = Dim.Fill();
        actionBar.Add(refreshButton, reloadButton, saveButton, status);

        root.Add(listFrame, editorFrame, actionBar);

        documents.ValueChanged += (_, _) =>
        {
            if (!suppressSelectionChanged)
            {
                RequestSelectedDocument();
            }
        };
        refreshButton.Accepted += (_, args) =>
        {
            args.Handled = true;
            RequestRefresh();
        };
        reloadButton.Accepted += (_, args) =>
        {
            args.Handled = true;
            RequestReload();
        };
        saveButton.Accepted += (_, args) =>
        {
            args.Handled = true;
            RequestSave();
        };

        RenderNoDocument(
            "No managed plugin configuration is currently exposed. Add explicit entries to config/admin-configuration.json to make selected plugin-owned documents administrable.");
    }

    public View Root => root;

    public void Pulse()
    {
        PumpCatalog();
        PumpDocument();
        PumpMutation();
        UpdateActionState();
    }

    public void RequestRefresh()
    {
        if (mutationCapture is not null)
        {
            SetStatus("A configuration mutation is still in progress.");
            return;
        }

        if (IsDirty)
        {
            SetStatus("Unsaved edits are present. Save them or select another document before refreshing it.");
            nextCatalogRefreshAt = DateTimeOffset.MinValue;
            return;
        }

        nextCatalogRefreshAt = DateTimeOffset.MinValue;
        forceDocumentRefresh = currentDocument is not null;
        SetStatus("Refreshing managed configuration metadata...");
    }

    private bool IsDirty => currentDocument is not null &&
        !string.Equals(editor.Text, currentDocument.Json, StringComparison.Ordinal);

    private void PumpCatalog()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (catalogCapture is null && now >= nextCatalogRefreshAt)
        {
            catalogCapture = adminClient.GetConfigurationsAsync().AsTask();
        }

        if (catalogCapture is null || !catalogCapture.IsCompleted)
        {
            return;
        }

        Task<IReadOnlyList<AdminConfigurationDescriptor>> completed = catalogCapture;
        catalogCapture = null;
        nextCatalogRefreshAt = now.AddSeconds(5);
        if (!completed.IsCompletedSuccessfully)
        {
            SetStatus($"Configuration catalog unavailable: {completed.Exception?.GetBaseException().Message ?? "unknown failure"}");
            return;
        }

        ApplyCatalog(completed.GetAwaiter().GetResult());
        if (forceDocumentRefresh && currentDocument is not null && !IsDirty)
        {
            forceDocumentRefresh = false;
            requestedDocument = new AdminConfigurationKey(
                currentDocument.Descriptor.Plugin,
                currentDocument.Descriptor.FileName);
        }
    }

    private void ApplyCatalog(IReadOnlyList<AdminConfigurationDescriptor> value)
    {
        AdminConfigurationKey? previous = SelectedKey();
        descriptors = value
            .OrderBy(static descriptor => descriptor.Plugin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static descriptor => descriptor.FileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        string[] rows = descriptors.Select(static descriptor =>
            $"{(descriptor.CanEdit ? "RW" : "RO")} · {descriptor.Plugin}/{descriptor.FileName}" +
            (descriptor.HasRedactions ? " · redacted" : string.Empty)).ToArray();

        suppressSelectionChanged = true;
        try
        {
            documents.SetSource(new ObservableCollection<string>(rows.Length == 0 ? ["(none exposed)"] : rows));
            int selected = previous is null
                ? (descriptors.Count > 0 ? 0 : -1)
                : FindDescriptor(previous);
            if (selected < 0 && descriptors.Count > 0)
            {
                selected = 0;
            }

            if (selected >= 0)
            {
                documents.SelectedItem = selected;
            }
        }
        finally
        {
            suppressSelectionChanged = false;
        }

        if (descriptors.Count == 0)
        {
            currentDocument = null;
            requestedDocument = null;
            RenderNoDocument(
                "No managed plugin configuration is exposed by config/admin-configuration.json. This is the fail-closed default.");
            return;
        }

        AdminConfigurationKey? selectedKey = SelectedKey();
        if (selectedKey is not null &&
            (currentDocument is null || !SameKey(currentDocument.Descriptor, selectedKey)))
        {
            requestedDocument = selectedKey;
        }
    }

    private void PumpDocument()
    {
        if (mutationCapture is not null)
        {
            return;
        }

        if (documentCapture is null && requestedDocument is not null)
        {
            AdminConfigurationKey key = requestedDocument;
            requestedDocument = null;
            documentCapture = ReadDocumentAsync(key);
            editor.ReadOnly = true;
            SetStatus($"Loading {key.Plugin}/{key.FileName}...");
        }

        if (documentCapture is null || !documentCapture.IsCompleted)
        {
            return;
        }

        Task<ConfigurationReadCompletion> completed = documentCapture;
        documentCapture = null;
        if (!completed.IsCompletedSuccessfully)
        {
            SetStatus($"Configuration read failed: {completed.Exception?.GetBaseException().Message ?? "unknown failure"}");
            return;
        }

        ConfigurationReadCompletion result = completed.GetAwaiter().GetResult();
        AdminConfigurationKey? selected = SelectedKey();
        if (selected is null || !SameKey(result.Key, selected))
        {
            return;
        }

        if (!result.Result.Succeeded || result.Result.Document is null)
        {
            currentDocument = null;
            editor.ReadOnly = true;
            editor.Text = result.Result.Error ?? "Configuration read failed.";
            SetStatus($"{result.Result.ErrorCode ?? "configuration.read_failed"}: {result.Result.Error ?? "read failed"}");
            return;
        }

        ApplyDocument(result.Result.Document, "Loaded");
    }

    private void RequestSelectedDocument()
    {
        if (mutationCapture is not null)
        {
            return;
        }

        AdminConfigurationKey? key = SelectedKey();
        if (key is null)
        {
            return;
        }

        currentDocument = null;
        requestedDocument = key;
        editor.ReadOnly = true;
        editorFrame.Title = $"{key.Plugin}/{key.FileName}";
        editor.Text = "Loading managed configuration...";
    }

    private void RequestSave()
    {
        if (mutationCapture is not null || documentCapture is not null || currentDocument is null)
        {
            return;
        }

        AdminConfigurationDescriptor descriptor = currentDocument.Descriptor;
        if (!descriptor.CanEdit)
        {
            SetStatus("This managed configuration is read-only for administration.");
            return;
        }

        if (!IsDirty)
        {
            SetStatus("No configuration changes to save.");
            return;
        }

        string json = editor.Text;
        mutationCapture = ReplaceAsync(new AdminConfigurationReplaceRequest(
            descriptor.Plugin,
            descriptor.FileName,
            descriptor.Revision,
            json));
        SetMutationState(true, "Validating and saving configuration...");
    }

    private void RequestReload()
    {
        if (mutationCapture is not null || documentCapture is not null || currentDocument is null)
        {
            return;
        }

        AdminConfigurationDescriptor descriptor = currentDocument.Descriptor;
        if (!descriptor.CanEdit)
        {
            SetStatus("This managed configuration is read-only for administration.");
            return;
        }

        if (IsDirty)
        {
            SetStatus("Unsaved edits are present. Reload is blocked so they are not silently discarded.");
            return;
        }

        mutationCapture = ReloadAsync(new AdminConfigurationMutationRequest(
            descriptor.Plugin,
            descriptor.FileName,
            descriptor.Revision));
        SetMutationState(true, "Reloading configuration through the owning plugin...");
    }

    private void PumpMutation()
    {
        if (mutationCapture is null || !mutationCapture.IsCompleted)
        {
            return;
        }

        Task<ConfigurationMutationCompletion> completed = mutationCapture;
        mutationCapture = null;
        documents.Enabled = true;
        if (!completed.IsCompletedSuccessfully)
        {
            SetStatus($"Configuration mutation failed: {completed.Exception?.GetBaseException().Message ?? "unknown failure"}");
            UpdateActionState();
            return;
        }

        ConfigurationMutationCompletion result = completed.GetAwaiter().GetResult();
        AdminConfigurationKey? selected = SelectedKey();
        if (selected is null || !SameKey(result.Key, selected))
        {
            nextCatalogRefreshAt = DateTimeOffset.MinValue;
            return;
        }

        if (!result.Result.Succeeded || result.Result.Document is null)
        {
            SetStatus($"{result.Result.ErrorCode ?? "configuration.mutation_failed"}: {result.Result.Error ?? "mutation failed"}");
            nextCatalogRefreshAt = DateTimeOffset.MinValue;
            UpdateActionState();
            return;
        }

        ApplyDocument(result.Result.Document, result.Kind == MutationKind.Replace ? "Saved" : "Reloaded");
        nextCatalogRefreshAt = DateTimeOffset.MinValue;
    }

    private void ApplyDocument(AdminConfigurationDocument document, string action)
    {
        currentDocument = document;
        editorFrame.Title = document.Descriptor.DisplayName ??
            $"{document.Descriptor.Plugin}/{document.Descriptor.FileName}";
        editor.Text = document.Json;
        editor.ReadOnly = !document.Descriptor.CanEdit;
        string redaction = document.Descriptor.HasRedactions
            ? " Redacted values remain server-owned and are preserved on save."
            : string.Empty;
        string description = string.IsNullOrWhiteSpace(document.Descriptor.Description)
            ? string.Empty
            : $" {document.Descriptor.Description}";
        SetStatus(
            $"{action} · revision {ShortRevision(document.Descriptor.Revision)} · {(document.Descriptor.CanEdit ? "editable" : "read-only")}.{redaction}{description}");
        UpdateActionState();
    }

    private void RenderNoDocument(string message)
    {
        editorFrame.Title = "Configuration";
        editor.ReadOnly = true;
        editor.Text = message;
        SetStatus(message);
        UpdateActionState();
    }

    private void UpdateActionState()
    {
        bool idle = mutationCapture is null && documentCapture is null;
        bool editable = idle && currentDocument?.Descriptor.CanEdit == true;
        refreshButton.Enabled = mutationCapture is null;
        reloadButton.Enabled = editable && !IsDirty;
        saveButton.Enabled = editable && IsDirty;
        documents.Enabled = mutationCapture is null;
    }

    private void SetMutationState(bool active, string message)
    {
        documents.Enabled = !active;
        refreshButton.Enabled = !active;
        reloadButton.Enabled = false;
        saveButton.Enabled = false;
        editor.ReadOnly = active || currentDocument?.Descriptor.CanEdit != true;
        SetStatus(message);
    }

    private void SetStatus(string message) => status.Text = message;

    private AdminConfigurationKey? SelectedKey()
    {
        int index = documents.SelectedItem ?? -1;
        if ((uint)index >= (uint)descriptors.Count)
        {
            return null;
        }

        AdminConfigurationDescriptor descriptor = descriptors[index];
        return new AdminConfigurationKey(descriptor.Plugin, descriptor.FileName);
    }

    private int FindDescriptor(AdminConfigurationKey key)
    {
        for (var index = 0; index < descriptors.Count; index++)
        {
            if (SameKey(descriptors[index], key))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool SameKey(AdminConfigurationDescriptor descriptor, AdminConfigurationKey key) =>
        string.Equals(descriptor.Plugin, key.Plugin, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(descriptor.FileName, key.FileName, StringComparison.OrdinalIgnoreCase);

    private static bool SameKey(AdminConfigurationKey first, AdminConfigurationKey second) =>
        string.Equals(first.Plugin, second.Plugin, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(first.FileName, second.FileName, StringComparison.OrdinalIgnoreCase);

    private async Task<ConfigurationReadCompletion> ReadDocumentAsync(AdminConfigurationKey key) =>
        new(key, await adminClient.ReadConfigurationAsync(key).ConfigureAwait(false));

    private async Task<ConfigurationMutationCompletion> ReplaceAsync(AdminConfigurationReplaceRequest request) =>
        new(
            new AdminConfigurationKey(request.Plugin, request.FileName),
            MutationKind.Replace,
            await adminClient.ReplaceConfigurationAsync(request).ConfigureAwait(false));

    private async Task<ConfigurationMutationCompletion> ReloadAsync(AdminConfigurationMutationRequest request) =>
        new(
            new AdminConfigurationKey(request.Plugin, request.FileName),
            MutationKind.Reload,
            await adminClient.ReloadConfigurationAsync(request).ConfigureAwait(false));

    private static string ShortRevision(string revision) =>
        revision.Length <= 12 ? revision : revision[..12];

    private static ListView CreateList() => new()
    {
        SchemeName = SchemeName,
        ViewportSettings = ViewportSettingsFlags.HasVerticalScrollBar | ViewportSettingsFlags.HasHorizontalScrollBar
    };

    private sealed record ConfigurationReadCompletion(
        AdminConfigurationKey Key,
        AdminConfigurationMutationResult Result);

    private sealed record ConfigurationMutationCompletion(
        AdminConfigurationKey Key,
        MutationKind Kind,
        AdminConfigurationMutationResult Result);

    private enum MutationKind
    {
        Replace,
        Reload
    }
}
