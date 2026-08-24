# Первый плагин Vega

Обычному плагину нужны один NuGet-пакет, один базовый класс и никакие знания о Terraria, OTAPI, packet buffers, worker lanes или collectible load contexts.

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

Фиксированный context предоставляет пять понятных фасадов:

- `Commands` регистрирует типизированные command handlers;
- `Players` даёт immutable snapshots, поиск, сообщения, kick/teleport и join/leave hooks;
- `Tasks` возвращает работу на game thread, планирует повторяющиеся действия и связывает async I/O с game-thread apply;
- `Storage` читает и атомарно пишет plugin-scoped JSON под `data/plugins/<plugin-name>/`;
- `Log` пишет структурные записи с собственным source плагина.

Host владеет всеми командами, подписками, повторяющимися задачами и async-операциями, созданными через эти фасады. При unload он отменяет и дожидается background work, затем снимает регистрации и освобождает collectible context. Плагин не хранит leases и списки обратной регистрации.

## Асинхронный I/O

Используйте `Tasks.RunIo` только для настоящего database/HTTP/file I/O. Операция выполняется без доступа к Terraria, а callback `apply` отправляется на авторитетный game thread. Cancellation, логирование исключений и ожидание при unload выполняет host.

Низкоуровневые `IPacketProcessor<T>` и packet-security contracts отделены от `IVegaPluginContext`: они требуют явного понимания pure/concurrent/buffer-lifetime правил. Специалист по lifecycle может реализовать `IHotReloadableVegaModule` напрямую, осознанно принимая дополнительную сложность.

Соответствие типичных TShock-паттернов определяется поведением: `Initialize` заменяется `Configure`, ручные add/remove команд — `Commands.Register`, player hooks — `Players.OnJoined`, `TSPlayer` — immutable lookup плюс узкая операция, timer/dispose — `Tasks.Every`, background I/O/main-thread callback — `Tasks.RunIo`, config/data — scoped `Storage`. Исходная и бинарная совместимость с TShock не обещается.
