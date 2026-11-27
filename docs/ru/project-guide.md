# Руководство по проекту Vega

[English version](../en/project-guide.md) · [README репозитория](../../README.md) · [Roadmap](../roadmap.md) · [ADR](../decisions/)

## 1. Обзор проекта

Vega — самостоятельный серверный framework для Terraria 1.4.6.1 на .NET 11. Process host использует TerrariaRuntime 1.1.6 и патченый официальный dedicated server, а Vega владеет серверными контрактами, политиками, жизненным циклом модулей, наблюдаемостью и application boundaries.

Проект строится вокруг трёх правил:

1. Изменяемое состояние Terraria остаётся на game thread.
2. Core-логика зависит от типизированных контрактов и неизменяемых snapshots, а не от глобального состояния Terraria.
3. Новые абстракции появляются вместе с рабочим потребителем, а не как спекулятивная поверхность framework.

Vega не загружает плагины TShock или TSAPI и не сохраняет их API-формы. Исторические проекты используются только как источник проверенного поведения и regression-сценариев.

## 2. Структура репозитория

| Путь | Ответственность |
|---|---|
| `src/Vega.Contracts` | Публичные идентификаторы, snapshots, события, команды, player/world/network/operations/cluster-контракты |
| `src/Vega.PluginSdk` | Тонкая точка входа обычного плагина и фасады command/player/task/storage/log |
| `src/Vega.Core` | Серверная и application-логика без зависимости от Terraria и TerrariaRuntime |
| `src/Vega.Terraria` | Terraria/TerrariaRuntime hooks, преобразование пакетов, player operations, game-thread scheduling и world integration |
| `src/Vega.Runtime` | Исполняемый host, composition root, запуск модулей, runtime options и разрешение сборок |
| `src/Vega.Operations` | Структурные логи, метрики, read models и безопасные command actions |
| `src/Vega.TerminalUI` | Опциональная Terminal.Gui-панель и command palette |
| `src/Vega.Cluster` | Опциональные типизированные межсерверные сценарии и in-memory reference transport |
| `src/Vega.Persistence` | Доменное хранение, базовая схема Vega 1.0 и провайдеры SQLite/PostgreSQL/MySQL |
| `src/Vega.Regions` | Команды и packet ACL регионов; SQL-хранение остаётся в Persistence |
| `src/Vega.Modules.Pvp` | PvP authority, projectile attribution, death windows и session state |
| `src/Vega.Modules.CustomItems` | Типизированные custom items и адресная доставка через Terraria |
| `tests/Vega.Architecture.Tests` | Проверки зависимостей и границ владения |
| `tests/Vega.Runtime.Tests` | Unit- и integration-тесты runtime-поведения |
| `benchmarks/Vega.WorldImage.Benchmarks` | Воспроизводимые измерения `.wld` и `.vworld` |
| `docs/decisions` | Architecture Decision Records и их статусы |

Разработку внешнего плагина начинайте с короткого [руководства по первому плагину](plugin-sdk.md). Оно намеренно предшествует внутренним архитектурным деталям.

## 3. Runtime-архитектура

```text
Terraria
   ↓
TerrariaRuntime
   ↓
Vega.Runtime ───────→ Vega.TerminalUI
   │                       ↓
   ├──→ Vega.Terraria → Vega.Operations
   ├──→ Vega.Core          ↓
   ├──→ Vega.Cluster   Vega.Contracts
   ├──→ Vega.Persistence   ↑
   ├──→ Vega.Regions       ↑
   └──→ Server modules ────↑
```

`Vega.Contracts` является общей семантической границей. `Vega.Core` не может ссылаться на Terraria, TerrariaRuntime, TSAPI или TShockAPI. `Vega.Terraria` владеет всеми преобразованиями между изменяемыми игровыми объектами и Vega snapshots/operations. Architecture tests падают, если запрещённая зависимость пересекает эту границу.

Runtime подключает встроенные модули явно. Для `ServerPlugins/` также существует отдельный unload-safe контракт административной горячей замены; он выключен по умолчанию и никогда не считает обычные сборки `IVegaModule` безопасно выгружаемыми.

## 4. Требования

- .NET SDK 11.0.100, закреплённый в `src/global.json`;
- доступ к настроенным NuGet feeds при первом restore;
- Git для сборки из клона;
- платформа, поддерживаемая выбранными Terraria/TerrariaRuntime runtime-пакетами.

