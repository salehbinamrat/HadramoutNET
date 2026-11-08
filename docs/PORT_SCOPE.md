# HadramoutNET scope and acceptance

This is an independent Windows C#/.NET port, not a rename or recolor of Python binaries.

## Immutable source

The original `salehbinamrat/Hadramout` repository MUST remain untouched.
A pinned copy of 61/63 original Git blobs is preserved under
`reference/python-v1.1.3` with original blob SHAs. Two >1 MiB PNGs
remain blocked from the GitHub connector upload and MUST be copied before
the reference can be called complete. See `docs/SOURCE_MANIFEST.json`.

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

## Current limitation

This changeset ports only a test-covered, *non-networking* RouterOS word codec,
command safety guard, and red palette constants. It does not contain a working
desktop UI, router connection or release packages. Do NOT merge or release until
the complete acceptance matrix and all package gates are satisfied.

.NET 10 targets modern Windows, not Windows 7/8/8.1. Backward support needs a
separate compatibility decision.
