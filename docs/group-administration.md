# Group commands and chat presentation

The existing `vega.groups.manage` command permission, group delegation checks,
optimistic revisions and account/group inheritance semantics remain in force.
The following aliases are supported alongside Vega's existing commands:

| Command | Meaning |
| --- | --- |
| `group help` | Show the available group syntax. |
| `group list` | List groups with parent and revision. |
| `group show NAME` / `group listperm NAME` | Show direct grants, denies, parent, revision and presentation. |
| `group create NAME [PARENT]` / `group add NAME` | Create an empty group; only `create` accepts an authorized parent. Legacy `add` permission arguments are rejected, not reinterpreted as parent names. |
| `group delete NAME` / `group del NAME` | Delete an unreferenced group using the existing group mutation. |
| `group grant NAME PERMISSION` / `group addperm NAME PERMISSION` | Grant one permission with the existing delegation check. |
| `group revoke NAME PERMISSION` / `group delperm NAME PERMISSION` | Remove one explicit permission entry. |
| `group deny NAME PERMISSION` | Explicitly deny a permission. |
| `group parent NAME PARENT_OR_NONE` | Change or clear parent. |
| `group prefix NAME "TEXT"` | Set a display prefix (up to 64 characters). |
| `group suffix NAME "TEXT"` | Set a display suffix (up to 64 characters). |
| `group color NAME RRGGBB` | Set RGB chat color; decimal `r,g,b` also accepted. |

The reserved value `none` clears an individual presentation field. Include
spaces in quoted affixes where separation is desired, e.g.
`group prefix builders "[Builder] "`. The result is `[Builder] Alice: hello`.
Presentation is resolved from the account's current effective group, including
an active temporary role lease; it is not inherited across parents and does not
affect permissions. Unauthenticated sessions and groups without configured
presentation retain existing chat behavior. Moderation, recipient/world filtering,
mention rendering and session-bound delivery remain active.

Database schema version 2 creates the additive `vega_group_presentation` table
for SQLite, PostgreSQL and MySQL. Existing v1 databases migrate when opened;
presentation mutations use the group's existing revision and audit transaction.
No reset or re-import is required. As with other schema upgrades, preserve a
pre-upgrade database backup. Older Vega versions do not support schema v2.

Group rename and multi-permission creation/grants are deliberately not emulated:
group names may be referenced by accounts, live sessions, temporary role leases,
region ACLs and external configuration. A rename must migrate all references
atomically, which the current stores and runtime do not provide. The command
aliases are compatible with the single-permission Vega authority model, not an
implicit wildcard or bulk-grant mechanism.
