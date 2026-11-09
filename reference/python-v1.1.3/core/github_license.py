"""Signed Supabase paid licensing, independent of the original 20-day local trial."""
from __future__ import annotations

import base64
import hashlib
import json
import os
import platform
import re
import secrets
import tempfile
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

from .ed25519_compat import verify as verify_ed25519

APP_VERSION = "1.1.3"
PRODUCT = "NIDI-MIKROTIK-CARDS"
FEED_URL = "https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/licenses.json"
TRUSTED_PUBLIC_KEY = base64.b64decode("yifdpnKvdmxoEGUOoQGqeytEaume9Ldp7sKPbpQuGBE=")
ACTIVATION_URL = "https://zlirqppwemfmzxkholcr.supabase.co/functions/v1/hadramout-license/v1/claim"
CHECK_EVERY_SECONDS = 1800
CHECK_TIMEOUT_SECONDS = 8
MAX_FEED_BYTES = 1024 * 1024
CODE = re.compile(r"[0-9A-F]{32}\Z")
DEVICE = re.compile(r"[0-9A-F]{64}\Z")


@dataclass(frozen=True)
class Access:
    mode: str
    message: str
    license_id: str = ""
    expires: str = ""
    remaining_term: str = ""
    remaining_seconds: int = -1
    reason: str = ""

    @property
    def permitted(self):
        return self.mode == "active"


def canonical(data):
    return json.dumps(data, ensure_ascii=False, sort_keys=True,
                      separators=(",", ":"), allow_nan=False).encode("utf-8")


def unique_pairs(items):
    data = {}
    for key, value in items:
        if key in data:
            raise ValueError("Duplicate JSON field")
        data[key] = value
    return data


def machine_code():
    """Identifier of Windows installation, not tamper-proof hardware attestation."""
    ident = ""
    if os.name == "nt":
        import winreg
        try:
            # Use the 64-bit registry view even for a 32-bit portable/installer
            # on x64 Windows: one Windows installation, one license/device ID.
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                                r"SOFTWARE\Microsoft\Cryptography", 0,
                                winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
                value, kind = winreg.QueryValueEx(key, "MachineGuid")
            if kind == winreg.REG_SZ and isinstance(value, str):
                ident = value.strip().lower()
        except OSError:
            pass
    if not ident:
        ident = "%s:%012x" % (platform.node().strip().lower(), uuid.getnode())
    if not ident or ident == ":000000000000":
        raise ValueError("تعذر التعرف على الجهاز.")
    return hashlib.sha256(("HADRAMOUT-GITHUB-LICENSE-V1|" + ident).encode("utf-8")).hexdigest().upper()


def normalize_code(code):
    cleaned = re.sub(r"[-\s]", "", str(code or "")).upper()
    return cleaned if CODE.fullmatch(cleaned) else ""