Важные версии пакетов закреплены в project files. Не нужно добавлять старый MonoMod development feed или восстанавливать удалённые TSAPI submodules.

Все first-party проекты компилируются с .NET 11 Runtime Async через централизованный feature `runtime-async=on` в `Directory.Build.props`. Не отключайте и не заменяйте его в отдельном проекте: architecture suite проверяет сгенерированные runtime-async metadata.

## 5. Сборка и тесты

Запускайте из корня репозитория:

```powershell
Push-Location src
dotnet restore Vega.sln
dotnet build Vega.sln --configuration Release --no-restore
dotnet test Vega.sln --configuration Release --no-build --no-restore
Pop-Location
```

Benchmark harness для world image собирается отдельно:

```powershell
dotnet build benchmarks/Vega.WorldImage.Benchmarks/Vega.WorldImage.Benchmarks.csproj `
  --configuration Release `
  --no-restore
```

Synchronous packet baseline стадии 22 запускается отдельно и выводит machine-readable throughput, allocations, CPU, GC и tick latency:

```powershell
dotnet run --project benchmarks/Vega.PacketPipeline.Benchmarks/Vega.PacketPipeline.Benchmarks.csproj `
  --configuration Release
```

Классификация и текущий baseline описаны в [packet pipeline](../packet-pipeline.md).

MonoMod выдаёт ожидаемое предупреждение из-за явного opt-in Vega в target runtime. Сборка при этом должна завершаться без ошибок.

## 6. Публикация и структура дистрибутива

```powershell
Push-Location src
dotnet publish Vega.Runtime/Vega.Runtime.csproj `
  --configuration Release `
  --output ../artifacts/server
Pop-Location
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

### Режимы runtime

`Vega.Server` работает на CoreCLR с tiered compilation и dynamic PGO. ReadyToRun допускается как отдельный измеряемый вариант публикации CoreCLR; публикация самого сервера через NativeAOT запрещена, потому что управляемые `ServerPlugins`, collectible hot reload, поколения soft restart и runtime TerrariaRuntime/MonoMod являются обязательными возможностями. Packet path стадии 22 остаётся внутри процесса.

Сейчас в репозитории нет отдельного поставляемого executable, подходящего для NativeAOT. `Vega.Contracts` собирается с анализаторами совместимости trimming/AOT, чтобы будущая узкая утилита могла использовать чистую границу контрактов без Terraria, plugin host и UI. Порядок кандидатов, правила сравнения и fallback описаны в [аудите специализации runtime](../native-aot.md).

### Провайдер базы данных

По умолчанию используется SQLite с файлом `data/vega.db`. Для внешней базы выберите `postgresql` или `mysql` в `config/database.json` и задайте `VEGA_DATABASE_CONNECTION_STRING`. Подключение и создание базовой схемы Vega 1.0 выполняются после дешёвой регистрации runtime/модулей, но до загрузки мира; ошибка останавливает запуск. Настройка, блокировки схемы, резервное копирование и восстановление описаны в [руководстве по хранению данных](persistence.md).

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

Поддерживаются `VEGA_WORLD`, `VEGA_PORT`, `VEGA_MAX_PLAYERS`, `VEGA_PASSWORD`, `VEGA_DEFAULT_CULTURE`, `VEGA_UI` и `VEGA_WORLD_IMAGE`. Некорректные JSON, порт, число слотов, boolean или культура останавливают запуск до инициализации базы и мира. `config show` показывает эффективные несекретные значения и их источник, `config validate` проверяет файл, а `config reload` выполняет проверку и явно сообщает, что настройки listener/мира требуют перезапуска. Пароль всегда отображается только как `<set>`. `help` показывает только доступные вызывающему команды, а `help <команда>` — канонический синтаксис, псевдонимы и требуемые права; каталог автоматически следует за горячей регистрацией и выгрузкой команд.

Модерация использует постоянные типизированные сервисы. `ban <игрок-в-сети> [причина]` сохраняет имя и доступный IP и отключает игрока. Для офлайн- и сетевых идентификаторов используйте `ban add <name|account|uuid|ip|cidr> <значение> <30m|12h|7d|permanent> [причина]`. Активные записи показывают `ban list` и `ban show <id>`, отменяет бан `unban <id>`. Для чата доступны `mute <игрок-в-сети> <30m|12h|7d> [причина]`, `mute add <name|account> <значение> <срок> [причина]`, `mute list`, `mute show <id>` и `unmute <id>`. Заглушённый игрок может выполнять команды, но обычный чат отклоняется с сообщением о сроке и причине. IP, UUID и CIDR банов скрыты без отдельного права `vega.moderation.sensitive`; ограниченное окно TUI открывается через `:show moderation`.

