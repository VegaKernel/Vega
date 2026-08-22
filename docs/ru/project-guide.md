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
| `src/Vega.Persistence` | Доменное хранение, миграции схемы и провайдеры SQLite/PostgreSQL/MySQL |
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
   ├──→ Vega.Persistence   ↑
   └──→ Server modules ────↑
```

`Vega.Contracts` является общей семантической границей. `Vega.Core` не может ссылаться на Terraria, OTAPI, TSAPI или TShockAPI. `Vega.Terraria` владеет всеми преобразованиями между изменяемыми игровыми объектами и Vega snapshots/operations. Architecture tests падают, если запрещённая зависимость пересекает эту границу.

Runtime подключает встроенные модули явно. Для `ServerPlugins/` также существует отдельный unload-safe контракт административной горячей замены; он выключен по умолчанию и никогда не считает обычные сборки `IVegaModule` безопасно выгружаемыми.

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
├── config/                база данных, TUI, часы, автосохранение, cleaner и модули
├── runtimes/              platform-specific assets пакетов, если есть
└── <culture>/             satellite resources, если есть
```

Assembly resolver сначала проверяет `libs/`, затем `ServerPlugins/`. Старые имена каталогов дистрибутива `bin/` и `Plugins/` не используются.

### Провайдер базы данных

По умолчанию используется SQLite с файлом `data/vega.db`. Для внешней базы выберите `postgresql` или `mysql` в `config/database.json` и задайте `VEGA_DATABASE_CONNECTION_STRING`. Подключение и миграции выполняются после дешёвой регистрации runtime/модулей, но до загрузки мира; ошибка останавливает запуск. Настройка, блокировки миграций, резервное копирование и восстановление описаны в [руководстве по хранению данных](persistence.md).

## 7. Запуск сервера

```powershell
artifacts/server/Vega.Server.exe --vega-ui=plain
```

Vega удаляет собственные параметры перед передачей остальных аргументов Terraria.

### Конфигурация сервера и приоритет значений

`config/server.json` — основная deploy-конфигурация пути к миру, порта, числа слотов, пароля, культуры Vega по умолчанию, режима UI и кэша `.vworld`. Оставьте `worldPath` равным `null`, чтобы сохранить интерактивный выбор мира Terraria. Приоритет всегда один и тот же:

```text
командная строка > переменные VEGA_* > config/server.json > значения Terraria/Vega по умолчанию
```

Поддерживаются `VEGA_WORLD`, `VEGA_PORT`, `VEGA_MAX_PLAYERS`, `VEGA_PASSWORD`, `VEGA_DEFAULT_CULTURE`, `VEGA_UI` и `VEGA_WORLD_IMAGE`. Некорректные JSON, порт, число слотов, boolean или культура останавливают запуск до инициализации базы и мира. `config show` показывает эффективные несекретные значения и их источник, `config validate` проверяет файл, а `config reload` выполняет проверку и явно сообщает, что настройки listener/мира требуют перезапуска. Пароль всегда отображается только как `<set>`.

Для команд, допуска подключения и кластерной репликации используется единый постоянный сервис типизированных банов. `ban <игрок-в-сети> [причина]` сохраняет имя и доступный IP и отключает игрока. Для офлайн- и сетевых идентификаторов используйте `ban add <name|account|uuid|ip|cidr> <значение> <30m|12h|7d|permanent> [причина]`. Активные записи показывают `ban list` и `ban show <id>`, отменяет бан `unban <id>`. IP, UUID и CIDR скрыты без отдельного права `vega.moderation.sensitive`.

Игрокам доступны `/lang`, `/lang list`, `/lang en-US`, `/lang ru-RU` и `/lang reset`. Выбор немедленно применяется только к этому получателю и не меняет глобальную культуру процесса; выбор гостя действует до конца текущего подключения, а выбор авторизованной учётной записи сохраняется и восстанавливается при следующем входе. Каталоги модулей изолированы пространством имён модуля и безопасно удаляются при выгрузке.

Управление учётными записями и группами постоянно хранится в схеме 2 для SQLite, PostgreSQL и MySQL. Первого администратора создайте из серверной консоли: `account create <имя> <пароль> administrator`. Игроки используют `/register <пароль>` (или `/register <аккаунт> <пароль>`), `/login <пароль>` (или `/login <аккаунт> <пароль>`), `/logout` и `/password <текущий> <новый>`. При входе проверяется бан аккаунта и запрещается одновременное использование одной записи, а изменения прав из постоянного кэша групп и временных ролей применяются без переподключения. Для управления доступны `account list`, `account show <имя>`, `account group <имя> <группа>`, `account password <имя> <новый-пароль>` и `account delete <имя>`. Временное назначение выполняют `account temp <имя> <группа> <30m|2h|7d>`, снимают через `account temp-clear <имя>`, а просматривают через `account temp-list`; новая lease атомарно заменяет старую и не создаёт таймер на игроке. Для групп используются `group list`, `group show <имя>`, `group create <имя> [родитель]`, `group parent <имя> <родитель|none>`, `group grant|deny|revoke <имя> <право>` и `group delete <имя>`. Применённые изменения используют оптимистичные ревизии и атомарно пишут аудит; пароли хранятся только как salted PBKDF2-SHA256 hashes. Оператор не может делегировать право, которого у него самого нет.

