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
| Hotspot and PPP | Account publishing, read-only crash review, count-verified preflight before writes; Hotspot time/byte limits sent per voucher | Router acceptance and real accounting; PPP time/volume restrictions still depend on router profile/RADIUS, and batch recovery remains manual |
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
1024 scaling, and uptime minutes are normalized. For **Hotspot** voucher
creation only, the exporter now passes nonzero byte and nonempty time
limits as `limit-bytes-total` and `limit-uptime` attributes to the RouterOS
`/ip/hotspot/user/add` command, matching the pinned Python source.
For **PPP and User Manager**, these printed values are NOT applied
automatically: the real quota depends on the selected RouterOS profile,
RADIUS and other policies and must be verified on physical routers.
No automatic retry is permitted on an uncertain response.

The upload target is now captured at the instant a local batch is generated
and compared at send time. Any later change to service, profile or
User Manager owner blocks the upload rather than sending an old batch to a
different destination. This prevents an operator mix-up but does not
prove actual remote delivery or historical batch identity.

## Original card-template file interchange

The .NET workspace now imports and exports card-only JSON templates.
`CardTemplateExchange` accepts a bounded UTF-8 file (up to 22 MiB,
including UTF-8 BOM), reads only `card.*` keys, and validates supported
fields via `CardArtPresetCodec` before applying a new design. Original
`create.*` and `print.*` values contained in a mixed JSON export cannot
silently override the current service, package or printer settings.

A nonempty `card.background_path` is rejected: imported templates must
carry an embedded validated `card.background_data` PNG. No external,
UNC or other filesystem image paths are resolved from the JSON. The
Python original may export embedded JPEG/GIF/BMP/WebP bytes; those
cannot yet be imported directly into the strict PNG .NET core and
must first be converted in the existing WPF image import editor.

Known font size, color, border and layer-position fields are applied
to the shared WPF/PDF artwork. Unknown original `card.*` settings
are preserved for export and in the package record but are explicitly
reported as unrendered. They are not silently treated as supported.
Templates contain artwork and settings only: no generated voucher
credentials are included. Exports use the existing atomic file writer,
not the original Python installation or database.

Pending: fully implement original per-layer color and size overrides,
all original font families and image formats, image-crop calibration
against physical printouts, and user-approved installation acceptance.

## High-priority RouterOS publication completeness fix

Standard Hotspot and PPP creation previously checked projected usernames
and profiles without verifying that the RouterOS response contained every
row. This could yield a false 'username absent' result on a truncated list.
The current implementation now uses `count-only=yes` followed by
`.proplist=name` for both menus, compares row counts, refuses malformed or
duplicate names and refuses any result exceeding 20,000 entries. Any
count/list mismatch is a **fail-closed preflight** with zero remote add calls.
Dedicated synthetic TCP/RouterOS protocol tests cover incomplete usernames,
profiles, invalid counts and oversized lists on both services. This is still
not an atomic cross-router transaction: a concurrent external account
creation after preflight may be rejected by RouterOS and must be reconciled.

For Hotspot, real `limit-uptime` and `limit-bytes-total` attributes now
accompany each new user and malformed negative/time quota values are
rejected before any router I/O. The PPP secret API does not support those
same per-user Hotspot attributes; the confirmation UI explicitly warns the
operator instead of implying a printed quota is enforced. User Manager
profile enforcement remains subject to profile/RADIUS acceptance.

## Per-layer source card-editor support

Python's `card.size.<layer>` multiplication factors (0.4–4.0) and
`card.color.<layer>` hex colors now change the same physical `CardArtwork`
scene used by PDF and Windows printing rather than being stored only as
unsupported JSON. The base username color intentionally controls
the password color as in the original `render.py`. Import/export of
recognized layer styles is roundtrip-safe; unknown original settings
continue to be preserved and disclosed as unrendered.
