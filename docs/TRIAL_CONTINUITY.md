# Independent trial continuity and start-gate acceptance

This component is a **separate .NET store**, never a rewrite of the
original Python program's Windows machine-DPAPI, HKCU registry,
LOCALAPPDATA or ProgramData trial files.

- Read the original Python state on every Preview and before every write.
- Read the independent .NET user and shared machine-scoped DPAPI files.
- If either original or new reader reports corrupt or wrong-device state,
  lock the free trial. Do not initialize a new 480-hour period.
- Merge by oldest first, newest last and highest activation revision.
  Never reset the first timestamp on renewal, restart or upgrade.
- Preview is strictly read-only, and a first start MUST follow successful
  /login + /system/identity/print via the live router session.
- ConfirmAfterAuthenticatedRouterLogin is deliberately not called by the
  current WPF RouterConnectionProbe. A successful connection PROBE is not
  an authorized application login.
- The newly persisted shared ProgramData mirror is mandatory for the first
  480-hour activation to prevent resetting by switching local accounts.
  If Windows permissions deny writing it, show an error without starting.
- All test state is synthetic and encrypted using Windows DPAPI on CI.
  No customer fingerprint or trial records are committed to Git.
- Release blockers: Windows authentic login gate; wall-vs-monotonic ClockGuard
  parity; cross-process trial-store locking; real x86 and x64 customer
  acceptance against Python state and permissions; all original services.
