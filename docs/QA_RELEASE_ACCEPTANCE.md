# HadramoutNET: QA candidate acceptance and release gate

**Status at 2026-11-09:** Not approved for production. This document separates automated
evidence from real-device acceptance. The original Python Hadramout repo is
reference-only and **must never be modified by migration work**.

## Automated checks required for each SHA

GitHub Actions `.github/workflows/windows-ci.yml` starts Windows Core and
Windows Release (x86 and x64) **concurrently on the same commit**.
Within each Release run, real .NET tests run before WPF compilation and
before packaging. Once **all three jobs succeed**, two QA packaging matrix
jobs generate four Windows candidates:

1. HadramoutNET-win-x86-Portable-QA.zip
2. HadramoutNET-win-x86-Installer-QA.exe
3. HadramoutNET-win-x64-Portable-QA.zip
4. HadramoutNET-win-x64-Installer-QA.exe

The two architecture-scoped artifacts contain `SHA256.txt` files.
Each Portable ZIP contains `SHA256-FILES.txt` for the included runtime,
libraries and executable. The packager verifies the Portable executable's
PE machine architecture, installer existence, archive contents, minimum
sizes, and package SHA-256 hashes before upload.

**Unsigned QA candidates are not official releases.** These PR artifacts
expire after seven days. Their presence does not authorize merging to
`main`, publishing on GitHub Releases, marketing the port as complete,
or deploying to paying clients.

## Required real-device/manual acceptance (NOT EXECUTED by CI)

All checks must be recorded with date, OS, hardware/printer model,
RouterOS version, input batch size and PASS/FAIL evidence:

- Fresh x86 installation on a supported **32-bit Windows 10 LTSC/Enterprise**
  system, and a clean uninstall/reinstall test with user data preserved.
- Fresh x64 installation and Portable launch on supported Windows 11 and/or
  Windows 10 LTSC/Enterprise, with clean exit, relaunch and data persistence.
- Licensing: the existing trial must remain **480 hours / 20 days**, start
  only after a first successful MikroTik connection, survive reboot and
  changed system time; confirm paid activation/renewal and offline handling.
  Do not alter trial or activation policy without owner approval.
- RouterOS v6: login, read-only inventory, Hotspot create/verify, PPP
  create/verify, User Manager v6 account create **and** profile binding,
  with real customer/profile data and authorization-limited credentials.
- RouterOS v7: User Manager v7 account/profile state, existing-account
  collision, denied commands, bad profiles, disconnected ACK and recovery
  without duplicate create/bind.
- Card generation: username-only, username=password, separate password,
  length/characters/sequence/uniqueness, 1 card and large batches; compare
  actual outputs with the pinned original Python behavior.
- Package save/reload: two distinct packages with different
  cards, geometry, optional text positions, QR modes and background images;
  verify no settings cross over and no missing data after app restart.
- Preview/PDF/native: page A4/A3 portrait/landscape, five card ordering modes,
  first page and later sheets, both duplex edge modes, borders/gaps, overflow,
  credential readability and text selection (including Arabic + mixed digits).
- QR: scan credentials, username-only and templated local login URL with a
  real mobile camera; verify full QR quiet zone in PDF and in WPF output,
  and ensure a QR never covers credentials.
- Image backgrounds: JPEG, PNG, BMP, GIF and available Windows WebP WIC codec;
  modes stretch, fill, fit and tile; verify the same crop/content between
  PDF, Windows preview and actual printed sheets.
- Printer drivers: one physical laser/inkjet printer, one PDF/virtual driver,
  A4 simplex and duplex, failure before spool on unsupported media/tickets,
  unplugged printer handling and no automatic retry after uncertain spool.
- Security: restart with unresolved upload audit entries; confirm original
  upload state is immutable and no passwords/usernames are written to the
  extra audit tables; verify offline and wrong-router rejection.
- Compatibility: **.NET 10 is NOT supported on Windows 7 or Windows 8.1**.
  x86 means a 32-bit application, not support for obsolete Windows releases.

## Approval policy

Do not merge PR #1 to `main` unless: (1) source parity review identifies
no remaining blocking gaps; (2) both Core and Release pass on the same
SHA with no skipped/failing tests; (3) all four package outputs are built
and SHA-256 verified; (4) physical RouterOS/printer/licensing acceptance
is recorded and approved; (5) no unresolved policy conflict remains.

When the owner approves, merge only the PR's verified SHA after checking
branch protection, review requirements and that original Python repo is
unchanged. Never bypass required checks or delete a failed run as a
substitute for fixing the failure.
