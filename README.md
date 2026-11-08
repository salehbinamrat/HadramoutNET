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

- RouterOS word framing and read/write safety guard in
  `src/HadramoutNET.Core` (not yet a working MikroTik client).
- Card-generation core and documented validation rules.
- Color tokens for the planned glossy-red WPF interface.
- Tests are under `tests/HadramoutNET.Tests`; Windows Core and Windows
  Release x86/x64 launch in parallel, all before packaging.
- Original Python reference in `reference/python-v1.1.3` pinned at
  `bdfb355b1dbaf8f868d811ea55d4c1af26eb3a37`.
- **61 of 63 reference files copied identically.** Two PNG files
  larger than 1 MiB still need a byte-for-byte upload. See
  `docs/SOURCE_COPY_STATUS.md` and `docs/SOURCE_MANIFEST.json`.

## Test locally

```powershell
dotnet restore tests/HadramoutNET.Tests/HadramoutNET.Tests.csproj
dotnet test tests/HadramoutNET.Tests/HadramoutNET.Tests.csproj -c Release
```

Release artifacts, installer and full feature parity are **not yet available**.
Do not merge this work-in-progress PR or distribute a binary. The target
is four validated Windows packages only after completing all services and
acceptance tests. .NET 10 does not support Windows 7 or Windows 8/8.1.
