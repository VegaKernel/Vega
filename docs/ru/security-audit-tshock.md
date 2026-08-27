# Аудит сетевой безопасности Vega относительно TShock

> Статус: документ ведётся параллельно с разработкой packet security в Vega.
>
> Базовая версия протокола: Terraria 1.4.5.7, protocol 325.  
> Эталон для сравнения: актуальная ветка `general-devel` репозитория `TerraZ-Team/TShock`.  
> Типизированный протокольный слой Vega: Multiplicity 2.6.3.

## Зачем нужен этот документ

TShock много лет накапливал проверки входящих Terraria-пакетов, в том числе проверки, появившиеся после реальных эксплойтов и некорректного поведения клиентов. Vega не является форком TShock и не переносит его API или обработчики один в один. Поэтому при построении собственного сетевого pipeline возникал риск потерять часть зрелых защит, даже если базовая обработка пакетов технически работала.

Этот документ фиксирует, какие классы рисков были обнаружены при сравнении Vega с TShock, как они закрыты в Vega и что ещё остаётся в работе. Формулировка «пробел Vega» здесь не означает уязвимость TShock. Напротив, TShock используется как зрелый reference implementation, а Vega пытается восстановить эквивалентную или более строгую границу доверия без наследования старой архитектуры.

Статусы:

- **[закрыто]** защита реализована и покрыта тестами или явным архитектурным инвариантом;
- **[частично]** основной риск закрыт, но остаются дополнительные проверки или перенос на новый контракт;
- **[в работе]** класс риска выявлен, реализация ещё продолжается.

## Сводка

| Класс риска | Как закрывает TShock | Старый пробел Vega | Защита Vega | Статус |
|---|---|---|---|---|
| Неизвестный новый packet ID | Имеет большой явный набор handlers и checks | `Other` мог фактически уйти в vanilla без осознанного решения | Полная ingress-матрица для каждого известного `PacketTypes`, `Unknown` ломает regression test | **[закрыто]** |
| Неверная фаза соединения | Разные handlers учитывают login state | Gameplay packet мог попасть в обработку до полной авторизации | `TerrariaInboundPacketGuard` разделяет metadata, handshake, early и gameplay | **[закрыто]** |
| Client injection серверных пакетов | Отбрасывает пакеты, которые клиент не должен отправлять | Часть packet IDs не имела явного ownership | `ServerOnly` disposition и fail-closed reject | **[закрыто]** |
| Лишний хвост payload | Парсеры TShock читают ожидаемую структуру пакета | Bounded parsing мог скрыть trailing bytes | Security ingress использует exact `TryParsePayload` Multiplicity 2.6.3 | **[закрыто]** |
| Drift схем протокола | Handler читает текущий wire layout Terraria | Несколько Multiplicity 2.6.2 views были устаревшими | Multiplicity 2.6.3 с исправленными protocol-325 layouts | **[закрыто]** |
| Ошибка security policy | В зрелом pipeline пакет обычно не должен продолжать путь после ошибки проверки | Exception в Vega ранее мог привести к vanilla continuation | Security-owned path работает fail-closed | **[закрыто]** |
| Подмена player identity | TShock часто заменяет client id на `whoAmI` либо проверяет владельца | Vega проверяла только часть sender-owned packets | Расширенный `PlayerPacketOwnershipPolicy` с учётом vanilla semantics | **[закрыто]** |
| NaN/Infinity и невозможные значения | Множество range checks в handlers | Не все typed packets проходили semantic validation | `PlayerActionSanityPolicy`, teleport/projectile/world sanity policies | **[закрыто]** |
| Reuse NPC slot | TShock работает с authoritative NPC state | Старый snapshot slot мог адресовать уже другого NPC | slot + type + generation, live generation validation | **[закрыто]** |
| Подделка portal teleport | Проверяет реальную пару порталов | Client мог заявить некорректный выход | `TerrariaPortalAuthorityPolicy` | **[закрыто]** |
| Projectile/build exploit | В TShock есть отдельные exploit guards | Аналогичная защита отсутствовала | `TerrariaProjectileExploitPolicy`, включая Cattiva `ai[0] == 3` | **[закрыто]** |
| Tile rectangle decompression abuse | TShock ограничивает и валидирует world mutations | Большой compressed rectangle мог быть дорогим до проверки | лимит площади до decompression + полный payload validation | **[закрыто]** |
| Packet flood | TShock содержит packet-specific spam protection | Vega имела rate limits только для нескольких пакетов | расширенный `PacketRateLimitPolicy` | **[частично]** |
| Наблюдаемость атак | TShock пишет diagnostic/admin logs | Ранние отказы Vega были хуже видны | security signals, metrics, structured operation logs | **[закрыто]** |
| Широкая server-side mutation surface | TShock исторически предоставляет плагинам `TSPlayer` и доступ к большому объёму mutable Terraria state | Команды Vega могли бы повторить этот путь прямыми обращениями к `Main.player`/`Main.npc` | Built-in commands и Plugin SDK используют узкие `IPlayerOperations`, world/NPC/inventory capabilities с game-thread dispatch | **[частично]** |

