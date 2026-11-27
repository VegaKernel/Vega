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

### Совместимость SDK

Шаблон и пример плагина объявляют поколение API на уровне сборки:

```csharp
using Vega.PluginSdk;
[assembly: VegaPluginApiGeneration(VegaPluginApiGenerationAttribute.Current)]
```

Текущее поколение — `1`. Число встраивается при компиляции. Если объявленное поколение не совпадает с сервером, загрузчик отклоняет сборку до создания плагина, сохраняя активную версию при неудачной замене. Старые плагины без атрибута загружаются по прежним правилам. Совпадение поколения **не гарантирует стабильный бинарный ABI**: плагины нужно собирать и проверять на конкретном развёрнутом пакете Vega. Поколение API не заменяет версию плагина, проверку .NET и проверки доступности необязательных возможностей SDK.

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

`Players.OnEnteredWorld` сообщает о нативном входе в мир на игровом потоке, с точной сессией.
`Players.OnClosing` вызывается до сброса персонажа, а `OnLeft` — при последующем закрытии сессии.
Для ресурсов плагина доступны `Resources.Own(IDisposable)`, `Resources.OwnAsync(IAsyncDisposable)` и
`Resources.OnCleanup(Action)`. Ресурс регистрируется один раз, освобождается в обратном порядке после
остановки callback и фоновой работы. Ошибка освобождения не пропускает остальные ресурсы, но отражается
как неполная выгрузка; асинхронное освобождение входит в таймаут выгрузки.

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

`CreateVisibilityLayer(name, observers)` создаёт принадлежащий плагину слой видимости. `AssignPlayer`/`RemovePlayer` требуют точную сессию; `AssignNpc`/`AssignProjectile`, `RemoveNpc`/`RemoveProjectile` работают с нативными слотами — плагин должен снять назначение при despawn, до повторного использования ID. Это фильтрация поддерживаемых пакетов и интеракций, не независимая физика и не автоматический spawn/despawn/resync клиента. `CreatePersonal(..., fillOutsideWithAir: true)` и `SetFillOutsideWithAir(true)` включают персональный воздушный фон: глобальные слои скрываются только для выбранных сессий, загруженные секции перерисовываются постепенно. Это визуальный эффект, не конфиденциальность канонических секций и не независимая физика. `SupportsVirtualChests` и `IPluginTileProvider.CreateVirtualChest(localX, localY, name, items)` предоставляют логические сундуки, привязанные к слою: неизменяемое содержимое, `TryReplace` с ревизией, `TryTakeOpenRequest` с точной сессией и автоматическое освобождение. Логические таблички используют `SupportsVirtualSigns`, `IPluginTileProvider.CreateVirtualSign(localX, localY, text)`, `TrySetText` с ревизией и `TryTakeReadRequest`. `SupportsVirtualSignEdits` и `CreateVirtualSign(..., allowEdits: true)` включают ограниченный по времени запрос редактирования после чтения той же таблички; плагин явно подтверждает его через `TryTakeEditRequest` / `TryAcceptEdit` с проверкой текущей сессии, видимости, дистанции и ревизии. Сундуки и таблички имеют общую изоляцию слоя, мира и сессии. Для ответа на интеракцию используйте `plugin.Players.Message(playerSnapshot, text)` или `MessageAsync(playerSnapshot, text)`: хост проверяет точную сессию и не подменяет её числовым слотом. `SupportsVirtualChestTransfers` и `Transfer(SceneVirtualChestTransferRequest)` позволяют явно переносить стаки между логическим сундуком и нативным инвентарём на игровом потоке при авторитетном SSC. Перенос проверяет актуальность сессии, видимость, дистанцию, ревизии и допустимые стаки/prefix; `SupportsManagedVirtualChestGui` / `TryPresentManagedGui` открывают нативное окно сундука в явном режиме управляемого сервером инвентаря до переподключения. Сбалансированный перенос между одним слотом сундука и одним слотом инвентаря или курсором оформляется через `TryTakeManagedGuiTransfer` и `AcceptManagedGuiTransfer`; неподдерживаемые действия закрывают окно и восстанавливают состояние. Это режим мини-игр с управляемым инвентарём; свободная совместимость с ванильным инвентарём и интерактивные инвентари tile entities ещё не квалифицированы. `SupportsVirtualSignUi` / `TryPresentRead` отправляют ванильный ответ открытия таблички в точную сессию без регистрации канонической таблички; `TryRefreshEdit` обновляет текст без повторного открытия. `SupportsVirtualDisplays` / `CreateVirtualDisplay` предоставляют read-only стойки оружия, манекены, вешалки и блюда с нативной метаинформацией; `SupportsManagedDisplayInteractions` / `TryArmManagedInteraction` включают размещение в пустой предметной рамке или стойке оружия при авторитетном SSC. `TryTakeManagedTransfer` / `AcceptManagedTransfer` атомарно применяют сбалансированное уменьшение инвентаря и новый revision объекта, отказ или отмена восстанавливают состояние. Управляемый режим длится до переподключения; автоматическое извлечение, интерактивные инвентари манекенов/вешалок и графические клиентские испытания остаются открыты. `SupportsVirtualItemFrames`, `CreateVirtualItemFrame` и `TryReplace` предоставляют read-only виртуальные рамки с нативной метаинформацией для точной сессии по завершении перерисовки; канонические реестры не изменяются. Пример — `samples/Vega.PluginSamples/VirtualChestPreviewCommand.cs`. `World.Scenes.Create` остаётся совместимым низкоуровневым API; для новых персональных провайдеров предпочтительна сессионная привязка FakeProvider.
Рабочий пример команды `samplepreview` находится в `samples/Vega.PluginSamples/FakePreviewCommand.cs`: снимок ванильной области, персональное превью, перемещение и очистка.