Игрокам доступны `/lang`, `/lang list`, `/lang en-US`, `/lang ru-RU` и `/lang reset`. Выбор немедленно применяется только к этому получателю и не меняет глобальную культуру процесса; выбор гостя действует до конца текущего подключения, а выбор авторизованной учётной записи сохраняется и восстанавливается при следующем входе. Каталоги модулей изолированы пространством имён модуля и безопасно удаляются при выгрузке.

Управление учётными записями и группами входит в базовую схему Vega 1.0 для SQLite, PostgreSQL и MySQL. Первого администратора создайте из серверной консоли: `account create <имя> <пароль> administrator`. Игроки используют `/register <пароль>` (или `/register <аккаунт> <пароль>`), `/login <пароль>` (или `/login <аккаунт> <пароль>`), `/logout` и `/password <текущий> <новый>`. При входе проверяется бан аккаунта и запрещается одновременное использование одной записи, а изменения прав из постоянного кэша групп и временных ролей применяются без переподключения. Для управления доступны `account list`, `account show <имя>`, `account group <имя> <группа>`, `account password <имя> <новый-пароль>` и `account delete <имя>`. Временное назначение выполняют `account temp <имя> <группа> <30m|2h|7d>`, снимают через `account temp-clear <имя>`, а просматривают через `account temp-list`; новая lease атомарно заменяет старую и не создаёт таймер на игроке. Для групп используются `group list`, `group show <имя>`, `group create <имя> [родитель]`, `group parent <имя> <родитель|none>`, `group grant|deny|revoke <имя> <право>` и `group delete <имя>`. Применённые изменения используют оптимистичные ревизии и атомарно пишут аудит; пароли хранятся только как salted PBKDF2-SHA256 hashes. Оператор не может делегировать право, которого у него самого нет.

Регионы с привязкой к миру хранятся в базовой схеме Vega 1.0. Создание: `region create <имя> <x> <y> <ширина> <высота> [приоритет] [владелец]`; просмотр: `region list` и `region show <имя>`. Изменения выполняют `region resize`, `region priority`, `region owner`, `region member <имя> add|remove <аккаунт>`, `region group <имя> add|remove <группа>`, `region flag <имя> <флаг> <true|false>` и `region delete`. По умолчанию регион запрещает изменение блоков и использование сундуков. Действовать могут владелец, участники и группы из ACL, а также игроки с `vega.regions.bypass`; флаги `protected false`, `build true` и `use true` открывают соответствующий доступ. До проверки ACL идентификатор, координаты и активный сундук игрока сверяются с состоянием Terraria. Пакетные проверки используют заранее прогретый снимок в памяти и не обращаются к SQL в горячем пути игрового потока. Пересечения разрешаются сначала по приоритету, затем по меньшей площади, а явный контекст мира команды не смешивает одинаковые имена разных sandbox-миров. Operations публикует те же ограниченные неизменяемые снимки; окно TUI открывается через `:show regions`, а любые изменения по-прежнему идут через авторизованную команду `region`.

Опциональные общесерверные gameplay-правила вынесены в `config/security.json` и по умолчанию отключены. После включения доступны ограничения living ghost/spectate, частоты смерти и дальности взаимодействия с правом обхода `vega.security.gameplay.bypass`. База данных, hot reload модулей, раскладка TUI, автосохранение, cleaner и world clock остаются в отдельных доменных файлах.

`config/network.json` задаёт `pendingConnectionLimit` (128 по умолчанию, 1–1024) и `framesPerSlotPerPass` (32, 1–256). Vega отдельно устанавливает бюджеты через хуки TerrariaRuntime; обработка кадров остаётся в штатном игровом потоке и сохраняет порядок. `useOrderedTcp: true` включает экспериментальный адаптер принятых сокетов: собственные буферы из пула, FIFO, бюджеты на соединение и мир, callback с проверкой поколения. `orderedCallbacksPerPass` (8192 по умолчанию, 1–8192) ограничивает число подтверждений Ordered при сохранении кооперативного временного бюджета 2 мс. По умолчанию `useOrderedTcp: false` (ванильный TCP); для production необходимы проверки реального входа клиентов и производительности. Настройки применяются при запуске. См. [архитектуру транспорта](../native-transport-design.md).

