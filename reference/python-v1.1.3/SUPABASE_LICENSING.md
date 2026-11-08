# Hadramout Cards — new Supabase licensing backend

This branch replaces all live **paid licensing** endpoints with an independent
Supabase production service and management portal built in Lovable.

- Product identifier: `NIDI-MIKROTIK-CARDS`.
- Public API: `https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license`.
- Client receives a random 32-hex license code from the administrator.
- No fingerprint is required to **issue** the code.
- The first client to enter an unclaimed code sends a hashed Windows installation
  identifier to Supabase via HTTPS. Supabase atomically binds the code to that device.
- The signed license feed is verified locally using a pinned, **public** Ed25519 key
  (`yifdpnKvdmxoEGUOoQGqeytEaume9Ldp7sKPbpQuGBE=`). No Supabase service credentials are embedded.
- A different device is rejected, and revoked/suspended/expired codes are denied.
- Only the **new** `supabase-license-id.txt` is used locally. Old paid license
  tokens are not migrated and old providers are not contacted.
- The original 20-day (480-hour) trial remains independent and starts after a
  successful MikroTik login; its clock/history is not reset.
- Lovable admin: https://hadramout-license-center-saleh.lovable.app
- GitHub PR gates: Windows Core + x86/x64 Release tests on same SHA; then
  x86/x64 Portable and Installer builds, integrity and SHA-256.

**Compatibility:** internal file `core/github_license.py` retains its name
for existing Python import paths but contains no GitHub or Cloudflare licensing
calls. It is Supabase-backed only.

**Shipping gate:** run real Windows first-device, wrong-device, renewal,
revocation, and trial verification in addition to CI before distributing
installer binaries to paying customers.
