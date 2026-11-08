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
- New transactional `PackageSettingsStore` saves and retrieves separate package presets, keeps Arabic JSON values, blocks opening legacy Python databases and resides in an independent `HadramoutNET` data folder. The original Python schema-v8 migration and all historical card records are **not yet implemented**.
- WPF login screen is a **visual foundation only**: live login, full licensing/trial and service calls are deliberately disabled until their exact contracts are ported.
- All source assets are copied unchanged. Their use in the new UI must be verified individually.
- x86/x64 standalone Portable/Installer binaries are **not available**. A green foundation-test suite is not a full acceptance test.

Do not merge or distribute until feature parity is verified end to end and all four Windows packages pass real installer/portable validation. 
.NET 10 targets modern Windows; Windows 7/8/8.1 require a separate compatibility design.