Регионы с привязкой к миру хранятся в схеме 3. Создание: `region create <имя> <x> <y> <ширина> <высота> [приоритет] [владелец]`; просмотр: `region list` и `region show <имя>`. Изменения выполняют `region resize`, `region priority`, `region owner`, `region member <имя> add|remove <аккаунт>`, `region group <имя> add|remove <группа>`, `region flag <имя> <флаг> <true|false>` и `region delete`. По умолчанию регион запрещает изменение блоков и использование сундуков. Действовать могут владелец, участники и группы из ACL, а также игроки с `vega.regions.bypass`; флаги `protected false`, `build true` и `use true` открывают соответствующий доступ. До проверки ACL идентификатор, координаты и активный сундук игрока сверяются с состоянием Terraria. Пакетные проверки используют заранее прогретый снимок в памяти и не обращаются к SQL в горячем пути игрового потока. Пересечения разрешаются сначала по приоритету, затем по меньшей площади, а явный контекст мира команды не смешивает одинаковые имена разных sandbox-миров.

Опциональные общесерверные gameplay-правила вынесены в `config/security.json` и по умолчанию отключены. После включения доступны ограничения living ghost/spectate, частоты смерти и дальности взаимодействия с правом обхода `vega.security.gameplay.bypass`. База данных, hot reload модулей, раскладка TUI, автосохранение, cleaner и world clock остаются в отдельных доменных файлах.

### Режимы Operations UI

| Параметр | Поведение |
|---|---|
| `--vega-ui=auto` | TUI в совместимом интерактивном терминале, иначе plain mode |
| `--vega-ui=tui` | Запросить Terminal.Gui dashboard |
| `--vega-ui=plain` | Обычный консольный ввод и вывод |
| `--vega-ui=headless` | Без интерактивного UI и operations stdout sink |

Инициализация TUI выполняется best-effort и не блокирует готовность сервера. Во время интерактивного выбора мира ввод остаётся у нативной консоли Terraria, затем на стадии `WorldLoading` Vega запускает Terminal.Gui с экраном прогресса и при `NetworkReady` переключается на operations dashboard. В режиме TUI вводом эксклюзивно владеет command palette; неизвестные Vega команды передаются в очередь нативного обработчика Terraria, поэтому `save`, `exit` и другие встроенные команды продолжают работать. Обычные логи остаются в dashboard/file sink и не портят экран терминала. Структурные file logs записываются в `logs/vega-operations.jsonl`, когда путь доступен.

Dashboard теперь представляет собой контрастный рабочий стол с перемещаемыми и изменяемыми по размеру окнами сервера, производительности, памяти, сети, игроков, модулей и логов. История TPS/CPU и памяти выводится ограниченными графиками. Tick telemetry также показывает среднее TPS за минуту, минимумы/максимумы и длительности p50/p95/p99 в dashboard и командах `tps`/`tpsstats`. Видимость и процентная геометрия настраиваются в `config/tui.json`; в палитре доступны `:windows`, `:show <name>`, `:hide <name>`, `:layout save` и `:layout reset`. Полный справочник по окнам и фильтрам находится в [руководстве Operations](../operations.md).

### World-image mode

`--vega-world-image=on` включает экспериментальный производный `.vworld` cache; `off` выключает его. По умолчанию режим выключен. `.wld` остаётся каноническим форматом, а устаревший, несовместимый или повреждённый image автоматически приводит к fallback на `.wld`.

Используйте `Vega.Server.exe --vega-world-image=on -world путь/к/world.wld` либо напрямую выберите готовый image: `Vega.Server.exe -world путь/к/world.vworld`. Прямой выбор автоматически включает cache и назначает соседний `.wld` каноническим файлом сохранения/восстановления, а валидная загрузка выполняется из `.vworld`. Ручное сохранение, автосохранение и сохранение при остановке сначала записывают `.wld`, затем снимают подготовленное runtime-состояние плиток и очередей жидкостей и публикуют один объединённый атомарный image. При следующем старте валидный runtime-снимок позволяет не повторять settling liquids; несовпадение исходника, схемы, checksums, очередей или последовательности инициализации автоматически возвращает загрузку к `.wld`.

