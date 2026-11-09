"""Twenty-day local trial engine restored from the pre-GitHub implementation.

Starts only after successful RouterOS authentication. Paid licensing remains
exclusively verified with the separate signed GitHub feed. Storage persists
outside portable/installer folders and is protected with Windows machine DPAPI.
"""
from __future__ import annotations

import base64
import ctypes
import hashlib
import platform
import uuid
import json
import math
import os
import re
import tempfile
import time
from dataclasses import dataclass
from datetime import date, datetime, time as day_time, timedelta, timezone
from pathlib import Path


from .github_license import unique_pairs, format_license_countdown

TRIAL_DAYS = 20
TRIAL_DURATION = timedelta(days=TRIAL_DAYS)
APP_VERSION = '1.1.2'
MAX_STATE_BYTES = 8192
_REGISTRY_KEY = r'Software\Nidi\MikroTikCards\Licensing'
_REGISTRY_VALUE = 'ActivationStateV1'
_STATE_NAME = 'activation-state.bin'
_CLOCK_GRACE = timedelta(minutes=5)


class _InvalidState(Exception):
    pass


@dataclass(frozen=True)
class Access:
    mode: str  # ready, trial, active, locked, clock_error
    message: str
    remaining_days: int = 0
    remaining_seconds: int = -1  # Precise display clock; -1 for unlimited/not started.
    customer: str = ''
    expires: str = ''
    license_id: str = ''
    remaining_term: str = ''
    reason: str = ''
    trial_started: str = ''  # Original protected UTC timestamp; never reset by renewal.
    trial_expires: str = ''

    @property
    def permitted(self):
        return self.mode in ('ready', 'trial', 'active')


def _utc(now=None):
    now = now or datetime.now(timezone.utc)
    if now.tzinfo is None:
        raise ValueError('الوقت يجب أن يتضمن المنطقة الزمنية.')
    return now.astimezone(timezone.utc)


def _iso(value):
    return value.isoformat(timespec='microseconds' if value.microsecond else 'seconds')


def _parse(text):
    try:
        value = datetime.fromisoformat(text)
        if value.tzinfo is None:
            raise ValueError()
        return value.astimezone(timezone.utc)
    except (TypeError, ValueError, OverflowError):
        raise _InvalidState()


def _elapsed_clock():
    # Windows monotonic time includes suspend. BOOTTIME provides the same
    # property on Linux, so a suspended machine does not look like clock tampering.
    if hasattr(time, 'CLOCK_BOOTTIME'):
        return time.clock_gettime(time.CLOCK_BOOTTIME)
    return time.monotonic()


class ClockGuard:
    """Detect wall-clock jumps during this process BEFORE persisting a new last.

    This is a second check, not a trusted online clock and not a replacement for
    the protected cross-restart rollback check. No expiry or start is changed.
    Correcting the clock restores access without rewriting activation history.
    """
    def __init__(self, now=None, elapsed=None):
        self._elapsed = elapsed or _elapsed_clock
        self._tick = self._elapsed()
        self._wall = _utc(now)

    def accepts(self, now):
        elapsed = self._elapsed() - self._tick
        if elapsed < 0:
            return False
        expected = self._wall + timedelta(seconds=elapsed)
        return abs(_utc(now) - expected) <= _CLOCK_GRACE


def _clock_status(clock, now):
    if clock is not None and not clock.accepts(now):
        return Access('clock_error', 'تغيّر وقت الجهاز أثناء التشغيل. صحّح ساعة Windows؛ لم تُغيّر بداية التجربة أو مدتها.',
                      reason='clock_changed')
    return None


def _dpapi(data, protect):
    if os.name != 'nt':
        return data
    class Blob(ctypes.Structure):
        _fields_ = [('size', ctypes.c_ulong), ('ptr', ctypes.POINTER(ctypes.c_ubyte))]
    raw = ctypes.create_string_buffer(data)
    src = Blob(len(data), ctypes.cast(raw, ctypes.POINTER(ctypes.c_ubyte)))
    dst = Blob()
    func = (ctypes.windll.crypt32.CryptProtectData if protect
            else ctypes.windll.crypt32.CryptUnprotectData)
    if protect:
        # Machine-scope DPAPI permits a shared ProgramData trial across local users.
        ok = func(ctypes.byref(src), ctypes.c_wchar_p('Nidi Activation'), None, None, None, 0x1 | 0x4, ctypes.byref(dst))
    else:
        ok = func(ctypes.byref(src), None, None, None, None, 0x1, ctypes.byref(dst))
    if not ok:
        raise _InvalidState('Windows DPAPI failed')
    try:
        return ctypes.string_at(dst.ptr, dst.size)
    finally:
        ctypes.windll.kernel32.LocalFree(dst.ptr)


