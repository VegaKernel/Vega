# Резервные копии развёртывания

Vega создаёт deploy-local архивы восстановления без остановки сервера. Расписание
задаётся ровно тремя параметрами в `config/backups.json`:

```json
{
  "enabled": false,
  "interval": "06:00:00",
  "retainedBackups": 7
}
```

`interval` — invariant `TimeSpan` от пяти минут до 30 дней, `retainedBackups` — число
от 1 до 365. `enabled` управляет расписанием; авторизованный оператор всё равно может
выполнить `backup` вручную. `backup status` показывает то же ограниченное состояние,
что Operations/TUI. Одновременные scheduled/manual запуски отклоняются.

## Консистентность и содержимое

Сначала backup присоединяется к обычному world-save barrier. Terraria снимает изменяемое
состояние мира на game thread, завершает worker-save, а при включённом cache Vega ждёт
сборку соответствующего `.vworld`. Затем provider создаёт согласованный снимок БД:

- SQLite использует online backup API и не копирует живую пару database/WAL;
- PostgreSQL запускает `pg_dump` в custom format, передавая `PGPASSWORD` только через
  окружение дочернего процесса;
- MySQL/MariaDB запускает `mysqldump --single-transaction`, передавая `MYSQL_PWD` только
  через окружение дочернего процесса.

Для внешней БД dump-tool должен находиться в `PATH`. Его отсутствие или ненулевой exit
завершает backup ошибкой без публикации частичного архива.

Staging находится в `data/backup-staging/`, вне видимого набора архивов. Vega проверяет
ZIP и только потом атомарно переносит его в `backups/Vega-backup-<UTC>.zip`. Внутри есть
`world/`, `database/`, очищенный `config/`, `manifest.json` и `checksums.sha256`.
Password/token/secret/connection-string заменяются на `<redacted>`. Retention удаляет
только подходящие архивы, чей полный путь остаётся внутри фиксированного `backups/`.

## Проверка восстановления

1. Скопируйте архив вне рабочего сервера и проверьте все строки `checksums.sha256`.
2. Остановите целевой процесс. Верните `.wld` и при наличии `.vworld` в настроенный путь.
3. Верните `vega.sqlite3` как SQLite-файл либо используйте `pg_restore`/MySQL client.
4. Проверьте конфиг, задайте секреты отдельно, запустите версию из `manifest.json` и
   проверьте accounts, groups, regions и bans.

Регулярно выполняйте restore drill на целевом хосте: архив считается проверенным backup
только после успешной проверки checksum и восстановления мира с базой.
