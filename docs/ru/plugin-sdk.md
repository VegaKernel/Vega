# Первый плагин Vega

Обычному плагину нужны один NuGet-пакет, один базовый класс и никакие знания о Terraria, TerrariaRuntime, packet buffers, worker lanes или collectible load contexts.

## Создание и сборка

Из checkout Vega установите поддерживаемый шаблон и создайте проект:

```powershell
dotnet new install ./templates/vega-plugin
dotnet new vegaplugin --name MyPlugin
dotnet restore MyPlugin/MyPlugin.csproj
dotnet build MyPlugin/MyPlugin.csproj --configuration Release
```

Созданный проект ссылается на единственный пакет `Vega`. Скопируйте только `MyPlugin.dll` в верхнеуровневый каталог сервера `ServerPlugins/`. Включите административный hot reload в `config/modules.json` либо перезапустите сервер после развёртывания.

## Основной путь

Унаследуйте один публичный тип от `VegaPlugin`, объявите descriptor и переопределите `Configure`:

```csharp
public sealed class HelloPlugin : VegaPlugin
{
    public override VegaModuleDescriptor Descriptor { get; } =
        new("MyPlugin", "1.0.0", ["Vega.PluginSdk"]);

    protected override void Configure(IVegaPluginContext plugin)
    {
        plugin.Commands.Register(new HelloCommand());
        plugin.Players.OnJoined(player =>
            plugin.Players.Message(player.PlayerId, "Привет от MyPlugin!"));
    }
}
```

Фиксированный context предоставляет понятные фасады:

- `Commands` регистрирует типизированные command handlers;
- `Players` даёт immutable snapshots, поиск, сообщения, kick/teleport, join/leave hooks и оптимистичный доступ к инвентарю;
- `World` даёт безопасный доступ к текущему миру: environment, tiles, chests, NPC и plugin-owned scenes;
- `Tasks` возвращает работу на game thread, планирует повторяющиеся действия и связывает async I/O с game-thread apply;
- `Configuration` создаёт типизированные, валидируемые и перезагружаемые настройки под `config/plugins/<plugin-name>/`;
- `Storage` хранит произвольные данные плагина под `data/plugins/<plugin-name>/`;
- `Log` пишет структурные записи с собственным source плагина;
- `Sandboxes` показывает управляемые миры и на control-plane процессе даёт lifecycle-операции;
- `Debug` является отдельным привилегированным диагностическим API для явно доверенных tooling-плагинов.

Host владеет командами, подписками, repeating tasks, scene leases и async-операциями, созданными через эти фасады. При unload он отменяет и дожидается background work, снимает регистрации и освобождает collectible context.

## Мир, NPC, сундуки и инвентарь

`plugin.World` является обычной игровой границей. Он никогда не выдаёт `Terraria.Main`, `Terraria.NPC`, `Terraria.Chest` или живые mutable tiles. Чтение возвращает immutable snapshots, а изменения выполняются host'ом на авторитетном game thread.

```csharp
WorldEnvironmentSnapshot? state = await plugin.World.Environment.CaptureAsync();
IReadOnlyList<NpcSnapshot> npcs = await plugin.World.Npcs.SnapshotAsync();
WorldTileAreaSnapshot? tiles = await plugin.World.Tiles.CaptureAsync(
    new SceneBounds(100, 200, 20, 10));
ChestSnapshot? chest = await plugin.World.Chests.CaptureAsync(120, 220);
```

Изменяемые ресурсы используют optimistic revision. Плагин меняет ровно тот snapshot, который видел. Если Terraria или другой плагин успел изменить объект раньше, Vega возвращает revision conflict вместо применения устаревшего состояния.

