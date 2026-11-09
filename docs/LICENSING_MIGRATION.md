# HadramoutNET licensing migration gate

## Pinned source contract

Original Python version 1.1.3: reference/python-v1.1.3/core/github_license.py
and reference/python-v1.1.3/core/trial_runtime.py, both copied without edits
from salehbinamrat/Hadramout pinned at
bdfb355b1dbaf8f868d811ea55d4c1af26eb3a37.

Paid license authority: **Supabase**, not the old GitHub/Cloudflare implementation.

- Product: NIDI-MIKROTIK-CARDS
- Signed feed: https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/licenses.json
- Explicit first claim: https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/v1/claim
- Public Ed25519 verify key: yifdpnKvdmxoEGUOoQGqeytEaume9Ldp7sKPbpQuGBE=
- Feed limit: 1 MiB. Verification interval in original: 30 minutes. Network timeout: 8 seconds.
- Actual paid access ONLY after verifying vendor signature, product, revision,
  generated_at, hashed 32-hex code, device fingerprint, status and expiry.
- For pending, only an explicitly requested first claim may send a JSON body
  containing license_id and device_code. An HTTP 200/202 is NOT entitlement;
  recheck signed feed for active status.

The pinned public key is public, not a secret. Never add a signing private key,
Supabase service token, user license code, machine fingerprint or unencrypted
RouterOS credential to GitHub or to diagnostic logs.

## Trial contract

Original free trial: **480 hours** after the first successful RouterOS login,
never on app launch. Original state lives in NidiMikroTikCards/licensing
under LOCALAPPDATA, HKCU Software\Nidi\MikroTikCards\Licensing,
and (when available) the ProgramData machine mirror. Python protects the state
with Windows machine DPAPI and reconciles all mirrors by taking the **oldest**
first, **latest** last and highest authorized activation revision.

The current .NET TrialAccessPolicy is a tested pure calculator. It does
**not** create, overwrite, migrate, erase, repair or read the original trial
state. The first start must not change during renewal. If this record cannot
be validated in the new application, fail closed and require a valid paid
license; **never** create a fresh trial simply because a reader failed.

## Blockers before live UI and release

1. Implement a Windows-only, read-only protected trial reader compatible with
   the original DPAPI, Registry and ProgramData mirrors; verify with fixtures
   on x86/x64 and actual existing Windows installations. Do not modify the
   original mirrors during development. Handle invalid/missing/partially
   corrupted mirrors explicitly; preserve earliest first and latest last.
2. Implement secure, isolated code storage and device identity retrieval:
   64-bit MachineGuid registry view on x86/x64, same SHA-256 prefixes as Python;
   no accidental paid entitlement from a saved code alone.
3. Integrate RouterOS login, user-visible activation and periodic online
   re-verification. Do not automatically claim at startup; require a user
   activation action. Paid checks must fail closed if overdue.
4. Real-machine Windows UI and integration tests for first activation,
   active/revoked/wrong-device/expired/offline signatures, the completed
   480-hour trial, clock rollback, renewal and restart, without using
   genuine customer credentials in CI.
5. Run Windows Core and Windows Release x86/x64 in parallel on the same SHA;
   all tests must pass before generating/validating four genuine packages.
   Preserve draft PR and do not merge prematurely.

These are implemented as **independent core primitives only**, not a working
license screen and not a shippable substitute for the original Python program.