## 1. Явная ingress-матрица вместо неявного fallback

### Риск

Самый опасный архитектурный вариант для игрового протокола выглядит безобидно: появился новый packet ID, классификатор его не знает, поэтому пакет просто передаётся vanilla серверу. После обновления Terraria такой fallback автоматически создаёт новую неаудированную поверхность атаки.

Ранее в Vega часть пакетов могла фактически оказаться в категории `Other` и продолжить путь без явного security-решения.

### Решение Vega

Каждый известный packet protocol 325 теперь получает один из явных ingress disposition:

- `Metadata`;
- `Handshake`;
- `EarlyValidated`;
- `GameplayValidated`;
- `ServerOnly`;
- `AllowedByDesign`;
- `Unknown`.

`Unknown` существует специально как сигнал ошибки аудита, а не как разрешение. Regression test перечисляет актуальный `PacketTypes` и требует, чтобы каждый известный ID был классифицирован.

Итог: после добавления packet 165 или другого нового ID обновление Multiplicity/Terraria должно заставить разработчика принять явное решение о trust semantics. Новый пакет больше не должен становиться доверенным по умолчанию.

**Статус: [закрыто].**

## 2. Валидация фазы соединения и handshake

### Риск

Не все пакеты допустимы во всех состояниях подключения. Некоторые относятся только к установлению сессии, часть допустима и во время join, и после него, а gameplay должен начинаться только после перехода соединения в состояние fully connected.

Проверять только payload недостаточно. Структурно корректный пакет, отправленный не в той фазе, всё ещё может нарушать state machine сервера.

### Решение Vega

`TerrariaInboundPacketGuard` получает текущий `TerrariaConnectionState` и применяет фазовые правила до обычного gameplay pipeline.

Отдельно выделены:

- connection metadata;
- handshake packets;
- early packets, например `PlayerInfo`, `PlayerSlot`, `TileGetSection`, `PlayerSpawn`, `RequestSection`;
- gameplay packets.

Gameplay до полной установки соединения отклоняется. Для ранних пакетов выполняется собственная структурная и range validation.

Для некорректного pre-login трафика Vega может не только отменить пакет, но и завершить соединение, чтобы повреждённая handshake state не продолжала жить внутри Terraria.

**Статус: [закрыто].**

## 3. Блокировка server-only пакетов от клиента

### Риск

Terraria использует общий набор packet IDs для обоих направлений. Само наличие валидной схемы не означает, что клиент имеет право отправить такой пакет серверу.

При сравнении с актуальным TShock были отдельно подтверждены server-origin или недопустимые client-origin пакеты, включая, среди прочего:

- `NpcItemStrike`;
- `NumberOfAnglerQuestsCompleted`;
- `NpcTeleportPortal`;
- `SyncCavernMonsterType`;
- `DamageNPCAck`;
- `DebugCommands`;
- ряд world/NPC synchronization сообщений.

### Решение Vega

Такие ID получают `PacketIngressDisposition.ServerOnly`. Попытка прислать их с клиента считается protocol violation и пакет не доходит до vanilla обработки.

Это важнее обычной semantic validation. Сервер не пытается придумать допустимые значения полей пакета, который клиент вообще не имеет права создавать.

**Статус: [закрыто].**

## 4. Exact payload parsing и trailing bytes

### Риск

Bounded parser полезен, когда API предоставляет окно буфера, внутри которого может находиться пакет и последующие данные. Но на security boundary `args.Length` уже обозначает конкретный payload текущего Terraria сообщения.

Если ожидать 5 байт, принять первые 5 и проигнорировать шестой, malformed packet становится формально валидным. Особенно плохо это в сочетании с изменениями wire layout: старый parser может прочитать старую часть новой структуры и молча не заметить хвост.

