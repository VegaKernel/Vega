# Vega

[Русский](README.md) | [English](README_EN.md)

Vega — самостоятельный современный server framework/runtime для Terraria.

Проект вырос из практического опыта TShock, но **не рассматривается как TShock с модернизированной внутренней архитектурой**. Старый TShock используется как источник проверенного production-поведения, требований, edge cases и тестовых сценариев. Его API и архитектурные формы не являются долгосрочным ограничением Vega.

## Архитектурное направление

```text
Terraria
   ↓
OTAPI
   ↓
Vega.Runtime / Vega.Terraria
   ↓
Vega.Contracts
   ↓
Vega.Core
   ↓
Vega.Modules
```

- OTAPI остаётся низкоуровневым instrumentation/patching layer.
- TSAPI не входит в целевую архитектуру Vega.
- `TShockAPI` не является целевым compatibility layer.
- Бинарная совместимость с TShock/TSAPI-плагинами не требуется.
- Source-level legacy compatibility не является долгосрочной целью.
- TerraZ-плагины будут переноситься на Vega module API.
- `Vega.Core` не зависит от Terraria, OTAPI, TSAPI или TShockAPI.

Существующие `TShock*` проекты и TSAPI submodule могут временно оставаться в дереве как migration scaffolding, пока не построен нативный runtime path Vega. Новая архитектура не должна углублять эти зависимости.

## Быстрый запуск

Скорость запуска является архитектурным инвариантом. Основная точка готовности — `NetworkReady`, после которой сервер может принимать игроков; необязательный warmup продолжается отдельно.

```text
ProcessStart
  → Bootstrap
  → RuntimeReady
  → ModulesRegistered
  → WorldLoading
  → WorldReady
  → NetworkReady
  → BackgroundWarmup
  → FullyWarmed
```

Terraria world loading измеряется отдельно от Vega startup overhead.

Подробности: [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md).

## Модули

Целевой module API будет Vega-native. Регистрация модуля должна быть дешёвой и синхронной; SQL, HTTP, длительный file I/O, world scans и warmup не выполняются в registration/constructors.

Startup-работа разделяется по readiness barrier: `RuntimeBarrier`, `WorldBarrier`, `NetworkBarrier`, `Background`, `Lazy`.

## .NET

Текущий runtime baseline — .NET 10.

## Документация

- [`docs/architecture.md`](docs/architecture.md) — целевая архитектура Vega;
- [`docs/roadmap.md`](docs/roadmap.md) — этапы перехода;
- [`docs/decisions`](docs/decisions) — ADR и история архитектурных решений;
- [`ADR 0016`](docs/decisions/0016-vega-independent-runtime-architecture.md) — переход от TShockNG migration model к самостоятельной Vega;
- [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md) — startup lifecycle, readiness и performance budgets.

## Текущее состояние дерева

Физические имена solution/projects (`TShock.sln`, `TShockAPI`, `TShockNG.*` и т. п.) ещё могут отражать промежуточный этап миграции. Их механическое переименование и удаление TSAPI/TShockAPI выполняются отдельными implementation changes, чтобы архитектурное решение не маскировалось огромным нерелевантным diff.

## Лицензия

GPL-3.0-or-later. Полный текст — [`COPYING`](COPYING).
