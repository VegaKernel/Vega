# Vega

<p align="center">
  <a href="README.md"><strong>Русский</strong></a>
  &nbsp;·&nbsp;
  <a href="README_EN.md">English</a>
  &nbsp;·&nbsp;
  <a href="docs/architecture.md">Архитектура</a>
  &nbsp;·&nbsp;
  <a href="docs/roadmap.md">Roadmap</a>
  &nbsp;·&nbsp;
  <a href="docs/decisions/">ADR</a>
</p>

Vega — самостоятельный современный server framework/runtime для Terraria.

Проект вырос из production-опыта TShock, но не рассматривается как TShock с модернизированной внутренней архитектурой. Старый код остаётся источником поведения, edge cases и regression-сценариев, а не архитектурным фундаментом.

## Архитектура

```mermaid
flowchart TB
    Terraria["Terraria / OTAPI"] --> TerrariaAdapter["Vega.Terraria<br/>Terraria integration boundary"]

    Runtime["Vega.Runtime<br/>process host + composition root"] --> TerrariaAdapter
    Runtime --> Core["Vega.Core<br/>server behavior"]
    Runtime --> Contracts["Vega.Contracts<br/>contracts + DTOs"]

    TerrariaAdapter --> Contracts
    Core --> Contracts

    Modules["Vega modules"] --> Contracts
    Runtime -. "explicit registration" .-> Modules

```

- `Vega.Contracts` — независимые контракты и DTO.
- `Vega.Core` — серверное поведение без Terraria и OTAPI.
- `Vega.Terraria` — единственное целевое место для Terraria/OTAPI-specific integration.
- `Vega.Runtime` — process host и composition root.
`Vega.Core` защищён architecture test: появление ссылки на Terraria/OTAPI ломает тесты. TShockAPI и TSAPI полностью удалены из runtime, solution и дерева исходников.

## Сборка

```bash
dotnet restore Vega.sln
dotnet build Vega.sln
dotnet test Vega.sln
```

Основной executable: `src/Vega.Runtime/Vega.Runtime.csproj`, assembly `Vega.Server`.

## Готовность сервера

`NetworkReady` является основной пользовательской точкой готовности. Необязательный warmup выполняется после открытия сервера. Terraria world loading измеряется отдельно от Vega startup overhead.

Подробнее: [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md).

## Документация

- [`docs/architecture.md`](docs/architecture.md)
- [`docs/roadmap.md`](docs/roadmap.md)
- [`docs/operations.md`](docs/operations.md)
- [`ADR 0016`](docs/decisions/0016-vega-independent-runtime-architecture.md)
- [`ADR 0017`](docs/decisions/0017-startup-lifecycle-readiness-and-performance-budgets.md)

## Лицензия

Оригинальный код Vega является закрытым и распространяется на условиях [`LICENSE`](LICENSE). Все права, прямо не предоставленные владельцем проекта, сохраняются за правообладателем.

Сторонние пакеты сохраняют лицензии своих правообладателей; подробности находятся в [`licenses/README.md`](licenses/README.md).