### Решение Vega

После выпуска Multiplicity 2.6.3 security ingress использует `PacketViewParser.TryParsePayload`, который требует, чтобы число реально разобранных байт совпадало со всей длиной payload.

Сценарий:

```text
ожидается 5 байт
5 байт  -> parse
4 байта -> reject
6 байт  -> reject
```

`TryParsePayloadBounded` остаётся допустимым инструментом для hook/API, где передаётся именно верхняя граница окна, но не используется как доверительная модель exact packet ingress.

**Статус: [закрыто].**

## 5. Drift wire schema и аудит Multiplicity 2.6.3

Во время сравнения Vega с актуальными TShock handlers выяснилось, что Multiplicity 2.6.2 заявлял protocol 325, но несколько packet models всё ещё соответствовали более старому layout.

Были обнаружены и исправлены:

### `PlayerSpawn`

Старая модель читала только player id и spawn coordinates. В Terraria 1.4.5.7 payload также содержит respawn timer, PvE/PvP death counters, team и spawn context. Актуальная длина protocol-325 payload: 15 байт.

### `TileGetSection`

Старая модель читала координаты, но пропускала `team`. Актуальный payload содержит дополнительный byte и имеет длину 9 байт.

### `NpcAddBuff`

Buff ID был описан как `byte`, хотя актуальный протокол использует `UInt16`. Это не только ограничивало ID значением 255, но и сдвигало чтение следующего поля времени.

### `PlayerAddBuff`

Та же проблема: buff ID в protocol 325 является `UInt16`.

### `PlayerBuff`

Старая модель предполагала фиксированный массив однобайтовых buff IDs. В 1.4.5.7 IDs являются `UInt16`. В Multiplicity 2.6.3 пакет моделируется как variable-length `ushort[]`, длина определяется фактическим payload, а не захардкоженным старым числом слотов.

### `LandGolfBallInCup`

Wire size был корректным, но имена последних полей не соответствовали актуальной семантике. Они обозначают hits и projectile type. Старые имена оставлены compatibility aliases, а новые API names отражают реальный протокол.

До публикации 2.6.3 Vega временно имела узкий compatibility path только для доказанно устаревших layouts. После публикации 2.6.3 этот дублирующий слой удалён: описание wire protocol снова принадлежит Multiplicity, а Vega занимается security policy.

**Статус: [закрыто].**

## 6. Fail-open exception в security pipeline

### Старое поведение

На раннем этапе Vega исключение внутри security policy логировалось, после чего пакет мог продолжить vanilla processing. С точки зрения эксплуатации это означает: ошибка или неучтённый edge case в защитном коде потенциально превращались в bypass.

```text
security policy -> exception -> log -> vanilla
```

### Новое поведение

Security-owned обработка работает fail-closed:

```text
security policy -> exception -> Violation -> Cancel
```

Ошибка защитного слоя теперь ухудшает доступность конкретного пакета/соединения, но не понижает уровень доверия.

Для pre-login нарушений соединение дополнительно может быть завершено.

**Статус: [закрыто].**

## 7. Player identity и ownership

### Риск

Многие Terraria packets содержат player slot. Наивная проверка `packet.PlayerId == connection.whoAmI` тоже может быть неправильной: vanilla/TShock в некоторых handlers сознательно игнорируют присланный ID и заменяют его authoritative `whoAmI`.

Поэтому есть две ошибки, которых надо избегать одновременно:

1. доверять client-supplied player id там, где он определяет владельца действия;
2. объявлять читом любой mismatch там, где vanilla всё равно заменяет поле на sender slot.

### Решение Vega

`PlayerPacketOwnershipPolicy` расширен на основные sender-owned families: player state, zones, animation, PvP/team state, buffs, dodge/stealth, harp/cannon, wiring, minion targets, emoji, golf, loadout, chest/tile-entity player fields и другие пакеты.

При этом правила сверялись с актуальным TShock и vanilla semantics. Там, где ID является недоверенным декоративным полем и движок заменяет его `whoAmI`, Vega не создаёт ложный exploit detector только ради формальной строгости.

**Статус: [закрыто], дальнейшее расширение будет происходить вместе с новыми packet IDs.**

## 8. Проверка числовых значений, NaN/Infinity и registry bounds

### Риск

