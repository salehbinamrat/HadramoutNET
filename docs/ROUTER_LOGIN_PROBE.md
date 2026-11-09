# RouterOS read-only login probe acceptance

Source contract: reference/python-v1.1.3/core/session.py,
login_settings() and authenticate_router(). TCP transport:
reference/python-v1.1.3/core/mikrotik.py.

## What is implemented

The new WPF login screen has one explicitly user-triggered
"اختبار اتصال MikroTik (للقراءة فقط)" action. On click, it reads
the host/selected API port, connection mode, username, password, SSL flags.

The RouterConnectionProbe:
1. Validates the local/remote address exactly with RouterAddress.Normalize;
   an embedded WinBox URL port NEVER overrides the separate API port.
2. Connects and authententicates with RouterOsApiClient using the supported
   RouterOS v6 direct/legacy challenge and v7 direct login.
3. Executes exactly one safe command, /system/identity/print; on an empty
   identity field, falls back to the normalized host as in Python.
4. Disposes the API session immediately, even on trap/timeout/cancellation.
5. Returns sanitized error categories, not raw trap strings that might
   reveal router passwords or other credentials.
6. Does not create a router record, write to SQLite, start a 480-hour
   free trial, issue/claim a paid license, or open management screens.
7. Clears the password control after an explicit test. No saved credentials
   are added by this milestone.

## Tests and limitations

Tests are real RouterOS wire-protocol TCP loopback integration tests, including
authentication success/rejection, missing identity permission, remote port
normalization, empty identity and user cancellation. Windows WPF x86 and
x64 compilation runs after automated tests.

**Important:** A loopback server is not a real MikroTik installation.
Real RouterOS v6/v7/API-SSL devices and actual VPN cases must pass acceptance
before production release. The production login button remains disabled and
no trial or paid grant is issued by the probe.

Next: trial activation writing in the new program only with continuity of
existing history and read-only original DPAPI files, test-first service
integration with signed paid checks, full live authenticated session lifecycle
and router data dashboards. Do not turn a probe into a production login
without real end-to-end permissions and authorization tests.
