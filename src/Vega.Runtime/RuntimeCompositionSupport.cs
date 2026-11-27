using System.Reflection;
using Vega.Backups;
using Vega.Cluster.Transport;
using Vega.Contracts.Localization;
using Vega.Contracts.Modules;
using Vega.Contracts.Operations;
using Vega.Core.Commands;
using Vega.Modules.Administration;
using Vega.Modules.Navigation;
using Vega.Modules.Restrictions;
using Vega.Operations.Logging;
using Vega.Operations.ReadModels;
using Vega.Persistence;
using Vega.Regions;
using Vega.Sandboxes;
using Vega.TerminalUI;
using Vega.Terraria;

namespace Vega.Runtime;

internal static class RuntimeCompositionSupport
{
    internal static IDisposable RegisterCoreRussianCatalog(ILocalizationService localization) =>
        localization.RegisterCatalog(
            "vega",
            "ru-RU",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["command.lang.current"] = "Текущий язык: {0}. Используйте /lang list, /lang <культура> или /lang reset.",
                ["command.lang.list"] = "Доступные языки: {0}.",
                ["command.lang.reset"] = "Язык сброшен на серверный по умолчанию: {0}.",
                ["command.lang.changed"] = "Язык изменён на {0}.",
                ["command.lang.unavailable"] = "Язык '{0}' недоступен. Доступные языки: {1}.",
                ["command.lang.usage"] = "Использование: lang | lang list | lang <культура> | lang reset.",
                ["command.time.current"] = "Время в мире: {0}.",
                ["command.time.applied"] = "Время установлено на {0}.",
                ["command.time.usage"] = "Использование: time [day|noon|night|midnight|HH:mm].",
                ["command.time.unavailable"] = "Не удалось получить или изменить время мира.",
                ["command.wind.applied"] = "Скорость ветра установлена на {0} миль/ч.",
                ["command.wind.usage"] = "Использование: wind <скорость от -40 до 40 миль/ч>.",
                ["command.wind.unavailable"] = "Не удалось изменить ветер в мире.",
                ["navigation.spawn"] = "Вы телепортированы на спавн мира.",
                ["navigation.teleported"] = "Игрок {0} телепортирован к {1}.",
                ["navigation.teleported_position"] = "Телепортация к тайлу ({0}, {1}) выполнена.",
                ["navigation.position"] = "Игрок {0} находится в тайле ({1}, {2}).",
                ["navigation.teleports_allowed"] = "Входящие телепортации разрешены.",
                ["navigation.teleports_blocked"] = "Входящие телепортации запрещены.",
                ["navigation.player_not_found"] = "Игрок не найден в этом мире.",
                ["navigation.teleport_disabled"] = "Игрок запретил входящие телепортации.",
                ["navigation.same_player"] = "Исходный и целевой игроки должны различаться.",
                ["navigation.ambiguous"] = "Найдено несколько игроков: {0}.",
                ["navigation.usage"] = "Использование: {0}.",
                ["navigation.failed"] = "Телепортация не выполнена.",
                ["command.permission_denied"] = "Недостаточно прав для этой команды.",
                ["command.not_found"] = "Неизвестная команда."
            });

    internal static JsonFileOperationsLogSink? TryCreateFileSink(SandboxBackendLaunchOptions backendLaunch)
    {
        try
        {
            string path = backendLaunch.IsBackend
                ? Path.Combine(AppContext.BaseDirectory, "logs", "sandboxes", backendLaunch.WorldId.Value, "vega-operations.jsonl")
                : Path.Combine(AppContext.BaseDirectory, "logs", "vega-operations.jsonl");
            return new JsonFileOperationsLogSink(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Vega file logging is unavailable; startup continues: {ex.Message}");
            return null;
        }
    }

    internal static IReadOnlyList<ModuleOperationsSnapshot> CreateModuleSnapshots(IModuleOperationsCatalog moduleHost) =>
    [
        Module(
            "Vega.Runtime",
            Assembly.GetExecutingAssembly(),
            ["Vega.Backups", "Vega.Cluster", "Vega.Core", "Vega.Modules.Administration", "Vega.Modules.AutoWorldCleaner", "Vega.Modules.Navigation", "Vega.Modules.Pvp", "Vega.Modules.Restrictions", "Vega.Operations", "Vega.Persistence", "Vega.Regions", "Vega.Sandboxes", "Vega.Terraria", "Vega.TerminalUI"]),
        Module("Vega.Backups", typeof(DeploymentBackupService).Assembly, ["Vega.Contracts"]),
        Module("Vega.Core", typeof(CommandDispatcher).Assembly, ["Vega.Contracts"]),
        Module("Vega.Modules.Administration", typeof(AccountCommand).Assembly, ["Vega.Contracts"]),
        Module("Vega.Modules.Navigation", typeof(NavigationModule).Assembly, ["Vega.Contracts"]),
        Module("Vega.Modules.Restrictions", typeof(RestrictionStore).Assembly, ["Vega.Contracts", "Multiplicity"]),
        Module("Vega.Cluster", typeof(UnavailableClusterClient).Assembly, ["Vega.Contracts"]),
        Module("Vega.Operations", typeof(OperationsSnapshotProvider).Assembly, ["Vega.Contracts"]),
        Module("Vega.Persistence", typeof(IVegaDatabase).Assembly, ["Vega.Contracts", "System.Data.Common"]),
        Module("Vega.Regions", typeof(RegionCommand).Assembly, ["Vega.Contracts", "Multiplicity"]),
        Module("Vega.Sandboxes", typeof(SandboxControlService).Assembly, ["Vega.Contracts"]),
        Module("Vega.Terraria", typeof(TerrariaServerHost).Assembly, ["Vega.Contracts", "TerrariaRuntime", "Multiplicity"]),
        Module("Vega.TerminalUI", typeof(TerminalUiHost).Assembly, ["Vega.Contracts", "Terminal.Gui"]),
        .. moduleHost.SnapshotModules()
    ];

    private static ModuleOperationsSnapshot Module(string name, Assembly assembly, IReadOnlyList<string> dependencies) =>
        new(
            name,
            assembly.GetName().Version?.ToString() ?? "unknown",
            ModuleLoadState.Loaded,
            dependencies,
            TimeSpan.Zero);
}

internal sealed class HotReloadableModuleOperationsCatalog(IModuleOperationsCatalog inner) : IModuleOperationsCatalog
{
    public IReadOnlyList<ModuleOperationsSnapshot> SnapshotModules() => Array.AsReadOnly(inner
        .SnapshotModules()
        .Select(static module => module with { IsHotReloadable = true })
        .ToArray());
}
