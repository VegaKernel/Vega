# Event pipeline

TShockNG evolves the existing `HandlerList<T>` API instead of introducing a second event framework.

## Existing behavior

`HandlerList<T>` already provides ordered priorities:

1. `Highest`
2. `High`
3. `Normal`
4. `Low`
5. `Lowest`

Handlers can opt into receiving an event after another handler marks it handled by setting `gethandled: true`.

## Monitor priority

TShockNG adds:

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

The existing `HandledEventArgs` model does not carry a structured cancellation reason. A reason should be added only through a compatibility-safe extension or a new typed event layer; the existing public member shape must not be broken merely to make the API prettier.
