"""Regression tests for replacing legacy activation with Supabase production."""
import base64
import json
import os
import urllib.error
import unittest
from unittest.mock import patch

from core import github_license as license_module

PRODUCTION = "https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license"
PINNED_PUBLIC_KEY = "yifdpnKvdmxoEGUOoQGqeytEaume9Ldp7sKPbpQuGBE="

class SupabaseLicenseContractTests(unittest.TestCase):
    def test_new_environment_is_production_supabase_only(self):
        self.assertEqual(license_module.FEED_URL, PRODUCTION + "/licenses.json")
        self.assertEqual(license_module.ACTIVATION_URL, PRODUCTION + "/v1/claim")
        self.assertEqual(license_module.TRUSTED_PUBLIC_KEY, base64.b64decode(PINNED_PUBLIC_KEY))
        self.assertNotIn("github.io", license_module.FEED_URL)
        self.assertNotIn("workers.dev", license_module.ACTIVATION_URL)

    def test_first_activation_transmits_only_code_and_hashed_device(self):
        code, device = "A" * 32, "B" * 64
        seen = []
        class Response:
            status = 200
            def __enter__(self): return self
            def __exit__(self, *args): pass
        def intercept(request, timeout):
            seen.append((request.full_url, request.get_method(), json.loads(request.data)))
            return Response()
        with patch("core.github_license.urllib.request.urlopen", side_effect=intercept):
            license_module.request_first_activation(code, device)
        self.assertEqual(seen, [(PRODUCTION + "/v1/claim", "POST",
                                {"license_id": code, "device_code": device})])

    def test_new_client_does_not_read_legacy_saved_code(self):
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as d:
            root = Path(d)
            old = root / "NidiMikroTikCards" / "licensing"
            old.mkdir(parents=True)
            (old / "github-license-id.txt").write_text("A"*32)
            with patch.dict(os.environ, {"LOCALAPPDATA":d}, clear=False):
                client = license_module.OnlineLicensing(device_id="B"*64,
                    fetcher=lambda: b"", claimer=lambda *_:None)
            self.assertEqual(client.code, "")
            self.assertIn("supabase", client.path.name.lower())

class LiveSupabaseSignatureTests(unittest.TestCase):
    @unittest.skipUnless(os.environ.get("HADRAMOUT_TEST_LIVE_LICENSE")=="1",
                         "Opt-in CI real production signature check")
    def test_real_signed_feed_is_verified_without_any_valid_license(self):
        # Nonexistent code avoids issuing, claiming or exposing user licenses.
        fake_code = "0" * 32
        fake_device = "0" * 64
        feed = license_module.download_feed()
        status = license_module.verify_feed(feed, fake_code, fake_device)
        self.assertEqual(status.reason, "not_found",
                         "Production feed signature or trust key did not verify")

if __name__ == "__main__":
    unittest.main()
