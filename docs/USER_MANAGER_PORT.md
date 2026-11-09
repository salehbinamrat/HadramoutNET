# User Manager 6/7 — safe read-only foundation

Ported from pinned Python core/network.py NetworkGateway.user_manager()
and profiles()/user_path(). The source Hadramout repository remains
untouched. This feature only touches the separate HadramoutNET branch.

## Implementation

- On demand, query RouterOS 7 /user-manager/profile/print. Fall back to
  RouterOS 6 /tool/user-manager/profile/print ONLY when the v7 command is
  unsupported, never when the operator lacks permissions.
- Read profiles with a narrow .proplist projection. Read the appropriate
  /user/print table with projected username, disabled, profile and owner.
  No passwords, API secrets or private login tokens are requested or logged.
- Display user/profile results in the glossy-red WPF User Manager tab.
  Both UI controls and service classes are read-only.
- A missing package reports not_installed; denied access reports
  permission_denied; partial user read reports users_unavailable and is
  **not** interpreted as an empty table with zero users.
- Reads require a currently authorized paid or trial RouterOS session.
- Tests exercise the actual RouterOS wire protocol against a local TCP
  test server for v6, v7, permissions, missing packages and partial reads.

## Not implemented yet

Original profile/limitation links, owner/customer editing, User Manager
v6 create-and-activate-profile, v7 user-profile subscription creation,
UM voucher lifecycle and accounting, safe remote mutations, renewal,
reports, live hardware acceptance and full UI parity. No remote write
operations were added in this change.

The existing dashboard currently labels its historical v6-only count as
v6; the new User Manager tab offers v7 discovery and read-only listing.
Do not claim v7 dashboard totals until separate count tests are added.

Continue to block final x86/x64 Portable/Installer release and merging
until all four artifacts and real hardware/service acceptance succeed.
