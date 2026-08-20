# Руководство по проекту Vega

[English version](../en/project-guide.md) · [README репозитория](../../README.md) · [Roadmap](../roadmap.md) · [ADR](../decisions/)

## 1. Обзор проекта

Vega — самостоятельный серверный runtime и framework для Terraria на .NET 10. Process host использует OTAPI для низкоуровневой интеграции с Terraria, а Vega владеет серверными контрактами, политиками, жизненным циклом модулей, наблюдаемостью и application boundaries.

Проект строится вокруг трёх правил:

1. Изменяемое состояние Terraria остаётся на game thread.
2. Core-логика зависит от типизированных контрактов и неизменяемых snapshots, а не от глобального состояния Terraria.
3. Новые абстракции появляются вместе с рабочим потребителем, а не как спекулятивная поверхность framework.

Vega не загружает плагины TShock или TSAPI и не сохраняет их API-формы. Исторические проекты используются только как источник проверенного поведения и regression-сценариев.

## 2. Структура репозитория

| Путь | Ответственность |
|---|---|
| `src/Vega.Contracts` | Публичные идентификаторы, snapshots, события, команды, player/world/network/operations/cluster-контракты |
| `src/Vega.Core` | Серверная и application-логика без зависимости от Terraria и OTAPI |
| `src/Vega.Terraria` | Terraria/OTAPI hooks, преобразование пакетов, player operations, game-thread scheduling и world integration |
| `src/Vega.Runtime` | Исполняемый host, composition root, запуск модулей, runtime options и разрешение сборок |
| `src/Vega.Operations` | Структурные логи, метрики, read models и безопасные command actions |
| `src/Vega.TerminalUI` | Опциональная Terminal.Gui-панель и command palette |
| `src/Vega.Cluster` | Опциональные типизированные межсерверные сценарии и in-memory reference transport |
| `src/Vega.Modules.Pvp` | PvP authority, projectile attribution, death windows и session state |
| `src/Vega.Modules.CustomItems` | Типизированные custom items и адресная доставка через Terraria |
| `tests/Vega.Architecture.Tests` | Проверки зависимостей и границ владения |
| `tests/Vega.Runtime.Tests` | Unit- и integration-тесты runtime-поведения |
| `benchmarks/Vega.WorldImage.Benchmarks` | Воспроизводимые измерения `.wld` и `.vworld` |
| `docs/decisions` | Architecture Decision Records и их статусы |

## 3. Runtime-архитектура

```text
Terraria
   ↓
OTAPI
   ↓
Vega.Runtime ───────→ Vega.TerminalUI
   │                       ↓
   ├──→ Vega.Terraria → Vega.Operations
   ├──→ Vega.Core          ↓
   ├──→ Vega.Cluster   Vega.Contracts
   └──→ Server modules ────↑
```

`Vega.Contracts` является общей семантической границей. `Vega.Core` не может ссылаться на Terraria, OTAPI, TSAPI или TShockAPI. `Vega.Terraria` владеет всеми преобразованиями между изменяемыми игровыми объектами и Vega snapshots/operations. Architecture tests падают, если запрещённая зависимость пересекает эту границу.

Текущий runtime подключает модули явно и не сканирует произвольные сборки. `ServerPlugins/` — каталог поставки встроенных серверных модулей, но пока не обещание динамической hot-load системы. Простое копирование DLL в этот каталог не регистрирует модуль.

## 4. Требования

- .NET 10 SDK;
- доступ к настроенным NuGet feeds при первом restore;
- Git для сборки из клона;
- платформа, поддерживаемая выбранными Terraria/OTAPI runtime-пакетами.

Важные версии пакетов закреплены в project files. Не нужно добавлять старый MonoMod development feed или восстанавливать удалённые TSAPI submodules.

## 5. Сборка и тесты

Запускайте из корня репозитория:

```powershell
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
```

Benchmark harness для world image собирается отдельно:

```powershell
dotnet build benchmarks/Vega.WorldImage.Benchmarks/Vega.WorldImage.Benchmarks.csproj `
  --configuration Release `
  --no-restore
```

MonoMod выдаёт ожидаемое предупреждение из-за явного opt-in Vega в target runtime. Сборка при этом должна завершаться без ошибок.

## 6. Публикация и структура дистрибутива

```powershell
dotnet publish src/Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output artifacts/server
```

Полученный каталог устроен так:

```text
artifacts/server/
├── Vega.Server.exe
├── Vega.Server.dll
├── Vega.Server.deps.json
├── Vega.Server.runtimeconfig.json
├── libs/                  общие библиотеки Vega и сторонние зависимости
├── ServerPlugins/         встроенные сборки Vega.Modules.*
├── runtimes/              platform-specific assets пакетов, если есть
└── <culture>/             satellite resources, если есть
```

Assembly resolver сначала проверяет `libs/`, затем `ServerPlugins/`. Старые имена каталогов дистрибутива `bin/` и `Plugins/` не используются.

## 7. Запуск сервера

```powershell
artifacts/server/Vega.Server.exe --vega-ui=plain
```

