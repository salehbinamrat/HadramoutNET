from __future__ import annotations

import hashlib
import re
import socket
import ssl
import struct
import time
from dataclasses import dataclass, field


MAX_API_WORD = 32 * 1024 * 1024
MAX_API_SENTENCE = 64 * 1024 * 1024
MAX_API_WORDS = 4096
MAX_API_RESPONSE = 128 * 1024 * 1024
MAX_API_ROWS = 250000


# Router-safety barrier: the application is a voucher/profile manager, not a
# general RouterOS configuration shell.  Read commands remain available, while
# mutation verbs are restricted to the exact menus this program intentionally
# manages.  This prevents an accidental future code path from changing IP
# addressing, DHCP, firewall, routes, interfaces, system scripts, files, etc.
_SAFE_MUTATION_PATHS = frozenset({
    "/ip/hotspot/user/add", "/ip/hotspot/user/set", "/ip/hotspot/user/remove",
    "/ip/hotspot/active/remove",
    "/ip/hotspot/set",
    "/ip/hotspot/user/profile/add", "/ip/hotspot/user/profile/set", "/ip/hotspot/user/profile/remove",
    "/ip/hotspot/ip-binding/add", "/ip/hotspot/ip-binding/remove",
    "/ppp/secret/add", "/ppp/secret/set", "/ppp/secret/remove",
    "/ppp/active/remove",
    "/ppp/profile/add", "/ppp/profile/set", "/ppp/profile/remove",
    "/user-manager/user/add", "/user-manager/user/set", "/user-manager/user/remove",
    "/user-manager/user-profile/add",
    "/user-manager/profile/add", "/user-manager/profile/set", "/user-manager/profile/remove",
    "/user-manager/limitation/add", "/user-manager/limitation/set", "/user-manager/limitation/remove",
    "/user-manager/profile-limitation/add", "/user-manager/profile-limitation/remove",
    "/tool/user-manager/user/add", "/tool/user-manager/user/set", "/tool/user-manager/user/remove",
    "/tool/user-manager/user/create-and-activate-profile",
    "/tool/user-manager/profile/add", "/tool/user-manager/profile/set", "/tool/user-manager/profile/remove",
    "/tool/user-manager/profile/limitation/add", "/tool/user-manager/profile/limitation/set",
    "/tool/user-manager/profile/limitation/remove",
    "/tool/user-manager/profile/profile-limitation/add",
    "/tool/user-manager/profile/profile-limitation/remove",
})

def _guard_routeros_command(path, attrs=None, queries=None):
    command = str(path or "").strip()
    if not re.fullmatch(r"(?:/[a-z0-9-]+)+", command):
        raise MikroTikError("مسار أمر RouterOS غير صالح.")
    action = command.rsplit("/", 1)[-1]
    attrs = attrs or {}
    # Fail closed for every non-read command, including unlisted top-level
    # menus such as /execute and /disk. A denylist of known verbs misses these.
    # Privileged maintenance commands are an explicit two-item allowlist.
    # No arguments, queries, scripts, file operations, resets or migrations.
    if command in ('/system/reboot','/user-manager/database/optimize-db'):
        if attrs or queries:
            raise MikroTikError('أمر صيانة غير مصرح به: معاملات إضافية ممنوعة.')
        return command
    if command != "/login" and action != "print" and command not in _SAFE_MUTATION_PATHS:
        raise MikroTikError("تم منع أمر RouterOS غير مصرح به لحماية إعدادات الشبكة: %s" % command)
    if action == "print" and any(key not in (".proplist", "count-only") for key in attrs):
        # print file=... writes to storage; follow/interval creates a live job.
        raise MikroTikError("تم منع خيار قراءة قد يكتب ملفات أو ينشئ متابعة مستمرة على الراوتر.")
    if command == "/ip/hotspot/set":
        if set(attrs) != {".id", "disabled"} or attrs.get("disabled") not in ("yes", "no"):
            raise MikroTikError("تعديل السيرفر محصور في حالة تفعيل/تعطيل سجل واحد.")
    if action in ("set", "remove"):
        if not re.fullmatch(r"\*[0-9A-Fa-f]+", str(attrs.get(".id", ""))):
            raise MikroTikError("العملية تحتاج معرف سجل واحد صالح؛ حدّث القائمة أولًا.")
    if command == "/tool/user-manager/user/create-and-activate-profile":
        if not re.fullmatch(r"\*[0-9A-Fa-f]+", str(attrs.get("numbers", ""))):
            raise MikroTikError("ربط الباقة يحتاج معرف حساب واحد؛ مُنعت الأسماء والأرقام والقوائم لحماية بقية المستخدمين.")
    if any(key in attrs for key in ("on-login", "on-logout", "on-up", "on-down", "source")):
        raise MikroTikError("لا يُسمح بكتابة سكربتات على الراوتر من هذا البرنامج.")
    for query in queries or ():
        if action != "print" or not str(query).startswith("?"):
            raise MikroTikError("مرشح قراءة RouterOS غير صالح.")
    return command