def verify_feed(raw, license_id, device_id, today=None, public_key=TRUSTED_PUBLIC_KEY):
    """Fail closed unless a vendor-signed license matches THIS Windows installation."""
    denied = lambda message, reason="denied": Access("locked", message, reason=reason)
    if not isinstance(raw, bytes) or len(raw) > MAX_FEED_BYTES:
        return denied("استجابة خدمة الترخيص غير صالحة.", "feed_invalid")
    if not CODE.fullmatch(license_id) or not DEVICE.fullmatch(device_id):
        return denied("رمز الترخيص أو بصمة الجهاز غير صالح.", "input_invalid")
    try:
        document = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_pairs)
        signature = base64.b64decode(document.pop("signature"), validate=True)
        if not verify_ed25519(signature, canonical(document), public_key):
            raise ValueError("bad signature")
        if (document.get("schema") != 1 or document.get("product") != PRODUCT
                or type(document.get("revision")) is not int
                or document["revision"] < 1
                or not isinstance(document.get("licenses"), dict)):
            raise ValueError("invalid feed")
        issued = datetime.fromisoformat(document["generated_at"].replace("Z", "+00:00"))
        if issued.tzinfo is None or issued > datetime.now(timezone.utc) + timedelta(minutes=5):
            raise ValueError("invalid signed timestamp")
        public_id = hashlib.sha256(license_id.encode("ascii")).hexdigest().upper()
        record = document["licenses"].get(public_id)
        if not isinstance(record, dict):
            return denied("الرمز لم يظهر بعد في سجل التراخيص المنشور. سيعاد الفحص تلقائياً؛ تأكد من الرمز إذا استمرت المشكلة.", "not_found")
        if record.get("status") == "pending" and record.get("device") is None:
            expiry = record.get("expires")
            if expiry is not None:
                try:
                    if date.fromisoformat(expiry) < (today or datetime.now(timezone.utc).date()):
                        return denied("انتهت صلاحية رمز الترخيص قبل أول تفعيل.", "expired")
                except (TypeError, ValueError):
                    return denied("بيانات انتهاء الترخيص غير صالحة.", "feed_invalid")
            return denied("هذا الرمز جاهز للربط بأول جهاز عبر الإنترنت.", "pending_activation")
        if record.get("device") != device_id:
            return denied("الترخيص مخصص لجهاز آخر.", "wrong_device")
        if record.get("status") != "active":
            return denied("هذا الترخيص موقوف أو ملغى من جهة إصدار الترخيص.", "revoked")
        expires = record.get("expires")
        today = today or datetime.now(timezone.utc).date()
        if expires is not None:
            if not isinstance(expires, str) or not re.fullmatch(r"\d{4}-\d{2}-\d{2}", expires):
                raise ValueError("invalid expiry")
            finish = date.fromisoformat(expires)
            if today > finish:
                return denied("انتهت صلاحية الترخيص. اطلب التجديد من المورّد.", "expired")
            seconds = max(0, (finish - today).days * 86400 + 86400)
            term = str((finish - today).days + 1) + " يوم"
        else:
            seconds = -1
            term = "دائم"
        return Access("active", "رمز الترخيص صالح — " + term,
                      license_id=license_id, expires=expires or "",
                      remaining_term=term, remaining_seconds=seconds, reason="online_valid")
    except (ValueError, TypeError, KeyError, AttributeError, UnicodeError, OverflowError):
        return denied("تعذر التحقق من التوقيع أو بيانات رمز الترخيص.", "feed_invalid")


def download_feed(url=FEED_URL):
    """Always read HTTPS GitHub Pages, without authenticating as the repository owner."""
    if not url.startswith("https://") or not url.endswith("/licenses.json"):
        raise ValueError("رابط التحقق غير موثوق.")
    request = urllib.request.Request(url + "?check=" + secrets.token_hex(8),
                                      headers={"Cache-Control": "no-cache",
                                               "Pragma": "no-cache",
                                               "Accept": "application/json",
                                               "User-Agent": "Hadramout-Cards-License/1"})
    with urllib.request.urlopen(request, timeout=CHECK_TIMEOUT_SECONDS) as response:
        if response.status != 200:
            raise ValueError("GitHub returned non-200 status")
        if not response.geturl().startswith("https://"):
            raise ValueError("HTTPS redirect rejected")
        content = response.read(MAX_FEED_BYTES + 1)
    if len(content) > MAX_FEED_BYTES:
        raise ValueError("GitHub feed is too large")
    return content


def request_first_activation(code, device_id, url=ACTIVATION_URL):
    """Send only the code and hashed device ID over HTTPS; never use a GitHub token here."""
    data = json.dumps({"license_id": code, "device_code": device_id},
                      separators=(",", ":")).encode("ascii")
    request = urllib.request.Request(url, data=data, method="POST",
                                     headers={"Content-Type": "application/json",
                                              "Accept": "application/json",
                                              "User-Agent": "Hadramout-Cards-License/1"})
    with urllib.request.urlopen(request, timeout=CHECK_TIMEOUT_SECONDS) as response:
        if response.status not in (200, 202):
            raise ValueError("Activation request rejected")


