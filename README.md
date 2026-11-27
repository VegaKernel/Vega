# Vega

<p align="center">
  <img src="docs/assets/logo.png" alt="Vega" width="180">
</p>

<p align="center">
  <strong>Русский</strong>
  · <a href="docs/en/README.md">English</a>
  · <a href="docs/ru/project-guide.md">Документация</a>
  · <a href="docs/roadmap.md">Roadmap</a>
  · <a href="docs/decisions/">ADR</a>
</p>

Vega — самостоятельный серверный framework для Terraria 1.4.6.1 на .NET 11. Он запускает патченый официальный dedicated server через TerrariaRuntime 1.1.7, отделяет игровую интеграцию от серверной логики и предоставляет типизированные контракты для команд, событий, игроков, пакетов, модулей, операций и кластерных сценариев.

Vega не является форком TShock и не поддерживает бинарную или исходную совместимость с TShock/TSAPI-плагинами. Старые реализации используются только как источник проверенных сценариев, требований и регрессионных тестов.

## Архитектура

```mermaid
flowchart TB
    Terraria["Terraria"] --> TerrariaRuntime["TerrariaRuntime"]
    TerrariaRuntime --> TerrariaAdapter["Vega.Terraria<br/>игровая интеграция"]

    Runtime["Vega.Runtime<br/>host + composition root"] --> TerrariaAdapter
    Runtime --> Core["Vega.Core<br/>серверная логика"]
    Runtime -. "явная регистрация" .-> Modules["ServerPlugins<br/>Vega.Modules.*"]

    subgraph ControlPlane["Операционный слой"]
        direction TB
        Operations["Vega.Operations<br/>логи + метрики + read models"]
        Persistence["Vega.Persistence<br/>доменные хранилища + schema baseline"]
        Cluster["Vega.Cluster<br/>межсерверные сценарии"]
        Operations ~~~ Persistence
        Persistence ~~~ Cluster
    end

    subgraph WorldServices["Сервисы мира"]
        direction TB
        Regions["Vega.Regions<br/>команды + ACL регионов"]
        Sandboxes["Vega.Sandboxes<br/>изолированные миры + supervisor"]
        Backups["Vega.Backups<br/>world + database archives"]
        Regions ~~~ Sandboxes
        Sandboxes ~~~ Backups
    end

    Runtime --> Operations
    Runtime --> Persistence
    Runtime --> Cluster
    Runtime --> Regions
    Runtime --> Sandboxes
    Runtime --> Backups

    TerrariaAdapter --> Contracts["Vega.Contracts<br/>контракты + snapshots"]
    Core --> Contracts
    Operations --> Contracts
    Persistence --> Contracts
    Cluster --> Contracts
    Regions --> Contracts
    Sandboxes --> Contracts
    Backups --> Contracts
    Modules --> Contracts

    TerminalUI["Vega.TerminalUI"] --> Operations
```

`Vega.Core` не зависит от Terraria или TerrariaRuntime. Всё изменяемое игровое состояние остаётся за границей `Vega.Terraria`, а остальные компоненты работают через типизированные контракты и неизменяемые snapshots.

## Что уже есть

- нативный host `Vega.Server` без TSAPI и TShockAPI;
- строгая граница `Vega.Contracts` / `Vega.Core` / `Vega.Terraria` / `Vega.Runtime`;
- типизированные события, команды и packet pipeline на базе `Multiplicity 4.0.1` (нативный протокол 329);
- безопасные player snapshots и операции через game-thread dispatcher;
- встроенные модули PvP и Custom Items;
- встроенный [FakeProvider SDK](docs/ru/plugin-sdk.md#fakeprovider-для-плагинов): `context.World.FakeProvider`, глобальные и персональные тайловые слои, команды `fake`/`pfake`, видимость сущностей;
- опциональное [постраничное хранилище нативных тайлов](docs/paged-native-tiles.md), сохраняющее поведение `ITile`;
- режимы Terminal UI, plain console и headless;
- структурные логи, runtime/GC/network/TPS-метрики и readiness telemetry;
- опциональные типизированные cluster-контракты и reference transport;
- доменное хранение с готовым SQLite и провайдерами PostgreSQL/MySQL;
- экспериментальный `.vworld` cache с безопасным fallback на `.wld`.

## Требования

- .NET SDK 11.0.100, закреплённый в `src/global.json`;
- доступ к NuGet при первом восстановлении пакетов;
- совместимая Terraria/TerrariaRuntime server runtime-среда.

Для разработки хуков используется соседний репозиторий `TerrariaRuntime`: его патчер создаёт пакет версии 1.1.7. Подробности о границах интеграции и сетевых настройках: [руководство](docs/ru/project-guide.md).

## Сборка и тесты

```powershell
Push-Location src
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
Pop-Location
```

Публикация готового сервера:

```powershell
Push-Location src
dotnet publish Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output ../artifacts/server
Pop-Location
```

После публикации launcher остаётся в корне, общие зависимости находятся в `libs/`, а встроенные серверные модули — в `ServerPlugins/`.

Горячая замена внешних unload-safe плагинов в `ServerPlugins/` доступна через `IHotReloadableVegaModule`, но выключена по умолчанию в `config/modules.json`. Встроенные `IVegaModule` пересоздаются отдельным поколением через также выключенный по умолчанию `config/soft-restart.json`; Terraria, мир, игроки и БД при этом остаются живы.

## Запуск

```powershell
artifacts/server/Vega.Server.exe --vega-ui=plain
```

Доступные режимы: `auto`, `tui`, `plain`, `headless`. World-image cache включается параметром `--vega-world-image=on` или прямым выбором `-world путь/к/world.vworld`. Ручное и автоматическое сохранение обновляют image. Автосохранение настраивается в `config/world-maintenance.json`; опциональная очистка предметов/снарядов модулем AutoWorldCleaner — в `config/auto-world-cleaner.json`.

## Документация

- [Полное руководство на русском](docs/ru/project-guide.md)
- [English project guide](docs/en/project-guide.md)
- [Архитектура](docs/architecture.md)
- [Operations API и Terminal UI](docs/operations.md)
- [Изолированные sandbox-миры](docs/ru/sandboxes.md)
- [Резервные копии](docs/ru/backups.md)
- [Мягкий перезапуск](docs/ru/soft-restart.md)
- [Cluster messaging](docs/cluster.md)
- [Хранение и провайдеры баз данных](docs/ru/persistence.md)
- [World image](docs/world-images.md)
- [Публикация релизов](docs/releasing.md)
- [Roadmap](docs/roadmap.md)
- [Архитектурные решения](docs/decisions/)

## Статус

Roadmap фиксирует завершённые возможности и переход на патченый официальный сервер Terraria 1.4.6.1; production-релиз требует compatibility-, deployment- и soak-проверок. Dimensions-forwarding, sandbox-процессы, мягкий перезапуск, резервное копирование, базовые whitelist и warps реализованы. Авторитетный SSC и полное функциональное покрытие веток TShock остаются отдельными задачами; бинарная совместимость с TShock не является целью.

## Лицензия

Оригинальный код Vega распространяется по [Vega Proprietary License](licenses/LICENSE). Публичная доступность репозитория или NuGet-пакета не делает проект open source и не предоставляет права использования, модификации или распространения сверх явно выданного разрешения.

Сторонние зависимости сохраняют лицензии своих правообладателей; см. [licenses/README.md](licenses/README.md).
