# Event pipeline

Vega uses one typed event pipeline with explicit ordering and cancellation semantics.

## Existing behavior

The pipeline provides ordered priorities:

1. `Highest`
2. `High`
3. `Normal`
4. `Low`
5. `Lowest`

Handlers can opt into receiving an event after another handler marks it handled by setting `gethandled: true`.

## Monitor priority

Vega adds:

6. `Monitor`

A `Monitor` registration always receives handled events, regardless of the `gethandled` argument. It runs after all existing priorities and is intended for read-only observers such as:

- audit plugins;
- diagnostics;
- metrics;
- logging.

A monitor handler should observe the final state and should not attempt to reverse or replace an earlier decision.

## Compatibility

Existing numeric values `Highest = 1` through `Lowest = 5` are unchanged. Existing registrations therefore keep their ordering and behavior. `Monitor = 6` is additive.

## Future work

Cancellation carries a structured reason and source through the typed event context. The integration boundary applies the final side effect after all handlers and monitors have run.