Vega удаляет собственные параметры перед передачей остальных аргументов Terraria.

### Режимы Operations UI

| Параметр | Поведение |
|---|---|
| `--vega-ui=auto` | TUI в совместимом интерактивном терминале, иначе plain mode |
| `--vega-ui=tui` | Запросить Terminal.Gui dashboard |
| `--vega-ui=plain` | Обычный консольный ввод и вывод |
| `--vega-ui=headless` | Без интерактивного UI и operations stdout sink |

Инициализация TUI выполняется best-effort и не блокирует готовность сервера. Vega запускает Terminal.Gui только после `NetworkReady`, чтобы Terraria сохраняла стандартный ввод во время интерактивного выбора и загрузки мира. Структурные file logs записываются в `logs/vega-operations.jsonl`, когда путь доступен.

### World-image mode

`--vega-world-image=on` включает экспериментальный производный `.vworld` cache; `off` выключает его. По умолчанию режим выключен. `.wld` остаётся каноническим форматом, а устаревший, несовместимый или повреждённый image автоматически приводит к fallback на `.wld`.

## 8. Lifecycle и готовность

Стадии runtime:

```text
ProcessStart → Bootstrap → RuntimeReady → ModulesRegistered
→ WorldLoading → WorldReady → NetworkReady
→ BackgroundWarmup → FullyWarmed
```

`NetworkReady` — операционная точка готовности. Загрузка мира Terraria измеряется отдельно от Vega framework overhead. Lifecycle transitions, состояние модулей, TPS, network counters, память, GC и world-image measurements доступны через operations snapshots и структурные логи.

## 9. Модули и границы расширения

Встроенные модули реализуют `IVegaModule` и создаются composition root runtime.

- `Vega.Modules.Pvp` владеет PvP-политиками и временным session state.
- `Vega.Modules.CustomItems` владеет определениями custom items, а `Vega.Terraria` выполняет packet delivery.

Модуль должен зависеть от минимально необходимой capability. Модули не должны менять `Terraria.Main`, удерживать изменяемые player objects, создавать конкурирующий packet codec или прятать бизнес-логику в глобальных helpers. Временный extension state привязан к конкретной player session и детерминированно удаляется при disconnect.

## 10. Operations и cluster behavior

`Vega.Operations` предоставляет неизменяемые server/player/module/log/metric projections. Terminal UI является только потребителем; административные действия проходят через обычные command и authorization boundaries.

Cluster support опционален. Текущий transport — in-memory reference implementation для детерминированных тестов и локальной композиции. Standalone server использует `UnavailableClusterClient` и остаётся работоспособным. Текущий ADR не выбирает production transport на Redis, NATS, RabbitMQ, HTTP или WebSocket.

## 11. Пакеты и релизы

Источником версии является `Directory.Build.props`. Библиотечные проекты упаковываются как `VegaKernel.*`; `Vega.Runtime` поставляется серверным архивом, а не NuGet-библиотекой.

Автоматический workflow `Release Tag` запускается при отправке тега `vMAJOR.MINOR.PATCH`. Он сверяет тег с `VersionPrefix`, восстанавливает, собирает и тестирует solution, упаковывает публичные библиотеки и symbol packages, публикует их в NuGet.org и создаёт или обновляет GitHub Release с готовым серверным ZIP-архивом и checksums. До отправки релизного тега в репозитории должен быть настроен Actions secret `NUGET_API_KEY`.

Отдельный workflow `Publish Manual` выбирает последний корректный тег и только пересобирает его готовый к развёртыванию серверный архив и assets GitHub Release. Он намеренно не запускает тесты и публикацию в NuGet и не требует NuGet-секрета. Подробности: [процедура релиза](../releasing.md).

## 12. Текущие ограничения

- per-player выбор языка и persistence каталогов переводов не реализованы;
- Dimensions packet forwarding не реализован;
- sandbox backends и управление multi-world процессами не реализованы;
- `ServerPlugins/` не поддерживает произвольный dynamic discovery или hot reload;
- для cluster-сценариев не выбран production transport;
- `.vworld` остаётся экспериментальным и выключен по умолчанию;
- расширенные inventory, chest, overlay, role-lease и world-reset примитивы остаются дальнейшей работой.

[Roadmap](../roadmap.md) фиксирует завершённые этапы. [ADR](../decisions/) являются нормативными архитектурными решениями, но принятые future-facing требования не означают автоматически завершённую реализацию.

## 13. Документация и дисциплина изменений

Если изменение переносит владение, меняет threading/readiness semantics, добавляет публичный контракт или меняет package/runtime layout:

1. обновите код и сфокусированные тесты;
2. обновите английское и русское руководства;
3. обновите документ подсистемы или ADR;
4. отмечайте пункт roadmap завершённым только после реализации и проверки.

Оригинальный код Vega является проприетарным. Перед использованием или распространением исходников, бинарных файлов, контейнеров или NuGet-пакетов прочитайте [LICENSE](../../LICENSE). Сторонние материалы сохраняют собственные условия.
