# حضرموت — HadramoutNET

**Status: port in progress, not a working release.**

An independent C#/.NET 10 migration of the existing Hadramout
MikroTik vouchers/cards application. The original repository is
`salehbinamrat/Hadramout`; it is read-only for this project and has
not been changed.

The application is intended to preserve the existing Arabic/RTL UI,
commands, card creation and printing, database, licensing and 480-hour
trial, with only the navy color scheme replaced by glossy red.
No functionality or interoperability is claimed until regression tests
confirm the complete behavior.

## Current implementation

- RouterOS word framing, command exchange, address parsing and read/write safety guard in `src/HadramoutNET.Core`. The API transport layer is not yet integrated into a working desktop login.
- Card-generation core and documented validation rules.
- Standalone transactional SQLite package settings store (`PackageSettingsStore`) with independent local app data path, Arabic JSON preservation, duplicate prevention and package switching tests. This is **not** a completed Python schema-v8 migration; it is not yet connected to WPF screens.
- Color tokens and a **visual-only, disabled** glossy-red WPF login foundation. No live login, trial activation, or licensing button is enabled.
- Tests are under `tests/HadramoutNET.Tests`; Windows Core and Windows
  Release x86/x64 launch in parallel, all before packaging.
- Original Python reference in `reference/python-v1.1.3` pinned at
  `bdfb355b1dbaf8f868d811ea55d4c1af26eb3a37`.
- **63 of 63 original source files copied byte-for-byte**, including the two large PNG assets, with all Git blob SHA values verified. See `docs/SOURCE_COPY_STATUS.md` and `docs/SOURCE_MANIFEST.json`.

## Test locally

```powershell
dotnet restore tests/HadramoutNET.Tests/HadramoutNET.Tests.csproj
dotnet test tests/HadramoutNET.Tests/HadramoutNET.Tests.csproj -c Release
```

Release artifacts, installer and full feature parity are **not yet available**.
Do not merge this work-in-progress PR or distribute a binary. The target
is four validated Windows packages only after completing all services and
acceptance tests. .NET 10 does not support Windows 7 or Windows 8/8.1.
