# Durable RouterOS maintenance audit — targeted session disconnect

## Storage and isolation

This milestone adds `SessionMaintenanceJournal` in
`src/HadramoutNET.Core/Data`. It uses only the independent .NET SQLite
database at `%LOCALAPPDATA%/HadramoutNET/data/hadramout-net.db`.
The constructor reuses the independent `PackageSettingsStore` safety checks
and refuses the original Python `nidi_cards.db` or its schema.
It creates one additive `maintenance_audit` table without resetting
packages, legacy history, paid-license state or the 480-hour trial.

Every audit row stores a UTC timestamp, router identity and endpoint,
service, selected RouterOS `*HEX` record ID, username, IP address,
monotonic row ID, state and a **fixed, non-secret result code**.
It does not save passwords, license codes, RouterOS raw `!trap` texts,
or arbitrary API responses. Entries are displayed in a dedicated
read-only **سجل العمليات** tab within network monitoring.

## Transactional local state machine

1. The operator selects one session and explicitly approves both prompts.
   Invalid or unconfirmed requests never begin a remote mutation.
2. `Begin` commits `started` **before any RouterOS read**. Failure
   to initialize or write the local journal blocks the operation.
3. The service reloads the exact record ID, username and IP from RouterOS.
   Missing or changed identities become terminal `rejected`.
4. `MarkDispatching` commits `dispatching` **before remote remove**.
   Failure to save this marker blocks the RouterOS command.
5. A fully acknowledged `!done` becomes `accepted` (**accepted by
   router**, not proof of actual disconnection); a consumed `!trap`
   becomes `rejected`, and lost acknowledgment, timeout or transport
   failure becomes `uncertain`. A failure to save a terminal outcome
   leaves the last committed state and reports an uncertain audit result.
6. Rows in `started`, `dispatching` or `uncertain` remain visible
   after restart for **manual review**. No automatic retry, recovery write,
   batch disconnect, or silent marking of an uncertain action as success.

SQLite checks enforce legal status values; one-row conditional updates
prevent duplicate completion or backward transitions. The recent-history
read is bounded to 500 rows at a time; UI loads 200.

## Test-first contracts

`SessionMaintenanceJournalTests` covers separate DB enforcement,
survival across restart, interrupted dispatch, legal and illegal
transitions, bounded reads and exclusion of passwords/errors.

`RouterSessionDisconnectTests` exercises the real RouterOS TCP wire
boundary. It verifies that `started` is already durable before the
preflight request and `dispatching` before the remote remove, that
a missing journal blocks even the read, and that accepted, rejected
and lost-acknowledgment outcomes are durably recorded.

## Safety limits that still block shipping

- The audit is **local, not centrally immutable or tamper-evident**.
  SQLite file protection, backup/recovery, retention and concurrent
  process behavior still need real-machine tests and hardening.
- RouterOS cannot make local SQLite and remote `/remove` one atomic
  distributed transaction. `dispatching` after a process crash
  therefore does **not** identify whether the router executed it.
- The identity recheck and subsequent `/remove` are not one
  atomic RouterOS operation; a second administrator may race them.
  Real v6/v7 device acceptance and manual reconciliation are required.
- No network-configuration maintenance or hotspot server disable/
  restore operation is enabled by this milestone.
- Windows Core and Release x86/x64 must all succeed on the same
  commit before any merge decision. Four installable/portable
  binaries and release acceptance remain gated by full product parity.

No original Python source repository was modified.
