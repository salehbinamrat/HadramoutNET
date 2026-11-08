from __future__ import annotations

import secrets
import math
import unicodedata
import threading
import time
from datetime import datetime


UNIT_MULTIPLIERS = {
    "B": 1,
    "KB": 1024,
    "MB": 1024 ** 2,
    "GB": 1024 ** 3,
    "TB": 1024 ** 4,
}

MAX_CODE_LENGTH = 64
MAX_BATCH_SIZE = 10000
CARD_FORMATS = ('code_only', 'code_same_password', 'code_password')

# Batch numbering state: a monotonic tie-breaker for same-millisecond calls.
_BATCH_COUNTER_LOCK = threading.Lock()
_BATCH_COUNTER = 0
_LAST_BATCH_MS = 0


class CardGenerationError(ValueError):
    """Raised when requested card options cannot produce a safe batch."""


def unique_characters(chars: str) -> str:
    """Return characters in their original order without duplicates."""
    return "".join(dict.fromkeys(str(chars or "")))


def lowercase_english(value):
    """Normalize new generation inputs without touching stored credentials."""
    return str(value or '').translate(str.maketrans(
        'ABCDEFGHIJKLMNOPQRSTUVWXYZ', 'abcdefghijklmnopqrstuvwxyz'))


def validate_characters(chars: str, field_name: str = "الأحرف") -> str:
    chars = unique_characters(chars)
    if not chars:
        raise CardGenerationError("يجب تحديد حرف واحد على الأقل في %s." % field_name)
    invalid = [ch for ch in chars if ch.isspace() or unicodedata.category(ch).startswith('C')]
    if invalid:
        raise CardGenerationError("لا يُسمح بالمسافات أو رموز التحكم داخل %s." % field_name)
    return chars


def validate_username_prefix(prefix: str, code_length: int, username_chars: str) -> str:
    """Validate the username prefix against the selected username alphabet."""
    prefix=str(prefix or "").strip()
    code_length=int(code_length)
    username_chars=validate_characters(username_chars,"أحرف اسم المستخدم")
    if any(ch.isspace() or unicodedata.category(ch).startswith('C') for ch in prefix):
        raise CardGenerationError("بادئة رقم الكرت لا تقبل المسافات أو رموز التحكم.")
    if len(prefix)>code_length:
        raise CardGenerationError("بادئة رقم الكرت أطول من طول اسم المستخدم المحدد.")
    # The UI labels the 0-9 alphabet as "digits only".  Enforce the same rule
    # in the core so imported presets and non-UI callers cannot create letters
    # while claiming a digits-only username mode.
    if username_chars and all(ch.isdigit() for ch in username_chars):
        if any(not ch.isdigit() for ch in prefix):
            raise CardGenerationError("عند اختيار أرقام فقط يجب أن تكون بادئة رقم الكرت أرقامًا فقط.")
    return prefix


def random_token(chars: str, length: int, chooser=None) -> str:
    chars = validate_characters(chars)
    length = int(length)
    if length < 1 or length > MAX_CODE_LENGTH:
        raise CardGenerationError("طول اسم المستخدم يجب أن يكون بين 1 و%d." % MAX_CODE_LENGTH)
    chooser = chooser or secrets.choice
    return "".join(chooser(chars) for _ in range(length))


def new_batch_no() -> str:
    """Return a collision-resistant batch identifier, backed by DB uniqueness.

    The wall clock alone is not enough: several batches can be created inside
    the same millisecond (a script, or a double click that slips past the busy
    guard), and a 2-byte random suffix then collides by birthday paradox after
    roughly a thousand batches.  A monotonic counter makes the value strictly
    increasing inside this process. The random suffix reduces collisions after
    a restart; the database UNIQUE constraint is the final cross-process guard.
    """
    stamp = datetime.now().strftime("%y%m%d-%H%M%S-%f")[:-3]
    with _BATCH_COUNTER_LOCK:
        global _LAST_BATCH_MS, _BATCH_COUNTER
        now_ms = time.time_ns() // 1_000_000
        if now_ms <= _LAST_BATCH_MS:
            _BATCH_COUNTER += 1
        else:
            _LAST_BATCH_MS = now_ms
            _BATCH_COUNTER = 0
        counter = _BATCH_COUNTER
    return "BT-%s-%03d-%s" % (stamp, counter, secrets.token_hex(2).upper())


def sequence_text(i: int, mode: str) -> str:
    width = {"00": 2, "000": 3, "0000": 4}.get(str(mode), 0)
    return ("%0*d" % (width, int(i))) if width else str(int(i))


def bytes_total(value, unit):
    try:
        number = float(value)
    except (TypeError, ValueError):
        raise ValueError("قيمة السعة غير صحيحة.")
    if not math.isfinite(number) or number < 0:
        raise ValueError("قيمة السعة لا يمكن أن تكون سالبة.")
    normalized = str(unit or "B").upper()
    if normalized not in UNIT_MULTIPLIERS:
        raise ValueError("وحدة السعة غير مدعومة: %s" % normalized)
    total=number * UNIT_MULTIPLIERS[normalized]
    if not math.isfinite(total):
        raise ValueError("قيمة السعة أكبر من الحد الذي يمكن حسابه؛ أدخل قيمة أصغر.")
    return int(total)


