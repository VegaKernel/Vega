# Vega / TShock: локальное сравнение 2026-11-26

## Среда и границы измерения

- AMD Ryzen 9 5950X, 16 физических ядер / 32 логических потока, 63,9 GiB RAM.
- Windows 10, build 19045. Vega: Terraria 1.4.6.1 через TerrariaRuntime 1.1.3, .NET 11.0.0.
- Актуальный upstream [TShock 6.2.1](https://github.com/Pryaxis/TShock/releases/tag/v6.2.1) заявляет Terraria 1.4.5.8; это не идентичная версия игрового движка. Сравнение серверных результатов ниже не изолирует вклад framework от версии ванильного движка и .NET.
- Нет готового проверенного headless-клиентского стенда для множества полноценных игровых сессий. Сетевой микробенчмарк не измеряет реальный TPS многопользовательского Terraria-сервера.

## Сетевой транспорт: штатный TCP против Ordered TCP

Воспроизводимый [benchmark](../benchmarks/Vega.NativeTcp.Benchmarks/Program.cs) запускается локально:

```sh
dotnet run -c Release --project benchmarks/Vega.NativeTcp.Benchmarks/Vega.NativeTcp.Benchmarks.csproj
```

Это две реализации **одного и того же TerrariaRuntime 1.1.3**: нативный `TcpSocket` и `NativeTcpTransportHost`; не TShock API. Проверены 1/8/32 локальных TCP-получателя, по 32 обычных отправки на получателя на условный тик 60 Hz, чередующиеся размеры 64/256/1024/4096 байт, 15 тиков прогрева и 120 измеряемых тиков; порядок прогонов ABBA (ванильный, Vega, Vega, ванильный). Все прогоны проверяют полученные байты и количество завершений. CPU и allocations включают локальных получателей в том же процессе; sampled peaks не являются точными глобальными максимумами.

| Получатели | Метрика | Штатный TCP (два прогона) | Ordered TCP (два прогона) |
| ---: | --- | ---: | ---: |
| 1 | МБ/с, MiB | 2.48–2.49 | 2.48–2.49 |
| 1 | p95 постановки отправок, мс/тик | 0.321–0.348 | 0.030–0.034 |
| 8 | МБ/с, MiB | 19.84–19.92 | 19.84–19.90 |
| 8 | p95 постановки отправок, мс/тик | 2.280–2.661 | 0.254–0.261 |
| 8 | аллокации, B/отправку | 209.5 | 19.5–19.6 |
| 32 | МБ/с, MiB | 79.61–79.66 | 78.21–79.40 |
| 32 | p95 постановки отправок, мс/тик | 9.256–9.405 | 1.978–2.027 |
| 32 | CPU time, с | 6.438–6.688 | 4.078–5.484 |
| 32 | аллокации, B/отправку | 208.3 | 289.3–359.8 |
| 32 | p95 enqueue → callback, мс | 0.010 | 29.441–29.639 |
| 32 | p95 game-thread completion pump, мс/тик | — | 0.400–0.440 |

32 получателя: 122 880 отправок, 159,38 MiB за измерение (~61 тыс. запросов отправки/с, фиксированная предложенная нагрузка, не максимальная пропускная способность). У Ordered TCP sampled peak 1024 outstanding entries, 712 704–821 824 bytes retained, 16–23 in-flight requests, 1024 pending completions. Все 12 прогонов завершились с правильным объёмом данных и количеством callback.

Среднее p95 постановки отправок уменьшилось с 9,33 до 2,00 мс (в 4,66 раза), агрегированное CPU time — примерно на 27% на этом коротком профиле. Пропускная способность сопоставима, но при 32 получателях аллокации выросли примерно на 56%; native callback намеренно переносится в ограниченный pump игрового потока, поэтому задержка callback существенно выросла. Это **не** задержка доставки игрового пакета клиенту и не доказательство роста TPS. Нужна оптимизация пакетирования смежных отправок, профилирование allocations и измерение wire→callback и реального игрового тика до активации транспорта по умолчанию.

Исходные 12 строк измерений сохранены локально в `artifacts/performance-2026-11-26/native-tcp-abba.txt`.

Контрольный повтор уже через репозиторный Release-проект с обычным `ProjectReference` и выключенным batch повторил результат: на 32 получателях p95 постановки 9.175–9.257 мс у vanilla и 1.800–1.914 мс у Ordered; throughput 79.36–79.68 против 79.41–79.62 MiB/s; CPU 5.828–6.734 против 4.062–4.531 с; allocations 208.3 против 346.8–356.8 B/request. Все 12 прогонов подтвердили объём данных и callback count. Файл `native-tcp-repo-confirmation.txt` в той же папке результатов.

### Проверка объединения соседних отправок

Добавлен экспериментальный batch (до 16 запросов и 32 KiB, с учётом собственного арендованного буфера в обоих бюджетах); совместимость FIFO, частичных записей и отдельных callback проверяется тестами. Повтор того же сетевого профиля с `--batch`, ABBA для 8 и 32 получателей:

| Получатели | Метрика | Batch 1 | Batch 16 |
| ---: | --- | ---: | ---: |
| 8 | Аллокации, B/отправку | 17.8–18.0 | 84.9–101.5 |
| 8 | p95 постановки, мс | 0.207–0.213 | 0.338–0.342 |
| 32 | CPU time, с | 4.891–7.938 | 4.438–5.766 |
| 32 | Аллокации, B/отправку | 338.8–352.9 | 425.8–428.3 |
| 32 | p95 постановки, мс | 2.054–4.019 | 2.472–2.797 |
| 32 | callback p95, мс | 28.989–29.622 | 29.387–29.487 |

Все 8 прогонов прошли проверку байтов и количества callback; throughput остался около 19,8 / 79 MiB/s. На 32 получателях CPU time в коротком шумном измерении уменьшился, но при обоих размерах нагрузки дополнительные аллокации ухудшились и callback latency не улучшилась. Поэтому `MaxBatchRequests=1` остаётся default, batch 16 доступен только в экспериментальном benchmark. Сырые результаты: `artifacts/performance-2026-11-26/native-tcp-batch-abba.txt`.

### Следующая итерация: пробуждение writer только по необходимости

Сравнение `--wake` (ABBA, тот же фиксированный loopback workload, 32/8 получателей, batch=1) заменяет подсчёт semaphore-сигнала на каждую отправку одним пробуждением ожидающего writer. Подтверждены порядок, отдельные completion metadata, частичные записи, отмена, ограничения памяти и пробуждение при `Complete`. На 32 TCP-получателях:

| Метрика | Counted wake | Edge wake |
| --- | ---: | ---: |
| Аллокации, B/отправку | 349–456 | 247–303 |
| p95 постановки отправок, мс | 2.020–4.357 | 1.855–1.953 |
| CPU time, с | 3.344–6.031 | 2.031–5.469 |
| Пропускная способность, MiB/s | около 79 | около 79 |

Все 8 прогонов подтвердили переданные байты и количество callback. CPU time шумный, callback p95 остаётся около 29–30 мс при периодическом pump. Edge wake включён внутри экспериментального Ordered TCP; vanilla остаётся общим default. Сырые результаты: `edge-results.txt` и `edge-micro.txt` в прежней папке измерений. Uncontended semaphore Release/Wait(0) приблизительно 49–51 нс, shared-budget reserve/release приблизительно 29–65 нс; последовательный loopback SendAsync 256 B около 5.3 µs без конкурирующих запросов. Это микрозамеры, а не доказательство TPS.

## Реальные процессы серверов: повторные полные сохранения мира

Использован официальный [TShock 6.2.1 / Terraria 1.4.5.8, Windows x64](https://github.com/Pryaxis/TShock/releases/tag/v6.2.1), запуск .NET 9.0.22; Vega — текущий Release build, Terraria 1.4.6.1 / .NET 11.0.0. Без сторонних плагинов и без подключённых клиентов. Для каждого прогона — отдельная инсталляция и копия medium `.wld` одного и того же исходного файла, 6 847 421 байт, SHA-256 `5F29C240EDAD629AA9A8FA087D10223729B74FC49ABE5300465373CBA7F3C12F`. TShock и Vega запускаются по очереди только на loopback, `-maxplayers 16`; измеряется readiness (native listener + сообщение старта), 10 секунд idle, одно прогревочное сохранение и затем 10 или 30 последовательных команд `save`. Каждая команда засчитывается только после изменения mtime `.wld` и сигнала завершения сохранения, а не постановки в очередь. RSS — sampled Windows working set, не размер managed heap; результаты коротких прогонов не являются доказательством memory leak.

| Нагрузка | Метрика | TShock 6.2.1 | Vega |
| --- | --- | ---: | ---: |
| 10 сохранений | Суммарное wall time, с | 3.560 | **3.061** |
| 10 сохранений | CPU time, с | 3.531 | **3.062** |
| 10 сохранений | Median / p95 одного сохранения, мс | 360.1 / 362.4 | **307.4 / 334.3** |
| 10 сохранений | Median / sampled peak RSS, MiB | **544.58 / 675.48** | 902.30 / 1033.35 |
| 30 сохранений | Старт до readiness, с | **4.985** | 5.735 |
| 30 сохранений | Idle CPU за 10 с, CPU-s | **0.188** | 0.641 |
| 30 сохранений | Idle median RSS, MiB | **400.68** | 741.35 |
| 30 сохранений | Суммарное wall time, с | 10.861 | **9.246** |
| 30 сохранений | CPU time, с | 10.844 | **9.344** |
| 30 сохранений | Median / p95 одного сохранения, мс | 360.9 / 363.6 | **308.4 / 333.6** |
| 30 сохранений | Median / sampled peak RSS, MiB | **798.41 / 1191.16** | 1163.57 / 1557.42 |

В повторе из 30 сохранений Vega завершил работу на 14,9% быстрее (throughput +17,5%; CPU time −13,8%). TShock стартовал примерно на 15% быстрее Vega и использовал заметно меньше памяти: у Vega +85% idle median RSS и +45,7% median RSS на серии сохранений. Следующий приоритет — профилирование загрузки плиток, save snapshot и GC, прежде чем менять GC-режим или число потоков. Разный движок/.NET не позволяют однозначно приписать результат Vega или TShock как framework.

Это **реальная серверная нагрузка на сохранение мира**, но не нагрузка от сотен игроков. В idle Terraria пропускает обычный игровой update; вывод `/tps` Vega при отсутствии клиентов отражает служебные тики и не является сравнительным TPS. Для рейтинга под игровой нагрузкой нужны совместимые версии движка и полноценные обычные игровые клиенты.

Результаты, stdout/stderr и логи завершения сохранений: `artifacts/performance-2026-11-26/world-10-saves/` и `world-30-saves/`. В той же папке лежат `world-save-benchmark.py` и отдельная копия исходного `input.wld`. Скрипт ожидает подпапки `tshock` и `vega` с тестовыми инсталляциями; запускается на Windows из этой папки с `BENCH_SAVE_COUNT=10` или `30` (в PowerShell `$env:BENCH_SAVE_COUNT='30'; python .\world-save-benchmark.py vega tshock`). Не использовать production world или production directories.

## TerrariaRuntime: unobserved hook fast path

У сгенерированных wrappers обнаружен корень больших transient allocations: даже при отсутствии подписчиков создавались `OriginalMethodDelegate` и `EventArgs`. Патчер теперь сначала читает один snapshot подписчиков; если он пуст, сразу вызывает оригинальный метод с исходными аргументами. При подписчиках сохраняются существующий публичный Invoke API, порядок обработчиков, изменяемые аргументы, отмена, ref/out и результат. Smoke `TerrariaRuntime/tests/HookEmitterSmoke` непосредственно проверяет сгенерированные методы.

Микротест 1 млн вызовов `Projectile.UnpackTargetIndex` без подписчиков: 96 000 000 B / 22.37 ms → 0 B / 1.93 ms (одинаковый checksum). Это измерение одного метода, не скорость всего сервера.

Нативный A/B на том же medium `.wld`, копии одной сборки Vega и разные TerrariaRuntime DLL (baseline 1.1.3+050dce3; optimized 1.1.3+590d815 с новым fast path; промежуточное изменение lifecycle hook не задействовалось, клиентов нет):

| Метрика | До | После |
| --- | ---: | ---: |
| Allocated bytes при загрузке | 7 721 653 344 | **568 394 392** |
| GC Gen0 / Gen1 / Gen2 | 453 / 47 / 2 | **26 / 26 / 2** |
| Загрузка мира (world-image telemetry), ms | 3741.6 | **3081.6** |
| Старт до NetworkReady, s | 6.217 | **5.423** |
| 10 сохранений, s | 3.131 | 3.106 |
| Idle median RSS, MiB | 741.10 | 744.29 |
| Save median RSS, MiB | 902.54 | 908.94 |

Transient allocations −92.6%, world load time −17.6%, readiness −12.8%; постоянный working set существенно не уменьшился. Исходные результаты: `artifacts/performance-2026-11-26/hook-fastpath/`. Отдельная причина churn при сохранении — штатный `InternalSaveWorld`: `MemoryStream`, копия через `ToArray()`, чтение прежнего файла для rollback, чтение сохранённого файла для проверки. Потенциальный следующий шаг — исключить только лишнюю копию при сохранении с ограничением `Count`, сохранить rollback и валидацию файла; требуется проверка byte-level сохранений для разных размеров миров.

### Повторная проверка интегрированной сборки 1.1.4

`edge-integrated-results.txt`: ещё один ABBA на актуальном Vega + TerrariaRuntime 1.1.4 подтвердил корректные байты и callback каждого из 8 прогонов. На 32 получателях counted wake: 250.0–287.7 B/запрос, p95 producer 1.992–4.613 ms; edge: 289.4–331.6 B/запрос, 2.261–2.300 ms. Около 78–80 MiB/s в обоих вариантах. В повторе выигрыш по аллокациям не воспроизвёлся: результат зависит от расписания writer и socket I/O. Edge устраняет лишние semaphore-операции конструктивно, остаётся экспериментальным, но его нельзя считать доказанным ускорением во всех прогонах.

`packet-metadata-abba.json`: четыре прогона по 1 024 000 PlayerUpdate, 200 слотов, окно 14 B в 128 KiB receive buffer. С наблюдением метаданных и без него: одинаковые 196 640 776 B суммарно (192.032 B/пакет), 11 Gen0 и 0 Gen1/Gen2. Оценка не включает сеть, vanilla simulation и весь набор политик. Наблюдение устойчивых метаданных не добавляет измеримых аллокаций к проверке ownership; оставшиеся аллокации требует исследовать отдельно.

Release Vega с локально сгенерированным TerrariaRuntime 1.1.4: сборка успешна, 32 architecture + 608 runtime тестов прошли, два интеграционных теста внешних СУБД пропущены. Native sandbox smoke подтверждает готовность двух процессов, локальную политику, общий admission storage и graceful shutdown. Пакет `artifacts/package-verified/Vega.1.0.0.nupkg` содержит 20 DLL и зависимость TerrariaRuntime 1.1.4. Сам Vega не опубликован.
Дополнительный native smoke на 1.1.4 с Ordered TCP: запуск, локальное подключение TCP, REST read/maintenance scopes, настоящее сохранение мира, reload общей whitelist/локальных политик, отзыв REST-токенов и штатное завершение прошли. TerrariaRuntime 1.1.4: commit `80ad1c6`, тег `v1.1.4`; [workflow публикации](https://github.com/VegaKernel/TerrariaRuntime/actions/runs/45715779036) завершён успешно, включая проверку нативных хуков. На момент проверки индекс NuGet ещё не содержал 1.1.4.
## Следующая итерация: сохранение, completion pump и FakeProvider

### Сохранение миров (TerrariaRuntime 1.1.5)

На одном и том же Vega host, нативный 1.1.4 против локального patched runtime с устранением только лишнего `MemoryStream.ToArray()` в локальном сохранении. `FileUtilities.Write` получает точный Count; cloud, скрытый буфер и смещение != 0 используют штатную копию. Сохранены прочтение старого мира, persisted-file validation, rollback, rolling backups.

| Workload | Allocated bytes before → after | Median RSS before → after | Wall time before → after |
| --- | ---: | ---: | ---: |
| Medium, 10 saves | 405 087 480 → **336 612 952** | 918.22 → **866.37 MiB** | 3.014 → 3.025 s |
| Small, 3 saves | 73 941 792 → **64 699 880** | 588.60 → 581.38 MiB | 0.491 → 0.489 s |

На medium минус 16.9% transient allocations, sampled peak RSS 1048.89 → 967.72 MiB. Время сохранения не улучшилось значимо. Все шесть пар `.wld`/`.bak`/`.bak2` имеют одинаковую длину и отличаются только штатными временем сохранения и `cloudBGActive` (6 байт); опубликованный runtime 1.1.4 перечитал оптимизированный medium world и завершился штатно. Реальный large world, аварийная запись и реальная cloud save не проверены. Подробная методология, IL, исходные логи и сравнение: `artifacts/performance-2026-11-26/save-buffer/REPORT.md`.

### Completion pump (Ordered TCP)

Active slot snapshot вместо сканирования всех 256 слотов для каждого callback, внутри прежнего bounded game-thread pump. ABBA, фиксированный workload, 12/12 прогонов подтвердили байты и число callback:

| TCP-получатели | p95 pump до, ms | p95 pump после, ms |
| ---: | ---: | ---: |
| 1 | 0.271–0.300 | **0.009–0.011** |
| 8 | 0.290–0.315 | **0.036–0.048** |
| 32 | 0.407–0.409 | **0.191–0.193** |

Производительность pump улучшилась, callback p95 по-прежнему около 29–30 ms (cadence). Throughput одинаков при заданной нагрузке. Vanilla transport остаётся общим default. Raw `pump-results.txt`; воспроизводимый A/B `--pump`.

### Multiplicity parser (отдельный эксперимент, ещё не интегрирован)

В изолированном worktree `Multiplicity-2.6.2-playerupdate-local` на pinned 829eada реализован canonical delegate exact-identity fast path typed view, с сохранением fallback. Main protocol326 и зависимость Vega 2.6.2 не менялись; пакет не публиковался. 6 тестов parser + regression cases Vega passed. 12 локальных A/B прогонов, 200 слотов / 1 024 000 обычных PlayerUpdate в каждом, идентичный Vega benchmark binary, менялась только библиотека Multiplicity:

| Метрика | Official 2.6.2 | Patched 2.6.2 experiment |
| --- | ---: | ---: |
| ns/packet (median) | 479.46 | **340.28** |
| Allocated bytes/packet | 192.032 | **0.032** |
| Gen0 | 11 | **0** |

Это изолированный парсер, не серверный TPS. Прирост throughput ~40.9% в этом тесте. Сырые данные: `playerupdate-parser/abba-raw.json`. Для интеграции нужен выпущенный совместимый пакет; Vega пока использует штатный parser.
### FakeProvider (встроенный нативный модуль) и индекс сцен

Существующие `Vega.Modules.Scenes` WorldEdit/Region-Vision/LobbyVisuals/FakeProviderClustering дополнены `FakeProviderService` и командами `fake` / `pfake`, без ссылки на TZ.TShockAPI и без подмены Main.tile. Основное отличие от legacy dense provider map: ленивый неизменяемый индекс только существующих слоёв/миров, отсортированный по приоритету; при >=8 слоях — sparse 64x64 chunks, до 16 384 ссылок, fallback на отсортированный scan для больших наборов. Результат не блокирует mutation lock на обычном чтении. Наблюдатели frozen, common singleton scopes кешируются. Изолированы world ID, observers, порядок и удаление при pre-reset/close. Реальный native smoke проверил команды create/list/move/layer/disable/enable/remove и `pfake list`; unit tests проверяют личные слои, разрешения и закрытие сессии.

ABBA `fakeprovider-sparse-abba.json` — один и тот же детерминированный harness (проверка возвращённых тайлов), прежние и новые Core/Contracts/Scenes DLL. 250 000 запросов среди 64 разнесённых overlays; 4096 тайлов в compose; без сокетов:

| Workload | До | После |
| --- | ---: | ---: |
| 64 overlays lookup | 1406–1432 ns/op | **294–322 ns/op** |
| compose 64x64 | 624–634 ns/tile | **350–372 ns/tile** |
| 1 000 personal creations | 3.20–3.43 us/op, 1.37 MB | 3.52–3.60 us/op, **1.18 MB** |
| 10 000 moves with mask | 3.04–3.16 us/op, 16.72 MB | 3.30–3.32 us/op, **15.76 MB** |
| lobby observer churn 200x300 | 14.88–15.31 us/op, 11.73 MB | 19.14–20.28 us/op, 12.26 MB |

Read hot-path выигрывает в 4.4–4.9 раза, отдельный compose в 1.7–1.8. Некоторые операции мутации дороже, особенно замена больших observer sets. Результат относится к сценам, а не к физической симуляции Terraria. Нативные кадры настоящего клиента не измерены. Модуль не сохраняет фейковые тайлы в `.wld`, не виртуализирует отдельную физику, сундуки/tile entities, не предоставляет бинарную совместимость legacy FakeProvider.

### Combat read model

Публикация состояния оружия теперь индексируется слотом, копируется только при изменении, старые читатели сохраняют immutable snapshot. Регрессионный микротест 200 неизменившихся игроков × 1000 ticks: прежний словарь + records 11 624 000 B → **0 B** в steady-state combat projection. Это не весь player snapshot или tick. При смене предмета/активности массив клонируется один раз; изменившиеся записи обновляются, ушедшие игроки удаляются.

TerrariaRuntime 1.1.4 теперь индексируется NuGet и проверен непосредственно скачанный DLL. 1.1.5: commit `95670b4`, [tag publication successful](https://github.com/VegaKernel/TerrariaRuntime/actions/runs/45719547003); native boot/hook/save buffer smokes прошли. На момент проверки индекс NuGet 1.1.5 ещё обновляется; локальная Vega использует проверенный 1.1.5 nupkg. Vega не опубликована.
Integrated `--pump` repetition with actual Vega/TerrariaRuntime 1.1.5: p95 all→active 1 peer 0.275–0.289 → 0.007–0.008 ms; 8 peers 0.289–0.303 → 0.035 ms; 32 peers 0.405–0.408 → 0.185–0.197 ms. All bytes/callbacks validated, throughput remains fixed, callback cadence unchanged; CPU and allocations still noisy. `pump-integrated-results.txt`.

Final Release build: 32 architecture + 619 runtime tests passed, two external DB tests skipped; native sandbox readiness/local policy/shared admission storage/graceful stop passed again. Native ordered TCP/REST save and FakeProvider commands smoke passed on 1.1.5. Local Vega package regenerated, 20 DLL, exact runtime dependency 1.1.5. TerrariaRuntime tag publishing and Windows/Ubuntu/macOS CI succeeded.
## FakeProvider SDK и paged native tiles

Плагин теперь получает `context.World.FakeProvider` (`IPluginFakeProvider`) напрямую из SDK. Provider handles: `CreateGlobal`, `CreatePersonal` с exact session observer, `Move`, `Replace`, `SetLayer`, `SetTop`, `SetEnabled`, `SetObservers`, `Draw`, `Dispose`. Visibility handle: session-aware player assignment/observers, native NPC/projectile slot assignment. Один tracked owner на поколение плагина, ранний Dispose удаляет временные ресурсы, unload/reload удаляет все активные handles. Тесты реальной collectible hot reload, два владельца, смена поколения слота, late creation и stale layer remove. Raw legacy `World.Scenes.Create` сохраняется, исправлено освобождение при race Track; отменённая фоновая задача не пропускает cleanup. Пример `samples/Vega.PluginSamples/FakePreviewCommand.cs`. `FillOutsideWithAir`, виртуальные chests/tile entities, независимая физика не реализованы; native NPC/projectile reuse должен отслеживать владелец.

### Native scene render

A/B одинаковый 64x64 encoder workload, 24 regions, 32 recipients; cache independently resolves exact tiles+frame-important flags before reusing immutable packet, scratch buffers pooled. Bytes verified against legacy encoder per observer. Unknown sockets get private buffer. Final renderer caps packets at 46x46 worst-case (15 B/tile + 10 B header <= Int16.MaxValue); benchmark 64x64 composition is an isolated encoder workload, not network test.

| 32 recipients | Before | After |
| --- | ---: | ---: |
| Identical global composition | 6.91–6.92 ms | 4.03–4.06 ms |
| Allocated bytes / region | 2,889,472 | 24,784 |
| Different personal compositions | 12.71–14.67 ms | 11.89–11.90 ms |
| Personal allocated bytes / region | 2,889,472 | 791,104 |

`scene-render-results.txt`. One recipient timing similar; 8 recipients JIT noise, no timing claim. Read/encode not actual client TPS.

В Core immutable observer sets передаются в render requests без копирования, при изменении содержимого/порядка/наблюдателей того же прямоугольника планируется одна перерисовка для объединения old/new; при subset/superset используется существующее frozen множество. `sdk-scene-abba-final.json`, old chunk index vs new SDK Core (same deterministic harness):

| Workload | Before | After |
| --- | ---: | ---: |
| 1,000 personal creates | 3.56–3.57 us, 1.18 MB | 2.59–2.60 us, 1.01 MB |
| 10,000 WorldEdit moves with mask | 3.32–3.34 us, 15.76 MB | 2.03–2.06 us, 8.40 MB |
| Lobby 200x300 observer churn | 19.58–21.21 us, 12.26 MB | 19.38–20.07 us, 10.73 MB |

Lookup/compose roughly unchanged versus previous optimized sparse index; no new performance claim about lookup.

### Canonical tile reference storage (opt-in)

`DefaultCollection<ITile>.OnCreateCollection` at Main.cctor lets Vega install a lazy paged 64x64 reference collection without replacing native `Tile` objects. Unlike legacy `StructTile`/coordinate proxy, null/reference identity/assignment aliases/Clone/CopyFrom remain native. Original maximum grid 8401x2401. Default off; `config/server.json` `pagedTileStorageEnabled=true`, `--vega-paged-tiles=on`, or `VEGA_PAGED_TILES=on`. A/B four independent native processes, same small/medium fixture, no players, current runtime 1.1.5.

| Workload | Native default | Paged |
| --- | ---: | ---: |
| Small idle RSS | 559.82 MiB | 447.69 MiB |
| Medium idle RSS | 767.19 MiB | 707.79 MiB |
| Small load allocations | 347,132,512 B | 226,994,112 B |
| Medium load allocations | 568,401,848 B | 502,109,872 B |
| Medium 10 saves elapsed | 3.017 s | 3.140 s |
| Medium save peak sampled RSS | 973.08 MiB | 916.39 MiB |

Reference-only scan synthetic small 30.97→56.49 ms, medium 69.52→129.81 ms: memory/speed tradeoff, not a general acceleration. Six output/backup file pairs same length and differ only in ordinary timestamp/cloud metadata; default collection reopened both paged outputs and stopped gracefully. Pages retained after shrink for lifetime, actual large world/live player benchmarks pending. Raw artifacts: `paged-tiles/REPORT.md`; config/limitations [paged-native-tiles.md](paged-native-tiles.md).
## Protocol 329, network and game-thread scheduler

### Multiplicity 4.0.1

Native assembly inspection `Terraria.Main.curRelease = 329` (Terraria 1.4.6.1). Vega had the older Multiplicity 2.6.2/protocol 325 dependency; migrated all projects/packaging to 4.0.1. Current native platform metadata (typed slot/platform/user ID/Nintendo name/host), direction-dependent QuickStack slot list using native `PlayerItemSlotID.Count`, and removal of width/height from TweakItem are adapted. Custom dimensions are explicitly rejected before applying a native item; remaining options preserved. Native protocol constant regression test added.

Multiplicity 4.0.1 standard PlayerUpdate path uses the built-in deserializer identity and existing typed view, fallback preserved for custom deserializers. 136 tests each for net9.0/net11.0. Commit c2a883275703c3cc8ed4f0cf59b5eb0da2be851e, tag v4.0.1, [CI publish](https://github.com/VegaKernel/Multiplicity/actions/runs/45729375301) build and NuGet/package/symbol push success. Configured self-hosted deploy still queued. Public [NuGet 4.0.1](https://www.nuget.org/packages/Multiplicity/4.0.1) indexed and signed package downloaded; nuspec repository commit matches c2a8832. Vega used the locally built exact same 4.0.1 source package for full tests, and replacement with the downloaded public net11 DLL in the identical benchmark confirmed 342.1 ns, 0.032 B/packet, 0 Gen0 (`protocol329-parser/public-verified.json`).

A/B on *current protocol329* Vega pipeline (ownership + metadata, 200 slots, 1,024,000 ordinary PlayerUpdate per run, 128 KiB receive backing, 14 B payload; same benchmark SHA-256, only Multiplicity DLL differed). Eight ABBA runs `protocol329-parser/abba.json`:

| Metric | 4.0.0 | 4.0.1 |
| --- | ---: | ---: |
| ns per packet | 474.7–492.0 | 334.7–349.2 |
| allocated bytes per packet | 192.032 | 0.032 |
| GC Gen0 | 11 | 0 |
| throughput | 2.03–2.11 M/s | 2.86–2.99 M/s |

This is processing ordinary packet payloads in memory, not client count or game TPS. The residual 32,776 bytes per test are harness allocations rather than per-packet model allocations.

### Game-thread scheduler

`TerrariaRuntimeGameThreadDispatcher` avoids wrapping an already supplied Action in a closure/Func<bool>, uses read-only known thread fast path and empty-queue short circuit. Same FIFO, reservation count including yielded work, per-tick snapshot and cooperative 4096/128/2ms production bounds. Isolated ABBA `game-thread-dispatch/REPORT.md`: 320,000 reused Action posts 37.0–37.2 → 25.8–25.9 ms, extra dispatch allocation 25,600,000→0 B; 1M empty ticks 47.2–61.3→19.4–19.5ms. Callers may still allocate callbacks. Plugin successful completed tasks pruned at registration; cancellations of queued-only cleaner work no longer stall shutdown, started native work finishes on the game thread.

### Ordered TCP experiment

`--send-cancellation` compares per-send CancellationToken against disposal-only cancellation (queue cancellation still present). 8 loopback runs validated exact bytes and callbacks; 32-peer allocations and CPU were worse for disposal-only on this host, so default remains cancellation token. No default transport change. `send-cancellation-results.txt`. 29 targeted transport tests. Existing active-slot callback pump improvement remains enabled only inside opt-in Ordered TCP.

Final Vega Release build with 4.0.1: 32 architecture + 646 runtime tests passed; external PostgreSQL/MySQL tests skipped. Native sandbox/shared admission storage smoke passed; actual small native host with paged tiles + Ordered TCP, local TCP connect/close, REST save, hot reload plugin FakeProvider and shutdown passed. 20-DLL local Vega nupkg verified exact TerrariaRuntime 1.1.5 + Multiplicity 4.0.1 dependencies. No new Vega publish. Full stock-client joins, real 200–255-player simulation, actual wire telemetry of all sends, large-world/production TPS remain qualification gates.

## Native tile capture and SSC checkpoint

Native tile snapshots allocate the destination array once, preserve immutable ownership, and compute the exact existing revision hash during the native row-major capture rather than scanning a copied array. Source-linked ABBA using real Terraria.Tile instances and public CaptureAsync:

| Workload | Before: allocation / time | After: allocation / time |
| --- | --- | --- |
| 512² × 6 | 50,333,808 B / 208.19–219.98 ms | 25,168,320 B / 215.97–226.54 ms |
| 1024² × 3 | 100,664,376 B / 125.71–125.85 ms | 50,332,896 B / 107.14–108.07 ms |

Approximately 50% less allocation at both sizes and ~14% lower elapsed time for the larger area; smaller capture latency did not improve. This reduces large-array churn in snapshots used by world/scene/plugin operations, not a claim of improved live server TPS. Raw ABBA, source and harness: `artifacts/performance-2026-11-26/world-tile-capture/REPORT.md`.

Native SSC / inventory checkpoint: opt-in primary authority, native SSC world-data flag, server-owned pending character, initial and joined inventory/loadout policy, exact authenticated restore and logout reset; all use released TerrariaRuntime 1.1.5. Final Release compilation succeeded; 669 runtime + 32 architecture tests passed, two external DB tests skipped. Native authority smoke (`artifacts/runtime-smoke-7e375d7505104c258a20543b8b27efc8`) reached NetworkReady with the native SSC flag, passed ordinary local TCP connect/close, scoped REST save, plugin reload/unload, FakeProvider operations, config reload and graceful save/exit. The separate sandbox isolation/shared admission smoke passed. The local Vega package was rebuilt. These tests do not establish stock-client SSC login/restore/reconnect or 200–255-player operation. See [roadmap review](roadmap-review-2026-11-26.md).

## Shared SSC persistence and production-cap transport round

100 actual `SandboxSscStore.Read` calls over a 1.25 MB disposable store (100 × 12 KiB synthetic character payloads), same source-linked contract assembly, ABCCBA:

| Variant | Elapsed | Allocations | Gen2 |
| --- | ---: | ---: | ---: |
| Old read plus JSON rewrite | 580.75–585.95 ms | 998.81 MB | 37 |
| Read-only, string parse | 274.26–284.51 ms | 749.58 MB | 18 |
| Read-only, streaming parse | 91.11–92.04 ms | 249.28 MB | 0 |

~84% lower elapsed time, ~75% lower allocation. All payload/revision checks pass. Normal periodic checkpoints skip a byte-identical write only after checking the current revision under the cross-process lock; manual/disconnect writes remain revisioned. Atomic writes are streamed and flushed. This is an SSC store microbenchmark, not a gameplay-server speedup. `ssc-read/REPORT.md` contains the raw measurements and source-linked harness.

Native TCP vs experimental Ordered TCP at 200 and 255 ordinary local peers (eight sends per peer per 60Hz offered tick, 120 measured ticks; same native runtime, warmup, 64/256/1024/4096-byte repeated buffer distribution, all bytes and completions verified). ABBA results from `native-tcp-production-peers-8.txt`:

| Peers | Transport | Throughput | CPU seconds | Producer p95 | Completion p95 | Allocated B/request |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 200 | Vanilla | 124.36–124.45 MiB/s | 10.72–11.28 | 13.58–16.60 ms | 0.010–0.011 ms | 208.1 |
| 200 | Ordered | 123.26–124.32 MiB/s | 6.09–7.47 | 3.01–4.32 ms | 28.88–29.75 ms | 365.0–396.2 |
| 255 | Vanilla | 157.97–158.30 MiB/s | 12.66–13.56 | 16.42–16.51 ms | 0.009 ms | 208.0 |
| 255 | Ordered | 155.59–155.80 MiB/s | 6.13–8.47 | 3.52–3.77 ms | 29.12–31.06 ms | 426.5–461.4 |

Ordered moves CPU off the producer substantially, but completion waits for the authoritative pass and allocation cost is higher. An initial 32-sends-per-peer stress case at 200 peers completed on vanilla and timed out on Ordered; this exceeds the candidate's documented bounded completion capacity per pass. Do not enable Ordered by default. These are actual local TCP peers, not joined Terraria clients, and completion timings differ in semantics (native callback vs game-thread-drained completion). No live TPS or TShock 255-player superiority claim follows.

An additional ABBA comparison of adjacent batch=1 vs batch=16 at both production peer counts delivered identical bytes and callbacks. At 255 peers batch=16 allocated 485.7–486.3 B/request versus 447.4–449.8 and consumed 8.67–8.88 CPU seconds versus 7.06–8.00. No robust benefit justifies enabling batching; keep max batch 1. Raw runs: `native-tcp-production-batch.txt`.

Latest integration checkpoint: 696 runtime + 32 architecture tests passed, two external database tests skipped. Current TShock SQLite synthetic migration smoke imported all five data families and legacy Vega SSC; source SHA-256 remained identical, shared/bans/regions/warp records verified and repeated ban import created no duplicate. `artifacts/migration-smoke-91085c5880154b3fbddf33f17759dc76`. Native primary authority startup, REST `/v1/metrics` and `/v1/scheduler`, five-sample CSV measurement (zero real players), and graceful exit passed in `artifacts/metrics-smoke-161f1a5bd67641469c97a073c0a9aa60`. Native sandbox/shared admission smoke passed. Local 20-DLL Vega package includes TerrariaRuntime 1.1.5, Multiplicity 4.0.1 and BCrypt.Net-Next 4.0.3. No Vega publish. Real stock-client qualification and sustained actual connected-player load remain open.

Final checkpoint after the plugin fault breaker and managed configuration diagnostic guard: 699 runtime + 33 architecture tests passed with two external database tests skipped (`roadmap-release-verification.trx`). The real collectible test plugin retires after configured repeated faults; independent callbacks remain functional, normal configuration rejection does not increment the fault counter, and an architecture test enumerates current ordinary SDK delegate surfaces. Rebuilt local package and reran native authority/REST CSV sampling with invariant numeric formatting (`artifacts/metrics-smoke-3fb4f4a8024e4b13b9f763060e446be7`). These remain synthetic and server-only checks where noted.

### Completion count ceiling: diagnosed overload and measured fix

The 32-sends-per-peer offered workload was repeated with explicit closure detection. Vanilla delivered every request at both peer counts; all four old Ordered 2048-cap runs closed a connection near the 131,072 shared outstanding-entry bound (about 130,619–130,662 entries were still retained after closure). The cap was the count ceiling rather than exhausted socket byte memory. Ordered A/B changed only the count ceiling, preserving the existing 2 ms cooperative deadline and generation checks.

| Peers | Vanilla baseline | Ordered cap 2048 | Ordered cap 8192 | Producer p95 (vanilla → raised-cap Ordered) |
| --- | ---: | ---: | ---: | ---: |
| 200 | 170.64–170.71 MiB/s | closed under load (2/2) | 485.97–491.87 MiB/s, all 768,000 callbacks | 49.43–52.93 → 11.34–17.72 ms |
| 255 | 168.36–169.21 MiB/s | closed under load (2/2) | 616.63–621.05 MiB/s, all 979,200 callbacks | 64.05–64.52 → 13.51–14.51 ms |

At 255 peers CPU was 24.88–28.80 s versus vanilla 51.91–52.81 s, pump p95 1.91–1.96 ms, and callback p95 42.32–50.89 ms (intentional game-thread completion cadence). Allocations remain higher (402.4–404.5 versus 208.0 B/request); the deadline is cooperative, not a hard wall-clock cutoff for individual callbacks. Enable the raised count ceiling only inside opt-in Ordered TCP (new `orderedCallbacksPerPass`, default 8192; bounded 1–8192); vanilla remains default and batching remains off. Raw: `native-tcp-production-overload.txt` and `native-tcp-production-pump-cap.txt`.

Final current solution verification after the transport ceiling, CIDR shared allowlist and offline whitelist import: 702 runtime + 33 architecture tests passed, two external DB tests skipped (`roadmap-whitelist-final.trx`). Actual native server with opt-in Ordered 8192, native SSC, normal local TCP connect/close, metrics CSV and graceful shutdown passed (`artifacts/metrics-smoke-881ca05652284c7d9c8e4afb9c41cd10`). The additional whitelist CLI import preserved source bytes and persisted both an IP and a CIDR network (`artifacts/whitelist-migration-smoke-bf37efc6db904b91b319431a37de1ee9`). Rebuilt the local 20-library Vega package. Stock client 1.4.6.1 is installed on the development host but no interactive client window was started; actual client qualification remains separate.

### Ordered TCP: receive readiness instead of polling on broadcast

A separate EventPipe trace of the batched Ordered transport under 64 joined loopback clients showed Winsock `select` on the game-thread broadcast path and on native client updates. Ordered now reads connection lifecycle state in `IsConnected`; the native read path polls for readiness. Readable includes EOF, so buffered bytes are still delivered before FIN retires the connection. The regression test covers an idle socket, buffered bytes followed by FIN, and the zero-byte completion.

Both variants used the same small world on Windows, the same 64-client load, batched Ordered TCP, and two runs of the 12-second full phase (movement, shots, chat and two bosses). These are ranges of observed results, not confidence intervals.

| Metric | Ordered before | Ordered with receive readiness |
| --- | ---: | ---: |
| CPU (process-seconds over 12 seconds) | 24.92–25.75 | 9.30–9.83 |
| Movement p99 (ms) | 29.35–32.19 | 34.06–34.77 |
| Chat p99 (ms) | 37.54–43.81 | 36.72–37.79 |
| Working set (MiB) | 680.6–681.9 | 684.1–685.7 |

All 64 clients remained connected and both boss NPCs were observed in each repeat. Ordered remains opt-in; these runs do not qualify real clients, WAN or long-running production load.

### 100 joined clients with emulated network delay

Same Vega binaries and small world, only `useOrderedTcp` differed. One run per mode, Ordered first. A bounded FIFO loopback proxy delayed application bytes by 20 ms in each direction, without added loss or a bandwidth limit. The proxy and generator ran on the server machine; generator wake p99 was 24 ms for Ordered and 12 ms for stock TCP. This tests delayed delivery, not real WAN behavior or TCP congestion.

| Metric, 60-second full-load phase | Ordered TCP | Stock TCP |
| --- | ---: | ---: |
| Clients still connected | 100 | 100 |
| Movement updates seen by one observer | 118,800 | 82,967 |
| Player shots seen by one observer | 5,940 | 4,155 |
| Chat messages seen by one observer | 1,188 | 830 |
| Chat p99 among received messages | 138 ms | 20.3 s |
| Server CPU (process-seconds) | 94.0 | 64.1 |
| Server working set (MiB) | 753 | 651 |

Both bosses were observed. Stock TCP delivered only about 70% as many updates within the observation window while all sockets stayed open; this is backlog, not evidence of TCP packet loss. Its lower process CPU is not an equal-throughput saving. Ordered matched all 1,200 movement probes (p99 137 ms). The stock movement percentile is omitted: delayed positions can repeat and 772 probes were unmatched or expired. The snapshot taken by `tpsstats` after the phase reported a one-minute mean of 60.0 TPS (Ordered) and 27.8 TPS (stock); it is not an independent per-phase capture. Keep Ordered opt-in until repeated runs, representative real clients, and a longer soak under bandwidth limits and loss have been checked.

### Ordered TCP: release batch reservations once

Coalescing still copies adjacent packets into an owned rental and keeps every completion credit. Returning the original rentals now releases their summed capacity in one operation under the shared world budget lock, instead of re-entering that lock for every packet. The temporary merged rental is still charged before copying, so both limits remain in force.

Four loopback runs, baseline/candidate/candidate/baseline, 100 joined clients and identical 18-second full phases. CPU fell from 27.56–28.08 to 24.27–25.30 process-seconds (about 11% on average). All runs delivered 35,640 movement updates and 1,782 player shots to the observer, with no disconnected clients. Chat p99 fell from 50.14–50.42 to 44.71–46.46 ms; movement p99 and working set overlapped. The results do not establish a WAN or slow-client improvement.

### Updated repository checkpoint (`e2f95603`): transport and scene network path

Windows / .NET 11, 32 logical cores. The existing loopback byte-validation workload
was repeated unchanged at 8 ordinary sends per peer per tick (120 measured ticks,
baseline/Ordered/Ordered/baseline). The values below are observed ranges, not confidence intervals.

| Peers | Vanilla throughput (MiB/s) | Ordered throughput (MiB/s) | Vanilla → Ordered process CPU (s) | Producer p95 (ms) |
| ---: | ---: | ---: | ---: | ---: |
| 200 | 116.19–123.62 | 121.12–122.81 | 11.52–11.95 → 8.66–10.39 | 22.14–26.05 → 4.59–6.44 |
| 255 | 100.35–129.50 | 155.47–155.55 | 14.75–16.56 → 9.14–11.70 | 25.22–40.00 → 5.89–6.35 |

All eight runs validated every byte and all 192,000 / 244,800 measured callbacks.
At 255 peers Ordered still allocates about 462–473 B/request versus 208 for vanilla.
Its callback p95 is about 28 ms because delivery is game-thread-pumped, not a direct
socket completion. The workload is transport-only, not 255 joined Terraria clients.

New benchmark option `--queue-age` compares the new diagnostics in an off/on/on/off
sequence, including three samples per tick. At 255 peers, diagnostics-off throughput
was 153.89–155.06 MiB/s; diagnostics-on was 153.49–155.16 MiB/s, with overlapping
CPU ranges. No reliable throughput improvement or regression is established. The
observed maximum send age was 1.49–1.57 ms and maximum undelivered completion age
30.03–30.57 ms (200 peers: 1.62–3.63 and 30.68–31.58 ms). These are sampled maxima,
not continuous maxima or percentiles. Diagnostics remain opt-in.

`--sustained` runs 255 peers for 1800 measured ticks (30 seconds of offered
workload, 3,672,000 requests and 4762.57 MiB per mode). An initial run exposed
32-bit overflow in the benchmark's aggregate byte counter, not in delivered data;
the counter was promoted to 64 bits and the whole workload was repeated.

| Verified sustained run | Vanilla | Ordered with queue diagnostics |
| --- | ---: | ---: |
| Measured elapsed (s) | 35.748 | 30.008 |
| Validated throughput (MiB/s) | 133.23 | 158.71 |
| Process CPU (s) | 218.30 | 148.70 |
| Producer p95 (ms) | 24.44 | 5.33 |
| Managed B/request | 208.0 | 484.6 |
| Callback p95 (ms) | 0.015 | 28.314 |

Both modes delivered all callbacks and validated all bytes. Ordered's sampled
queue maxima were 5.58 ms send age and 45.31 ms completion age; the prior run
observed 108.19 ms completion age, so short-run p95 does not bound the tail.
The long run also shows the allocation cost accumulating (about 1.78 GB versus
0.76 GB across the measured period). Keep the transport opt-in; this is a local,
equal-payload transport test, not a joined-client or WAN qualification.

The above run deliberately uses `batch=1`. The updated server host already
selects `batch=16` when Ordered is enabled. `orderedBatchRequests` now exposes
that setting (default 16, normalized to 1–16), without changing the transport
default. An additional `--sustained-production` run matches that host setting:
vanilla / Ordered respectively completed in 32.382 / 30.005 s, validated
147.07 / 158.72 MiB/s, used 204.94 / 141.13 process CPU-seconds, and measured
19.26 / 5.16 ms producer p95. All 3,672,000 callbacks and 4762.57 MiB were
validated in each mode. Ordered's sampled send/completion maxima were 5.97 / 31.60 ms;
managed allocations were 208.0 / 490.7 B/request (including the benchmark process).
The separate short `--production-batch` ABBA measurements showed overlapping
throughput and CPU ranges for batch sizes 1 and 16, so these runs do not establish
a robust allocation reduction from batching. Keep the existing opt-in setting
and use the configuration for controlled comparisons rather than changing defaults.

### Ordered TCP: lifetime cancellation wake

The expensive idle path was `SemaphoreSlim.WaitAsync(token)`, which attaches a
cancellable task wrapper to each wait. The measured alternative registers one
cancellation callback per writer and uses the same queue-gated semaphore signal
for idle cancellation. The writer checks cancellation under the publication gate
before waiting; actual socket writes still use their cancellation token. The A/B
changes only this wait mechanism and uses the current host's batch size of 16.

| Peers | Per-wait allocation (B/request) | Lifetime wake (B/request) | Lifetime wake throughput (MiB/s) |
| ---: | ---: | ---: | ---: |
| 200 | 493.8–520.6 | 139.7–141.3 | 121.86–123.58 |
| 255 | 503.6–506.8 | 143.2–143.9 | 156.72–158.34 |

At 255 peers, the allocation reduction is approximately 71–72%, and gen0
collections fell from 9 to 2 in each measured repeat. Baseline throughput was
156.85–157.34 MiB/s, overlapping the candidate range. All bytes and callbacks
were validated; CPU and callback timing are not a hard guarantee. Regression
coverage checks idle and pre-start cancellation, concurrent publication, complete,
abort and active-I/O cancellation. The lifetime registration is now the internal
default for opt-in Ordered TCP. Reproduce old/new comparisons with
`--wait-cancellation` and the historical sustained wait path using
`--sustained-production --legacy-wait-cancellation`.

The current `--sustained-production` repeat (255 peers, 30 seconds offered)
validates all 3,672,000 requests and 4762.57 MiB in each mode:

| Metric | Vanilla | Ordered, batch 16 + lifetime wake |
| --- | ---: | ---: |
| Elapsed (s) | 32.791 | 30.007 |
| Validated MiB/s | 145.24 | 158.72 |
| Process CPU (s) | 214.08 | 136.80 |
| Producer p95 (ms) | 20.66 | 4.14 |
| Managed allocation (MB) | 763.83 | 508.32 |
| B/request | 208.0 | 138.4 |
| Gen0 / gen1 / gen2 collections | 46 / 5 / 0 | 31 / 4 / 0 |
| Callback p95 (ms) | 0.010 | 28.485 |

The previous same-host batch-16 run with per-wait cancellation allocated
1801.96 MB / 490.7 B/request. The lifetime wake reduces that measured allocation
by about 72%, and is even below vanilla in this particular loopback workload.
Queue-age diagnostics were enabled in Ordered: maximum sampled send age 4.10 ms,
completion age 31.62 ms. As always, process allocations include the benchmark
reader tasks; these are not a measurement of native world ticks or a claim about
WAN or joined-player workloads. Raw output: `vega-e2f95603-native-tcp-single-wake-abba.txt`
and `vega-e2f95603-native-tcp-sustained-single-wake.txt` in the same local artifact directory.

Scene packet interception now uses the existing sparse 64x64 index for small
rectangles, falling back to the flat projection for broad regions. Section refresh
enumerates only enabled intersecting overlay definitions from the immutable runtime
projection instead of constructing diagnostic snapshots (including conflict counts).
Direct-recipient sends reuse singleton observer scopes instead of building sets.

| Scene workload | Before / flat comparator | After | Allocations |
| --- | ---: | ---: | ---: |
| 1M mixed small-region checks, 64 overlays (actual before/after) | 261–274 ns/check | 210–242 ns/check | 56 B total in both, harness noise |
| 200k mixed small-region checks, 1024 sparse overlays | 2.71–2.78 µs/check | 30–37 ns/check | 56 B total in both |
| 10k native-section intersections, 64 overlays | 40.0–44.2 µs/query | 1.37–1.39 µs/query | ~52,170 → 104 B/query |

The 1024-overlay comparator scans an already materialized flat projection and does
not include the world lookup; the indexed measurement uses the public runtime query.
All queries check expected results. These are isolated CPU/managed-allocation
measurements, not client latency or overall gameplay TPS. The raw output is saved in
`artifacts/performance-2026-11-26/vega-e2f95603-*` locally; reproduce with
`Vega.NativeTcp.Benchmarks --production-peers`, `--queue-age`, `--sustained`, and
`Vega.Scene.Benchmarks` in Release.

### REST/deployment regression checkpoint

After the scoped REST world read/time/weather changes, the same 255-peer
30-second `--sustained-production` run again validated 3,672,000 requests and
4762.57 MiB in both modes. Vanilla / Ordered respectively measured 157.57 /
158.69 MiB/s, 205.89 / 150.41 process CPU-seconds, 18.98 / 4.10 ms producer p95,
and 208.0 / 138.0 B/request. Ordered's sampled maximum send / completion ages
were 5.27 / 31.59 ms. The repeated allocation result supports the lifetime-wake
improvement; throughput was almost equal at this offered rate, so the earlier
throughput advantage should not be generalized. Local raw output:
`artifacts/rest-world-final-964acfa67b66436ba9e5362990333535/native-tcp-sustained-regression.txt`.

The isolated native Windows package also passed REST world read and mutations,
maintenance audit, native console commands, sandbox admission-policy isolation
and graceful shutdown. A separate package-copy smoke enabled Ordered TCP,
verified its native listener and normal local connect/close, and observed queue
diagnostics. The deployment workflow now includes both transport modes on
Windows/Linux; the local result does not establish joined-client compatibility.

### FakeProvider logical chest path

The new scene benchmark resolves one visible logical chest from a world with
64 existing overlays and 200 exact-session observers. It also records 200
simultaneous logical open intents and consumes all 200 in a batch, checking
that each is delivered once. No network or inventory transfer is performed.

| Workload | Initial reference snapshots | Final value snapshots + indexed list loop |
| --- | ---: | ---: |
| 200,000 chest resolutions | 104 B/lookup | 56 B total (harness noise) |
| 1,000 batches of 200 open/consume pairs | 336 B/pair | 56 B total (harness noise) |

Two final runs measured 1.35–1.39 µs per resolve and 0.266–0.279 ms per batch
of 200 open/consume pairs. The final ordinary tile lookup remained allocation-free
in steady state (56 B across 250,000 checks); region checks were 216–220 ns/check.
No latency improvement is claimed for the new chest path. Existing overlay
mutation benchmarks allocate more than the previous checkpoint (WorldEdit move
about 160 B more per operation); container metadata changes skip tile redraw but
still invalidate the lazy immutable projection. Raw local outputs:
`artifacts/performance-2026-11-26/vega-virtual-chest-scene-benchmark-*.json`.

### Mixed FakeProvider chests and logical signs

The same 64-overlay / 200-observer scene workload now includes a chest and a
logical sign in one provider. Two final Release runs measured:

| Workload | Time | Managed allocations |
| --- | ---: | ---: |
| 200,000 sign resolutions | 0.768–0.769 µs/lookup | 56 B total |
| 1,000 batches of 200 sign read/consume pairs | 0.289–0.302 ms/batch | 56 B total |
| 200,000 chest resolutions | 1.40–1.46 µs/lookup | 56 B total |
| 1,000 batches of 200 chest open/consume pairs | ~0.279 ms/batch | 56 B total |

Every intent was checked for one-time consumption. The steady-state allocation
figures are harness noise. SDK empty polling also avoids captured delegates and
interface enumeration; a regression test checks 20,000 combined empty polls.
Ordinary tile and region lookups retain the same sparse projection (328–330 ns
and 213–219 ns respectively in these runs). No joined-client latency claim is made.

The initial sign implementation exposed another allocation in the existing
metadata cleanup path: captured LINQ predicates were allocating even with no
pending intents. Replacing them with an empty fast path and explicit scans
reduced the 10,000-move WorldEdit preview allocation from 11,924,144 to
9,364,144 bytes (256 B/move), also saving 128 B/move versus the prior published
chest checkpoint (10,644,144 bytes). The earlier pre-chest baseline was
9,044,144 bytes; the remaining ~32 B/move difference is not claimed as a
regression-free result. The shared nullable metadata reference avoids adding
another field to every ordinary overlay. Raw local outputs:
`artifacts/performance-2026-11-26/vega-virtual-sign-scene-benchmark-*.json`.

### P0/P1: completion polling, SSC capture, and redraw scheduling

The Ordered TCP completion pump now skips connection gates when the published
completion-ready hint is empty. Publishing and consuming completions still use the
connection gate and existing order, credit and generation checks. The hint is
only an early-out; a racing publication remains eligible for the next probe.
Reproduce the Release ABBA loopback experiment with
`dotnet run --project benchmarks/Vega.NativeTcp.Benchmarks -c Release -- --completion-gate`.
255 connected peers, eight sends per active peer per tick; two samples per variant:

| Metric | Locked empty probes | Skip empty probes |
| --- | ---: | ---: |
| 8 active peers, completion drain p95 | 0.129 / 0.121 ms | 0.094 / 0.095 ms |
| 8 active peers, completion drain p99 | 0.157 / 0.143 ms | 0.097 / 0.102 ms |
| 255 active peers, completion drain p95 | 0.545 / 0.527 ms | 0.593 / 0.483 ms |
| 255 active peers, throughput | 156.56 / 158.65 MiB/s | 157.18 / 155.90 MiB/s |
| 255 active peers, send-to-callback p95 | 28.771 / 28.663 ms | 28.602 / 28.758 ms |

All bytes and callbacks were verified. Sparse drain p95/p99 improved about
24%/34%; dense throughput and latency were effectively unchanged across these
samples. The Ordered transport remains opt-in.

SSC periodic capture now copies native character data on the game thread, then
validates and serializes the detached snapshot on the existing asynchronous
reconciliation path. Game-thread callers (including pre-reset capture) still
complete synchronously. For 255 synthetic full-size snapshots (18,994-character
JSON payload), the measured dispatcher callback time for encoding alone was
77.094 ms before versus 0.651 ms after, with 13,049,880 versus zero managed
bytes allocated inside the measured callback. Native copy, storage I/O and
end-to-end checkpoint latency are excluded; this moves work rather than
eliminating total process CPU or allocations. The regression measurement is in
`TerrariaSscSnapshotTests`.

Scene redraws rotate unfinished requests after each cooperative batch. A native
renderer integration fixture with a recording socket confirms a one-tile update
arrives second rather than after all four batches of a larger preceding redraw.
The admission cap, cooperative budget and socket identity checks remain in force.
These are synthetic/loopback measurements, not joined-player or WAN qualification.

### World-event SDK dispatch (27 November)

The guarded, typed `WorldEvents` path is exercised by
`benchmarks/Vega.PluginSdk.Benchmarks`: five million calls per path, one native
event subscriber/policy and the same per-plugin guard used by runtime callbacks.
The local Release run measured 13.62 ns for an empty subscriber probe,
81.31 ns for probe plus guarded NPC-event delivery, and 54.46 ns for probe plus
guarded allow-policy evaluation; each loop allocated zero managed bytes.
Exact delivery and removal after disposal are asserted by the benchmark. These
are hub costs, not native patch overhead or live-client TPS. Raw output:
`artifacts/performance-2026-11-26/vega-world-events-benchmark.txt`.
