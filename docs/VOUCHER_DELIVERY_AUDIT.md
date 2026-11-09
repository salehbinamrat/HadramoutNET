# Durable voucher delivery audit — migration milestone

This is a test-first **partial** delivery-safety improvement, not complete
parity with the original Python application's account/batch lifecycle.

## Scope and guarantees

- Every **explicitly confirmed** Hotspot, PPPoE or User Manager 6/7 voucher
  upload from the new WPF workflow must first create a local audit record and
  commit a conservative `dispatching` marker to the **independent**
  `HadramoutNET/data/hadramout-net.db` SQLite database.
- The RouterOS publisher is not called if audit initialization, insertion or
  the dispatch marker fails. The user cannot upload from the WPF page until
  the journal initializes successfully.
- The read-only audit view lists the router identity and endpoint, date,
  service/profile, attempted/acknowledged counts and outcome.
  **It never saves usernames or passwords, license codes, raw router errors,
  voucher export files or RouterOS API credentials.**
- States are `started`, `dispatching`, `accepted`, `rejected`,
  `partial` and `uncertain`. Terminal states cannot be overwritten.
  Only allowlisted outcome reason codes can be persisted.
- A process crash at or after `dispatching` may mean **no RouterOS write
  occurred**, or that some writes succeeded. The local journal deliberately
  does not guess. These attempts stay visible after restart and require the
  operator to inspect the router manually; no automatic retries/deletes.
- `accepted` means the RouterOS API acknowledged the upload steps, **not**
  proof that the accounts remain active or have the correct limits later.
  `partial` and `uncertain` always require reconciliation.
- Archive and audit metadata are written only to HadramoutNET storage. The
  original `salehbinamrat/Hadramout` repository and `nidi_cards.db` are not
  modified or used for audit.

## Tests and limitations

Unit tests cover SQLite restart persistence, immutable transitions, invalid
metadata, refusal to write to Python data, and secret-free records. A real
synthetic TCP RouterOS peer verifies the write-ahead marker before the
`/ip/hotspot/user/add` command and the acknowledged terminal result.

This is a **batch-level** audit only. It does not yet provide a verified
per-voucher reconciliation or durable per-stage User Manager profile audit.
When re-creating a fresh draft, remote duplicate checks remain mandatory.
Never use an `accepted` state to issue a repeat upload automatically.
Cross-process SQLite concurrency, power-loss durability, Windows installer
behavior and physical MikroTik integration still require acceptance tests.

Release stays gated: independent Windows Core and Windows Release x86/x64
must pass on the same SHA; four Windows Portable/Installer outputs and
real-device parity are still missing. Do not merge the draft PR or ship.