```csharp
if (chest is not null)
{
    ChestMutationResult result = await plugin.World.Chests.ReplaceAsync(
        chest,
        chest.Items,
        "Event rewards");
}

PlayerInventorySnapshot? inventory = await plugin.Players.Inventory.CaptureAsync(playerId);
if (inventory is not null)
{
    InventoryMutationResult result = await plugin.Players.Inventory.ReplaceSlotAsync(
        inventory,
        new InventorySlotReplacement(0, itemType: 1, stack: 100));
}
```

`World.Environment` показывает размеры мира, spawn, день/ночь, время, фазу луны, blood moon, eclipse, дождь, ветер и hardmode. Валидируемый patch API меняет время, погоду и event flags. Hardmode намеренно не сделан обычным boolean setter.

`World.Npcs` поддерживает snapshot, guarded capture, spawn, teleport и despawn. `NpcHandle` содержит world, slot, type и revision, поэтому устаревший slot не сможет внезапно изменить другого NPC после переиспользования Terraria.

`World.Tiles` читает ограниченную `SceneTileArea` и атомарно заменяет ранее снятую область. `World.Chests` использует ту же модель snapshot + revision. `World.Scenes.Create` создаёт `ISceneLease`, которым владеет lifetime плагина, поэтому host автоматически снимает сцену при unload.

`World.Current` соответствует процессу: primary server получает `WorldId.Primary`, sandbox backend получает собственный `WorldId`. Управление жизненным циклом других миров выполняется через `plugin.Sandboxes`, а не через facade живого мира.

## Конфигурация

Любой обычный плагин может создать конфигурацию без DEBUG-прав. Vega использует единый каталог:

```text
config/plugins/<PluginName>/
```

```csharp
public sealed record Settings(
    bool Enabled = true,
    int IntervalSeconds = 30);

private IPluginConfig<Settings>? settings;

protected override void Configure(IVegaPluginContext plugin)
{
    settings = plugin.Configuration.Use(
        "settings.json",
        static () => new Settings(),
        static value => value.IntervalSeconds > 0
            ? null
            : "IntervalSeconds must be positive.");
}
```

Если файла нет, Vega валидирует и записывает default. `Reload()` перечитывает файл, `Save(value)` валидирует и атомарно заменяет его, `Save()` сохраняет текущее значение. Путь не может выйти за каталог конфигурации плагина.

`Storage` предназначен для произвольных данных плагина, а `Configuration` для operator-editable settings с defaults, validation и reload semantics.

Привилегированный DEBUG tooling может перечислять зарегистрированные конфиги, читать, merge-patch, replace и reload их. Запись требует `StateMutation` и всё равно проходит через типизированную десериализацию и валидатор владельца.

## Sandboxes

Каждый плагин получает `Sandboxes`:

```csharp
IReadOnlyList<SandboxStatus> worlds = plugin.Sandboxes.Snapshot();

if (plugin.Sandboxes.CanControl)
{
    await plugin.Sandboxes.RestartAsync(new WorldId("event-world"));
}
```

Доступны `Snapshot`, `CreateAsync`, `StartAsync`, `StopAsync`, `RestartAsync`, `ResetAsync` и `DestroyAsync`. `CanControl` явно показывает, владеет ли текущий процесс sandbox control plane.

## Асинхронный I/O

Используйте `Tasks.RunIo` только для настоящего database/HTTP/file I/O. Операция выполняется без доступа к Terraria, а callback `apply` отправляется на авторитетный game thread. Cancellation, логирование исключений и ожидание при unload выполняет host.

## DEBUG и advanced boundary

`Debug` обычному gameplay-плагину не нужен. Его capabilities равны `None`, пока конкретный плагин явно не разрешён в `config/debug-api.json`. DEBUG предназначен для tooling вроде `VegaToolsMCP`: runtime inspection, packet tracing и отдельно разрешаемые опасные операции описаны в `docs/debug-api.md`.

Низкоуровневые `IPacketProcessor<T>` и packet-security contracts отделены от `IVegaPluginContext`. Они требуют явного понимания pure/concurrent/buffer-lifetime правил и остаются advanced API.

