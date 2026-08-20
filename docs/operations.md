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

The TUI uses separate high-contrast, movable and resizable windows instead of repainting one large text document. Its default desktop contains:

- **Server / world**: lifecycle, uptime, world/readiness, listener port and player count;
- **Performance**: bounded TPS and process CPU history; the server window also shows rolling-minute TPS, lifetime min/max and tick-duration p50/p95/p99;
- **Memory**: bounded working-set and managed-heap history, plus current GC data;
- **Network**: inbound/outbound rates and cumulative packet/byte counters;
- **Players**: immutable `PlayerSnapshot` entries;
- **Modules**: version, load state, dependencies, startup duration and failures;
- **Logs**: recent structured records and degraded-capability warnings.

The default refresh interval is 750 ms, which avoids the full-screen 150 ms document replacement that made some Windows terminals flicker. Static labels are updated only when their text changes; graph and log history is bounded.

### Window layout

Drag a window title to move it and drag its border to resize it. The following local palette commands manage the desktop:

```text
:windows                 List available windows and visibility.
:show performance        Show a window.
:hide logs               Hide a window.
:layout save             Persist current positions, sizes and visibility.
:layout reset            Restore and persist the default desktop.
```

Layout is loaded from `config/tui.json`. `refreshIntervalMilliseconds` is clamped to 250–5000 ms, `historySamples` to 20–600, and unsafe window geometry is normalized. A missing or malformed file falls back to the built-in layout and produces a structured warning instead of preventing startup. The file is included in development and published server layouts.

The current network window intentionally exposes only counters that exist in the operations contract. Packet-type, handler-latency and security-action breakdowns will be added with the packet observability work in Phase 13; the TUI does not fabricate them from unavailable data.

The `tps` command (alias `tpsstats`, permission `tickrate.check`) reports the same immutable tick statistics outside the TUI. Below 15 TPS, the operations snapshot carries a critical warning. Tick statistics start at `NetworkReady`, so world-loading stalls do not corrupt the serving lifetime's extrema. Vega intentionally does not expose the legacy plugin's global reset/toggle switches because they could hide diagnostic history or alerts from other operations consumers.

Structured operational events are written independently to the bounded in-memory store, stdout outside headless mode, and `logs/vega-operations.jsonl` when the file sink is available.

## Command palette and logs

Enter a Vega command such as `serverinfo` or `kick <player> <reason>` in the palette to execute it through the normal `ICommandDispatcher` authorization and application boundary. Commands not owned by Vega are queued to Terraria's native server-console parser, so built-in operations such as `save`, `say`, `help` and `exit` remain available without a second thread reading terminal input.

Local palette actions control only the view:

```text
:pause
:follow
:level warning
:level all
:source lifecycle
:find budget
:clear
:windows
:show <overview|performance|memory|network|players|modules|logs>
:hide <overview|performance|memory|network|players|modules|logs>
:layout save
:layout reset
```

Log retention is bounded and filtering supports minimum level, source and text. Pausing freezes the displayed immutable snapshot; it never pauses server execution or log ingestion.
