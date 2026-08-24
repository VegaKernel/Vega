# Автосохранение мира

`config/world-maintenance.json` управляет расписанием автосохранения dedicated server.
Vega заменяет фиксированный таймер Terraria, поэтому работает только одно расписание.
По умолчанию мир сохраняется каждые 10 минут.

```json
{
  "autoSave": {
    "enabled": true,
    "intervalMinutes": 10
  }
}
```

Интервал ограничивается диапазоном 1–1440 минут. Изменяемое состояние Terraria
подготавливается в game thread, после чего файловая запись выполняется worker-потоком.
Если включён world image, успешное ручное или автоматическое сохранение также обновляет
`.vworld` через объединённую атомарную очередь публикации.

Авторизованный оператор использует `save`; команда входит в тот же объединённый барьер,
поэтому одновременное ручное сохранение и autosave не дублируются. Команда
`shutdown [причина]` (псевдонимы `stop` и `off`) запрашивает штатный выход server loop
Terraria с `SaveOnServerExit=true` и не убивает процесс напрямую. Обе команды требуют
право `vega.cfg.maintenance`.

Очистка предметов и снарядов намеренно не относится к world-maintenance config. Её
политикой и scheduler владеет опциональный модуль `Vega.Modules.AutoWorldCleaner`;
см. [руководство AutoWorldCleaner](auto-world-cleaner.md) и
`config/auto-world-cleaner.json`.

Ошибки автосохранения используют source `world-maintenance`, публикация world image —
`world-image`.
