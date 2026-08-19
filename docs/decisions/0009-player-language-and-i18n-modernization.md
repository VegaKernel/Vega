# ADR 0009: Modernize i18n and add per-player language selection

Status: Planned

## Context

TShock currently stores translation assets under `i18n/` and uses GetText-based `GetString()` calls. Language selection is effectively process-wide, with `TSHOCK_LANGUAGE` used to select the server language. That model cannot support players using different languages concurrently.

The translation tree and extraction/build workflow also predate the planned TShockNG architecture. Localization should become an explicit service with player/session context rather than an ambient global setting.

The existing `Users` table has no language preference, so registered players cannot retain a language selection across sessions.

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

Introduce an explicit localization abstraction, for example:

```text
ILocalizationService
ILanguageCatalog
PlayerLanguagePreference
```

The exact names are implementation details, but the responsibilities must be separated:

- discover available languages;
- validate and normalize culture identifiers;
- resolve the effective culture for a player;
- format translated messages for an explicit culture/player context;
- provide a server/default culture for console, logs, and contexts without a player;
- cache loaded translation catalogs safely.

Do not implement per-player localization by mutating `CultureInfo.CurrentCulture`, `CurrentUICulture`, a process-global `TSHOCK_LANGUAGE`, or another shared static before sending a message. Multiple players with different languages must be safe concurrently.

Prefer APIs shaped around an explicit target, for example conceptually:

```text
localizer.GetString(player, "...")
localizer.GetString(culture, "...")
localizer.GetServerString("...")
```

Existing `GetString()`-based public/legacy APIs may remain as a compatibility facade during migration, resolving to the server/default culture where no player context exists.

### Translation assets and tooling

Audit and simplify the `i18n/` tree and its extraction/compilation workflow.

Requirements:

- preserve useful existing translations;
- normalize culture directory identifiers to a consistent convention;
- detect duplicate, invalid, obsolete, and suspicious culture identifiers;
- remove stale generated artifacts and obsolete workflow machinery where it is no longer needed;
- make extraction/validation reproducible in CI without private external infrastructure where practical;
- validate translation files during CI;
- avoid requiring administrators to manually compile translation files on the production server;
- document how new languages are added and updated.

The GetText backend may be retained if it remains the best fit for the existing translation corpus, but TShock code must depend on the localization abstraction rather than directly coupling runtime behavior to GetText.NET. If a simpler maintained backend can consume or migrate the existing translations without losing translator workflow or pluralization support, evaluate it during this phase instead of preserving a package merely because it is old and present.

### Fallback rules

Use deterministic fallback behavior:

1. explicit player/account culture;
2. neutral/parent culture when appropriate and available;
3. configured server/default culture;
4. source/default message as the final fallback.

A missing translation must never prevent a command or gameplay action from completing.

### Compatibility and plugin API

- Preserve existing plugins that call legacy localization helpers where practical.
- Add modern explicit-culture APIs rather than changing global language state behind plugin code.
- Do not require plugins to understand database persistence for player language.
- Expose the player's effective culture through a stable read API so plugins can localize their own responses consistently.

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
- language catalog discovery and normalization.

## Sequencing

This is a post-baseline modernization phase. Do not mix it into the active .NET 10 stabilization work.

Recommended order:

1. green .NET 10 baseline;
2. normalize repository layout;
3. introduce localization abstractions and tests;
4. migrate translation loading/tooling;
5. add per-session player language state;
6. add persistent account language preference;
7. add `/lang` command;
8. migrate player-facing core messages to explicit player localization;
9. remove obsolete global-only i18n paths after compatibility verification.

AntiProxy/reputation work is independent and is not part of this ADR.

## Consequences

Players can independently select their language without affecting other sessions, registered users retain their preference, and localization becomes a testable service rather than process-global state. The migration touches a large number of player-facing messages, so it must be incremental and protected by compatibility tests rather than performed as one giant textual replacement.
