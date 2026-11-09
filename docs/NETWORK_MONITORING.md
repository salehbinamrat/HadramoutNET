# Network monitoring — read-only parity milestone

This page ports the **read-only** data acquisition from the original pinned
Python `core/network.py` / `ui/operations.py` into C#/.NET 10. It does not
create a general Winbox clone and does not grant write access to the router.

## Implemented in this milestone

- **Hotspot servers:** `/ip/hotspot/print` with an exact safe property list
  (`.id,name,interface,profile,disabled,invalid,running`). Only legitimate
  `*HEX` record IDs are included; rows are sorted by server name/interface.
- **Discovered neighbors:** count-only preflight then
  `/ip/neighbor/print` with explicit discovery fields. The result is denied
  when the count is unknown or exceeds **5000**. This does not scan the LAN.
  WPF permits local search and explicit copying of selected IP/MAC.
- **Live sessions:** first count-only requests on
  `/ip/hotspot/active/print` and `/ppp/active/print`, then permitted
  property-only listings. Denies each service over **2500** rows; failure
  of either count or list means the combined view is unavailable, not zero.
- All reads use an existing **authorized RouterOS session**. No passwords,
  comments, scripts, hidden properties or arbitrary server attributes are
  included in result DTOs, even if a misbehaving peer sends them.
- UI has separate status messages for permission denial, unreachable
  services and excessive counts. Refresh is explicit. There is no auto
  mutation, server disable, remote command execution or
  scheduled network scan.

## Test-first evidence

The dedicated `RouterNetworkInventoryTests.cs` uses a synthetic TCP
RouterOS protocol endpoint, not mocks of the core reader. It verifies
exact command paths and property lists, count limits before listing,
unknown field exclusion, disabled state and session authorization.
`Windows Core` and `Windows Release` x86/x64 run concurrently on one
SHA, including compilation of the WPF UI.

## User Manager database health (read-only, RouterOS 6 and 7)

- Ports the original Python `database_maintenance_status()` service
  without allowing optimization, backup, restore, file writes or reboot.
- Detects the User Manager command family with
  `/user-manager/profile/print count-only=yes`; falls back to
  `/tool/user-manager/profile/print count-only=yes` **only** for a
  genuinely unsupported command. A permission denial never causes an
  incorrect v6 fallback.
- Reads only `db-size,free-disk-space` from the corresponding
  `/user-manager/database/print` (v7) or
  `/tool/user-manager/database/print` (v6) command with explicit
  `.proplist`. Unexpected properties such as credentials, paths and
  comments are discarded, even if a peer returns them.
- An empty, partial, denied or malformed response is classified as
  unavailable, not silently treated as zero free space. Raw exception
  texts are not shown in the UI. Measurements are displayed exactly as
  returned; no invented units or conversion.
- Adds a **حالة قاعدة User Manager** tab inside network monitoring,
  with explicit manual refresh and separate version, database size,
  free space and status.
- `UserManagerDatabaseStatusTests.cs` exercises real RouterOS TCP
  sentences for v6/v7, permission denial, unsupported menu detection,
  incomplete responses, field exclusions and expired sessions.
- **CI validation pending:** the latest GitHub Actions jobs failed before
  Windows runner assignment (zero executed steps). No pass claim can
  be made on this milestone until both Windows Core and Windows Release
  have run successfully on the same final SHA. Device-side acceptance
  is separately required.

## Targeted disconnect stage (after read-only inventory)

- The user explicitly selects **one** row from the current Hotspot/PPPoE
  session list and accepts **two** warning/confirmation dialogs.
  No background, automatic or batch disconnect exists.
- The trusted core service requires the original `.id` (`*HEX`),
  service, username and IP address, and reloads the selected session from
  RouterOS with a query by `.id`. A missing, duplicated or changed
  identity rejects the operation before any write.
- A new internal authenticated-session method allows ONLY
  `/ip/hotspot/active/remove` or `/ppp/active/remove` with ONE exact
  `.id`. No generic delete or router console command is exposed.
- A fully acknowledged `!done` is reported as **accepted**, not proven
  disconnected. The user refreshes the current live-session listing to
  verify the outcome.
- Explicit RouterOS `!trap` is reported as rejected, without exposing
  raw trap messages; a dropped socket, cancellation or lost ACK after
  dispatch returns **uncertain**, never retried automatically. The UI
  clears its possibly stale listing and requires an explicit refresh.
- Synthetic TCP integration tests verify no write without confirmation,
  changed identity refusing the remove, an exact single remove,
  PPPoE lost acknowledgment, rejected trap and invalid service/ID.
- This remains a deliberately narrow maintenance operation. A
  read-check/write sequence is not atomic against other router users.
  Targeted-session attempts are now durably audited (see
  `docs/MAINTENANCE_AUDIT.md`); hardware acceptance, tamper protection
  and live hardware acceptance remain required before release.
  An initial **read-only**, manually triggered session-record
  reconciliation has now been added with independent append-only
  observations; it does not establish the cause of disconnection.
  See `docs/MAINTENANCE_AUDIT.md`.

## Work still required before full Python parity

- RouterOS v6/v7 hardware tests with restricted API accounts and high
  loads; TLS certificate verification and real network access.
- Other original two-confirmation maintenance actions, safe temporary
  Hotspot-mode recording/recovery, complete post-write state rechecks,
  centralized/tamper-evident audit and durable audits for card publication
  and other maintenance operations.
- User/account maintenance, richer reports, live print-template parity
  and four Windows packages only **after** full release acceptance.
- An explicit, safe recovery plan for lost acknowledgments; never retry
  uncertain RouterOS writes automatically.

The original `salehbinamrat/Hadramout` repository is never modified.
No shipping claim is made from passing synthetic CI alone.
