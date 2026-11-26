# Изолированные sandbox-миры

Каждый активный sandbox Vega запускает как отдельный контролируемый процесс
`Vega.Server`. Глобальное состояние Terraria, game thread, entity и изменения мира
остаются внутри backend-процесса. Основной процесс управляет только descriptor,
файлами, lifecycle и периодическими immutable status. Backend не запускает дочерний
control plane.

## Конфигурация и данные

`config/sandboxes.json` задаёт лимиты допуска и отказов. По умолчанию разрешено восемь
активных backend, старты выполняются последовательно, readiness ожидается 60 секунд,
heartbeat считается устаревшим через 15 секунд, graceful shutdown получает 15 секунд,
а crash-loop ограничен тремя рестартами с паузой пять секунд. CPU, working set и размер
мира ограничены. Для временного мира idle TTL по умолчанию равен 30 минутам.

Descriptor атомарно сохраняются в `data/sandboxes/catalog.json`. Сгенерированный
256-битный `data/sandboxes/control.key` подписывает heartbeat и transfer ticket, поэтому
это секрет. Управляемые миры находятся в
`data/sandboxes/instances/<WorldId>/world.wld`. При создании/reset неизменяемый `.wld`
копируется во staging, сбрасывается на диск и атомарно подменяет instance. Исходный
template не меняется; неудачный reset сохраняет прежний instance и `.previous` backup.
Миры `external` Vega никогда не сбрасывает и не удаляет.

Descriptor содержит `WorldId`, имя, пути, режим temporary/persistent/external,
capacity, public/permission/allow-list access, idle TTL, SSC policy, набор встроенных
модулей, port, revision и hash template. Пустой набор модулей означает все встроенные.
Команды и регионы backend получают настоящий sandbox `WorldId`; accounts, bans и
базовые permissions используют общую БД, а регионы и policy модулей остаются
world-scoped.

Режимы SSC при transfer подключены явно. `Inherit` использует общий primary-scope аккаунта,
`CopyOnEnter` создаёт world-local копию и не перезаписывает primary baseline при возврате,
`Isolated` хранит отдельный character по `WorldId`, а `Disabled` оставляет character
клиенту. Межпроцессное хранилище использует file lock, revisions и transfer leases;
Terraria-адаптер сохраняет и восстанавливает inventory, экипировку, банки, loadout'ы,
life/mana, spawn, внешний вид и постоянные character-флаги. Revision conflict закрывается
fail-closed без перезаписи более нового состояния. Периодическое сохранение и transfer
привязаны к исходной игровой сессии. Финальный capture берётся до нативного сброса слота;
если это невозможно, сохраняется последний checkpoint. Полноценная SSC-авторитетность
при обычном входе в основной мир и проверка живым клиентом пока не завершены.

Отдельный экспериментальный режим `config/primary-character-persistence.json` (`enabled: false` по умолчанию) восстанавливает и сохраняет персонажа обычной авторизованной сессии в `data/primary-characters.json`. Первое состояние создаётся сервером из базового набора, без импорта клиентского инвентаря. Хранилище отделено от прежних transfer snapshots. Это основа persistence, а не полноценный SSC: сервер пока не запрещает gameplay до восстановления, не устанавливает нативный SSC flag и не связывает новое хранилище с переходом между мирами. Для production SSC режим пока не включать.

## Lifecycle и команды

```text
sandbox list
sandbox status <id>
sandbox create <id> <name> <template.wld> <temporary|persistent|external> <capacity> [disabled|inherit|copyonenter|isolated] [port]
sandbox start|stop|restart <id>
sandbox reset <id> [replacement.wld] confirm
sandbox destroy <id> confirm
sandbox rolling-update confirm
worldtransfer <точное-имя-игрока> <target-world>
```

Повтор create/start/stop идемпотентен. Reset и destroy требуют `confirm`. При остановке
сначала отправляется штатная команда `shutdown`; kill применяется только после
deadline. Readiness и heartbeat раз в две секунды подписаны HMAC и связаны с `WorldId`,
PID, instance, state и version. Потеря heartbeat, exit, превышение ресурсов и несовпадение
версии дают явный degraded/failed status. После рестарта control plane живой подписанный
PID подхватывается; orphan instance каталог показывается в логах и не удаляется.