class MikroTikError(Exception):
    pass


class RouterConnectionError(MikroTikError):
    """Recoverable TCP/transport failure, distinct from login or TLS rejection."""


class RouterOSTrapError(MikroTikError):
    """A fully consumed !trap/!done reply: the operation was rejected."""


class DeliveryUncertainError(MikroTikError):
    """A user exists or may exist, but complete delivery is not confirmed."""


class _MalformedProtocolError(MikroTikError):
    """Internal marker for a framing violation that makes a stream unsafe."""


def _encode_length(length: int) -> bytes:
    length = int(length)
    if length < 0:
        raise MikroTikError("invalid negative API word length")
    if length < 0x80:
        return bytes([length])
    if length < 0x4000:
        length |= 0x8000
        return bytes([(length >> 8) & 0xFF, length & 0xFF])
    if length < 0x200000:
        length |= 0xC00000
        return bytes([(length >> 16) & 0xFF, (length >> 8) & 0xFF, length & 0xFF])
    if length < 0x10000000:
        length |= 0xE0000000
        return bytes(
            [
                (length >> 24) & 0xFF,
                (length >> 16) & 0xFF,
                (length >> 8) & 0xFF,
                length & 0xFF,
            ]
        )
    return b"\xF0" + struct.pack(">I", length)


def _recv_exact(sock, length, deadline=None):
    """Read exactly *length* bytes, tolerating arbitrary TCP fragmentation.

    ``deadline`` is monotonic and applies to the whole operation rather than
    being reset for every recv.  The optional reader object below lets a
    sentence parser retain bytes read ahead from the socket.
    """
    if hasattr(sock, "read_exact"):
        return sock.read_exact(length, deadline)
    data = bytearray()
    while len(data) < length:
        _set_socket_deadline(sock, deadline)
        chunk = sock.recv(length - len(data))
        if not chunk:
            raise EOFError("connection closed")
        data.extend(chunk)
    return bytes(data)


def _set_socket_deadline(sock, deadline, idle_timeout=None):
    if deadline is None or not hasattr(sock, "settimeout"):
        return
    remaining = deadline - time.monotonic()
    if remaining <= 0:
        raise socket.timeout("RouterOS command deadline exceeded")
    sock.settimeout(min(remaining, idle_timeout) if idle_timeout is not None else remaining)


class _BufferedSocketReader:
    """A small read-ahead buffer that reduces recv syscalls on API replies."""

    def __init__(self, sock, idle_timeout=None):
        self.sock = sock
        self.idle_timeout = idle_timeout
        self.buffer = bytearray()

    def read_exact(self, length, deadline=None):
        length = int(length)
        if length < 0:
            raise _MalformedProtocolError("invalid negative API read length")
        while len(self.buffer) < length:
            _set_socket_deadline(self.sock, deadline, self.idle_timeout)
            # Read ahead, but keep the chunk bounded so a slow/fake socket and
            # a malformed peer cannot cause an unbounded allocation.
            needed = max(1, length - len(self.buffer))
            chunk = self.sock.recv(max(4096, min(64 * 1024, needed)))
            if not chunk:
                raise EOFError("connection closed")
            self.buffer.extend(chunk)
        result = bytes(self.buffer[:length])
        del self.buffer[:length]
        return result


def _read_len(sock, deadline=None):
    first = _recv_exact(sock, 1, deadline)[0]
    if first & 0x80 == 0:
        result = first
    elif first & 0xC0 == 0x80:
        result = ((first & 0x3F) << 8) + _recv_exact(sock, 1, deadline)[0]
    elif first & 0xE0 == 0xC0:
        b1, b2 = _recv_exact(sock, 2, deadline)
        result = ((first & 0x1F) << 16) + (b1 << 8) + b2
    elif first & 0xF0 == 0xE0:
        b1, b2, b3 = _recv_exact(sock, 3, deadline)
        result = ((first & 0x0F) << 24) + (b1 << 16) + (b2 << 8) + b3
    elif first == 0xF0:
        result = struct.unpack(">I", _recv_exact(sock, 4, deadline))[0]
    else:
        raise _MalformedProtocolError("invalid API word length")
    if result > MAX_API_WORD:
        raise _MalformedProtocolError("API response word is too large")
    return result


@dataclass
class RouterConfig:
    host: str
    port: int = 8728
    username: str = "admin"
    password: str = field(default="", repr=False)
    use_ssl: bool = False
    verify_ssl: bool = False
    timeout: float = 8.0
    # A flowing large inventory may take longer than an idle TCP response.
    command_timeout: float = 45.0
    # Appended to preserve existing positional RouterConfig arguments.
    remote_mode: bool = False