def machine_fingerprint():
    """Stable Windows installation identifier, not an online identity service.

    Windows MachineGuid is shared between local Windows accounts and survives
    reinstalling this portable app. Reinstalling Windows may change it.
    """
    identifier = ''
    if os.name == 'nt':
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE,
                                r'SOFTWARE\Microsoft\Cryptography', 0,
                                winreg.KEY_READ | winreg.KEY_WOW64_64KEY) as key:
                guid, kind = winreg.QueryValueEx(key, 'MachineGuid')
            if kind == winreg.REG_SZ and isinstance(guid, str):
                identifier = guid.strip().lower()
        except OSError:
            pass
    if not identifier:
        # Stable fallback if MachineGuid is not readable; not cryptographic hardware attestation.
        identifier = '%s:%012x' % (platform.node().strip().lower(), uuid.getnode())
    if not identifier or identifier == ':000000000000':
        raise _InvalidState('تعذر التعرف على بصمة الكمبيوتر.')
    return hashlib.sha256(('NIDI-MIKROTIK-CARDS-TRIAL-V2|' + identifier).encode('utf-8')).hexdigest().upper()



class StateStore:
    def __init__(self, folder=None, registry_enabled=None, machine_folder=None, fingerprint=None):
        self.fingerprint = fingerprint or machine_fingerprint()
        custom_folder = folder is not None
        if folder is None:
            base = os.environ.get('LOCALAPPDATA') or os.environ.get('APPDATA')
            folder = (Path(base) / 'NidiMikroTikCards' / 'licensing') if base else (Path.home() / '.NidiMikroTikCards' / 'licensing')
        self.path = Path(folder) / _STATE_NAME
        self.registry_enabled = (os.name == 'nt') if registry_enabled is None else registry_enabled
        if machine_folder is None and not custom_folder and os.name == 'nt':
            common = os.environ.get('PROGRAMDATA')
            machine_folder = Path(common) / 'NidiMikroTikCards' / 'licensing' if common else None
        self.machine_path = (Path(machine_folder) / 'trial-machine.bin') if machine_folder is not None else None

    @staticmethod
    def _read_path(path):
        if path is None:
            return None
        if path.is_symlink():
            raise _InvalidState()
        if not path.exists():
            return None
        with path.open('rb') as handle:
            data = handle.read(MAX_STATE_BYTES + 1)
        if len(data) > MAX_STATE_BYTES:
            raise _InvalidState()
        return data

    def _read_file(self):
        return self._read_path(self.path)

    def _read_registry(self):
        if not self.registry_enabled:
            return None
        import winreg
        try:
            with winreg.OpenKey(winreg.HKEY_CURRENT_USER, _REGISTRY_KEY) as key:
                encoded, kind = winreg.QueryValueEx(key, _REGISTRY_VALUE)
                if kind != winreg.REG_SZ or not isinstance(encoded, str):
                    raise _InvalidState()
                if len(encoded) > ((MAX_STATE_BYTES + 2) // 3) * 4:
                    raise _InvalidState()
                data = base64.b64decode(encoded, validate=True)
                if len(data) > MAX_STATE_BYTES:
                    raise _InvalidState()
                return data
        except FileNotFoundError:
            return None

    @staticmethod
    def _write_path(path, raw):
        path.parent.mkdir(parents=True, exist_ok=True)
        descriptor, temporary = tempfile.mkstemp(prefix='.activation-', dir=str(path.parent))
        try:
            with os.fdopen(descriptor, 'wb') as stream:
                stream.write(raw)
                stream.flush()
                os.fsync(stream.fileno())
            os.replace(temporary, str(path))
        finally:
            if os.path.exists(temporary):
                os.unlink(temporary)

    def _write_file(self, raw):
        self._write_path(self.path, raw)

    def _write_machine(self, raw):
        if self.machine_path is not None:
            self._write_path(self.machine_path, raw)

    def _write_registry(self, raw):
        if self.registry_enabled:
            import winreg
            with winreg.CreateKeyEx(winreg.HKEY_CURRENT_USER, _REGISTRY_KEY, 0,
                                    winreg.KEY_SET_VALUE) as key:
                winreg.SetValueEx(key, _REGISTRY_VALUE, 0, winreg.REG_SZ,
                                  base64.b64encode(raw).decode('ascii'))

    def _decode(self, raw):
        try:
            data = json.loads(_dpapi(raw, False).decode('utf-8'), object_pairs_hook=unique_pairs)
            if (not isinstance(data, dict) or type(data.get('schema')) is not int or data.get('schema') not in (1, 2)
                    or not isinstance(data.get('license_id'), str)
                    or not (not data['license_id'] or re.fullmatch(r'[0-9A-F]{16}', data['license_id']))):
                raise _InvalidState()
            if data['schema'] == 2 and data.get('machine_id') != self.fingerprint:
                raise _InvalidState('سجل التجربة يعود إلى جهاز Windows آخر.')
            first, last = _parse(data.get('first')), _parse(data.get('last'))
            if first > last or last > datetime.now(timezone.utc) + timedelta(days=36500):
                raise _InvalidState()
            revision = data.get('activation_rev', 0)
            if type(revision) is not int or not 0 <= revision <= 2147483647:
                raise _InvalidState()
            return {'schema': 2, 'first': _iso(first), 'last': _iso(last),
                    'license_id': data['license_id'], 'machine_id': self.fingerprint,
                    'activation_rev': revision}
        except (UnicodeError, ValueError, TypeError, OverflowError):
            raise _InvalidState()

    def load(self, now=None):
        """Merge mirrors while preserving the oldest trial start and latest clock.

        A replaced paid license is NOT a trial reset. Activation revision resolves
        a partially-written mirror after a renewal; the old copy is repaired.
        """
        try:
            readers = [self._read_file]
            if self.registry_enabled:
                readers.append(self._read_registry)
            if self.machine_path is not None:
                readers.append(lambda: self._read_path(self.machine_path))
            sources = []
            for read in readers:
                try:
                    sources.append(read())
                except (OSError, _InvalidState, ValueError):
                    # If a second mirror is intact it can repair this unreadable copy.
                    sources.append(b'INVALID')
            if all(raw is None for raw in sources):
                return None
            valid = []
            decoded = {}
            for raw in sources:
                if raw is None:
                    continue
                try:
                    # Read every mirror on every validation, but decrypt identical
                    # bytes only once within this call. No stale cross-call cache.
                    if raw not in decoded:
                        decoded[raw] = self._decode(raw)
                    valid.append(decoded[raw])
                except (_InvalidState, ValueError, TypeError):
                    continue
            if not valid:
                raise _InvalidState('تعذر قراءة أي نسخة سليمة من سجل هذا الكمبيوتر.')
            first = min(_parse(item['first']) for item in valid)
            last = max(_parse(item['last']) for item in valid)
            # Highest activation revision is the most recent user-authorized
            # license change; clock updates never outrank a renewal.
            chosen = max(valid, key=lambda item: (item['activation_rev'], _parse(item['last']), bool(item['license_id'])))
            state = {'schema': 2, 'first': _iso(first), 'last': _iso(last),
                     'license_id': chosen['license_id'], 'machine_id': self.fingerprint,
                     'activation_rev': max(item['activation_rev'] for item in valid)}
            if (len(valid) != len(sources) or any(item != state for item in valid)):
                self.save(state)
            return state
        except (OSError, _InvalidState, ValueError, TypeError) as exc:
            raise _InvalidState('تعذر قراءة سجل بصمة الجهاز أو التجربة. يمكن تفعيل ترخيص سليم. ' + str(exc))

    def save(self, state):
        if state.get('machine_id') != self.fingerprint:
            raise _InvalidState('بصمة الجهاز غير متطابقة.')
        raw = _dpapi(json.dumps(state, sort_keys=True, separators=(',', ':')).encode('utf-8'), True)
        saved = False
        writers = [self._write_file]
        if self.registry_enabled:
            writers.append(self._write_registry)
        if self.machine_path is not None:
            writers.append(self._write_machine)
        for write in writers:
            try:
                write(raw)
                saved = True
            except OSError:
                continue
        if not saved:
            raise _InvalidState('تعذر حفظ بيانات تجربة هذا الكمبيوتر.')


def _fresh(now, store):
    return {'schema': 2, 'first': _iso(now), 'last': _iso(now),
            'license_id': '', 'machine_id': store.fingerprint, 'activation_rev': 0}


def _get_state(store, now, create=False):
    try:
        state = store.load(now)
        if state is None and create:
            state = _fresh(now, store)
            store.save(state)
        return state, None
    except (OSError, _InvalidState, ValueError) as exc:
        return None, str(exc)


def begin_trial(root=None, version=APP_VERSION, *, store=None, now=None, clock=None):
    """Start trial only AFTER a successful RouterOS login, never on app launch."""
    now = _utc(now)
    clock_error = _clock_status(clock, now)
    if clock_error:
        return clock_error
    try:
        store = store or StateStore()
    except (_InvalidState, OSError) as exc:
        return Access('locked', str(exc), reason='state_invalid')
    state, error = _get_state(store, now, create=True)
    if error:
        return Access('locked', error, reason='state_invalid')
    return trial_access(root, version, store=store, now=now)


def _check_clock(state, now):
    return now + _CLOCK_GRACE < _parse(state['last'])


def trial_access(root=None, version=APP_VERSION, *, store=None, now=None, clock=None):
    now = _utc(now)
    clock_error = _clock_status(clock, now)
    if clock_error:
        return clock_error
    try:
        store = store or StateStore()
    except (_InvalidState, OSError) as exc:
        return Access('locked', str(exc), reason='state_invalid')
    state, error = _get_state(store, now)
    if error:
        return Access('locked', error, reason='state_invalid')
    if state is None:
        return Access('ready', 'تجربة مجانية 20 يوماً تبدأ عند أول دخول ناجح. بعدها يلزم شراء ترخيص لتستمر في استخدام البرنامج.',
                      remaining_days=TRIAL_DAYS, remaining_seconds=int(TRIAL_DURATION.total_seconds()), reason='not_started')
    first = _parse(state['first'])
    deadline = first + TRIAL_DURATION
    trial_details = {'trial_started': _iso(first), 'trial_expires': _iso(deadline)}
    if _check_clock(state, now):
        return Access('clock_error', 'تاريخ أو ساعة الجهاز رجع للخلف. صحّح ساعة Windows ثم افتح البرنامج.',
                      reason='clock_rollback', **trial_details)
    if now > _parse(state['last']) + timedelta(seconds=60):
        state['last'] = _iso(now)
        try:
            store.save(state)
        except (_InvalidState, OSError) as exc:
            return Access('locked', str(exc), reason='state_invalid', **trial_details)
    effective_now = max(now, _parse(state['last']))
    remaining = deadline - effective_now
    if remaining.total_seconds() > 0:
        seconds = max(0, math.ceil(remaining.total_seconds()))
        days = max(1, math.ceil(seconds / 86400))
        return Access('trial', 'التجربة المجانية — المتبقي: ' + format_license_countdown(seconds),
                      remaining_days=days, remaining_seconds=seconds,
                      reason='trial_valid', **trial_details)
    return Access('locked',
                  'انتهت التجربة المجانية بعد 20 يوماً (480 ساعة). يلزم تفعيل رمز الترخيص.',
                  remaining_seconds=0, reason='trial_expired', **trial_details)


def choose_access(online, trial, has_paid_code=False):
    """Signed online entitlement always wins; trial fallback never resets its start."""
    if online.permitted:
        return online
    if trial.mode in ('ready', 'trial', 'clock_error'):
        return trial
    # Once the free period ends, show a meaningful paid activation/network error
    # if a paid code exists; it cannot grant access without online verification.
    if has_paid_code and online.reason:
        return online
    return trial