`reload` / `config reload` (`vega.config.manage`) проверяет стартовую конфигурацию, перечитывает общий whitelist и отдельно локальные `restrictions.json` и `build-protection.json` из каталога данных мира. Отсутствующий или некорректный локальный файл не сбрасывает активную политику. Если общий whitelist повреждён или исчез, новые входы отклоняются, чтобы процессы не использовали разные кэшированные правила. Стартовые настройки listener, пароля, базы данных и мира требуют перезапуска.

Необязательный REST основного мира: задайте `VEGA_REST_PORT` и `VEGA_REST_TOKEN_SHA256` (64 шестнадцатеричных символа SHA-256 случайного bearer-токена длиной не менее 32 символов). API слушает только `127.0.0.1`; передавайте `Authorization: Bearer <token>` в HTTP-заголовке для `GET /v1/status`, `/v1/players`, `/v1/network`. Отдельный `VEGA_REST_MAINTENANCE_TOKEN_SHA256` разрешает типизированный `POST /v1/world/save` без тела после готовности мира; токен чтения сохранять мир не может. Без порта сервис выключен. Команды `rest status`, `rest listusers`, `rest destroytokens` требуют `vega.operations.rest.manage`; отзыв действует до перезапуска. Нет авторизации паролем аккаунта, токенов в URL, исполнения произвольных команд, выдачи чувствительных данных игрока или отдельных REST listeners sandbox.

Первичная настройка через доверенную консоль: `setup` показывает инструкцию, `account create <name> <password> administrator` создаёт администратора. В отличие от TShock, Vega не реализует безусловное временное повышение всех прав через `su`/`sudo`; используются явные ограниченные аренды групп `tempgroup` или авторизованные консольные команды.

Дополнительные нативные команды: `bossdamage [page]` читает недавние завершённые бои из ванильного трекера с историческими именами, `evil [crimson|corruption]` меняет только флаг зла мира, `settle` запускает нативное оседание жидкостей, `grow` вызывает ванильный рост деревьев/грибов на подходящем существующем грунте (координаты консоли или авторизованный игрок). `annoy` планирует ограниченный звуковой эффект, `firework` запускает декоративный нативный снаряд, `godmode`/`god` использует нативную Creative Power игрока и отзывает административное включение при отключении. `rocket` требует нативного SSC, доступного в явно включённом авторитетном режиме. Эффекты для игроков ещё необходимо подтвердить реальным клиентом.

Включаемое отдельно сохранение персонажей основного мира поддерживает `savessc`, `overridessc <authenticated-player>` (`ossc`), `uploadssc [player] confirm`. Это отдельные авторизованные операции с проверкой ревизии; импорт использует исходный снимок персонажа с нативной границы перед WorldData и требует подтверждения, не подменяя отсутствующий снимок текущим состоянием. Для нового сервера нативный SSC включается в `config/primary-character-persistence.json`:

```json
{"enabled":true,"authoritative":true,"requireLogin":true,"requireCharacterReady":true}
```

По умолчанию режим выключен. `authoritative` требует включённого сохранения и готовности персонажа. До точной привязки аккаунта и успешного восстановления сервер использует пустое состояние; синхронизация инвентаря, loadout и других данных персонажа не может заменить это состояние. Чат авторизации, движение и штатные подтверждения доступны. При выходе из аккаунта состояние очищается. Авторитетный основной мир использует общее хранилище SSC с sandbox; существующий отдельный `primary-characters.json` переносится офлайн-командой `--vega-migrate-primary-ssc` перед включением режима ([инструкция миграции](migration.md)). Sandbox с SSC, отличным от `Disabled`, также включает нативный флаг SSC. Перенос текущей схемы SQLite TShock реализован и протестирован на фикстурах; полное подтверждение входа, восстановления и переподключения штатным клиентом и эквивалентность всех проверок ещё открыты.

### Режимы Operations UI

| Параметр | Поведение |
|---|---|
| `--vega-ui=auto` | TUI в совместимом интерактивном терминале, иначе plain mode |
| `--vega-ui=tui` | Запросить Terminal.Gui dashboard |
| `--vega-ui=plain` | Обычный консольный ввод и вывод |
| `--vega-ui=headless` | Без интерактивного UI и operations stdout sink |

