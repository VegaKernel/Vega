# Plugin ceremony comparison

This is the Phase 24 authoring-cost comparison for ordinary Vega plugins. It measures the public author surface, not runtime implementation complexity and not source compatibility with TShock.

## References and counting rules

Vega evidence is the maintained `templates/vega-plugin/HelloPlugin.cs`, `samples/Vega.PluginSamples/UsabilityPlugin.cs` and the public `Vega.PluginSdk` surface on this branch. The TShock reference is the project-owned `TerraZ-Team/Homes@4e7d33fe3dba7c94a498b04553df10c92f056007`: its `Homes.cs` owns `TerrariaPlugin`, `Initialize` and `Dispose`; `TShockCommandsHandler.cs` pairs `Commands.ChatCommands.Add` with `Remove`; `TShockEventsHandler.cs` pairs event subscription with unsubscription.

The comparison uses normalized lower-bound ceremony rather than raw repository LOC:

- a **ceremony line** is a non-empty source line required to connect the behavior to the framework; imports, namespace declarations and domain/business logic are excluded, while type declarations, lifecycle methods, registrations and required result/lifetime plumbing are included;
- **framework concepts** are distinct author-visible framework types/surfaces that must be understood for the scenario;
- **lifetime calls** are explicit author-written unregister, unsubscribe, cancel or dispose operations that must mirror setup;
- **documentation steps** are distinct mandatory workflow topics a new author must learn to implement and safely unload the scenario;
- values written as `>=` are conservative lower bounds because TShock does not provide one owned facade equivalent to Vega's storage/task lifetime helpers.

A normalized empty Vega plugin shell is 8 ceremony lines (`VegaPlugin`, descriptor and `Configure`). The equivalent TShock shell modeled directly on the pinned Homes plugin is 15 lines (`ApiVersion`, `TerrariaPlugin`, metadata, constructor, `Initialize` and `Dispose`). This baseline is included in complete-plugin scenarios below so a terse TShock command delegate is not compared against a Vega handler while silently ignoring the TShock plugin lifecycle around it.

## Ordinary scenario matrix

| Scenario | Vega ceremony lines | TShock ceremony lines | Vega framework concepts | TShock framework concepts | Vega lifetime calls | TShock lifetime calls | Documentation steps | Result |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| One command with permission, alias and reply | 17 | 22 | 4 | 5 | 0 | 1+ | 3 vs 5 | Vega is no more ceremonial after the callback registration overload; dedicated `ICommandHandler` remains available for larger commands. |
| Join/leave hooks with messaging | 12 | 25 | 3 | 5 | 0 | 2 | 3 vs 5 | Vega owns both subscriptions and removes them on unload. |
| Player lookup plus message/kick/teleport | 3 | 3 | 2 | 2 | 0 | 0 | 2 vs 2 | Roughly equal call-site cost; Vega uses immutable snapshots and operations instead of mutable `TSPlayer`. |
| Validated JSON configuration | 4 | >= 8 | 2 | >= 4 | 0 | 0 | 3 vs >= 4 | Vega owns path confinement, deserialize/load and validation plumbing. |
| Small persistent JSON state | 2 | >= 5 | 2 | >= 3 | 0 | 0 | 2 vs >= 3 | Vega exposes plugin-scoped storage without the server database or manual path ownership. |
| Repeating gameplay task | 2 | >= 6 | 1 | >= 3 | 0 | >= 1 | 2 vs >= 4 | `Tasks.Every` owns timer lifetime, cancellation and game-thread posting. |
| Async external I/O then safe game-state apply | 4 | >= 6 | 1 | >= 3 | 0 | >= 1 | 3 vs >= 5 | `Tasks.RunIo` owns cancellation and the return to the authoritative game thread. |
| Hot unload of the above registrations/tasks | 0 reverse lines | >= 4 | 1 | >= 4 | 0 | >= 4 | 1 vs >= 3 | Vega reverses tracked registrations/tasks centrally; the pinned Homes plugin explicitly unregisters commands and hooks. |

The open cancellable build/gameplay-hook sample is not counted yet because it does not exist as an ordinary sample. The advanced typed-packet validator is deliberately excluded: Phase 24 treats it as a specialist API whose concurrency and trust-boundary constraints are intentionally more complex than ordinary plugin authoring.

## Command ceremony finding and correction

The first comparison pass found one real Vega defect: a trivial command required a dedicated `ICommandHandler` class even when the command had no reusable behavior. That made the smallest command locally more verbose than TShock's delegate registration even though Vega already removed manual unload work.

`IPluginCommands` therefore now supports callback registration with inline name, aliases, permissions, console policy and syntax. The callback is wrapped in an internal `ICommandHandler` and passed to the existing command registry. There is no second dispatcher, reflection scan or alternate authorization path. The host still owns the returned registration lease through the same plugin lifetime scope.

The maintained `dotnet new` template uses this callback form. Plugin authors can still register an explicit `ICommandHandler` when a command is large enough to deserve its own type.

## Conclusion

For the implemented ordinary scenarios, Vega is at or below TShock-class total ceremony once the complete plugin lifecycle is counted. The main structural difference is favorable: Vega moves reverse registration, task cancellation, thread return and plugin-local storage ownership into the host rather than requiring every plugin to reproduce them.

This comparison closes only the Phase 24 ceremony-measurement item. It does not close the missing cancellable gameplay-hook sample, SDK compatibility/versioning gate, independent-new-author usability exercise or final public-surface review.
