# Source reference status (migration preparation)

- Original repository: `salehbinamrat/Hadramout`, pinned at `bdfb355b1dbaf8f868d811ea55d4c1af26eb3a37`.
- Reference source is a **copy**, never a move. The original repository has not been modified.
- 61 / 63 Git blobs were uploaded to this **new** repository and their Git blob SHAs were checked against the source.
- **Two binary PNG files are not yet copied:** `assets/default_card.png` and `assets/login_hero.png`. GitHub's standard contents API did not return their contents because each exceeds 1 MiB. Do not present this reference as complete until both are copied and SHA-verified.
- The archive under `reference/` is read-only migration reference. Python workflows nested under it are not release workflows.
- The .NET version is not implemented yet. This commit is NOT a functional port and is NOT shippable.
- All migration work must remain in a PR until full feature parity, real Windows Core/Release tests and four verified packages succeed.
