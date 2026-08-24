# AutoWorldCleaner

`Vega.Modules.AutoWorldCleaner` отвечает за опциональную политику очистки предметов и
снарядов. Модуль по умолчанию выключен и настраивается отдельно от автосохранения в
`config/auto-world-cleaner.json`.

```json
{
  "enabled": false,
  "intervalMinutes": 30,
  "onlyWhenPlayersOnline": true,
  "items": {
    "enabled": true,
    "minimumActiveCount": 200,
    "minimumAgeSeconds": 300
  },
  "projectiles": {
    "enabled": true,
    "minimumActiveCount": 700,
    "excludeMinions": true,
    "excludeSentries": true
  }
}
```

`intervalMinutes` ограничивается диапазоном 1–1440. Очистка предметов начинается только
после достижения `minimumActiveCount`; кандидатами становятся лишь предметы не моложе
`minimumAgeSeconds`. Для снарядов порог считается только по сущностям, которые не
исключены настройками minion/sentry.

Расписание, пороги и исключения принадлежат модулю. Доступ к Terraria ему не принадлежит:
модуль использует `IWorldEntityMaintenance` для immutable snapshot предметов/снарядов и
защищённых операций удаления. Один запуск целиком помещается в game thread Terraria:
snapshot, выбор кандидатов и удаление выполняются в одной операции. Terraria-адаптер
дополнительно сверяет возраст предмета и identity снаряда перед удалением, поэтому
устаревший handle не удалит сущность из повторно использованного слота.

`onlyWhenPlayersOnline` пропускает запуск, если на сервере нет игроков. До стадии `WorldReady` cleaner также ничего не трогает, поэтому короткий интервал не может попасть в частично загруженный мир. Запуски не
перекрываются, а остановка модуля отменяет периодический scheduler. Текущий Terraria
adapter обслуживает только `WorldId.Primary`; контракт уже world-scoped, поэтому будущие
sandbox backend смогут дать собственную реализацию без утечки Terraria globals в модуль.

## Команды и наблюдаемость

`worldclean run` (или `cleanworld run`) запускает очистку немедленно через ту же политику,
что и scheduler. `worldclean status` показывает последнее состояние. Требуется permission
`vega.cfg.maintenance`; выключенный в конфиге модуль не допускает и ручной обход
политики.

Operations snapshot содержит enabled/state, времена запуска/завершения и количество
удалённых предметов/снарядов. Ошибка cleaner появляется как dashboard warning.
Структурированные логи используют source `auto-world-cleaner`.