## Нативные игровые события и схемы

```csharp
context.WorldEvents.OnNpcKilled(e =>
    context.Log.Write(OperationsLogLevel.Information,
        $"NPC {e.Npc.Type} погиб в мире {e.Npc.WorldId}."));
context.WorldEvents.AllowNpcTransform(e => e.RequestedType != blockedType);
context.WorldEvents.AllowNpcLoot(e => e.Stack <= configuredStackLimit);
```

Доступны уведомления `OnNpcCreating`, `OnNpcKilled`, `OnNpcTransforming`,
`OnNpcLootDropped`, `OnProjectileSynchronized` и `OnProjectileRemoved`.
Они передают отделённые от Terraria данные текущего мира. Уведомление о
создании NPC относится к запросу выделения слота, а события снарядов — к
нативной входящей синхронизации, не ко всему жизненному циклу. Политики
возвращают `false` для отмены операции. Исключения изолируются, подписки
автоматически удаляются при выгрузке плагина. Обработчики выполняются на
игровом потоке; последующие изменения мира лучше ставить через `Tasks.Post`.

`context.World.Schematics.CaptureAsync(bounds, token)` возвращает закодированный
`.trschem` с тайлами и данными объектов. Сериализация вынесена в фоновый поток.
`PlaceAsync(data, x, y, token)` размещает поддерживаемые схемы с тайлами и NPC
в изолированной песочнице без подключённых клиентов. Другие нативные объекты
пока отклоняются, а не теряются при размещении. См. [схемы](../schematics.md).

## Кастомные NPC

`context.World.Npcs.CreateActorAsync(definition, position, token)` создаёт
нативного NPC, принадлежащего плагину. Доступны имя, дружественность,
неуязвимость, управление горизонтальным движением, телепортация, каталог и
точно привязанные к сессии взаимодействия. События `OnNpcActorInteracted` и
`OnNpcActorPurchased` поддерживают обработку торговли. Представление каталога
в ванильном окне (`PresentShopAsync`) и явная серверная транзакция
(`PurchaseAsync`) — разные операции. Подробности и пример:
[кастомные NPC и магазины](../custom-npcs.md).
