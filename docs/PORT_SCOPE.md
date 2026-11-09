# HadramoutNET scope and acceptance

This is an independent Windows C#/.NET port, not a rename or recolor of Python binaries.

## Immutable source

The original `salehbinamrat/Hadramout` repository MUST remain untouched.
A pinned **complete copy of all 63/63 Git blobs** is preserved under
`reference/python-v1.1.3`, with exact source SHA verification, including
`assets/default_card.png` and `assets/login_hero.png`. See `docs/SOURCE_MANIFEST.json`.

Do not copy user databases, DPAPI records, machine fingerprints, or private
license keys into this repository.

## Test-first port order

1. RouterOS API framing, authentication, read/mutation guard, retries and timeouts.
2. Card and voucher generation, distinct/same passwords, safe duplicate handling.
3. SQLite schema v8, package-specific settings and data isolation.
4. Online signed licensing and immutable 480-hour trial history.
5. Export PDF and Excel, card layout, print spool and Windows printer selection.
6. All visible screens and interactions: login, dashboard, voucher creation/printing,
   cards, profiles, maintenance, network settings and reporting.
7. Visual parity: Arabic RTL, Arial, layout/grid/control geometry; only navy becomes
   glossy red. Preserve semantic danger/success colors and confirm contrast.
8. Windows Core and Windows Release tests on the same SHA; only after all pass,
   build and verify real x86/x64 Portable and Installer with SHA-256.

## Current implementation and limitation

- RouterOS API word/sentence framing, command exchange and safety barriers have unit coverage.
- Voucher creation core has regression coverage.
- Transactional `PackageSettingsStore` persists separate package presets, Arabic JSON and settings in a separate `HadramoutNET` data folder. Never opens the Python application's database for writes.
- `LegacyDatabaseImporter` imports a validated **SQLite schema-v8** operational history snapshot to isolated `legacy_*` tables in the independent .NET database. A read-only source connection, in-memory online backup (including WAL), integrity/foreign key validation, a single destination transaction, and duplicate-import refusal protect the source and target. Preserved: card IDs, batch relations, original card usernames/passwords, router metadata, packages, print settings, selected creation/card/print settings and activity headers. Router passwords (including DPAPI), arbitrary activity details, license secrets and trial activation state are never imported. Presets and batch JSON are filtered.
- The imported history is **not automatically activated**, migrated into native live services or wired into WPF yet. A separate service-layer and UI integration with parity tests remains mandatory. Call the core importer only after explicit user selection/consent and with the old program stopped or a verified snapshot; never silently import during app startup.
- Real offline RouterOS .rsc guarded exports for Hotspot, PPP, User Manager v6/v7, CSV/text, and XLSX (RTL, text credentials, filters and column widths) are implemented and regression-tested. These exports are **not yet connected to the WPF UI**, and PDF render/native print operations remain unimplemented.
- SignedLicenseFeed verifies original **Supabase** schema-v1 Ed25519 feed signatures against its unchanged pinned public key, product, device binding, status, expiry and timestamps. Production never accepts a caller-supplied public key; only regression tests can use the internal test-key seam.
- SupabaseLicenseClient implements read-only HTTPS license checks and a separate **explicit** first-device claim against the original Supabase endpoints. A claim response can never grant access; a new signed active feed is required. Redirects, bad signatures, oversized/invalid feeds and network errors fail closed. WPF now exposes **explicit user-triggered activation and stored-license verification** through PaidLicenseCoordinator; it does not auto-claim or auto-unlock management.
- TrialAccessPolicy reproduces the exact 20-day (480-hour) trial clock, deadline, rollback safeguards and immutable start across renewal revisions. This is **evaluation only**, not a new trial store and not a trial reset.
- LegacyTrialStateReader now reads original DPAPI-protected user file, HKCU registry and ProgramData machine mirrors **without writing or repairing them**, verifies Windows 64-bit MachineGuid identity and reconciles oldest first/latest last/highest revision. CI uses isolated, real Windows DPAPI-encrypted fixtures on x86 and x64; it does **not** prove compatibility against actual customer installations. Live trial start remains disabled pending real-device acceptance and safe persistence.
- TrialStateManager now adds separate Windows machine-scoped DPAPI-protected continuation files in HadramoutNET's LOCALAPPDATA and ProgramData, with the original Python trial history merged **read-only** to preserve the earliest known start. Preview never creates a trial. The only writing entrypoint is **ConfirmAfterAuthenticatedRouterLogin**; first use requires a writable shared machine mirror, and corrupt/mismatched old state fails closed. This pure service is not yet connected to the WPF login, and real customer-state parity and time-of-check cross-process concurrency still require acceptance.
- PaidCodeStore saves an **explicitly user-entered** Supabase code in the .NET user's own DPAPI CurrentUser encrypted file (atomic replacement). It can read the existing Python Supabase v1.1.3 code as a read-only fallback, never reads obsolete GitHub-era codes, never modifies Python's licensing or trial files, and refuses invalid protected files instead of silently falling back. A saved code alone is never an entitlement.
- WindowsLicenseIdentity reads the 64-bit HKLM MachineGuid on x64 Windows (even from x86 programs), or the 32-bit view on 32-bit Windows. It reproduces the original paid and trial SHA-256 prefixes, without generating a new random fingerprint or storing raw MachineGuid. If MachineGuid cannot be read, it fails closed rather than inventing a paid-device identity.
- PaidLicenseSession enforces the original 30-minute signed-verification interval, 20-second grace, 60-second denied retry, and no cross-restart grant. Unchecked, revoked, offline, overdue or monotonic-regressed states are locked.
- PaidLicenseCoordinator connects protected/legacy read-only code loading, Windows paid-device identity, the signed Supabase client and monotonic session checks. It NEVER auto-claims at startup; only an explicitly invoked activation action can request first-device claim. Saving a candidate code does not confer permission. The WPF activation/verify buttons are wired to this coordinator, but **full management login remains disabled** pending trial-start continuity, permission tests and RouterOS services.
- WPF login now includes an **explicit read-only real RouterOS TCP/API-SSL connection test**. RouterConnectionProbe validates original local/remote address and selected external API port, authenticates to RouterOS v6/v7, reads only /system/identity/print, then disposes the socket. On failure, no raw router trap/password is displayed. WPF clears the test password after use; no password, trial or license code is saved. The **full application login remains disabled** until signed paid access or verified 480-hour trial start/persistence and all service UI contracts are accepted. A successful connection test never grants application access.
- All source assets are copied unchanged. Their use in the new UI must be verified individually.
- x86/x64 standalone Portable/Installer binaries are **not available**. A green foundation-test suite is not a full acceptance test.

Do not merge or distribute until feature parity is verified end to end and all four Windows packages pass real installer/portable validation. 
.NET 10 targets modern Windows; Windows 7/8/8.1 require a separate compatibility design.