`float` может быть NaN или Infinity, а integer ID может быть структурно корректным, но выходить за размеры runtime array/registry. Если такие значения доходят до Terraria, последствия варьируются от некорректного состояния до исключений или дорогой обработки.

### Решение Vega

Semantic policies проверяют:

- finite coordinates и velocity;
- world bounds;
- player/NPC/projectile/item indices;
- item types и prefixes;
- buff IDs;
- loadout indices;
- team values;
- tile/wall/color registries;
- selected inventory slots;
- другие enum/range invariants.

Принцип: protocol parser отвечает за структуру байтов, policy отвечает за допустимость значения в текущем runtime.

**Статус: [закрыто] для текущего audited набора.**

## 9. NPC slot reuse, generation и `DamageNPCAck`

### Риск stale slot

NPC в Terraria адресуется индексом массива, но slot переиспользуется. Клиент может иметь устаревшее представление: NPC в slot 37 уже умер, а сервер успел создать там другого NPC. Одна проверка диапазона `0 <= slot < Main.npc.Length` этого не обнаруживает.

### Решение Vega

В Vega NPC handles включают не только slot, но и generation/type/revision information. Для сетевого `NpcStrike` `TerrariaNpcGenerationPolicy` сверяет generation пакета с живым authoritative NPC state.

Stale generation отклоняется как устаревшее действие и не применяется к новому NPC, который случайно занял тот же slot.

### ACK при отклонении strike

Сравнение с актуальным TShock показало ещё один важный protocol detail. Если server-side hook поглотил `NpcStrike`, vanilla уже не отправит ожидаемое клиентом подтверждение. Поэтому при отклонённом strike Vega отправляет `DamageNPCAck`, чтобы security cancellation не ломал сетевую state клиента.

**Статус: [закрыто].**

## 10. Portal teleport authority

### Риск

Недостаточно проверить, что координаты телепорта находятся внутри мира. Клиент не должен самостоятельно объявлять произвольную точку выходом Portal Gun.

### Решение Vega

`TerrariaPortalAuthorityPolicy` использует authoritative server state:

- проверяет owner/side;
- находит реальную пару `PortalGunGate`;
- убеждается, что заявленная новая позиция действительно находится около допустимого выхода;
- отклоняет forged portal transitions.

Это пример проверки, которую нельзя корректно реализовать только в pure Core policy, потому что она зависит от живого Terraria state.

**Статус: [закрыто].**

## 11. Projectile ownership и Cattiva exploit

При сравнении с TShock была перенесена защита от известного Cattiva projectile sync сценария, где определённое состояние `ai[0] == 3` позволяло использовать projectile update для обхода build permission logic.

Vega блокирует этот случай отдельной `TerrariaProjectileExploitPolicy`, а не прячет magic ID внутри общего range validator.

Кроме того, projectile policies проверяют sender/owner attribution, indices, type registry, finite position/velocity и связанные combat invariants.

**Статус: [закрыто] для известных audited сценариев.**

## 12. World, tile, chest и tile-entity mutations

### Риск

Пакеты изменения мира имеют высокую authority: неправильная координата, forged chest state или несуществующая tile entity не должны попадать напрямую в vanilla mutation path.

### Решение Vega

Текущий pipeline проверяет:

- tile coordinates и tile types;
- liquid/paint/object/door operations;
- chest coordinates, active chest ownership, item slot и resize bounds;
- signs;
- tile entities и их authoritative coordinates;
- display dolls/hat racks/food platters/leashed anchors;
- mass wire endpoints;
- switch/cannon/weapon-rack operations;
- region/build authorization там, где это относится к gameplay policy.

Важно, что bounds validation и authorization разделены. Пакет с невозможной структурой отклоняется как protocol/security issue независимо от того, имеет ли игрок permission на строительство.

**Статус: [закрыто] для текущего protocol-325 набора, новые Terraria operations должны добавляться через ingress audit.**

## 13. Compressed `TileSendSquare` и DoS до decompression

### Риск

Даже корректно ограниченные координаты не защищают от CPU/memory abuse, если сервер сначала распаковывает огромный rectangle, а только потом решает, допустима ли операция.

### Решение Vega

Для client-authored tile rectangle введена двухступенчатая защита:

1. до decompression проверяется размер области, максимальная площадь одного rectangle ограничена 4096 tile cells;
2. после этого payload полностью декодируется и проверяются tile/wall/color и связанные значения;
3. parser обязан полностью потребить содержимое rectangle, malformed хвост не игнорируется.