Инициализация TUI выполняется best-effort и не блокирует готовность сервера. Во время интерактивного выбора мира ввод остаётся у нативной консоли Terraria, затем на стадии `WorldLoading` Vega запускает Terminal.Gui с экраном прогресса и при `NetworkReady` переключается на operations dashboard. В режиме TUI вводом эксклюзивно владеет command palette; неизвестные Vega команды передаются в очередь нативного обработчика Terraria, поэтому `save`, `exit` и другие встроенные команды продолжают работать. Нативный stdout/stderr записывается в ограниченный Operations log с источником `terraria-console`, а строки без завершения ограничены 8192 символами, поэтому вывод больше не исчезает за dashboard. Обычные логи остаются в dashboard/file sink и не портят экран терминала. Структурные file logs записываются в `logs/vega-operations.jsonl`, когда путь доступен.

Dashboard представляет собой контрастный тайловый рабочий стол с окнами сервера, производительности, памяти, сети, игроков, регионов, модулей и логов. Перетаскивание общей границы перераспределяет место между соседними панелями, перетаскивание заголовка на другую панель меняет их местами, а двойной щелчок по заголовку разворачивает и восстанавливает панель. Снимок Operations собирается вне UI-потока без очереди запросов, поэтому медленный сбор метрик не задерживает ввод. История TPS/CPU и памяти выводится ограниченными графиками. Tick telemetry также показывает среднее TPS за минуту, минимумы/максимумы и длительности p50/p95/p99 в dashboard и командах `tps`/`tpsstats`. Видимость и процентная геометрия настраиваются в `config/tui.json`; в палитре доступны `:windows`, `:show <name>`, `:hide <name>`, `:layout save` и `:layout reset`. Полный справочник по окнам и фильтрам находится в [руководстве Operations](../operations.md).

### World-image mode

`--vega-world-image=on` включает экспериментальный производный `.vworld` cache; `off` выключает его. По умолчанию режим выключен. `.wld` остаётся каноническим форматом, а устаревший, несовместимый или повреждённый image автоматически приводит к fallback на `.wld`.

Используйте `Vega.Server.exe --vega-world-image=on -world путь/к/world.wld` либо напрямую выберите готовый image: `Vega.Server.exe -world путь/к/world.vworld`. Прямой выбор автоматически включает cache и назначает соседний `.wld` каноническим файлом сохранения/восстановления, а валидная загрузка выполняется из `.vworld`. Ручное сохранение, автосохранение и сохранение при остановке сначала записывают `.wld`, затем снимают подготовленное runtime-состояние плиток и очередей жидкостей и публикуют один объединённый атомарный image. При следующем старте валидный runtime-снимок позволяет не повторять settling liquids; несовпадение исходника, схемы, checksums, очередей или последовательности инициализации автоматически возвращает загрузку к `.wld`.

### Автосохранение и очистка мира

`config/world-maintenance.json` задаёт только включение и интервал автосохранения; по умолчанию оно выполняется каждые 10 минут. Опциональная очистка предметов/снарядов принадлежит встроенному модулю `Vega.Modules.AutoWorldCleaner` и независимо настраивается в `config/auto-world-cleaner.json`. Cleaner по умолчанию выключен и владеет своим интервалом, требованием подключённых игроков, порогами количества/возраста предметов, порогом снарядов и исключением миньонов/турелей. Snapshot, решение и защищённое удаление атомарно выполняются в game thread Terraria. См. [руководство по обслуживанию мира](world-maintenance.md) и [AutoWorldCleaner](auto-world-cleaner.md).

### Режим world clock

`config/world-clock.json` может привязать ход игровых дня и ночи к монотонно прошедшему времени, а не к просевшему TPS. По умолчанию функция выключена. Коррекции выполняются только на game thread Terraria; большой явный перевод времени устанавливает новый anchor. Текущий drift и счётчики коррекций/re-anchor видны в Operations/TUI.

Модуль Administration предоставляет `time [day|noon|night|midnight|HH:mm]` (`vega.world.time`) и `wind <-40..40 mph>` (`vega.world.wind`) в консоли и чате. Изменения применяются через игровой поток в текущем мире; ручной перевод времени сбрасывает anchor часов, а ветер изменяет текущую и целевую скорость. В отсутствие параметра `time` показывает игровое время.

