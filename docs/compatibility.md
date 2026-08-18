# Compatibility policy

## Objective

TShockNG should modernize internals aggressively while keeping the existing TShock plugin ecosystem usable during migration.

## Binary compatibility

Existing compiled plugins reference public metadata from `TShockAPI.dll` and `TerrariaServer.dll`. Preserving only type names in source is not enough.

During the compatibility period:

- keep assembly names expected by existing plugins;
- keep existing public types where practical;
- keep public member shapes and signatures where binary compatibility matters;
- do not change a public field into a property merely because the new design would prefer one;
- do not remove constructors used by existing plugins without an adapter path.

## Source compatibility

New typed APIs may be added alongside old APIs.

Old APIs may be marked:

```csharp
[Obsolete("Use the typed packet API instead.", false)]
```

`false` is intentional: migration warnings are acceptable; forced immediate breakage is not.

## Legacy packet hooks

The canonical internal path is:

```text
TSAPI raw packet
    -> Multiplicity typed packet
    -> new TShockNG logic
    -> legacy event adapter
    -> existing plugin hook
```

The legacy API must not remain the architectural source of truth.

## Deprecation stages

1. Supported and recommended.
2. Supported but obsolete with a warning.
3. Frozen legacy API: bug fixes only.
4. Removal only in a documented incompatible major API transition.

## Compatibility testing

TShockNG should eventually maintain a small compatibility test suite containing representative prebuilt plugins to verify that existing binaries still load and their key hooks remain callable.
