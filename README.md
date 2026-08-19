# Vega

[Русский](README.md) | [English](README_EN.md)

Vega — самостоятельный современный server framework/runtime для Terraria.

Проект вырос из production-опыта TShock, но не рассматривается как TShock с модернизированной внутренней архитектурой. Старый код остаётся источником поведения, edge cases и regression-сценариев, а не архитектурным фундаментом.

## Текущие границы

```text
Terraria / OTAPI
       ↓
Vega.Terraria
       ↓
Vega.Runtime
      / \
Vega.Contracts  Vega.Core
       ↑
Vega modules
```

- `Vega.Contracts` — независимые контракты и DTO.
- `Vega.Core` — серверное поведение без Terraria, OTAPI, TSAPI и TShockAPI.
- `Vega.Terraria` — единственное целевое место для Terraria/OTAPI-specific integration.
- `Vega.Runtime` — process host и composition root.
- `TShockAPI` и `external/TSAPI` пока остаются только как migration scaffolding и не являются частью target architecture.

`Vega.Core` защищён architecture test: появление ссылки на Terraria/OTAPI/TShockAPI ломает тесты.

## Solution

```bash
dotnet restore Vega.sln
dotnet build Vega.sln
dotnet test Vega.sln
```

Основной executable: `src/Vega.Runtime/Vega.Runtime.csproj`, assembly `Vega.Server`.

## Быстрый запуск

`NetworkReady` является основной пользовательской точкой готовности. Необязательный warmup выполняется после открытия сервера. Terraria world loading измеряется отдельно от Vega startup overhead.

Подробнее: [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md).

## Документация

- [`docs/architecture.md`](docs/architecture.md)
- [`docs/roadmap.md`](docs/roadmap.md)
- [`ADR 0016`](docs/decisions/0016-vega-independent-runtime-architecture.md)
- [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md)

## Лицензия

GPL-3.0-or-later. Полный текст — [`COPYING`](COPYING).
