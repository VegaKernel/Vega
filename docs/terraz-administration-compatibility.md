# TerraZ administration compatibility

The built-in commands reuse the account/role-lease, moderation, world item and tick-rate services rather than loading the TShock plugins. Existing Vega commands remain available.

| Original plugin | Commands |
| --- | --- |
| TempGroup | `/tg help`, `/tg add <account name/id> <group> [duration]`, `/tg del <account>`, `/tg info <account>`, `/tg list [page]`, `/tempstatus` (`/temps`, `/ts`); `/tempg` and `/tempgroup` are aliases for `/tg` |
| ExtendedBans | `/bt add <player/account> <reason> [time]`, `/bt addip <ip> <reason> [time]`, `/bt addrange <CIDR> <reason> <time>`, `/bt delrange <CIDR>`, `/bt listrange [page]`, `/bt info <player/IP>`, `/mute add <player/account> [time] [reason]`, `/mute del <player/account>`, `/mute list [page]` |
| TSExt_ItemCommands | `/item <item name/id> [amount] [prefix]`, `/give <item name/id> <player> [amount] [prefix]` (`/i`, `/g`); omitted or zero amount means native maximum stack |
| TSExt_UICommand | `/ui <player>`, `/ai <account>`, `/ai-id <id>`; separate sensitive information permissions and platform on `/ui` |
| TPSChecker | `/tps`, `/tpsstats`, `/tpsalerts`, `/tpsreset`; Vega-specific percentile diagnostics remain available via `/tpsmetrics` |

Durations accept combinations such as `1d2h` and `10h-30m+2m`. TempGroup without expiry lasts until revoked. ExtendedBans uses Vega's durable UUID tickets rather than integer TShock tickets; `0` means permanent. Range operations require `vega.admin.ban.iprange` or `tshock.admin.ban.iprange` in addition to ban permission. TPS administration requires `tickrate.admin`; low-TPS alerts are sent at most once per five seconds when TPS is below 15.

## ControlGroupOption

Optional `data/ControlGroupOption.json` at the server installation root, read at startup:

```json
{
  "Groups": {
    "moderator": ["default", "vip"],
    "administrator": ["*"]
  }
}
```

When configured, delegated account creation, permanent group changes and temporary-group grants/revocations through the chat commands must satisfy both this allowlist and Vega's existing permission-delegation checks. An unlisted delegating group is denied; the trusted server console is unrestricted. The allowlist is optional and does not grant permissions by itself. Restart after changing it. Trusted administrative REST/data-service operations retain their existing authorization boundary.

## Persistence

The initial release schema includes account registration/last connection metadata and optional mute identity associations to the shared relational schema (SQLite, MySQL and PostgreSQL). Old account rows without a registration timestamp remain unknown rather than being assigned an invented history. TShock account import preserves registration, last IP and UUID when the source has those fields. Ordinary plugin identity snapshots and REST account listings do not expose raw connection identifiers. Live external-provider verification uses the existing `VEGA_TEST_MYSQL` / `VEGA_TEST_POSTGRESQL` test connections to disposable databases.