Generic cancellable gameplay hooks не имитируются декоративным API. Они добавляются только там, где Vega реально владеет авторитетным event pipeline, deterministic registration и unload semantics. Join/leave уже соответствуют этому правилу; packet-level обработка остаётся advanced boundary.

Типичные TShock-паттерны переводятся по поведению: `Initialize` → `Configure`, ручные add/remove команд → `Commands.Register`, player hooks → `Players.OnJoined`, `TSPlayer` → immutable snapshot + узкая операция, inventory → `Players.Inventory`, world/NPC/chests → `World`, timer/dispose → `Tasks.Every`, config → `Configuration.Use<T>`, произвольные данные → `Storage`, lifecycle управляемых миров → `Sandboxes`.

Исходная и бинарная совместимость с TShock не обещается.

## FakeProvider для плагинов

Обычный плагин получает инструмент напрямую через `context.World.FakeProvider`, не через команды, reflection или ссылку на `Vega.Modules.Scenes`/TShock. Инструмент работает в текущем мире процесса; имена провайдеров разных плагинов не конфликтуют. Владение определяется lease и выполняется host автоматически на unload/hot reload.

Пример персонального превью копирования региона, без изменения `.wld`:

```csharp
using Vega.Contracts.Scenes;
using Vega.PluginSdk;

// context — IVegaPluginContext, player — актуальный PlayerSnapshot.
var captured = await context.World.Tiles.CaptureAsync(new SceneBounds(100, 200, 16, 16));
if (captured is not null && player.SessionId is not null)
{
    IPluginTileProvider preview = context.World.FakeProvider.CreatePersonal(
        "clipboard", new SceneBounds(150, 200, 16, 16), captured.Tiles,
        [SceneSessionObserver.From(player)], layer: 100);
    preview.Move(5, 0);
    preview.SetTop(); // среди провайдеров с тем же layer
    // Храните preview в состоянии плагина, вызовите Dispose() когда превью не нужно.
    // Host сам освободит его при выгрузке/перезагрузке плагина.
}
```

Глобальные панели создаются `CreateGlobal(name, bounds, tiles, layer)`. `SceneTileArea.Fill(width,height,tile)` создаёт неизменяемую заливку без лишней копии исходного массива. `Replace(bounds, tiles)` меняет размер/содержимое с сохранением ID; также доступны `SetEnabled`, `SetLayer`, `SetTop`, `SetObservers`, `Draw`, `Dispose`. Для одного и того же имени можно создавать разные независимые leases; храните соответствующий handle. Персональный scope принимает точные `SceneSessionObserver` (`From(PlayerSnapshot)` или `From(PlayerSessionOpened)`), пустой набор никому не виден; предыдущая сессия не восстанавливается при перемещении слоя и повторном использовании слота.

`CreateVisibilityLayer(name, observers)` создаёт принадлежащий плагину слой видимости. `AssignPlayer`/`RemovePlayer` требуют точную сессию; `AssignNpc`/`AssignProjectile`, `RemoveNpc`/`RemoveProjectile` работают с нативными слотами — плагин должен снять назначение при despawn, до повторного использования ID. Это фильтрация поддерживаемых пакетов и интеракций, не независимая физика и не автоматический spawn/despawn/resync клиента. `CreatePersonal(..., fillOutsideWithAir: true)` и `SetFillOutsideWithAir(true)` включают персональный воздушный фон: глобальные слои скрываются только для выбранных сессий, загруженные секции перерисовываются постепенно. Это визуальный эффект, не конфиденциальность канонических секций и не независимая физика. Фейковые сундуки и tile entities пока не предоставляются этим SDK. `World.Scenes.Create` остаётся совместимым низкоуровневым API; для новых персональных провайдеров предпочтительна сессионная привязка FakeProvider.
Рабочий пример команды `samplepreview` находится в `samples/Vega.PluginSamples/FakePreviewCommand.cs`: снимок ванильной области, персональное превью, перемещение и очистка.
