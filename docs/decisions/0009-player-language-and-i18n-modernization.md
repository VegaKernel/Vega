# ADR 0009: Modernize i18n and add per-player language selection

Status: Planned

## Context

TShock currently stores translation assets under `i18n/` and uses GetText-based `GetString()` calls. Language selection is effectively process-wide, with `TSHOCK_LANGUAGE` used to select the server language. That model cannot support players using different languages concurrently.

The translation tree and extraction/build workflow also predate the planned TShockNG architecture. Localization should become an explicit service with player/session context rather than an ambient global setting.

The existing `Users` table has no language preference, so registered players cannot retain a language selection across sessions.

Modern TShockNG modules must also not need to reference GetText.NET, duplicate the server `i18n/` loader, own culture fallback rules, or reimplement player-language resolution. Localization is a server service and modules consume it through stable contracts.

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
ILocalizer
ILanguageCatalog
ILocalizationContext
ILocalizationScopeFactory
PlayerLanguagePreference
```

The exact names are implementation details, but the responsibilities must be separated:

- discover available languages;
- validate and normalize culture identifiers;
- resolve the effective culture for a player;
- format translated messages for an explicit culture/player context;
- provide a server/default culture for console, logs, and contexts without a player;
- create module-scoped localization views/namespaces;
- cache loaded translation catalogs safely.

Do not implement per-player localization by mutating `CultureInfo.CurrentCulture`, `CurrentUICulture`, a process-global `TSHOCK_LANGUAGE`, or another shared static before sending a message. Multiple players with different languages must be safe concurrently.

Prefer APIs shaped around an explicit target, for example conceptually:

```text
localizer.Get(playerContext, "command.home.saved", args)
localizer.Get(culture, "command.home.saved", args)
localizer.GetServer("server.started", args)
```

Existing `GetString()`-based public/legacy APIs may remain as a compatibility facade during migration, resolving to the server/default culture where no player context exists.

### Module localization contracts

Modern modules must consume localization through `TShockNG.Contracts`; they must not require a direct package reference to GetText.NET or another concrete localization backend merely to translate their own messages.

Target usage should be comparable to:

```text
IModuleLocalizer<HomeModule> localizer

localizer.Get(playerContext, "home.teleported", homeName)
```

or an equivalent module-scope API created by the host.

Requirements:

- module code depends on interfaces/contracts only;
- the TShockNG host owns backend selection, catalog loading, culture normalization, caching and fallback;
- a module receives an isolated localization namespace/domain so keys from unrelated modules cannot collide accidentally;
- modules may ship source translation resources, but they must not need to ship the TShockNG runtime localization implementation or duplicate the server's `i18n/` infrastructure;
- module catalogs are discovered/registered through the module lifecycle rather than by writing into global static collections;
- unloading/reloading a modern module removes or replaces its catalog registration cleanly;
- the host provides the effective player culture, so modules do not query the account database or `/lang` persistence themselves;
- console/background/module startup messages can request the server/default culture without inventing a fake player;
- pluralization and formatted arguments remain backend-independent at the module-facing contract;
- missing module translations follow the same deterministic fallback rules as core translations and never make the module operation fail;
- localization contracts should permit future non-GetText backends without recompiling modules against a different localization package.

The preferred dependency direction is:

```text
Module
  -> TShockNG.Contracts.Localization
        ^
        |
TShockNG.Core localization service
        |
        -> GetText / other backend
```

A module must never depend on `TShockNG.Core` solely to obtain translations. The composition/module host injects the contract implementation.

Legacy plugins may continue to use historical `GetString()` helpers through `TShockAPI.dll`, but new module APIs should not expose GetText-specific types such as concrete catalog classes.

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
- Expose the player's effective culture through a stable read API so plugins can localize their own responses consistently.
- Provide localization interfaces suitable for dependency injection into modern modules.
- Keep backend-specific translation types out of `TShockNG.Contracts`.

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
- a module consuming localization without referencing the concrete localization backend package.

## Sequencing

This is a post-baseline modernization phase. Do not mix it into the active .NET 10 stabilization work.

Recommended order:

1. green .NET 10 baseline;
2. normalize repository layout;
3. introduce localization contracts and tests;
4. implement the Core localization service and module localization scopes;
5. migrate translation loading/tooling;
6. add per-session player language state;
7. add persistent account language preference;
8. add `/lang` command;
9. migrate player-facing core messages to explicit player localization;
10. migrate modern modules to the localization contracts;
11. remove obsolete global-only i18n paths after compatibility verification.

AntiProxy/reputation work is independent and is not part of this ADR.

## Consequences

Players can independently select their language without affecting other sessions, registered users retain their preference, and localization becomes a testable service rather than process-global state. Modern modules can localize messages through stable TShockNG contracts without carrying GetText.NET or duplicating the server localization stack. The migration touches a large number of player-facing messages, so it must be incremental and protected by compatibility tests rather than performed as one giant textual replacement.
