# Plugin identity SDK

`plugin.Identity` supports account lookups by name or `AccountId`, group and permission queries, exact authenticated session lookup, and the current temporary group. Account projections deliberately omit passwords, IPs and UUIDs.

`OnAccountChanged`, `OnGroupChanged` and `OnTemporaryGroupChanged` observe successful committed mutations in the current process. Their messages include operation, actor and detached before/after values. SDK callbacks are posted to the game-thread queue, isolated from the SQL writer, and retired with the plugin activation. The bounded queue can drop events; snapshots remain authoritative. Separate server processes and automatic expiry of a lease are not an event replication stream. Permanent group reassignment updates current local authenticated sessions immediately.

## Delegated group assignment

Use the existing server plugin loader configuration `config/modules.json` (`identityGroupAssignments`) to explicitly authorize specific groups for a plugin, for example:

```json
{
  "identityGroupAssignments": {
    "TerraZ.Donations": ["default", "vip", "supporter"]
  }
}
```

Without an entry, `GroupAssignmentsAvailable` is false. With one, `AssignGroupAsync`, `GrantTemporaryGroupAsync` and `RevokeOwnTemporaryGroupAsync` perform typed persistent operations, audited as `plugin:<name>`. The old and new base groups must be listed. A temporary assignment cannot overwrite another issuer's active lease, and a plugin can revoke only its own lease. The persistence layer compares account and lease revisions under the transaction. Group definitions and credentials are not writable through this facade. This is useful for donation and reward plugins without granting arbitrary account administration.