class RouterOSAPI:
    """Small dependency-free RouterOS sentence API client for ROS 6/7."""

    def __init__(self, cfg: RouterConfig):
        self.cfg = cfg
        self.sock = None
        self.last_done = {}
        self._reader = None

    def connect(self):
        # Both the login page and saved-router connection test use this same
        # API client. Retry only an initial remote transport failure once.
        # A rejected login, invalid TLS certificate or local endpoint is never
        # retried, and no router mutation is replayed.
        if not self.cfg.remote_mode:
            return self._connect_once()
        try:
            return self._connect_once()
        except RouterConnectionError:
            return self._connect_once()

    def _connect_once(self):
        self.close()
        try:
            sock = socket.create_connection(
                (self.cfg.host, int(self.cfg.port)), timeout=float(self.cfg.timeout)
            )
            self.sock = sock
            sock.settimeout(float(self.cfg.timeout))
            if self.cfg.use_ssl:
                if self.cfg.verify_ssl:
                    context = ssl.create_default_context()
                else:
                    context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
                    context.check_hostname = False
                    context.verify_mode = ssl.CERT_NONE
                sock = context.wrap_socket(
                    sock, server_hostname=self.cfg.host if self.cfg.verify_ssl else None
                )
            self.sock = sock
            self._login()
            return self
        except MikroTikError:
            self.close()
            raise
        except socket.timeout:
            self.close()
            raise RouterConnectionError("انتهت مهلة الاتصال بالراوتر. تحقق من العنوان والمنفذ وخدمة API.")
        except ssl.SSLError as exc:
            self.close()
            raise MikroTikError("فشل اتصال API-SSL: %s" % exc)
        except socket.gaierror:
            self.close()
            raise MikroTikError('تعذر حل اسم الراوتر؛ تحقق من عنوان VPN أو DNS.') from None
        except OSError as exc:
            self.close()
            raise RouterConnectionError("تعذر الاتصال بالراوتر: %s" % exc)

    def _login(self):
        # RouterOS 6.43+ and 7 use the direct login. Older releases return a
        # challenge (ret), which we also handle for broader compatibility.
        _, done = self.command(
            "/login",
            {"name": self.cfg.username, "password": self.cfg.password},
            return_done=True,
        )
        challenge = done.get("ret", "")
        if challenge:
            try:
                digest = hashlib.md5(
                    b"\x00" + self.cfg.password.encode("utf-8") + bytes.fromhex(challenge)
                ).hexdigest()
            except (ValueError, TypeError) as exc:
                raise MikroTikError("invalid RouterOS login challenge: %s" % exc)
            self.command(
                "/login", {"name": self.cfg.username, "response": "00" + digest}
            )

    def close(self):
        if self.sock:
            try:
                self.sock.shutdown(socket.SHUT_RDWR)
            except OSError:
                pass
            try:
                self.sock.close()
            except OSError:
                pass
            self.sock = None
            self._reader = None

    def __enter__(self):
        return self.connect()

    def __exit__(self, *args):
        self.close()

    def _write_sentence(self, words):
        if self.sock is None:
            raise MikroTikError("not connected")
        if len(words) > MAX_API_WORDS:
            raise MikroTikError("API request contains too many words")
        packet = bytearray()
        for word in words:
            encoded = word.encode("utf-8") if isinstance(word, str) else bytes(word)
            if len(encoded) > MAX_API_WORD:
                raise MikroTikError("API request word is too large")
            packet.extend(_encode_length(len(encoded)))
            packet.extend(encoded)
            if len(packet) > MAX_API_SENTENCE:
                raise MikroTikError("API request sentence is too large")
        packet.append(0)
        self.sock.sendall(bytes(packet))

    def _read_sentence(self, reader=None, deadline=None):
        reader = reader or self.sock
        result = []
        total = 0
        while True:
            length = _read_len(reader, deadline)
            if length == 0:
                return result
            total += length
            if total > MAX_API_SENTENCE or len(result) >= MAX_API_WORDS:
                raise _MalformedProtocolError("API response sentence is too large")
            result.append(_recv_exact(reader, length, deadline).decode("utf-8", "replace"))

    @staticmethod
    def _parse_sentence(sentence):
        kind = sentence[0] if sentence else ""
        data = {}
        for word in sentence[1:]:
            if word.startswith("="):
                pair = word[1:].split("=", 1)
                data[pair[0]] = pair[1] if len(pair) > 1 else ""
            elif "=" in word:
                pair = word.split("=", 1)
                data[pair[0].lstrip("=")] = pair[1]
        return kind, data

    def command(self, path, attrs=None, queries=None, return_done=False, row_callback=None):
        if not self.sock:
            raise MikroTikError("غير متصل بالراوتر.")
        path = _guard_routeros_command(path, attrs, queries)
        words = [path]
        for key, value in (attrs or {}).items():
            if value is not None:
                words.append("=%s=%s" % (key, value))
        words.extend(str(query) for query in (queries or []))
        deadline = time.monotonic() + max(0.001, float(self.cfg.command_timeout))
        if self._reader is None or self._reader.sock is not self.sock:
            self._reader = _BufferedSocketReader(self.sock, max(0.001, float(self.cfg.timeout)))
        reader = self._reader
        try:
            _set_socket_deadline(self.sock, deadline, max(0.001, float(self.cfg.timeout)))
            self._write_sentence(words)
            rows = []
            row_count = 0
            response_bytes = 0
            trap_error = None
            while True:
                if time.monotonic() >= deadline:
                    raise socket.timeout("RouterOS command deadline exceeded")
                sentence = self._read_sentence(reader, deadline)
                response_bytes += sum(len(word.encode('utf-8')) for word in sentence)
                if response_bytes > MAX_API_RESPONSE:
                    raise _MalformedProtocolError("API response exceeded the memory safety limit")
                if not sentence:
                    continue
                kind, data = self._parse_sentence(sentence)
                if kind == "!re":
                    if row_count >= MAX_API_ROWS:
                        raise _MalformedProtocolError("API response contains too many rows")
                    row_count += 1
                    if row_callback is None:
                        rows.append(data)
                    else:
                        # Stream a read-only report without retaining tens of
                        # thousands of full RouterOS session dictionaries.
                        row_callback(data)
                elif kind == "!trap":
                    # RouterOS normally follows !trap with !done. Consume it so
                    # the next command cannot accidentally read the old reply.
                    trap_error = data.get("message") or "RouterOS API trap"
                elif kind == "!fatal":
                    self.close()
                    raise MikroTikError(data.get("message") or "RouterOS API fatal")
                elif kind == "!empty":
                    # ROS 7.18 adds an empty-data marker, NOT a terminator.
                    # The official API contract still ends each command with
                    # !done. Leaving it unread corrupts the following command.
                    continue
                elif kind == "!done":
                    self.last_done = data
                    if trap_error:
                        raise RouterOSTrapError(trap_error)
                    return (rows, data) if return_done else rows
                else:
                    self.close()
                    raise MikroTikError("استجابة بروتوكول RouterOS غير معروفة: %s" % kind)
        except socket.timeout:
            self.close()
            raise RouterConnectionError("انتهت مهلة استجابة الراوتر.")
        except EOFError:
            self.close()
            raise RouterConnectionError("أغلق الراوتر الاتصال قبل اكتمال العملية.")
        except _MalformedProtocolError:
            self.close()
            raise
        except UnicodeError:
            self.close()
            raise
        except OSError as exc:
            self.close()
            raise RouterConnectionError("انقطع الاتصال بالراوتر: %s" % exc)
        except RouterOSTrapError:
            # !done was consumed, so this stream is still synchronized.
            raise
        except Exception:
            # A streaming consumer may fail before !done. Never reuse that
            # partial reply as the result of the next command.
            self.close()
            raise

    def identity(self):
        rows = self.command("/system/identity/print")
        return rows[0].get("name", "") if rows else ""

    def resource(self):
        rows = self.command("/system/resource/print")
        return rows[0] if rows else {}

    def hotspot_servers(self):
        rows = self.command("/ip/hotspot/print")
        return [row.get("name", "") for row in rows if row.get("name")]

    def add_hotspot_user(
        self,
        username,
        password="",
        profile="default",
        server="all",
        limit_uptime="",
        limit_bytes_total=0,
        comment="",
    ):
        attrs = {"name": username, "profile": profile}
        if password:
            attrs["password"] = password
        if server and server != "all":
            attrs["server"] = server
        if limit_uptime:
            attrs["limit-uptime"] = limit_uptime
        if limit_bytes_total:
            attrs["limit-bytes-total"] = str(int(limit_bytes_total))
        if comment:
            attrs["comment"] = comment
        return self.command("/ip/hotspot/user/add", attrs)








def connect_from_row(row):
    keys = set(row.keys()) if hasattr(row, "keys") else set(row)
    return RouterOSAPI(
        RouterConfig(
            host=row["host"],
            port=int(row["port"]),
            username=row["username"],
            password=row["password"],
            use_ssl=bool(row["use_ssl"]),
            verify_ssl=bool(row["verify_ssl"]) if "verify_ssl" in keys else False,
            timeout=15.0 if 'connection_mode' in keys and row['connection_mode']=='remote' else 8.0,
            remote_mode=('connection_mode' in keys and row['connection_mode']=='remote'),
        )
    )
