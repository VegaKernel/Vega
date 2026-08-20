# Vega

<p align="center">
  <strong>Русский</strong>
  · <a href="README_EN.md">English</a>
  · <a href="docs/ru/project-guide.md">Документация</a>
  · <a href="docs/roadmap.md">Roadmap</a>
  · <a href="docs/decisions/">ADR</a>
</p>

Vega — самостоятельный серверный runtime и framework для Terraria на .NET 10. Он запускает Terraria через OTAPI, отделяет игровую интеграцию от серверной логики и предоставляет типизированные контракты для команд, событий, игроков, пакетов, модулей, операций и кластерных сценариев.

Vega не является форком TShock и не поддерживает бинарную или исходную совместимость с TShock/TSAPI-плагинами. Старые реализации используются только как источник проверенных сценариев, требований и регрессионных тестов.

## Что уже есть

- нативный host `Vega.Server` без TSAPI и TShockAPI;
- строгая граница `Vega.Contracts` / `Vega.Core` / `Vega.Terraria` / `Vega.Runtime`;
- типизированные события, команды и packet pipeline на базе `TZ.Multiplicity`;
- безопасные player snapshots и операции через game-thread dispatcher;
- встроенные модули PvP и Custom Items;
- режимы Terminal UI, plain console и headless;
- структурные логи, runtime/GC/network/TPS-метрики и readiness telemetry;
- опциональные типизированные cluster-контракты и reference transport;
- экспериментальный `.vworld` cache с безопасным fallback на `.wld`.

## Требования

- .NET 10 SDK для сборки;
- доступ к NuGet при первом восстановлении пакетов;
- совместимая Terraria/OTAPI server runtime-среда.

## Сборка и тесты

```powershell
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
```

Публикация готового сервера:

```powershell
dotnet publish src/Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output artifacts/server
```

После публикации launcher остаётся в корне, общие зависимости находятся в `libs/`, а встроенные серверные модули — в `ServerPlugins/`.

## Запуск

```powershell
artifacts/server/Vega.Server.exe --vega-ui=plain
```

Доступные режимы: `auto`, `tui`, `plain`, `headless`. Для world-image cache используйте явный параметр `--vega-world-image=on`; по умолчанию он выключен, потому что измерения Vega 1.0 не показали стабильного выигрыша.

## Документация

- [Полное руководство на русском](docs/ru/project-guide.md)
- [English project guide](docs/en/project-guide.md)
- [Архитектура](docs/architecture.md)
- [Operations API и Terminal UI](docs/operations.md)
- [Cluster messaging](docs/cluster.md)
- [World image](docs/world-images.md)
- [Публикация релизов](docs/releasing.md)
- [Roadmap](docs/roadmap.md)
- [Архитектурные решения](docs/decisions/)

## Статус

Roadmap до Phase 12 закрывает основу Vega 1.0, но не означает реализацию каждого будущего требования из ADR. Per-player localization, Dimensions, sandbox backends и часть расширенных Core-примитивов остаются дальнейшей работой.

## Лицензия

Оригинальный код Vega распространяется по проприетарной лицензии [Vega Proprietary License](LICENSE). Публичная доступность репозитория или NuGet-пакета не делает проект open source и не предоставляет права использования, модификации или распространения сверх явно выданного разрешения.

Сторонние зависимости сохраняют лицензии своих правообладателей; см. [licenses/README.md](licenses/README.md).
