# User Manager 6/7 — read-only current-draft verification

This milestone adds **manual, read-only observation**, not remote
reconciliation or an assertion that a previously submitted batch was the
cause of the observed remote account/profile state. The original Python
repository and original customer database remain untouched.

## User flow

From the WPF card-creation page, keep the draft available in memory, select
**User Manager 6/7**, retain the intended profile, then select **التحقق من
المسودة (User Manager)**. No router write, retry, activation or deletion is
ever issued by this verifier. The result is an **aggregate count** for each
status, not a saved or exported list of voucher usernames or passwords.

## RouterOS 6

The existing `UserManagerCatalog` discovers the RouterOS 6 path only after
the RouterOS 7 path is genuinely unsupported. If permissions fail, v6 is
not tried as an authorization bypass. Users are compared by exact username;
`actual-profile` is reported only as a **currently reported profile**,
not as definitive proof of prepaid access, successful billing or active
authentication. Disabled users are distinguished from missing users.

## RouterOS 7

A username in `/user-manager/user/print` is not equivalent to a profile
association. This verifier additionally reads `/user-manager/user-profile/print`
with only `user,profile,state`. It distinguishes:

- `profile_active`: the expected association reports `running active`.
- `profile_ready`: the expected association reports `running`.
- `profile_used`: the expected association reports `used`.
- `profile_unverified`: association exists but state is unknown.
- `profile_other`: associations exist but none for the expected profile.
- `profile_missing`: account is listed, with no association.
- `disabled` and `not_found`: user-level observations.

Read-only `count-only` responses cross-check list completeness and impose a
5000-record limit for users and user-profile links. Missing counts, denied
rights, incomplete rows or oversized results return **unavailable /
incomplete** — never false absence or success.

Source: [MikroTik User Manager documentation](https://help.mikrotik.com/docs/spaces/ROS/pages/2555940/User+Manager),
which documents separate User Profiles, the possibility of multiple
associations per user and `running active`, `running`, `used` states.

## Safety and uncompleted acceptance

- Only the **currently loaded draft** is checked. No voucher usernames or
  passwords are written to the SQLite delivery audit. After a restart the
  draft is gone: the operator must verify old attempts separately against
  trusted router/account records. An audit entry does not authorize replay.
- Current observations may change immediately after they are read.
  Matching names can predate an upload and must not automatically turn the
  historical audit outcome from uncertain to accepted.
- Hardware/router v6/v7 correctness, printer parity, trial continuity and
  all four signed-off Windows installer/portable packages remain
  outstanding. These CI tests use synthetic RouterOS TCP peers.
- Keep PR #1 a **draft**. Do not merge or ship until feature parity, safe
  recovery policy and end-to-end acceptance are complete.