### Автосохранение и очистка мира

`config/world-maintenance.json` задаёт только включение и интервал автосохранения; по умолчанию оно выполняется каждые 10 минут. Опциональная очистка предметов/снарядов принадлежит встроенному модулю `Vega.Modules.AutoWorldCleaner` и независимо настраивается в `config/auto-world-cleaner.json`. Cleaner по умолчанию выключен и владеет своим интервалом, требованием подключённых игроков, порогами количества/возраста предметов, порогом снарядов и исключением миньонов/турелей. Snapshot, решение и защищённое удаление атомарно выполняются в game thread Terraria. См. [руководство по обслуживанию мира](world-maintenance.md) и [AutoWorldCleaner](auto-world-cleaner.md).

### Режим world clock

`config/world-clock.json` может привязать ход игровых дня и ночи к монотонно прошедшему времени, а не к просевшему TPS. По умолчанию функция выключена. Коррекции выполняются только на game thread Terraria; большой явный перевод времени устанавливает новый anchor. Текущий drift и счётчики коррекций/re-anchor видны в Operations/TUI.

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

Внешний горячо загружаемый плагин напрямую реализует `IHotReloadableVegaModule` и имеет одну публичную точку входа без параметров. Административная функция включается в `config/modules.json`, после чего watcher автоматически применяет create/change/delete/rename. Для явного управления есть `plugin list`, `plugin reload <name-or-dll>` и `plugin unload <name-or-dll>`. Команды и хуки плагина должны регистрироваться через переданный `IHotModuleContext`: хост снимает их до утилизации модуля и проверяет сборку изолированного load context. Ошибка очистки требует перезапуска. Для встроенных ревизий `IVegaModule` всегда нужен перезапуск.

## 10. Operations и cluster behavior

`Vega.Operations` предоставляет неизменяемые server/player/module/log/metric projections. Terminal UI является только потребителем; административные действия проходят через обычные command и authorization boundaries.

Cluster support опционален. Текущий transport — in-memory reference implementation для детерминированных тестов и локальной композиции. Standalone server использует `UnavailableClusterClient` и остаётся работоспособным. Текущий ADR не выбирает production transport на Redis, NATS, RabbitMQ, HTTP или WebSocket.

## 11. Пакеты и релизы

Источником версии является `Directory.Build.props`. Библиотечные проекты упаковываются как `VegaKernel.*`; `Vega.Runtime` поставляется серверным архивом, а не NuGet-библиотекой.

Автоматический workflow `Release Tag` запускается при отправке тега `vMAJOR.MINOR.PATCH`. Он сверяет тег с `VersionPrefix`, восстанавливает, собирает и тестирует solution, упаковывает публичные библиотеки и symbol packages, публикует их в NuGet.org и создаёт или обновляет GitHub Release с готовым серверным ZIP-архивом и checksums. До отправки релизного тега в репозитории должен быть настроен Actions secret `NUGET_API_KEY`.

Отдельный workflow `Publish Manual` выбирает последний корректный тег и только пересобирает его готовый к развёртыванию серверный архив и assets GitHub Release. Он намеренно не запускает тесты и публикацию в NuGet и не требует NuGet-секрета. Подробности: [процедура релиза](../releasing.md).

## 12. Текущие ограничения

- Dimensions packet forwarding не реализован;
- sandbox backends и управление multi-world процессами не реализованы;
- горячая замена поддерживает только unload-safe контракт Vega и выключена по умолчанию; legacy и встроенные DLL требуют перезапуска;
- для cluster-сценариев не выбран production transport;
- `.vworld` остаётся экспериментальным и выключен по умолчанию;
- расширенные inventory, chest, overlay и world-reset примитивы остаются дальнейшей работой.

[Roadmap](../roadmap.md) фиксирует завершённые этапы. [ADR](../decisions/) являются нормативными архитектурными решениями, но принятые future-facing требования не означают автоматически завершённую реализацию.

## 13. Документация и дисциплина изменений

Если изменение переносит владение, меняет threading/readiness semantics, добавляет публичный контракт или меняет package/runtime layout:

1. обновите код и сфокусированные тесты;
2. обновите английское и русское руководства;
3. обновите документ подсистемы или ADR;
4. отмечайте пункт roadmap завершённым только после реализации и проверки.

Оригинальный код Vega является проприетарным. Перед использованием или распространением исходников, бинарных файлов, контейнеров или NuGet-пакетов прочитайте [LICENSE](../../LICENSE). Сторонние материалы сохраняют собственные условия.
