# ADR 0022: Domain persistence and relational providers

- Status: Accepted
- Date: 2026-08-20

## Context

Vega needs persistent accounts, groups, bans, regions, language preferences and module-owned state. TShock exposed database helpers, query builders and mutable managers as a broad application surface. Recreating those shapes would couple Core and modules to SQL, make transactions implicit and allow every consumer to invent incompatible schemas and concurrency rules.

The runtime must ship with an embedded store while allowing operators to use PostgreSQL or MySQL. Database initialization is required for admission-critical data, but ADR 0017 forbids SQL during cheap Bootstrap and module registration.

## Decision

`Vega.Persistence` owns relational drivers, connection creation, schema history and provider-specific migration SQL. The supported providers are:

- SQLite as the default embedded provider;
- PostgreSQL through Npgsql;
- MySQL/MariaDB through MySqlConnector.

The common implementation uses the .NET `System.Data.Common` abstractions. Vega does not add EF Core, Dapper or a generic repository/SQL service. Provider differences remain explicit where they are real: DDL, upsert syntax, migration locks and transactional capabilities.

Contracts expose domain capabilities such as `IGlobalBanReplica`, not `DbConnection`, table names or arbitrary query execution. A future external provider implements the same domain contracts; Core, Terraria integration and `ServerPlugins` do not receive a database session.

Schema changes are ordered, versioned and recorded in `vega_schema_history`. SQLite and PostgreSQL apply schema changes in transactions; PostgreSQL serializes concurrent upgrades with a transaction-scoped advisory lock. MySQL uses a database-scoped named lock because its DDL implicitly commits; each MySQL migration must therefore be atomic or idempotent and safely retryable before its history row is recorded.

Database configuration is resolved during Bootstrap because it determines startup, but connections and migrations run as a required `IRuntimeStartupParticipant` after `RuntimeReady` and `ModulesRegistered`, before world loading. A selected provider that cannot connect or migrate fails startup. Vega must never fall back silently to SQLite from an explicitly selected external provider.

External connection strings should come from the configured environment variable. A literal configuration value is supported for controlled deployments but is not the recommended secret path.

The first real repository is the revision-aware global-ban replica already consumed by cluster scenarios. It persists create/revoke tombstones, rejects stale or duplicate revisions and filters expired entries. This establishes persistence infrastructure; it does not make the complete account, moderation or administration product surface finished.

## Consequences

- The default archive can start without an external service and creates `data/vega.db`.
- PostgreSQL and MySQL use the same domain behavior without leaking provider APIs into Contracts/Core.
- Adding a domain requires a narrow contract, provider migration and cross-provider behavioral tests; it does not justify a universal DAL.
- External database integration tests require real disposable services in CI; constructor/configuration tests are not a substitute for those provider contract tests.
- Operators must back up the selected database before upgrading across released schema versions.