Таким образом дорогое действие имеет дешёвый precondition guard.

**Статус: [закрыто].**

## 14. Packet-specific rate limits

Изначально Vega имела measured rate limits только для небольшого числа пакетов. Этого недостаточно: пакет может быть полностью валиден по структуре, но дорог при тысячах вызовов в секунду.

`PacketRateLimitPolicy` расширен на наиболее значимые категории:

- NPC interactions/strikes;
- teleport/portal operations;
- world events;
- tile/tile-entity operations;
- wiring;
- другие дорогие или authority-bearing действия.

Пределы намеренно не используются как античит скорости игры. Например, combat traffic имеет достаточно высокий потолок, чтобы обычный быстрый gameplay не становился collateral damage. Цель rate limit здесь: containment flood/DoS, а не наказание макросов.

**Статус: [частично].** Пороговые значения должны продолжать подтверждаться production telemetry, а новые дорогие packet classes добавляться по мере наблюдения.

## 15. Pre-login rejection и termination

Malformed handshake нельзя обрабатывать так же, как ошибочную gameplay-команду уже подключённого игрока. После нарушения state machine безопаснее закрыть недоверенную сессию, чем оставить её в промежуточном состоянии.

Vega различает эти случаи. Для reject/violation до `FullyConnected` hook может выполнить `BootPlayer`; если отправка disconnect сама завершается ошибкой, соединение помечается для termination.

После полной авторизации обычный malformed gameplay packet отменяется без автоматического disconnect, если конкретная policy не требует более жёсткой реакции.

**Статус: [закрыто].**

## 16. Security logging, metrics и signals

Отказ пакета полезен только для защиты текущего состояния. Для расследования атаки нужно понимать, какой packet, откуда и по какому правилу был отклонён.

Vega при security rejection:

- записывает `PacketDecisionKind` и reason;
- обновляет network/security metrics;
- создаёт bounded `SecuritySignal`;
- сохраняет transport/effective endpoint и forwarding provenance через отдельную metadata boundary;
- пишет structured operation log;
- не выводит чувствительные connection details через обычный player snapshot без отдельного доступа.

Этот слой также нужен будущему Vega Tools MCP: он сможет показывать причины rejects и packet rates через поддерживаемые diagnostics API, а не читать внутренние коллекции сервера напрямую.

**Статус: [закрыто] для базовой наблюдаемости.**

## 17. Что намеренно отличается от TShock

Vega не пытается воспроизвести TShock handler architecture один в один.

### Нет giant handler registry как публичной модели

Wire parsing вынесен в Multiplicity, protocol trust boundary живёт в `Vega.Terraria`, а business/authorization policies могут жить в Core или отдельных модулях.

### Не каждый mismatch player id считается читом

Если vanilla/TShock не доверяет полю и всё равно заменяет его на `whoAmI`, Vega не обязана кикать нормального клиента за то, что protocol исторически содержит недоверенное поле.

### Protocol safety отделяется от anti-cheat

NaN, out-of-range index, forged owner и server-only injection являются protocol/security проблемами. «Игрок слишком быстро ударил» уже относится к gameplay anti-cheat и не должен смешиваться с wire validation без отдельной модели и telemetry.

### Fail-closed применяется только к security-owned path

Не каждый внутренний exception Vega автоматически означает disconnect. Жёсткий fail-closed нужен там, где код уже взял на себя ответственность решить, можно ли доверять входящему packet.

## 18. Оставшаяся работа

На текущем этапе основные пробелы trust boundary относительно audited TShock handlers закрыты, но работа не считается раз и навсегда законченной.

### [в работе] Production tuning rate limits

Нужны реальные распределения packet rates на живом сервере, особенно для массовых событий, wiring и интенсивного combat.

### [в работе] Новые Terraria / Multiplicity packet IDs

Любое обновление протокола должно сопровождаться:

1. обновлением Multiplicity schema;
2. явной ingress classification;
3. решением об ownership/server-only semantics;
4. semantic tests;
5. обновлением этого документа, если появляется новый класс риска.

### [частично] Полная таблица соответствия TShock handlers

Vega уже классифицирует все protocol-325 packet IDs, но отдельная человекочитаемая таблица «TShock handler -> Vega policy -> test» ещё может быть расширена. Это полезно как audit ledger, хотя runtime safety от неё не зависит.

### [частично] Командный административный surface