`Vega.Modules.Navigation` предоставляет игровые команды (не из консоли):

| Команда | Право | Действие |
| --- | --- | --- |
| `/spawn` | `vega.navigation.spawn` | Телепорт на спавн текущего мира |
| `/tp <игрок>` | `vega.navigation.tp` | Телепорт к игроку |
| `/tp <источник> <назначение>` | `vega.navigation.tp` + `vega.navigation.others` | Телепорт другого игрока |
| `/tphere <игрок>` | `vega.navigation.others` | Телепорт игрока к вызывающему |
| `/tppos <x> <y>` | `vega.navigation.tppos` | Телепорт к координатам в тайлах; небезопасные края мира отклоняются |
| `/pos [игрок]` | `vega.navigation.pos` | Координаты игрока в тайлах |
| `/tpallow` | `vega.navigation.allow` | Переключить разрешение входящих телепортов на текущую сессию |

Имена разрешаются по точному совпадению, уникальному префиксу или номеру слота. Для входящих телепортов требуется согласие игрока или право `vega.navigation.override`; заявки привязаны к сессии и текущему миру. `/home` (`vega.navigation.home`) проверяет персональный спавн с fallback на спавн мира, `/tpnpc` (`vega.navigation.tpnpc`) ищет активного NPC по имени или `slot:<номер>`. `/warp <имя>` и `warp list [страница]` используют `vega.navigation.warp`; управление `add`, `del`, `hide` — дополнительно `vega.navigation.warp.manage`, `send` — `vega.navigation.others`. Скрытые warp не показываются в списке, но доступны по известному имени. Хранилище привязано к UUID файла мира и контексту runtime. Массовый телепорт `*` ещё не реализован.

Дополнительные административные команды: `worldinfo`, `worldevent <rain|bloodmoon|eclipse|fullmoon> [on|off]`, `spawnmob <id|имя> [1..20]`, `item <id|имя> [stack] [prefix-id]`, `give <предмет> <игрок> [stack] [prefix-id]`, `heal [игрок] [amount]`, `buff <id|имя> [1..3600 секунд]`, `gbuff <игрок> <buff> [seconds]`, `broadcast <сообщение>` и `clear <item|projectile|npc> [all]`. Они управляют только текущим миром и используют отдельные права команд. `clear all` требует дополнительного права; по умолчанию охраняются миньоны/сентри, дружественные и boss NPC. Не все расширенные ветки TShock и реальные клиентские синхронизации подтверждены.

`whitelist list|on|off|add <ip|cidr>|del <ip|cidr>` (`vega.admin.whitelist`) использует один `data/whitelist.json` в корне инсталляции для основного мира и всех песочниц. Запись атомарна и сериализована между процессами, изменения видны уже работающим мирам при следующем входе; эффективный IP проверяется до отправки WorldData. Храните вместе с конфигурацией `whitelist.json.lock` (маркер инициализации). Старые локальные whitelist песочниц не объединяются и не используются. Whitelist не выкидывает уже подключённых игроков. Баны идут через общую БД, активные баны повторно проверяются для игроков всех запущенных миров. `/itemban`, `/projban`, `/tileban` (`vega.restrictions.manage`) принимают `list [page]`, `add <id>`, `del <id>` и сохраняются в локальном `data/restrictions.json` каждого мира. Проверки выполняются на игровом потоке перед применением действий; исключения групп выдаются через `vega.restrictions.bypass.item`, `.projectile` и `.tile`. Полный SSC и коррекция клиентской инвентарной синхронизации при запрете пока не заявлены.

Команды чата: `/whisper` (`w`, `tell`, `pm`, `dm`), `/reply` (`r`), `/wallow` (`wa`) используют право `vega.chat.whisper`; `/party` (`p`) — `vega.chat.party`; `/me` — `vega.chat.me`. Mute действует и на эти команды. Сообщения доставляются только указанным игрокам текущего мира; party — игрокам своей команды. Предпочтение whisper и адресат reply принадлежат текущей сессии и сбрасываются при отключении.

`antibuild [on|off|status]` (`vega.world.antibuild`) запрещает изменения мира; `protectspawn [on|off|status|radius <1..500>]` (`vega.world.protectspawn`) защищает квадрат вокруг актуального нативного спавна. Вызов без аргументов переключает состояние. Настройки сохраняются отдельно для каждого runtime в `data/build-protection.json`. Права обхода — `vega.world.build.bypass` и `vega.world.spawn.bypass`; обход регионов не даёт автоматически обход этих ограничений. Обычное использование объектов регулируется региональной политикой отдельно.