Временный backend выключается после idle TTL. Persistent-файлы не удаляются, а
остановка control plane завершает процессы без удаления миров. Rolling update
перезапускает миры по одному и прекращается при первой ошибке readiness.

## Routing и безопасность

Перед transfer проверяются readiness, capacity и access, затем выдаётся 30-секундный
HMAC ticket, привязанный к account, origin, target, nonce и ticket ID. Dimensions
packet 67/type 3 передаёт только host и port, поэтому ticket клиенту не раскрывается:
source записывает короткоживущий rendezvous, связанный с trusted forwarded IP и
каноническим UUID. Принимающий sandbox после trusted type-0 и packet 68 обязан атомарно
поглотить rendezvous. Replay, expiry и неверный target закрываются fail-closed. Identity и bans общие для сервера.

Dimensions по умолчанию выключен в `config/security.json`. Packet 67/type 0 принимается
только от точного IP из `dimensionsTrustedProxies`; transport peer и effective client IP
хранятся отдельно. Подделка и битый payload отклоняются. `worldtransfer` отправляет
Dimensions packet 67/type 3 только соединению с trusted-forwarder provenance — прямой
клиент не получает привилегированную команду переключения.

## Operations и базовая производительность

Operations/TUI показывает lifecycle, endpoint, PID, players, timestamps, TPS, CPU,
working set, размер instance, restart count, revision template и TTL. stdout/stderr
backend попадает в bounded-логи родителя, а отдельный JSONL хранится в
`logs/sandboxes/<WorldId>/`.

Каждый backend запускается в собственном `data/sandboxes/instances/<WorldId>/runtime/` с отдельным каталогом `saves/`. Изменяемые конфиги и данные плагинов, шаблоны сундуков, warp и ограничения находятся в `runtime/data/` соответствующего мира. Исполняемые файлы и общие настройки остаются общими; база аккаунтов/банов и состояние transfer control plane разделяются намеренно. Нативные хуки открытия/закрытия сокета отвечают за время жизни метаданных соединения, а hook остановки мира снимает readiness. Кодировка управляющей консоли задана явно для штатной остановки Windows/Linux backend. TCP listener sandbox по умолчанию привязан к `127.0.0.1` (`backendBindAddress`); другой адрес можно указать при отдельном развёртывании доверенного proxy.

Whitelist и баны общие для всей инсталляции, включая уже запущенные backend. Один whitelist находится в корневом `data/whitelist.json` вместе с `data/whitelist.json.lock`, а не в `runtime/data` песочницы. Межпроцессные изменения сериализуются, новые входы читают последнюю записанную политику. Само изменение whitelist не отключает уже вошедших игроков. Баны используют общую БД и периодическую проверку существующих игровых сессий. Регионы, строительные ограничения, warps, контентные запреты и состояние плагинов остаются локальными для мира.

Регрессионный запуск:
`dotnet run --project benchmarks/Vega.Sandbox.Benchmarks -c Release -- 32 4`. На x64,
.NET 10.0.11 четыре тестовых backend и template 32 МиБ дали:

| Нагрузка | Результат |
| --- | ---: |
| materialization | 113,2 мс/мир |
| подписанный process readiness | 269,8 мс/процесс |
| Operations snapshot четырёх миров | 206 мкс/snapshot |
| graceful shutdown | 23,3 мс/процесс |

Это измерение control plane, а не симуляция Terraria. Отдельный smoke-тест опубликованной
сборки использовал настоящий мир Terraria 1.4.5.7 размером 4200x1200: контролируемый
backend достиг `WorldReady` за 3,804 с (загрузка Terraria — 2,062 с), открыл назначенный
порт, показал 536 МиБ working set через `sandbox status` и корректно завершился по
graceful-stop handshake. Перед увеличением лимитов нужно
проверить реальные `.wld`, NPC/player load, tick p95/p99, GC, disk I/O и transfer
latency на целевом сервере. Snapshot bounded и sampled; per-tick IPC и сериализации мира
нет. Много миров Terraria в одном процессе остаются отдельным research-проектом, а не
обещанием совместимости.
