# Stock Terraria 1.4.6.1 SSC qualification

This is a reproducible **manual stock-client acceptance fixture**, not a client bot or protocol simulator.
The installed Windows Steam `Terraria.exe` was inspected locally: its product, file and managed
assembly versions are all **1.4.6.1**. Its `-savedirectory <path>` argument sets the native save root.
The fixture records the exact executable and SHA-256, copies the input world and starts only a private
loopback Vega server; it never launches the graphical client, presses keys, touches existing character
profiles, or emits client packets.

## Prepare a case (Windows)

From a current Vega checkout, publish a fresh server package. Supply a **copyable test world**, not a
production instance. The script uses checkout defaults rather than inheriting a deployed server's
database or network configuration, disables dynamic plugin loading, strips inherited `VEGA_*` settings
from the child process, supplies `-savedirectory` to the server and uses an ephemeral `127.0.0.1` port.

```powershell
dotnet publish src/Vega.Runtime -c Release -r win-x64 --self-contained false -o artifacts/stock-ssc-publish
.\scripts\New-StockSscQualification.ps1 -ServerRoot artifacts/stock-ssc-publish `
    -WorldFile C:/path/to/test.wld -OutputDirectory artifacts/stock-ssc-qualification `
    -CharacterMode authoritative -JoinMode password -RunServer
```

`-RunServer` runs the isolated server in the invoking console. Omit it to prepare files only, or
use `-SmokeReady` instead to verify native startup, expected SSC mode, isolated SQLite creation and
graceful shutdown without opening any client. `-ClientExecutable <path>` selects a stock executable
if Steam is installed elsewhere. Client version mismatch stops preparation. `-OrderedTcp` selects
the optional transport for a separate comparison; this qualification fixture explicitly selects native TCP for its baseline.

Run the four cases independently (new directory and new data for each):

| Primary character mode | Join mode |
| --- | --- |
| `authoritative` | `password` |
| `authoritative` | `passwordless` |
| `disabled` | `password` |
| `disabled` | `passwordless` |

Each directory has `qualification.json`, `client-manual.txt`, separate server/native/client save roots,
copied world, database and operation logs. The manifest contains a disposable local fixture password
if needed; never reuse production credentials. Preserve the case directory when investigating failures.

## Human client steps

Wait for the server to report `NetworkReady`. In another terminal **manually** use the command from
`client-manual.txt`, which passes the isolated save directory to stock Terraria. Its local save root is
separate from the usual Documents directory. Steam Cloud is a separate facility: create a new local-only
test character, never select an existing cloud character or click a move-to-cloud operation. Do not use
an existing play profile or share the disposable character with production worlds.

1. Select Multiplayer → Join via IP, choose the new character and enter `127.0.0.1` and the manifest
   port. A password case should prompt and accept its fixture password; a passwordless one should not
   prompt. An incorrect password should reject an ordinary join. Record the actual client outcome.
2. In authoritative mode, the pending character must be blank, with server starter life/mana and no
   inherited local items. Gameplay requiring a restored character must remain gated until account login.
   In disabled mode, the local character remains client-owned. Record inventory and life/mana as shown.
3. Register a disposable account using `/register ssc_a <fixture-password>` (registration establishes
   authentication), or `/login ssc_a <fixture-password>` after it exists. In authoritative mode expect
   the server's initial starter state, not imported local inventory. In disabled mode check that normal
   login does not replace the character with a server checkpoint.
4. Use ordinary gameplay and the fixture server console (for example `give <item> <character> <stack>`)
   to change inventory, equipment, a secondary loadout, health/mana and a bank slot. Record exact stacks,
   loadout selection and vitals visible in stock client. Use only fixture accounts and items.
5. `/logout`: authoritative mode must persist the original account's state and reset the pending
   character; disabled mode must leave local state intact. Log in as a second disposable account on
   the **same socket**; it must not receive the first account's server character. Log out and log back in
   as the first account, then verify its exact inventory, loadout and vitals.
6. Disconnect normally, rejoin with the same local character, and authenticate as the original account.
   Verify the most recent checkpoint and client-visible equipment, bank, health and mana synchronization.
   Repeat after a normal server save/shutdown if testing persistence across process restart.
7. A managed sandbox requires a valid trusted transfer admission. A direct stock client must not be
   admitted merely by knowing its backend port, and `worldtransfer` from a direct non-forwarded connection
   must not issue a privileged switch. Actual cross-world transfer qualification requires the configured
   trusted Dimensions forwarding path; an ordinary stock client is not that path. The existing native
   lifecycle regression fixtures separately verify typed admission consumption and all four SSC modes.

Record each observed pass/fail in the manifest's `checks`, plus screenshots or client observations and
server logs if needed. Finish with the normal server-console `shutdown` command and close the disposable
client. There is no automatic deletion of any client save directory. Do not mark a case passed solely
from server console messages; stock-client display and behavior are part of the acceptance criteria.

## Evidence checkpoint

On 27 November 2026 all four combinations passed the script's **server readiness smoke** against the
fixed local Vega publish, while preflight verified the actual installed stock client version. Artifacts:
`artifacts/stock-ssc-qualification/*/qualification.json`. All retain `actualClientQualification` equal to
`pending-manual`; no graphical client was launched. This is independent of the six existing native SSC
integration regressions (real account persistence, native lifecycle hooks, character synchronization
and typed transfer admission). Neither establishes the complete stock-client network join or visual test.
