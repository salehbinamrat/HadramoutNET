# WPF paid-license UI: interactive but not a full login

## Implemented controls

The WPF login panel has three distinct groups:

- **تفعيل الرمز**: an explicit operator action. The entered 32-hex
  code is normalized and verified against the original signed Supabase
  feed. A first-device claim is sent only when its signed record is
  pending and unbound. An HTTP 200/202 claim receipt does not grant access.
  The operator must check again for a newly signed active record.
- **فحص الترخيص المحفوظ**: a read-only signed feed lookup of the
  candidate code in the new protected .NET store, falling back to the
  original Python Supabase code **read-only** if the new store is absent.
  It cannot cause a first-device claim.
- **اختبار اتصال MikroTik (للقراءة فقط)**: separately authenticates,
  reads the router identity and closes the connection. It does not
  activate or start a trial.

Buttons are disabled while their own request executes, and the
entered license code/password input control is cleared. No candidate
code or RouterOS password is logged. Network and storage failures lock
the paid permission instead of granting a cached entitlement.

## Intentional safety gates

- The primary **تسجيل الدخول** button and free-trial **بدء التجربة**
  button remain disabled. The original Python program's protected
  480-hour trial start/mirror maintenance semantics have NOT yet been
  accepted against real existing Windows customer installations.
- Full RouterOS session, card management and printing workflows are
  not yet wired. Paid activation correctness is not the same as
  full application parity.
- CI compiles the actual WPF x86/x64 screen and exercises the
  paid-license core through signed, synthetic HTTP responses and
  the RouterOS probe through real TCP loopback. Actual production
  Supabase/customer licenses and actual routers are NOT contacted
  by CI.
- Tests first, then Windows Core and Windows Release x86/x64 in
  parallel on the same SHA; no merging or production packages until
  all acceptance gates and the four real binaries pass.