Расширение серверных команд идёт параллельно и использует те же Vega capabilities, что Plugin SDK. Уже перенесены world/player/NPC операции, включая teleport, spawn, heal, kill, buff и respawn. Следующие команды, особенно `give/item`, не должны обходить revision-aware inventory contracts прямой записью в `Terraria.Player.inventory`.

## 19. Административные мутации через capability boundary

### Почему это относится к security

Это не сетевой exploit в обычном смысле. Речь о server-side blast radius: чем больше mutable runtime state доступно встроенной команде или плагину, тем проще ошибке, уязвимому плагину или плохо проверенному MCP-tool нарушить инварианты Terraria в обход общей модели доступа.

Исторически TShock строился вокруг очень удобного, но широкого объекта `TSPlayer`. Плагины также часто имеют прямой доступ к `TPlayer`, `Main.player`, `Main.npc` и другим runtime структурам. Это удобно для экосистемы, но делает границу между «поддерживаемой операцией» и «произвольной мутацией процесса» размытой.

### Модель Vega

Vega намеренно не выдаёт обычному Plugin SDK объект Terraria player. Вместо этого разделены:

- immutable `PlayerSnapshot` для чтения;
- `IPlayerOperations` для целевых действий;
- `IPlayerInventoryService`/plugin inventory facade для revision-aware изменения inventory;
- `IWorldNpcService` с handle + revision для NPC;
- world/environment contracts;
- отдельный DEBUG API, который выключен по умолчанию и имеет явные unsafe capabilities.

Встроенные команды следуют той же модели. Например:

```text
heal command   -> IPlayerOperations.HealAsync    -> game-thread dispatch -> Terraria.Player.Heal
kill command   -> IPlayerOperations.KillAsync    -> game-thread dispatch -> Terraria.Player.KillMe
buff command   -> IPlayerOperations.ApplyBuffAsync -> validation -> game-thread dispatch
respawn command -> IPlayerOperations.RespawnAsync -> game-thread dispatch -> normal Terraria respawn path
```

Command handler отвечает только за permission, разрешение target и проверку пользовательских аргументов. Он не получает права самостоятельно менять `statLife`, `dead`, buff arrays или `respawnTimer` из произвольного потока.

Тот же `IPlayerOperations` прокинут через `IPluginPlayers`, поэтому расширение Plugin SDK не создаёт вторую, менее безопасную реализацию этих действий.

### Что это закрывает

- mutation из неправильного потока;
- размножение прямых обращений к `Main.player[]` по handlers/plugins;
- расхождение семантики одной операции между console command, plugin и будущим MCP;
- отсутствие единообразной validation/error model;
- более широкий ущерб от ошибки обычного плагина, которому для одной операции пришлось бы дать доступ ко всему player object graph.

Это не является полноценной sandbox-изоляцией плагина: загруженный managed code внутри процесса в принципе остаётся доверенным кодом процесса. Но поддерживаемый API больше не поощряет прямую мутацию Terraria как нормальный способ расширения сервера.

**Статус: [частично].** Основной player/world/NPC capability boundary уже действует. Аудит продолжается для inventory/item выдачи, будущих boss/event operations и остальных административных команд.

## Инварианты, которые нельзя потерять при будущих изменениях

1. Новый packet ID не получает implicit allow.
2. Client-origin server-only packet не доходит до vanilla.
3. Security parser не принимает trailing bytes текущего exact payload.
4. Ошибка security policy не превращается в allow.
5. Client ID не считается authoritative только потому, что он присутствует в payload.
6. World/NPC/projectile indices проверяются до обращения к runtime arrays.
7. Дорогой compressed payload имеет дешёвый size/range guard до decompression.
8. Live-state проверки выполняются только там, где pure protocol validation недостаточно.
9. Любое изменение wire layout сначала исправляется в Multiplicity, а не дублируется постоянным compatibility parser в Vega.
10. Security rejection остаётся наблюдаемым через reason, metrics и bounded signals.
11. Built-in command, Plugin SDK и MCP не должны получать отдельные обходные пути к mutable Terraria state, если операция уже выражается узким Vega capability.
12. Terraria-dependent service constructors не должны инициировать `Terraria.Main` до bootstrap/save-path initialization.

---

Документ обновляется вместе с PR #39 и последующими изменениями packet security. Если реализация и этот текст расходятся, источником истины является код и regression tests, а документ должен быть исправлен в том же изменении.
