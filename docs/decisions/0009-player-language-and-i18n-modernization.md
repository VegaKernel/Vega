# ADR 0009: Modernize i18n and add per-player language selection

Status: Planned

## Context

TShock currently stores translation assets under `i18n/` and uses GetText-based `GetString()` calls. Language selection is effectively process-wide, with `TSHOCK_LANGUAGE` used to select the server language. That model cannot support players using different languages concurrently.

The translation tree and extraction/build workflow also predate the planned TShockNG architecture. Localization should become an explicit service with player/session context rather than an ambient global setting.

The existing `Users` table has no language preference, so registered players cannot retain a language selection across sessions.

Modern TShockNG modules must also not need to reference GetText.NET, duplicate the server `i18n/` loader, own culture fallback rules, or reimplement player-language resolution. Localization is a server service and modules consume it through stable contracts.

A module owns the wording it contributes to the system, but it does not own delivery policy. The module provides its own localization resources and stable message keys. TShockNG decides when a message is emitted, which recipient/context it targets, which effective culture applies, and how the localized result is rendered and delivered.

## Decision

After the .NET 10 baseline is green, rework the i18n subsystem around an explicit localization service and add a `lang` player command.

### Player-facing behavior

Provide a built-in `lang` command with at least these forms:

```text
/lang
/lang list
/lang <culture>
/lang reset
```

Behavior:

- `/lang` shows the player's current language and concise usage information.
- `/lang list` shows the languages actually available to the running server.
- `/lang <culture>` changes the language immediately for that player after validating the culture identifier.
- `/lang reset` returns the player to the server/default language.
- Guests may select a language for the current connection/session only.
- Registered players persist their selected language in their account and automatically restore it after login/reconnect.
- A player changing language while already logged in must not need to reconnect.
- Invalid or unavailable culture identifiers must fail cleanly and show available alternatives.

### Persistence

Extend the user account model and `Users` table with a nullable language/culture preference, using a stable culture identifier such as `ru-RU`, `en-US`, `de-DE`, etc.

Requirements:

- schema migration must be automatic and compatible with existing SQLite, MySQL, and PostgreSQL databases;
- existing accounts default to no explicit preference;
- no preference means use the server/default language;
- account creation must not require a language value;
- login restores the persisted preference into the player's session;
- changing `/lang` while authenticated updates both the live player state and persistent account record.

### Localization architecture

Introduce explicit localization contracts in `TShockNG.Contracts`, for example:

```text
ILocalizationCatalog
ILocalizationRegistry
ILocalizationFormatter
ILocalizationContextResolver
PlayerLanguagePreference
LocalizedMessage
```

The exact names are implementation details, but the responsibilities must be separated:

- discover available languages;
- validate and normalize culture identifiers;
- resolve the effective culture for a player;
- format translated messages for an explicit culture/player context;
- provide a server/default culture for console, logs, and contexts without a player;
- create module-scoped localization namespaces;
- cache loaded translation catalogs safely;
- keep message selection/delivery separate from message text ownership.

Do not implement per-player localization by mutating `CultureInfo.CurrentCulture`, `CurrentUICulture`, a process-global `TSHOCK_LANGUAGE`, or another shared static before sending a message. Multiple players with different languages must be safe concurrently.

The preferred modern flow is conceptually:

```text
module/domain logic
    -> returns outcome / message key + arguments
TShockNG Core
    -> decides whether/when/to whom to emit
localization service
    -> resolves module namespace + recipient culture
message renderer/output
    -> sends the final localized text
```

For example, a Homes module may define resources such as:

```text
homes.teleported = Teleported to home '{0}'.
homes.not-found = Home '{0}' was not found.
```

but the module should return a structured result such as conceptually:

```text
CommandResult.Success("homes.teleported", homeName)
```

rather than directly calling `player.SendSuccessMessage(...)`. TShockNG owns the recipient/output channel and localizes that key using the module's registered catalog.

This rule is especially important for reusable domain services: domain code must not decide chat formatting, recipient routing, console-vs-player behavior, or language selection.

Existing `GetString()`-based public/legacy APIs may remain as a compatibility facade during migration, resolving to the server/default culture where no player context exists.

### Module-owned localization resources

Modern modules own their localization content. They register a namespaced catalog of message keys and translations with the host during module activation.

Requirements:

