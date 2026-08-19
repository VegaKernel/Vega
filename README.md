# TShockNG

[Русский](README.md) | [English](README_EN.md)

TShockNG — модернизируемая серверная платформа для Terraria на базе TShock и Terraria Server API. Проект сохраняет привычную модель TShock там, где она ещё нужна плагинам, но постепенно переносит поведение в отдельные контракты и сервисы без требования бинарной совместимости со старыми сборками плагинов.

> Проект находится в активной архитектурной переработке. Принятые решения и порядок миграции описаны в [`docs/decisions`](docs/decisions) и [`docs/roadmap.md`](docs/roadmap.md).

## Основные направления

- .NET 10 как единая целевая платформа.
- `TShockNG.Contracts` для независимых контрактов, DTO и интерфейсов.
- `TShockNG.Core` как целевой слой серверного поведения и прикладных сервисов.
- `TShockAPI` как переходный интеграционный слой и source-level поверхность для существующих хуков и команд.
- Типизированная обработка сетевого протокола через `TZ.Multiplicity`.
- Явное разделение игрового потока, фоновой работы, инфраструктуры и доменной логики.
- Постепенная декомпозиция старых монолитных подсистем без сохранения бинарной совместимости ради уже собранных плагинов.

## Структура репозитория

```text
src/
  TShockAPI/            переходный TShock/API и интеграция
  TShockNG.Contracts/   независимые контракты
  TShockNG.Core/        современная серверная логика
  TShockLauncher/       запуск сервера
  TShockInstaller/      установщик
  TShockPluginManager/  управление плагинами

tests/                  тесты
external/
  TSAPI/                 Terraria Server API, git submodule
  legacy/                временные внешние бинарные зависимости
docs/                    архитектура, ADR и документация
i18n/                    локализации
```

## Требования

- .NET SDK 10
- Git с поддержкой submodules

## Клонирование

```bash
git clone --recurse-submodules https://github.com/TShockNG/TShockNG.git
cd TShockNG
```

Если репозиторий уже был клонирован без submodules:

```bash
git submodule update --init --recursive
```

## Сборка

```bash
dotnet restore TShock.sln
dotnet build TShock.sln
```

## Тесты

```bash
dotnet test TShock.sln
```

CI дополнительно собирает и тестирует подключённый TSAPI.

## Запуск

```bash
dotnet run --project src/TShockLauncher
```

## Публикация

Пример framework-dependent сборки для Linux x64:

```bash
dotnet publish src/TShockLauncher/TShockLauncher.csproj \
  -c Release \
  -f net10.0 \
  -r linux-x64 \
  --self-contained false
```

Runtime Identifier можно заменить, например, на `win-x64`, `linux-arm64` или другой поддерживаемый .NET 10 RID.

## GeoIP

GeoIP использует локальные MMDB-базы MaxMind. При включённом GeoIP сервер ищет в каталоге TShock:

```text
GeoLite2-Country.mmdb
GeoLite2-ASN.mmdb
```

Базы не скачиваются при подключении игрока. Геолокация и ASN отделены от будущей системы определения VPN/proxy/Tor.

## Совместимость плагинов

TShockNG **не гарантирует бинарную совместимость** с уже собранными плагинами TShock. Плагины могут требовать пересборки.

На переходном этапе сохраняется узкая source-level поверхность для регистрации существующих хуков и `Command`. Новые подсистемы должны использовать контракты и сервисы TShockNG, а не расширять старые глобальные API.

Подробнее: [`ADR 0015`](docs/decisions/0015-source-level-plugin-registration-contracts.md).

## Архитектура

Основная документация:

- [`docs/architecture.md`](docs/architecture.md) — текущая архитектура;
- [`docs/roadmap.md`](docs/roadmap.md) — порядок модернизации;
- [`docs/decisions`](docs/decisions) — архитектурные решения ADR.

## Лицензия

Проект распространяется на условиях GPL-3.0-or-later. Полный текст лицензии находится в [`COPYING`](COPYING).
