# HadramoutNET versus pinned Python v1.1.3: feature-parity status

**Baseline:** `reference/python-v1.1.3/` and
`docs/SOURCE_MANIFEST.json`. Source is copied/pinned; original repository
`salehbinamrat/Hadramout` is never modified by this migration.

This is a **working-gap matrix**, not a claim that the entire original
application has been ported. Statuses are evidence-scoped.

| Area | Implemented in .NET branch | Still missing / required evidence |
|---|---|---|
| Source recovery and origin | Python reference files verified against Git blobs by Windows Core CI | External acceptance of original functional behavior |
| RouterOS API login | Live API auth, current-access gating, session expiration | Physical-router acceptance under supported v6/v7 permissions |
| Local card generation | 3 credential formats, sequence and uniqueness guards | Full original options/preview parity, large real batches |
| Hotspot and PPP | Account publishing and read-only crash review | Router acceptance, real accounting and batch recovery |
| User Manager 6/7 | Version-specific provisioning, profile binding, per-command write-ahead intent + ACK, manual unresolved review | Real v6/v7 profile accounting and RADIUS end-to-end |
| Delivery audit | Separate SQLite journal and append-only aggregate observations, no stored passwords/usernames | Provenance of each individual remote card after crashes (policy and implementation) |
| Print sheet geometry | A3/A4, portrait/landscape, five orders, long/short-edge duplex, per-package persisted print settings | Printer hardware driver and physical cut/duplex calibration |
| Card artwork | One shared physical scene for WPF preview, native and PDF; saved positions, optional text, credential overlap rejection, QR and red theme; dynamic price, binary capacity, uptime, validity and creation date per voucher | Visual/pixel parity with original Python raster layer editor, font/shaping metrics, and printed dynamic-field acceptance |
| Card backgrounds | Embedded normalized PNG from Windows import, max 15 MB / 32 million pixels, modes stretch/fill/fit/tile in WPF/PDF | Physical output comparison, WebP availability depends on installed Windows image codec, border and crop parity |
| PDF | PDFsharp output, atomic write, same card-scene placements and printed data as native | Independent searchable/selection copy-paste verification for Arabic and complex scripts |
| Native printing | Single explicit Windows print job, ticket/capabilities checks, first-sheet preview uses same WPF renderer | Real printer, duplex and spool failure acceptance |
| Package settings | Separate .NET SQLite, persisted creation/print/artwork settings per package | Exhaustive import/export of original custom packages and all optional fields |
| Legacy Python archive | Read-only import and paging work on a separate database | Full original archive lifecycle/rollback acceptance |
| Licensing/trial | Existing independent port work and automated unit tests | Confirm original 480-hour/20-day policy under real license/offline environments |
| Four Windows deliverables | CI QA Portable + Inno Setup Installer for win-x86/win-x64 with SHA-256 manifests, after Core + Release pass | Installer/portable **real host** acceptance, signing, final release approval |
| Network management/Winbox-like tools | Selected read-only inventory/session management | Every original screen/service/operation and permissions matrix parity |

## Key compatibility notes

- Original Python `card.positions` use a 280×130 reference coordinate system.
  The .NET card-art editor preserves this scheme and creates a physical
  millimeter-based scene for all renderers. Some original raster-specific
  layers (pixel-perfect text shaping, background EXIF/crop and additional
  original metadata such as pricing by generated card) still require
  line-by-line comparison and physical/visual tests.
- `.NET 10` is not a Windows 7 / 8.1 compatibility strategy, regardless of
  32-bit or 64-bit output.
- A successful GitHub Actions matrix is **not** an account-authentication
  test with MikroTik or a printed-sheet inspection. Keep the PR Draft until
  the attached `QA_RELEASE_ACCEPTANCE.md` checklist is signed off.

## Subsequent batch creation and real card metadata parity

The WPF creation form now exposes the Python-supported username prefix,
separate password length/character set, start sequence, sequence format
and basic price/volume/time/validity fields. Those settings are saved under
the original `create.*` keys, with compatibility for older `.NET`
`create.characters` / `create.format` package values. Saved values are
fully validated BEFORE any controls are changed. The original Python
database remains untouched.

Generated card objects carry `price`, `limit_bytes`, `limit_uptime`,
`validity_days`, and `created_at`. The shared card-art scene uses these
individual card values for the corresponding enabled design layers, unless
a user explicitly overrides the display text. Capacity units use binary
1024 scaling, and uptime minutes are normalized. These fields are
**print metadata** and do NOT silently alter the MikroTik server profile,
RADIUS usage counters or live account restrictions. The router's actual
policy still depends on the selected RouterOS service/profile and must
be verified on real devices.

The upload target is now captured at the instant a local batch is generated
and compared at send time. Any later change to service, profile or
User Manager owner blocks the upload rather than sending an old batch to a
different destination. This prevents an operator mix-up but does not
prove actual remote delivery or historical batch identity.