Управление нативными настройками: `worldmode [normal|expert|master]`, `spawnrate [1..60000]`, `maxspawns [0..200]`, `setspawn [x y]`, `setdungeon [x y]`, `forcehalloween [on|off|status]`, `forcexmas [on|off|status]`, `clearangler`. Режим путешествия не переключается на работающем сервере; `setspawn` и `setdungeon` без координат используют текущего игрока. `worldevent` дополнительно поддерживает `slimerain`, `sandstorm`, `pumpkinmoon`, `frostmoon`, `goblinarmy`, `snowlegion`, `pirateinvasion`, `martianmadness`; вторжения запускаются через ванильные условия, принудительное выключение не поддерживается.

`death`/`pvpdeath` показывают нативные счётчики персонажа, `alldeath`/`allpvpdeath [page]` — онлайн-игроков текущего мира (право `vega.players.deaths.others`). Это не рейтинг всех исторических аккаунтов. `version` показывает сборку Vega и патченой Terraria; `motd` и `rules` читают локальный `data/motd.txt`/`data/rules.txt` либо общий `config/`-файл, если локальный отсутствует.

`kill` (`slay`), `slap`, `respawn` отправляют штатные команды игровому клиенту конкретной сессии; результаты ещё требуют подтверждения клиента. `hardmode status` показывает состояние, `hardmode start confirm` запускает штатную перестройку мира, обратного переключения нет. `serverpassword set <password>`/`clear` изменяет пароль новых подключений до перезапуска (`vega.cfg.password`), без вывода пароля. `off-nosave` (`exit-nosave`, `stop-nosave`, право `vega.cfg.maintenance.nosave`) отключает сохранение `.wld` при выходе, но не откатывает уже завершённые сохранения.

`accountinfo <account>` (`ai`, право `vega.accounts.inspect` или `vega.accounts.manage`) показывает данные аккаунта через существующий account service. `tempgroup <account> <group> <duration>` / `tempgroup <account> clear` / `tempgroup list` используют постоянные ограниченные по времени role leases, а не переиспользуемый слот игрока. `sync [radius:1..16]` отправляет локальное состояние тайлов текущей сессии; `sync <player> [radius]` требует `vega.world.sync.others` дополнительно к `vega.world.sync`.

`displaylogs [count:1..20] [information|warning|error|critical]` (`vega.operations.logs`) выводит ограниченное число недавних записей без stack trace. `dump-reference-data [item|npc|buff|projectile|tile|all]` (`vega.world.reference`) экспортирует нативные символические ID в `data/reference/<kind>.json` текущего runtime, не затрагивая мир или данные персонажей.

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

Самостоятельные продуктовые домены подчиняются тому же правилу. `Vega.Regions` владеет полным application-срезом команд и ACL и напрямую использует Contracts; Persistence владеет SQL, а Terraria — адаптерами изменяемого игрового состояния. Новый доменный проект должен переносить вертикальный срез целиком и не использовать Core как service locator или транзитную зависимость. См. [аудит связности Core](../core-cohesion-audit.md) и ADR 0023.

Внешний горячо загружаемый плагин напрямую реализует `IHotReloadableVegaModule` и имеет одну публичную точку входа без параметров. Административная функция включается в `config/modules.json`, после чего watcher автоматически применяет create/change/delete/rename. Для явного управления есть `plugin list`, `plugin reload <name-or-dll>` и `plugin unload <name-or-dll>`. Команды и хуки плагина должны регистрироваться через переданный `IHotModuleContext`: хост снимает их до утилизации модуля и проверяет сборку изолированного load context. Ошибка очистки требует перезапуска. Для встроенных ревизий `IVegaModule` всегда нужен перезапуск.

## 10. Operations и cluster behavior

`Vega.Operations` предоставляет неизменяемые server/player/module/log/metric projections. Terminal UI является только потребителем; административные действия проходят через обычные command и authorization boundaries.

`Vega.Sandboxes` управляет изолированными процессами `Vega.Server` для дополнительных миров. Для жизненного цикла служат `sandbox create/start/stop/restart/reset/destroy`, а `worldtransfer` работает через доверенный Dimensions-forwarder. Descriptor, лимиты и TTL находятся в `config/sandboxes.json`; forwarding по умолчанию отключён в `config/security.json`. Accounts и bans остаются общесерверными, а команды и регионы получают явный `WorldId` backend-процесса. Подробности приведены в [руководстве по sandbox-мирам](sandboxes.md).