class OnlineLicensing:
    """Remember only the code, never local entitlement or an offline grant."""
    def __init__(self, storage=None, device_id=None, fetcher=None, claimer=None, sleeper=None):
        base = os.environ.get("LOCALAPPDATA") or os.environ.get("APPDATA")
        default = (Path(base) if base else Path.home()) / "NidiMikroTikCards" / "licensing"
        self.path = Path(storage) if storage is not None else default / "supabase-license-id.txt"
        self.device_id = device_id or machine_code()
        self.fetcher = fetcher or download_feed
        self.claimer = claimer or request_first_activation
        self.sleeper = sleeper or time.sleep
        self._last_claim = 0.0
        self.code = self._read_code()
        self._status = Access("locked", "جارٍ فحص الترخيص عبر خدمة الترخيص..." if self.code
                              else "أدخل رمز الترخيص الصادر لك؛ ستُربط بصمة Windows تلقائياً عند أول تفعيل.")
        self._last_attempt = 0.0
        self._last_success = 0.0

    def _read_code(self):
        try:
            if self.path.is_symlink():
                return ""
            if self.path.stat().st_size > 64:
                return ""
            return normalize_code(self.path.read_text(encoding="ascii"))
        except (OSError, UnicodeError):
            return ""

    def _save_code(self, code):
        self.path.parent.mkdir(parents=True, exist_ok=True)
        fd, name = tempfile.mkstemp(prefix=".supabase-license-", dir=str(self.path.parent))
        try:
            with os.fdopen(fd, "w", encoding="ascii") as output:
                output.write(code + "\n")
                output.flush()
                os.fsync(output.fileno())
            os.replace(name, self.path)
        finally:
            if os.path.exists(name):
                os.unlink(name)

    def status(self):
        if self._status.permitted and time.monotonic() - self._last_success > CHECK_EVERY_SECONDS + 20:
            return Access("locked", "انتهت مهلة التحقق الإلكتروني. اتصل بالإنترنت.", reason="check_overdue")
        return self._status

    def needs_check(self):
        if not self.code:
            return False
        last = self._last_success if self._status.permitted else self._last_attempt
        interval = CHECK_EVERY_SECONDS if self._status.permitted else 60
        return not last or time.monotonic() - last >= interval

    def verify(self, code=None):
        requested = normalize_code(code if code is not None else self.code)
        self._last_attempt = time.monotonic()
        if not requested:
            self._status = Access("locked", "رمز الترخيص يجب أن يتكون من 32 خانة.", reason="bad_code")
            return self._status
        try:
            raw = self.fetcher()
            result = verify_feed(raw, requested, self.device_id)
        except (OSError, ValueError, TimeoutError, urllib.error.URLError):
            result = Access("locked", "تعذر الاتصال بخدمة الترخيص. تأكد من الإنترنت وحاول مجدداً.",
                            reason="network_error")
        if result.reason == "pending_activation":
            try:
                now = time.monotonic()
                if not self._last_claim or now - self._last_claim > 120:
                    self.claimer(requested, self.device_id)
                    self._last_claim = now
                for _ in range(4):
                    self.sleeper(3)
                    next_status = verify_feed(self.fetcher(), requested, self.device_id)
                    if next_status.reason != "pending_activation":
                        result = next_status
                        break
                else:
                    result = Access("locked", "تم إرسال بصمة جهازك؛ جارٍ اعتماد الترخيص. أعد المحاولة بعد قليل.",
                                    reason="claim_pending")
            except (OSError, ValueError, TimeoutError, urllib.error.URLError):
                result = Access("locked", "تعذر تسجيل الجهاز مؤقتاً؛ سيعاد التفعيل تلقائياً عند توفر الخدمة دون إدخال الرمز مجدداً.",
                                reason="claim_unavailable")
        if result.permitted or result.reason in ("claim_pending", "not_found", "claim_unavailable"):
            try:
                if requested != self.code:
                    self._save_code(requested)
            except OSError:
                result = Access("locked", "فشل حفظ رمز الجهاز؛ راجع صلاحيات ملفات المستخدم.",
                                reason="storage_error")
            if result.permitted or result.reason in ("claim_pending", "not_found", "claim_unavailable"):
                self.code = requested
                if result.permitted:
                    self._last_success = time.monotonic()
        self._status = result
        return result

def format_license_countdown(seconds):
    """Simple formatting only: UI updates never access GitHub or disk."""
    if seconds < 0:
        return "غير محدود"
    days, remainder = divmod(max(0, int(seconds)), 86400)
    hours, remainder = divmod(remainder, 3600)
    minutes, seconds = divmod(remainder, 60)
    return "%d يوم، %02d ساعة، %02d دقيقة، %02d ثانية" % (days, hours, minutes, seconds)