def readable_bytes(n):
    try:
        n = float(n or 0)
    except (TypeError, ValueError):
        n = 0.0
    for unit in ("B", "KB", "MB", "GB", "TB"):
        if abs(n) < 1024 or unit == "TB":
            return ("%.0f %s" % (n, unit)) if abs(n) >= 10 else ("%.1f %s" % (n, unit))
        n /= 1024.0
    return "0 B"


def uptime_string(hours: int, minutes: int) -> str:
    hours = int(hours or 0)
    minutes = int(minutes or 0)
    if hours < 0 or minutes < 0:
        raise ValueError("مدة الاستخدام لا يمكن أن تكون سالبة.")
    hours += minutes // 60
    minutes %= 60
    parts = []
    if hours:
        parts.append("%dh" % hours)
    if minutes:
        parts.append("%dm" % minutes)
    return "".join(parts)


def _combination_count(chars: str, length: int) -> int:
    # Stop once the result is already far above any supported batch.
    total = 1
    for _ in range(length):
        total *= len(chars)
        if total > MAX_BATCH_SIZE * 1000:
            break
    return total


def generate_cards(
    db,
    quantity: int,
    code_length: int,
    username_chars: str,
    card_format: str = "code_only",
    password_length: int = 6,
    password_chars: str = "abcdef",
    sequence_mode: str = "0000",
    avoid_duplicate: bool = True,
    start_sequence: int = 1,
    username_prefix: str = "",
):
    """Generate a finite, validated batch of voucher dictionaries."""
    if card_format not in CARD_FORMATS:
        raise CardGenerationError("صيغة بيانات الدخول غير صحيحة.")
    quantity = int(quantity)
    code_length = int(code_length)
    start_sequence = int(start_sequence)
    if quantity < 1 or quantity > MAX_BATCH_SIZE:
        raise CardGenerationError("الكمية يجب أن تكون بين 1 و%d كرت." % MAX_BATCH_SIZE)
    if start_sequence < 0:
        raise CardGenerationError("بداية الترقيم لا يمكن أن تكون سالبة.")

    username_chars = validate_characters(lowercase_english(username_chars), "أحرف اسم المستخدم")
    if code_length < 1 or code_length > MAX_CODE_LENGTH:
        raise CardGenerationError("طول اسم المستخدم يجب أن يكون بين 1 و%d." % MAX_CODE_LENGTH)
    username_prefix = validate_username_prefix(lowercase_english(username_prefix),code_length,username_chars)
    if all('a' <= ch <= 'z' for ch in username_chars) and any(
            not 'a' <= ch <= 'z' for ch in username_prefix):
        raise CardGenerationError('عند اختيار أحرف فقط يجب أن تحتوي البادئة على أحرف إنجليزية فقط.')
    random_length = code_length - len(username_prefix)

    if card_format == "code_password":
        password_chars = validate_characters(lowercase_english(password_chars), "أحرف كلمة المرور")
        password_length = int(password_length)
        if password_length < 1 or password_length > MAX_CODE_LENGTH:
            raise CardGenerationError("طول كلمة المرور يجب أن يكون بين 1 و%d." % MAX_CODE_LENGTH)

    available = _combination_count(username_chars, random_length)
    # A one-character password alphabet has only one possible password. That
    # username cannot be part of a batch whose credentials must be different.
    fixed_password = None
    if card_format == 'code_password' and len(password_chars) == 1:
        fixed_password = password_chars * password_length
        if (len(fixed_password) == code_length and fixed_password.startswith(username_prefix)
                and all(ch in username_chars for ch in fixed_password[len(username_prefix):])):
            available -= 1
    if quantity > available:
        raise CardGenerationError(
            "لا يمكن توليد %d كرت مختلف؛ تركيبة الأحرف والطول توفر %d فقط. "
            "زِد طول اسم المستخدم أو عدد الأحرف." % (quantity, available)
        )

    cards = []
    seen = set()
    # A finite retry bound protects against an exhausted namespace in the DB.
    max_attempts = max(200, quantity * 80)
    attempts = 0
    while len(cards) < quantity:
        attempts += 1
        if attempts > max_attempts:
            raise CardGenerationError(
                "تعذر العثور على أسماء مستخدمين جديدة غير مكررة. زِد طول اسم المستخدم أو غيّر الأحرف المستخدمة."
            )
        suffix = random_token(username_chars, random_length) if random_length else ""
        username = username_prefix + suffix
        # Preserve the argument for saved presets, but uniqueness is mandatory
        # for vouchers regardless of an old preset's avoid_duplicate value.
        if username == fixed_password or username in seen or db.username_exists(username):
            continue
        seen.add(username)
        password = ""
        if card_format == 'code_same_password':
            password = username
        elif card_format == 'code_password':
            password = random_token(password_chars, password_length)
            if password == username:
                # Guarantee inequality without an unbounded random retry.
                position = secrets.randbelow(password_length)
                alternatives = password_chars.replace(username[position], '')
                replacement = secrets.choice(alternatives)
                password = password[:position] + replacement + password[position + 1:]
        sequence = start_sequence + len(cards)
        cards.append(
            {
                "sequence": sequence_text(sequence, sequence_mode),
                "username": username,
                "password": password,
            }
        )
    return cards
