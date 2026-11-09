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

## Operator-assisted User Manager review after restart

The new WPF action **مراجعة رفع سابق (User Manager)** offers a manual
inspection of unresolved, persisted `userman` attempts. The user must
select the exact audit attempt and supply the exact number of card
**usernames** from a separate trusted card export, one per line.
**Passwords and entire CSV files must not be pasted.** Input is kept
only in application memory for the immediate read-only verification.

Before any router read, `VoucherDeliveryReconciler` checks the attempt
identity and endpoint against the authenticated router, the service
type, the unresolved status, the batch count (maximum 5,000), and
duplicate or invalid names. It then uses the already exercised User
Manager 6/7 verifier. Only allowlisted aggregate state/count pairs,
the check time and the User Manager version are appended to
`voucher_delivery_checks` with a router/attempt/batch-size scoped
SQLite insert. No username, password, license data or RouterOS trap text
is written.

Repeated manual checks are append-only. The original upload state
(`started`/`dispatching`/`partial`/`uncertain`) is preserved,
including when newly observed current accounts appear active.
A historical check is **evidence of current router state only**: it
does not establish that an earlier attempt created the accounts or
that the cards will authenticate successfully. No automatic recovery,
profile activation, retry or delete is triggered.

This still does **not** provide a guaranteed identity match without
the operator's original trusted export, a persisted per-card delivery
record or hardware acceptance. The Hotspot/PPPoE reconcilers and
per-stage User Manager creation-versus-profile-attachment audit remain
future work. Do not merge/ship solely because read-only review passed.

## Hotspot / PPPoE read-only restart review

The same WPF manual audit action now supports persisted **Hotspot** and
**PPPoE** attempts, alongside User Manager. An operator selects an
unresolved attempt on the authenticated router, then supplies **only**
the exact number of original usernames from a trusted export. The
program performs *read-only* `/ip/hotspot/user/print` or
`/ppp/secret/print` queries. A bounded `count-only=yes` preflight
is compared with the final `.proplist` projection; duplicate or
missing name fields, count mismatches, permissions failures or
oversized router tables fail closed without claiming cards are absent.

Only aggregate counts in allowlisted categories are saved to the
separate append-only `voucher_delivery_standard_checks` table:
`not_found`, `disabled`, `disabled_unknown`, `profile_matches`, `profile_other`,
`profile_unknown`, and (PPPoE only) `service_other`. Exactly
the original attempt's count must be observed. The insertion is
atomic and bound to router identity, endpoint, service, batch size and
unresolved audit state. Original `voucher_delivery_checks` (UM6/UM7)
are not rebuilt or altered, so historical observations remain intact.

A `profile_matches` result only means the CURRENT router snapshot
contains an enabled account with the expected profile (and for PPP
the `pppoe` service value). It never means the recorded upload
caused the account, that credentials are valid, or that a session has
been authenticated. No passwords are read or persisted. Account
statuses can change immediately after a read. An operator may supply
wrong usernames; the app cannot prove their origin.

The action never retries, deletes, edits, activates or creates remote
accounts, and never reclassifies an old failed/uncertain audit record
to success. On-router hardware acceptance remains outstanding.

## Write-ahead User Manager 6/7 account creation and profile binding stages

The WPF upload path now supplies a durable `IUserManagerStageAudit` to
`RouterUserManagerPublisher` for all actual User Manager mutations. Two
immutable, append-only tables are created in HadramoutNET's existing SQLite
database:

- `voucher_delivery_um_stages`: attempt ID, card **ordinal** within the
  batch, RouterOS User Manager version, an allowlisted phase
  (`create_user` or `bind_profile`), and the UTC time when the phase
  was committed **before** the corresponding RouterOS write.
- `voucher_delivery_um_stage_ack`: a unique link to the prepared phase,
  and the timestamp at which RouterOS returned a successful `!done`
  acknowledgement. Failed or interrupted commands have no acknowledgement.

The journal does **not** record usernames, passwords, returned RouterOS record
IDs, remote error text, license keys or credentials. The phase ordering is
strict: the profile binding cannot begin without the matching create phase
acknowledgement; the next card cannot begin without the preceding binding
acknowledgement. Attempt/service/version/ordinal/state are validated by SQL.

If preparing or acknowledging a phase fails, no later phase is permitted to
reach the router. Missing acknowledgements survive an app restart and
remain *unknown*, whether the packet actually reached the router or not.
The WPF audit displays the latest recorded phase and its acknowledgement
status, not a fabricated success. Acknowledgement only reflects a successful
API response, not independent proof of a working voucher, consumed allowance
or correct RADIUS authentication.

The earlier manual read-only UM6/UM7 reconciliation and the immutable
batch-level audit history remain intact. No new automatic retries, deletes,
repairs, profile activations or per-account persistent identifiers are
introduced. This is a safety and diagnostics milestone, not full parity or
a shipping release. Hardware verification remains necessary.
