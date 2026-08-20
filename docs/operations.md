# Operations API and Terminal UI

Vega exposes operational state through immutable contracts in `Vega.Contracts` and implementations in `Vega.Operations`. `Vega.TerminalUI` is an optional consumer of that API. Neither project reads or mutates Terraria globals; `Vega.Terraria` samples world and tick state on the game thread and publishes an immutable snapshot.

## Runtime modes

Select the operations mode with `--vega-ui`:

```text
--vega-ui=auto       Use TUI for an interactive terminal, otherwise plain mode.
--vega-ui=tui        Request the Terminal.Gui dashboard.
--vega-ui=plain      Keep normal console input/output for scripts and SSH.
--vega-ui=headless   Start without an interactive UI or operations stdout sink.
```

The Vega option is removed before arguments are passed to Terraria. TUI initialization is best-effort on a background thread. A missing or incompatible terminal never blocks server startup; structured file logging and normal server execution continue.

## Dashboard

The TUI refreshes a bounded snapshot containing:

- lifecycle, uptime, world/readiness, listener port, players and sampled TPS;
- immutable `PlayerSnapshot` entries;
- module name, version, load state, dependencies, startup duration and failures;
- process working set, managed memory, GC heap/fragmentation/collections and thread-pool availability;
- inbound/outbound packet and byte counters;
- recent structured log records and degraded-capability warnings.

Structured operational events are written independently to the bounded in-memory store, stdout outside headless mode, and `logs/vega-operations.jsonl` when the file sink is available.

## Command palette and logs

Enter an ordinary command such as `serverinfo` or `kick <player> <reason>` in the palette to execute it through the normal `ICommandDispatcher` authorization and application boundary.

Local palette actions control only the view:

```text
:pause
:follow
:level warning
:level all
:source lifecycle
:find budget
:clear
```

Log retention is bounded and filtering supports minimum level, source and text. Pausing freezes the displayed immutable snapshot; it never pauses server execution or log ingestion.