`Vega.Backups` создаёт согласованные deploy-local архивы мира и базы. В `config/backups.json` находятся только `enabled`, `interval` и `retainedBackups`; команда `backup` запускает ручной архив, а `backup status` показывает прогресс. Сервис использует world-save/`.vworld` barrier, provider-owned SQLite/`pg_dump`/`mysqldump` snapshot, очищает конфигурацию от секретов и атомарно публикует проверенный ZIP в `backups/`. См. [резервное копирование и восстановление](backups.md).

Встроенные модули можно пересоздать внутри процесса через выключенную по умолчанию границу `config/soft-restart.json`. `softrestart confirm` сохраняет Terraria, загруженный мир, подключённых игроков и БД, заменяя маршрутизируемое поколение модулей. Нативные/runtime-изменения и небезопасно выгруженный плагин всё равно требуют перезапуска процесса. См. [мягкий перезапуск](soft-restart.md).

Cluster support опционален. Текущий transport — in-memory reference implementation для детерминированных тестов и локальной композиции. Standalone server использует `UnavailableClusterClient` и остаётся работоспособным. Текущий ADR не выбирает production transport на Redis, NATS, RabbitMQ, HTTP или WebSocket.

## 11. Пакеты и релизы

Источником версии является `Directory.Build.props`. Для NuGet создаётся один пакет `Vega` со всеми публичными first-party assemblies; компонентные проекты отдельно не публикуются. `Vega.Runtime` по-прежнему поставляется как deployable server archive, а не NuGet executable.

`Vega` передаёт consumer-проектам явный opt-in Vega для MonoMod на `net11.0`, поэтому consumer build показывает то же предупреждение о неподдерживаемом runtime. Сохраняйте его видимым, пока dependency graph официально не поддержит .NET 11; подробности записаны в [.NET 11 dependency audit](../net11-dependency-audit.md).

Автоматический workflow `Release Tag` проверяет prerelease-тег, восстанавливает, собирает и тестирует solution, упаковывает единственный пакет `Vega` как validation artifact, затем создаёт или обновляет GitHub Release с серверным ZIP и checksums. Публикация в NuGet.org и требование credentials жёстко отключены до отдельного решения после завершения отладки.

Отдельный workflow `Publish Manual` выбирает последний корректный тег и только пересобирает его готовый к развёртыванию серверный архив и assets GitHub Release. Он намеренно не запускает тесты и публикацию в NuGet и не требует NuGet-секрета. Подробности: [процедура релиза](../releasing.md).

## 12. Текущие ограничения

- Dimensions forwarding и перенос игрока требуют явно доверенного proxy и по умолчанию отключены;
- Авторитетный SSC доступен по явной настройке; штатный клиентский цикл и перенос существующих данных остаются критериями выпуска;
- Vega предоставляет собственные whitelist, home и warp; совместимость со старой схемой данных TShock, команды tpa/clan и REST routes TShock не заявлены. Удалённое администрирование требует отдельного интерфейса;
- горячая замена поддерживает только unload-safe контракт Vega и выключена по умолчанию; legacy и встроенные DLL требуют перезапуска;
- для cluster-сценариев не выбран production transport;
- `.vworld` остаётся экспериментальным и выключен по умолчанию;
- single-process multi-world остаётся исследовательским backend; production sandbox использует отдельный процесс на активный мир.

[Roadmap](../roadmap.md) фиксирует завершённые этапы. [ADR](../decisions/) являются нормативными архитектурными решениями, но принятые future-facing требования не означают автоматически завершённую реализацию.

## 13. Документация и дисциплина изменений

Если изменение переносит владение, меняет threading/readiness semantics, добавляет публичный контракт или меняет package/runtime layout:

1. обновите код и сфокусированные тесты;
2. обновите английское и русское руководства;
3. обновите документ подсистемы или ADR;
4. отмечайте пункт roadmap завершённым только после реализации и проверки.

Оригинальный код Vega является проприетарным. Перед использованием или распространением исходников, бинарных файлов или NuGet-пакетов прочитайте [Vega Proprietary License](../../licenses/LICENSE). Сторонние материалы сохраняют собственные условия.
