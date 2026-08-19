# TShockLauncher composition root

`TShockLauncher` is the process composition root for ADR 0005.

It is responsible for constructing concrete `TShockNG.Core` services and passing a `TShockNG.Contracts.Runtime.ServerServices` bundle into the TShock integration boundary before TSAPI creates/initializes the TShock plugin.

This architecture-only PR intentionally does not implement or debug that construction yet. The next pass wires concrete services and restores compilation after the ownership split has been reviewed.

Domain code must not resolve services from the launcher or from a global container.
