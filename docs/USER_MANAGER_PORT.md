# User Manager 6/7 — read and confirmed voucher provisioning

Ported from pinned Python core/network.py, preserving original owner,
user/profile and RouterOS version-specific operations. The original
Hadramout source and trial files are untouched.

## Authenticated read-only discovery

- Probe RouterOS 7 /user-manager/profile/print and fall back to RouterOS 6
  /tool/user-manager/profile/print ONLY when the newer command is unsupported.
  A permission failure is never treated as "try another command".
- Profiles/users are projected by .proplist and never fetch passwords.
  Missing services, denied permissions, partial user reads, and genuine
  empty tables have distinct results.
- The WPF Manager 6/7 tab shows the remote users and profiles in RTL layout.
  The dashboard now counts UM6 and falls back to UM7 only when the UM6
  command is unsupported. Permission failures remain unavailable, not zero.

## Confirmed User Manager voucher creation

- A user must explicitly choose User Manager and confirm the batch.
- Preflight reloads the actual UM users and profiles; refuses existing
  usernames, missing profiles, insufficient permissions, unavailable UM,
  missing UM6 owner and invalid credentials before sending any write.
- In UM6, send /tool/user-manager/user/add with username/password/customer.
  Use ONLY the single *HEX record ID returned by RouterOS to call
  /tool/user-manager/user/create-and-activate-profile with the verified
  customer and selected profile.
- In UM7, send /user-manager/user/add with name/password, then
  /user-manager/user-profile/add with user/profile.
- After creating a user, any uncertain/lost acknowledgment of its profile
  association immediately stops delivery and reports uncertainty.
  No auto-retry and no automatic user removal/rollback.
- Even with zero confirmed successes, a possibly delivered write
  locks that draft against subsequent replay in the WPF screen.
  Operators must review the remote users and create a fresh draft.
- The separate Session API exposes ONLY these four exact allowed writes
  to the trusted UM publisher; all still pass the RouterOS command guard
  and recheck paid/trial authorization before and after execution.
- Tests first: real RouterOS TCP loopback checks for UM6/7 commands,
  owner requirements, duplicate prevention, absent returned IDs and
  uncertain profile association without any retry or password leakage.

## Remaining blockers

- Real RouterOS 6/7 User Manager hardware acceptance, subscription
  accounting, profile/limitation and validity lifecycle, owner/customer
  editing, batch/publish audit, idempotent reconciliation after a lost
  connection, full print-template parity, and remaining Winbox/network
  management screens.
- A two-call remote write protocol is not a database transaction.
  No automatically retried mutation is safe without reconciliation.
- The original Python 1.1.3 trial and paid activation semantics must
  remain unchanged. No final merge or four Windows shipping packages
  until all services, security and hardware acceptance gates succeed.