- each module defines and ships its own source/default messages and translations;
- module resources are namespaced by stable module identity so unrelated keys cannot collide;
- TShockNG does not require modules to reuse or reference TShock core message strings;
- TShockNG owns culture resolution, catalog lookup, fallback, formatting and delivery;
- a module may expose semantic message keys/outcomes to Core without knowing the final recipient;
- Core may suppress, aggregate, redirect, log, render differently, or deliver a module-provided message according to policy;
- module code must not need a direct dependency on GetText.NET or another concrete localization backend;
- module resources must be discoverable/registrable through the modern module lifecycle;
- unload/reload removes or replaces the module catalog cleanly;
- module translations follow the same fallback chain as core translations;
- resource registration should support future packaging formats without changing module business logic.

A useful separation is:

```text
Module
  owns:
    message keys
    source/default text
    translations
    semantic outcomes

TShockNG Core
  owns:
    recipient/context
    timing
    severity/channel
    culture selection
    fallback
    rendering
    final delivery
```

This means localization contracts are not merely a `GetString()` service handed to modules. The preferred modern architecture is structured results/events whose localization occurs at the application/output boundary. A direct module localizer may still exist for module-internal formatting needs, but it must not become the default way command/domain handlers send player-facing output.

### Translation assets and tooling

Audit and simplify the `i18n/` tree and its extraction/compilation workflow.

Requirements:

- preserve useful existing translations;
- normalize culture directory identifiers to a consistent convention;
- detect duplicate, invalid, obsolete, and suspicious culture identifiers;
- remove stale generated artifacts and obsolete workflow machinery where it is no longer needed;
- make extraction/validation reproducible in CI without private external infrastructure where practical;
- validate core and module translation files during CI;
- avoid requiring administrators to manually compile translation files on the production server;
- document how new core and module languages are added and updated.

The GetText backend may be retained if it remains the best fit for the existing translation corpus, but TShock code and modern modules must depend on the localization abstraction rather than directly coupling runtime behavior to GetText.NET. If a simpler maintained backend can consume or migrate the existing translations without losing translator workflow or pluralization support, evaluate it during this phase instead of preserving a package merely because it is old and present.

### Fallback rules

Use deterministic fallback behavior:

1. explicit player/account culture;
2. neutral/parent culture when appropriate and available;
3. configured server/default culture;
4. module/core source/default message as the final fallback.

A missing translation must never prevent a command or gameplay action from completing.

### Compatibility and plugin API

- Preserve existing plugins that call legacy localization helpers where practical.
- Add modern explicit-culture APIs rather than changing global language state behind plugin code.
- Do not require plugins/modules to understand database persistence for player language.
- Expose the player's effective culture through a stable read API so plugins can localize their own responses consistently when direct formatting is genuinely needed.
- Provide localization contracts suitable for dependency injection into modern modules.
- Keep backend-specific translation types out of `TShockNG.Contracts`.
- Prefer structured message keys/results over direct localized string emission from modern command/domain handlers.

### Tests

Add automated tests for at least:

- two simultaneous players using different languages;
- guest session-only selection;
- registered account persistence and restore after login;
- reset to server default;
- unavailable/invalid culture handling;
- fallback when a translation is missing;
- SQLite/MySQL/PostgreSQL schema compatibility where the existing test infrastructure permits;
- plugin/legacy `GetString()` compatibility behavior;
- language catalog discovery and normalization;
- two modules using the same local key without namespace collision;
- module translation using the player's effective `/lang` culture;
- fallback for a missing module translation;
- module catalog registration and clean removal/reload;
- a module consuming localization without referencing the concrete localization backend package;
- a module returning a structured message key while Core controls final delivery and culture resolution.

## Sequencing

This is a post-baseline modernization phase. Do not mix it into the active .NET 10 stabilization work.

Recommended order:

1. green .NET 10 baseline;
2. normalize repository layout;
3. introduce localization/message contracts and tests;
4. implement the Core localization service and module catalog registry;
5. migrate translation loading/tooling;
6. add per-session player language state;
7. add persistent account language preference;
8. add `/lang` command;
9. migrate player-facing core messages to structured localized output;
10. migrate modern modules to module-owned catalogs + structured results;
11. remove obsolete global-only i18n paths after compatibility verification.

AntiProxy/reputation work is independent and is not part of this ADR.

## Consequences

Players can independently select their language without affecting other sessions, registered users retain their preference, and localization becomes a testable service rather than process-global state. Modern modules own their own message text/translations without carrying the server localization backend, while TShockNG remains authoritative for when, where and in which culture those messages are rendered. The migration touches a large number of player-facing messages, so it must be incremental and protected by compatibility tests rather than performed as one giant textual replacement.
