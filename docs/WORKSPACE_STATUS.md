# HadramoutNET functional workspace status

The new WPF MainWindow and WorkspaceWindow are **working foundations**, not
yet a complete recreation of all the original Python program's services.

## Live authentication and session

- Paid license: original pinned Supabase signed Ed25519 feed only.
  No paid code, HTTP status or cached entitlement alone opens the workspace.
- Free trial: user must explicitly choose trial, then RouterOS /login
  and /system/identity/print must succeed BEFORE committing the new .NET
  protected trial history (which merges original history READ-ONLY).
- Session remains open for live read-only operations and is disposed on
  logout, failed/expired authorization and application close.
- Paid validity is rechecked from the signed network feed at the original
  interval and the authorization is reevaluated before every new read.
- No RouterOS password is persisted. Trial start is immutable after renewal.

## Live screens currently wired

- Dashboard: router name, RouterOS version, uptime, CPU, RAM, Hotspot
  voucher count, active Hotspot, PPP active and UM6 user counts.
  Unavailable/forbidden services appear as **unavailable**, not zero.
- Hotspot users: read-only table with name, profile, disabled, limits.
  Passwords are not requested from RouterOS.
- Create cards: secure local username/password draft generation,
  Excel, CSV, genuine PDF card sheets and guarded .rsc exports. A separate explicit button permits
  **confirmed Hotspot or PPP publication**, preflight-reading only live usernames
  and profiles. Duplicates or unknown profiles refuse the batch before writes.
  Per-card RouterOS traps stop delivery and uncertain transport responses
  NEVER trigger automatic retries. Partial changes require manual reconciliation.
  User Manager v6/v7 now also supports **explicit confirmed provisioning**
  with real remote owner/profile preflight and two-phase account/profile
  linking. Any uncertain stage blocks replay of the draft. Full durable
  operation audit is still unimplemented.
- Package settings: explicit local SavePackage/LoadPackage, persisted
  in HadramoutNET's own SQLite store, not Python's original database.
- Logout: close active RouterOS socket and return to protected login.

## Not finished / release blockers

- Real hardware parity acceptance for Windows x86/x64, RouterOS v6/v7
  and API-SSL; trial continuation against existing customer DPAPI files;
  sharing ProgramData storage permissions; cross-process serialization;
  resilient clock handling across suspend/resume.
- Atomic online card creation/delivery with duplicate prevention,
  activity/error recovery, remaining Hotspot PPP and UM6/UM7 service
  operations, user maintenance, subscriptions and end-to-end profile lifecycle.
- Physical card layouts, live preview, PDF and native Windows print,
  router file transfer, reports, full styling/navigation parity and
  installer/portable post-install validation.
- Packaging CI remains gated. Passing unit/integration tests on synthetic
  TCP servers does not constitute release acceptance or reason to merge
  the draft PR into main.
