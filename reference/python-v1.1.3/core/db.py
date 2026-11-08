from __future__ import annotations

import base64
import ctypes
import json
import os
import sqlite3
import sys
import tempfile
from contextlib import contextmanager
from datetime import datetime, timedelta
from pathlib import Path
from .connection_address import normalize_address


SOURCE_DIR = Path(__file__).resolve().parents[1]
PORTABLE_DIR = Path(sys.executable).resolve().parent if getattr(sys, "frozen", False) else SOURCE_DIR
BUNDLE_DIR = Path(getattr(sys, "_MEIPASS", SOURCE_DIR))
APP_FOLDER_NAME = "NidiMikroTikCards"
SCHEMA_VERSION = 8


_DPAPI_PREFIX = "dpapi:v1:"


def _protect_secret(value):
    """Protect saved router credentials with Windows DPAPI when available."""
    value = str(value or "")
    if not value or os.name != "nt":
        return value
    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", ctypes.c_ulong), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]
    raw = value.encode("utf-8")
    buffer = ctypes.create_string_buffer(raw)
    source = DATA_BLOB(len(raw), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    target = DATA_BLOB()
    if not ctypes.windll.crypt32.CryptProtectData(ctypes.byref(source), ctypes.c_wchar_p("Nidi Router Credential"), None, None, None, 0x1, ctypes.byref(target)):
        raise OSError("تعذر حماية كلمة المرور بواسطة Windows.")
    try:
        encrypted = ctypes.string_at(target.pbData, target.cbData)
        return _DPAPI_PREFIX + base64.b64encode(encrypted).decode("ascii")
    finally:
        ctypes.windll.kernel32.LocalFree(target.pbData)


def _unprotect_secret(value):
    value = str(value or "")
    if not value.startswith(_DPAPI_PREFIX):
        return value
    if os.name != "nt":
        return ""
    class DATA_BLOB(ctypes.Structure):
        _fields_ = [("cbData", ctypes.c_ulong), ("pbData", ctypes.POINTER(ctypes.c_ubyte))]
    try:
        raw = base64.b64decode(value[len(_DPAPI_PREFIX):], validate=True)
        buffer = ctypes.create_string_buffer(raw)
        source = DATA_BLOB(len(raw), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
        target = DATA_BLOB()
        if not ctypes.windll.crypt32.CryptUnprotectData(ctypes.byref(source), None, None, None, None, 0x1, ctypes.byref(target)):
            return ""
        try:
            return ctypes.string_at(target.pbData, target.cbData).decode("utf-8")
        finally:
            ctypes.windll.kernel32.LocalFree(target.pbData)
    except (ValueError, OSError, UnicodeDecodeError):
        return ""


def _is_writable_directory(path: Path) -> bool:
    try:
        path.mkdir(parents=True, exist_ok=True)
        probe = path / (".nidi-write-%s.tmp" % os.getpid())
        probe.write_bytes(b"ok")
        probe.unlink()
        return True
    except (OSError, PermissionError):
        return False


def _data_directory() -> Path:
    explicit = os.environ.get("NIDI_DATA_DIR")
    if explicit:
        return Path(explicit).expanduser().resolve()

    portable_data = PORTABLE_DIR / "data"
    marker = PORTABLE_DIR / "portable.mode"
    # Source runs and official portable packages keep their data locally.
    if not getattr(sys, "frozen", False) or marker.exists():
        if _is_writable_directory(portable_data):
            return portable_data

    # A copy placed under Program Files must still start without admin rights.
    local_app_data = os.environ.get("LOCALAPPDATA") or os.environ.get("APPDATA")
    if local_app_data:
        fallback = Path(local_app_data) / APP_FOLDER_NAME / "data"
    else:
        fallback = Path.home() / (".%s" % APP_FOLDER_NAME) / "data"
    fallback.mkdir(parents=True, exist_ok=True)
    return fallback


DATA_DIR = _data_directory()
DB_PATH = DATA_DIR / "nidi_cards.db"
WRITABLE_ROOT = DATA_DIR.parent


SCHEMA = """
CREATE TABLE IF NOT EXISTS meta (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS settings (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS routers (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL,
    host TEXT NOT NULL,
    port INTEGER NOT NULL DEFAULT 8728,
    username TEXT NOT NULL DEFAULT 'admin',
    password TEXT NOT NULL DEFAULT '',
    use_ssl INTEGER NOT NULL DEFAULT 0,
    verify_ssl INTEGER NOT NULL DEFAULT 0,
    connection_mode TEXT NOT NULL DEFAULT 'local',
    ftp_port INTEGER NOT NULL DEFAULT 21,
    is_default INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS packages (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE,
    settings_json TEXT NOT NULL,
    updated_at TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS batches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    batch_no TEXT NOT NULL UNIQUE,
    router_id INTEGER,
    server TEXT,
    profile TEXT,
    quantity INTEGER NOT NULL,
    price REAL DEFAULT 0,
    created_at TEXT NOT NULL,
    notes TEXT,
    FOREIGN KEY(router_id) REFERENCES routers(id) ON DELETE SET NULL
);
CREATE TABLE IF NOT EXISTS cards (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    batch_id INTEGER,
    sequence TEXT NOT NULL,
    username TEXT NOT NULL UNIQUE,
    password TEXT,
    price REAL DEFAULT 0,
    profile TEXT,
    server TEXT,
    validity_days TEXT,
    limit_uptime TEXT,
    limit_bytes INTEGER DEFAULT 0,
    created_at TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'CREATED',
    printed INTEGER NOT NULL DEFAULT 0,
    print_count INTEGER NOT NULL DEFAULT 0,
    comment TEXT,
    sent_at TEXT,
    last_error TEXT,
    FOREIGN KEY(batch_id) REFERENCES batches(id) ON DELETE CASCADE
);
CREATE TABLE IF NOT EXISTS activity (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    ts TEXT NOT NULL,
    category TEXT NOT NULL,
    action TEXT NOT NULL,
    status TEXT NOT NULL,
    details TEXT
);
CREATE INDEX IF NOT EXISTS idx_cards_batch ON cards(batch_id);
CREATE INDEX IF NOT EXISTS idx_cards_status ON cards(status);
CREATE INDEX IF NOT EXISTS idx_cards_created ON cards(created_at);
CREATE INDEX IF NOT EXISTS idx_activity_ts ON activity(ts);
"""


DEFAULT_SETTINGS = {
    "theme": "nidi_dark_teal",
    "language": "ar",
    "card.width_mm": 50.0,
    "card.height_mm": 19.0,
    "card.dpi": 300,
    "card.background_path": str(BUNDLE_DIR / "assets" / "default_card.png"),
    "card.background_mode": "stretch",
    "card.font_family": "Arial",
    "card.font_size": 14,
    "card.font_bold": True,
    "card.font_color": "#E7FFF7",
    "card.color.username": "#000000",
    "card.color.password": "#000000",
    "card.border_color": "#00C98D",
    "card.border_width": 1,
    "card.border_radius": 0,
    "card.qr_size_mm": 24,
    "card.qr_mode": "credentials",
    "card.qr_login_url": "http://hotspot.local/login",
    "card.add_title": False,
    "card.add_sequence": False,
    "card.add_price": False,
    "card.add_capacity": False,
    "card.add_duration": False,
    "card.add_validity": False,
    "card.add_date": False,
    "card.add_phone": False,
    "card.add_qr": False,
    "card.add_other": False,
    "card.add_other2": False,
    "card.title_text": "شبكة حضرموت",
    "card.phone_text": "رقم الهاتف",
    "card.other_text": "أخرى 1",
    "card.other2_text": "أخرى 2",
    "card.positions": {
        "title": [141, 9],
        "sequence": [4, 118],
        "username": [76, 13],
        "password": [122, 58],
        "price": [8, 8],
        "capacity": [142, 71],
        "duration": [223, 71],
        "validity": [4, 42],
        "date": [4, 111],
        "phone": [182, 96],
        "qr": [7, 37],
        "other": [224, 89],
        "other2": [225, 67],
    },
    "create.server": "all",
    "create.userman_customer": None,
    "create.quantity": 60,
    "create.profile": "default",
    "create.card_format": "code_only",
    "create.username_chars": "0123456789abcdefghijklmnopqrstuvwxyz",
    "create.username_prefix": "",
    "create.password_chars": "abcdef",
    "create.code_length": 10,
    "create.password_length": 6,
    "create.start_sequence": 1,
    "create.limit_uptime_h": 1,
    "create.limit_uptime_m": 0,
    "create.limit_bytes": 512,
    "create.limit_unit": "MB",
    "create.validity_days": 7,
    "create.price": 10.5,
    "create.reset_sequence_each_batch": True,
    "create.avoid_duplicate": True,
    "create.export_excel": False,
    "create.export_csv": False,
    "create.export_text": False,
    "create.export_script": False,
    "create.sequence_mode": "0000",
    "print.paper": "A4",
    "print.orientation": "portrait",
    "print.rows": 15,
    "print.columns": 4,
    "print.gap_h_mm": 0.5,
    "print.gap_v_mm": 0.5,
    "print.page_margin_mm": 0.5,
    "print.auto_pages": True,
    "print.order": "left_to_right",
    "print.duplex": "single",
    "print.show_borders": True,
    "print.cut_marks": False,
    "print.default_printer": "",
    "backup.keep_count": 20,
    "paths.output": str(WRITABLE_ROOT / "exports"),
    "paths.backup": str(WRITABLE_ROOT / "backups"),
    "paths.hotspot_templates": str(WRITABLE_ROOT / "hotspot_templates"),
}


class Database:
    def __init__(self, path: Path = DB_PATH):
        self.path = Path(path)
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.conn = sqlite3.connect(str(self.path), timeout=10)
        self.conn.row_factory = sqlite3.Row
        self._closed = False
        try:
            self._check_schema_version()
            self._configure()
            self.conn.executescript(SCHEMA)
            self._migrate()
            self._seed_settings()
            self._protect_saved_router_passwords()
        except Exception:
            self.conn.close()
            self._closed = True
            raise

    def _check_schema_version(self):
        if not self.conn.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name='meta'").fetchone():
            return
        row = self.conn.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()
        if row:
            try:
                version = int(row[0])
            except (TypeError, ValueError):
                raise sqlite3.DatabaseError('رقم إصدار قاعدة البيانات غير صالح؛ لم تُعدل البيانات.') from None
            if version > SCHEMA_VERSION:
                raise sqlite3.DatabaseError('قاعدة البيانات تحتاج إصدارًا أحدث من البرنامج؛ لم تُعدل البيانات.')

    def _configure(self):
        self.conn.execute("PRAGMA foreign_keys=ON")
        self.conn.execute("PRAGMA busy_timeout=5000")
        self.conn.execute("PRAGMA journal_mode=WAL")
        self.conn.execute("PRAGMA synchronous=NORMAL")

    def _columns(self, table):
        return {row[1] for row in self.conn.execute("PRAGMA table_info(%s)" % table)}

    def _has_router_column(self):
        """True once the cards table carries the per-router unique index."""
        if not hasattr(self, '_router_scoped'):
            self._router_scoped = "router_id" in self._columns("cards")
        return self._router_scoped

    def refresh_router_uniqueness(self):
        """Rebuild username uniqueness after a router is added or removed.

        Called by the UI right after a router change so a second device becomes
        usable immediately instead of only after the next program start.
        """
        self._migrate_username_uniqueness()

    def _ensure_column(self, table, name, declaration):
        if name not in self._columns(table):
            self.conn.execute("ALTER TABLE %s ADD COLUMN %s %s" % (table, name, declaration))

    _CARDS_INSERT_COLUMNS = (
        "id", "batch_id", "sequence", "username", "password", "price", "profile",
        "server", "validity_days", "limit_uptime", "limit_bytes", "created_at",
        "status", "printed", "print_count", "comment", "sent_at", "last_error",
        "service", "router_id",
    )

    def _migrate_username_uniqueness(self):
        """Replace the legacy global username UNIQUE with a per-router one.

        Voucher names are only meaningful per router: two different MikroTik
        devices routinely number their cards 1..N independently, and the legacy
        ``UNIQUE`` on ``cards.username`` made the second device impossible to
        use.  The table is rebuilt only when the installation actually holds
        cards for more than one router, so single-router databases are left
        byte-for-byte untouched.
        """
        index = self.conn.execute(
            "SELECT 1 FROM sqlite_master WHERE type='index' AND name='idx_cards_username_router'"
        ).fetchone()
        if index:
            self._router_scoped = True
            return
        configured = int(self.conn.execute("SELECT COUNT(*) FROM routers").fetchone()[0] or 0)
        # A second *configured* router is enough: the operator may still have to
        # create the first batch on it, and the legacy global UNIQUE would refuse
        # a name that is perfectly valid on that separate device.  A single-router
        # installation keeps the original table exactly as it was.
        if configured < 2:
            return
        columns = ", ".join(self._CARDS_INSERT_COLUMNS)
        with self.conn:
            # sqlite3 starts implicit transactions for DML, not CREATE TABLE.
            # Include the staging table in rollback if copying the cards fails.
            if not self.conn.in_transaction:
                self.conn.execute("BEGIN IMMEDIATE")
            leftover = self.conn.execute(
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='cards_migrated'").fetchone()
            if leftover:
                if self.conn.execute('SELECT 1 FROM cards_migrated LIMIT 1').fetchone():
                    raise sqlite3.DatabaseError('توجد بيانات في جدول ترقية سابق؛ احتفظ بنسخة من القاعدة وراجعها قبل المتابعة. لم تُحذف البيانات.')
                self.conn.execute('DROP TABLE cards_migrated')
            self.conn.execute(
                "CREATE TABLE cards_migrated ("
                "id INTEGER PRIMARY KEY AUTOINCREMENT,"
                "batch_id INTEGER, sequence TEXT NOT NULL, username TEXT NOT NULL,"
                "password TEXT, price REAL DEFAULT 0, profile TEXT, server TEXT,"
                "validity_days TEXT, limit_uptime TEXT, limit_bytes INTEGER DEFAULT 0,"
                "created_at TEXT NOT NULL, status TEXT NOT NULL DEFAULT 'CREATED',"
                "printed INTEGER NOT NULL DEFAULT 0, print_count INTEGER NOT NULL DEFAULT 0,"
                "comment TEXT, sent_at TEXT, last_error TEXT,"
                "service TEXT NOT NULL DEFAULT 'hotspot', router_id INTEGER,"
                "FOREIGN KEY(batch_id) REFERENCES batches(id) ON DELETE CASCADE)"
            )
            # The router id is denormalized from the owning batch so the unique
            # index can stay a plain (username, router_id) pair.
            source = ", ".join(
                "(SELECT b.router_id FROM batches b WHERE b.id=cards.batch_id) AS router_id"
                if name == "router_id" else name
                for name in self._CARDS_INSERT_COLUMNS)
            self.conn.execute(
                "INSERT INTO cards_migrated (%s) SELECT %s FROM cards" % (columns, source))
            self.conn.execute("DROP TABLE cards")
            self.conn.execute("ALTER TABLE cards_migrated RENAME TO cards")
            self.conn.execute(
                "CREATE UNIQUE INDEX idx_cards_username_router "
                "ON cards(username, COALESCE(router_id,0))")
            self.conn.execute(
                "CREATE INDEX IF NOT EXISTS idx_cards_batch ON cards(batch_id)")
            self.conn.execute(
                "CREATE INDEX IF NOT EXISTS idx_cards_status ON cards(status)")
            self.conn.execute(
                "CREATE INDEX IF NOT EXISTS idx_cards_created ON cards(created_at)")
        self._router_scoped = True

    def _migrate(self):
        # Non-destructive migrations for databases created by v1.21.
        self._ensure_column("routers", "verify_ssl", "INTEGER NOT NULL DEFAULT 0")
        self._ensure_column("routers", "connection_mode", "TEXT NOT NULL DEFAULT 'local'")
        self._ensure_column("cards", "sent_at", "TEXT")
        self._ensure_column("cards", "last_error", "TEXT")
        self._ensure_column("batches", "package_id", "INTEGER REFERENCES packages(id) ON DELETE SET NULL")
        self._ensure_column("batches", "package_name", "TEXT")
        self._ensure_column("batches", "print_settings_json", "TEXT")
        self._ensure_column("batches", "service", "TEXT NOT NULL DEFAULT 'hotspot'")
        self._ensure_column("cards", "service", "TEXT NOT NULL DEFAULT 'hotspot'")
        self.conn.execute('''CREATE TABLE IF NOT EXISTS router_presets (
            router_id INTEGER NOT NULL REFERENCES routers(id) ON DELETE CASCADE,
            service TEXT NOT NULL, remote_id TEXT NOT NULL, profile_name TEXT NOT NULL,
            package_id INTEGER NOT NULL REFERENCES packages(id) ON DELETE CASCADE,
            PRIMARY KEY(router_id,service,remote_id))''')
        self.conn.execute("CREATE INDEX IF NOT EXISTS idx_batches_package ON batches(package_id)")
        self.conn.execute("CREATE INDEX IF NOT EXISTS idx_batches_router_service_profile ON batches(router_id,service,profile,id)")
        self._migrate_username_uniqueness()
        previous=self.conn.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()
        try:previous_version=int(previous[0]) if previous else 0
        except (TypeError,ValueError):previous_version=0
        if previous_version < 5:
            width=self.conn.execute("SELECT value FROM settings WHERE key='card.width_mm'").fetchone()
            height=self.conn.execute("SELECT value FROM settings WHERE key='card.height_mm'").fetchone()
            if width and height:
                try:
                    if abs(float(json.loads(width[0]))-85.6)<0.001 and abs(float(json.loads(height[0]))-53.98)<0.001:
                        self.conn.execute("UPDATE settings SET value=? WHERE key='card.width_mm'",(json.dumps(60.0),))
                        self.conn.execute("UPDATE settings SET value=? WHERE key='card.height_mm'",(json.dumps(18.0),))
                except (TypeError,ValueError,json.JSONDecodeError):pass
        if previous_version < 6:
            # Adopt the new creation defaults only for an unused database.
            # Existing installations keep their saved packages, batches and user choices.
            has_user_data = any(self.conn.execute("SELECT 1 FROM %s LIMIT 1" % table).fetchone()
                                for table in ("packages", "batches", "cards"))
            if not has_user_data:
                for key in (
                    "card.width_mm", "card.height_mm", "card.positions",
                    "card.add_title", "card.add_sequence", "card.add_price",
                    "card.add_capacity", "card.add_duration", "card.add_phone", "card.add_qr",
                    "create.quantity", "create.code_length",
                    "print.rows", "print.columns", "print.gap_h_mm",
                    "print.gap_v_mm", "print.page_margin_mm",
                ):
                    if key in DEFAULT_SETTINGS:
                        self.conn.execute(
                            "INSERT INTO settings(key,value) VALUES(?,?) "
                            "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
                            (key, json.dumps(DEFAULT_SETTINGS[key], ensure_ascii=False)),
                        )
        self.conn.execute(
            "INSERT INTO meta(key,value) VALUES('schema_version',?) "
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value",
            (str(SCHEMA_VERSION),),
        )
        self.conn.executescript(
            """
            CREATE INDEX IF NOT EXISTS idx_cards_batch ON cards(batch_id);
            CREATE INDEX IF NOT EXISTS idx_cards_status ON cards(status);
            CREATE INDEX IF NOT EXISTS idx_cards_created ON cards(created_at);
            CREATE INDEX IF NOT EXISTS idx_activity_ts ON activity(ts);
            """
        )
        self.conn.commit()

    def _seed_settings(self):
        with self.conn:
            for key, value in DEFAULT_SETTINGS.items():
                self.conn.execute(
                    "INSERT OR IGNORE INTO settings(key,value) VALUES(?,?)",
                    (key, json.dumps(value, ensure_ascii=False)),
                )

    def _protect_saved_router_passwords(self):
        if os.name != "nt":
            return
        rows = self.conn.execute("SELECT id,password FROM routers WHERE password<>''").fetchall()
        updates=[]
        for row in rows:
            value=str(row["password"] or "")
            if value and not value.startswith(_DPAPI_PREFIX):
                updates.append((_protect_secret(value), int(row["id"])))
        if updates:
            with self.conn:
                self.conn.executemany("UPDATE routers SET password=? WHERE id=?", updates)

    @contextmanager
    def transaction(self):
        try:
            self.conn.execute("BEGIN IMMEDIATE")
            yield self.conn
            self.conn.commit()
        except Exception:
            self.conn.rollback()
            raise

    def get_setting(self, key, default=None):
        row = self.conn.execute("SELECT value FROM settings WHERE key=?", (key,)).fetchone()
        if not row:
            return default
        try:
            return json.loads(row["value"])
        except (TypeError, ValueError, json.JSONDecodeError):
            return row["value"]

    def set_setting(self, key, value):
        self.set_settings({key: value})

    def set_settings(self, values):
        rows = [
            (str(key), json.dumps(value, ensure_ascii=False, default=str))
            for key, value in values.items()
        ]
        with self.conn:
            self.conn.executemany(
                "INSERT INTO settings(key,value) VALUES(?,?) "
                "ON CONFLICT(key) DO UPDATE SET value=excluded.value "
                "WHERE settings.value IS NOT excluded.value",
                rows,
            )

    def all_settings(self):
        rows = self.conn.execute("SELECT key,value FROM settings").fetchall()
        out = {}
        for row in rows:
            try:
                out[row["key"]] = json.loads(row["value"])
            except (TypeError, ValueError, json.JSONDecodeError):
                out[row["key"]] = row["value"]
        return out

    def packages(self):
        return [self._package_row(row) for row in self.conn.execute(
            "SELECT * FROM packages ORDER BY name COLLATE NOCASE,id")]

    def replace_preset_settings(self, settings):
        values = {key:value for key,value in DEFAULT_SETTINGS.items() if key.startswith(('card.','create.','print.'))}
        values.update({key:value for key,value in settings.items() if key.startswith(('card.','create.','print.'))})
        with self.conn:
            self.conn.execute("DELETE FROM settings WHERE key LIKE 'card.%' OR key LIKE 'create.%' OR key LIKE 'print.%'")
            self.conn.executemany('INSERT INTO settings(key,value) VALUES(?,?)',
                                 [(key,json.dumps(value,ensure_ascii=False)) for key,value in values.items()])

    @staticmethod
    def _package_row(row):
        if row is None:
            return None
        result = dict(row)
        result['settings'] = json.loads(result.pop('settings_json'))
        return result

    def package(self, package_id):
        if package_id is None:
            return None
        return self._package_row(self.conn.execute(
            "SELECT * FROM packages WHERE id=?", (int(package_id),)).fetchone())

    def save_package(self, name, settings, package_id=None):
        from .packages import package_snapshot
        name = str(name).strip()
        if not name or len(name) > 80:
            raise ValueError('اسم الباقة مطلوب، وبحد أقصى 80 حرفًا.')
        payload = json.dumps(package_snapshot(settings), ensure_ascii=False, allow_nan=False)
        now = datetime.now().isoformat(timespec='seconds')
        try:
            with self.conn:
                if package_id is None:
                    cur = self.conn.execute('INSERT INTO packages(name,settings_json,updated_at) VALUES(?,?,?)',
                                            (name, payload, now))
                    package_id = cur.lastrowid
                else:
                    cur = self.conn.execute('UPDATE packages SET name=?,settings_json=?,updated_at=? WHERE id=?',
                                            (name, payload, now, int(package_id)))
                    if cur.rowcount != 1:
                        raise ValueError('الباقة لم تعد موجودة.')
        except sqlite3.IntegrityError:
            raise ValueError('توجد باقة بهذا الاسم؛ اختر اسمًا آخر.') from None
        return int(package_id)

    def delete_package(self, package_id):
        # Batch snapshots keep historical templates available after deletion.
        with self.conn:
            self.conn.execute('DELETE FROM packages WHERE id=?', (int(package_id),))

    def assign_batch_package(self, batch_id, package_id):
        package=self.package(package_id)
        if package is None:raise ValueError('الباقة لم تعد موجودة.')
        with self.conn:
            self.conn.execute('UPDATE batches SET package_id=?,package_name=?,print_settings_json=? WHERE id=?',
                              (package_id,package['name'],json.dumps(package['settings'],ensure_ascii=False),int(batch_id)))

    def update_batch_print_settings(self, batch_id, settings):
        """Persist an archive batch's own print-template snapshot.

        Unassigned/legacy batches do not have a package row to carry design
        changes.  Store their validated snapshot on the batch itself so a later
        refresh does not silently revert a user-selected background/template.
        """
        from .packages import package_snapshot
        snapshot=package_snapshot(settings)
        payload=json.dumps(snapshot,ensure_ascii=False,allow_nan=False)
        with self.conn:
            cur=self.conn.execute('UPDATE batches SET print_settings_json=? WHERE id=?',(payload,int(batch_id)))
            if cur.rowcount!=1:raise ValueError('الدفعة لم تعد موجودة.')
        return snapshot

    def saved_router_preset(self,router_id,service,remote_id):
        row=self.conn.execute('SELECT package_id FROM router_presets WHERE router_id=? AND service=? AND remote_id=?',
                              (int(router_id),service,str(remote_id))).fetchone()
        return self.package(row['package_id']) if row else None

    def router_preset(self, router_id, service, profile):
        """Get the print template attached to one actual router profile."""
        from .network import profile_defaults
        import hashlib
        remote_id=str(profile.get('id') or profile['name'])
        saved=self.saved_router_preset(router_id,service,remote_id)
        if saved:return saved
        legacy=self.conn.execute('''SELECT p.id FROM packages p JOIN batches b ON b.package_id=p.id
            WHERE b.router_id=? AND b.service=? AND b.profile=? ORDER BY b.id DESC LIMIT 1''',
            (int(router_id),service,profile['name'])).fetchone()
        if legacy:
            legacy_id=legacy['id']
            if self.conn.execute('SELECT 1 FROM router_presets WHERE package_id=?',(legacy_id,)).fetchone():
                old=self.package(legacy_id)
                suffix=hashlib.sha256(('%s/%s/%s' % (router_id,service,remote_id)).encode()).hexdigest()[:8]
                legacy_id=self.save_package(old['name'][:68]+' · '+suffix,old['settings'])
            with self.conn:
                self.conn.execute('INSERT INTO router_presets VALUES(?,?,?,?,?)',
                                  (int(router_id),service,remote_id,profile['name'],legacy_id))
            return self.package(legacy_id)
        seed=dict(DEFAULT_SETTINGS)
        # Reuse the operator's artwork, but never invent router prices or limits.
        seed.update({k:v for k,v in self.all_settings().items() if k.startswith(('card.','print.'))})
        seed.update(profile_defaults(service,profile))
        suffix=hashlib.sha256(('%s/%s/%s' % (router_id,service,remote_id)).encode()).hexdigest()[:8]
        name=('%s · %s' % (service,profile['name']))[:68]+' · '+suffix
        ident=self.save_package(name,seed)
        with self.conn:
            self.conn.execute('INSERT INTO router_presets VALUES(?,?,?,?,?)',
                              (int(router_id),service,remote_id,profile['name'],ident))
        return self.package(ident)

    def _validate_router(self, name, host, port, username, ftp_port, connection_mode='local'):
        name = str(name or "").strip()
        host = str(host or "").strip()
        username = str(username or "").strip()
        if not name or not host:
            raise ValueError("اسم الراوتر وعنوانه مطلوبان.")
        host, port = normalize_address(host, port, connection_mode)
        if any(ord(ch) < 32 for ch in username):
            raise ValueError("اسم مستخدم الراوتر يحتوي على أحرف تحكم غير صالحة.")
        port = int(port)
        ftp_port = int(ftp_port)
        if not 1 <= port <= 65535 or not 1 <= ftp_port <= 65535:
            raise ValueError("رقم المنفذ يجب أن يكون بين 1 و65535.")
        if not username:
            raise ValueError("اسم مستخدم الراوتر مطلوب.")
        return name, host, port, username, ftp_port

    def add_router(
        self,
        name,
        host,
        port,
        username,
        password,
        use_ssl=False,
        ftp_port=21,
        is_default=False,
        verify_ssl=False,
        connection_mode='local',
    ):
        name, host, port, username, ftp_port = self._validate_router(
            name, host, port, username, ftp_port, connection_mode
        )
        with self.conn:
            if is_default:
                self.conn.execute("UPDATE routers SET is_default=0")
            cur = self.conn.execute(
                """INSERT INTO routers
                (name,host,port,username,password,use_ssl,verify_ssl,ftp_port,is_default,created_at,connection_mode)
                VALUES(?,?,?,?,?,?,?,?,?,?,?)""",
                (
                    name,
                    host,
                    port,
                    username,
                    _protect_secret(password),
                    int(bool(use_ssl)),
                    int(bool(verify_ssl)),
                    ftp_port,
                    int(bool(is_default)),
                    datetime.now().isoformat(timespec="seconds"),
                    connection_mode,
                ),
            )
        router_id = cur.lastrowid
        # Login also creates connection profiles; it does not visit the router
        # settings page that used to be the only caller of this migration.
        self.refresh_router_uniqueness()
        return router_id

    def update_router(self, router_id, **fields):
        allowed = {
            "name",
            "host",
            "port",
            "username",
            "password",
            "use_ssl",
            "verify_ssl",
            "ftp_port",
            "is_default",
            "connection_mode",
        }
        values = {key: value for key, value in fields.items() if key in allowed}
        if not values:
            return
        current = self.router(router_id)
        if not current:
            raise ValueError("الراوتر المحدد غير موجود.")
        merged = dict(current)
        merged.update(values)
        name, host, port, username, ftp_port = self._validate_router(
            merged["name"], merged["host"], merged["port"], merged["username"], merged["ftp_port"],
            merged.get('connection_mode','local')
        )
        if (host.casefold() != current['host'].casefold() or port != current['port']) and self.conn.execute(
                'SELECT 1 FROM batches WHERE router_id=? LIMIT 1', (int(router_id),)).fetchone():
            raise ValueError('هذا الاتصال مرتبط بدفعات محفوظة؛ أضف اتصالًا جديدًا للعنوان أو المنفذ الجديد لحماية كروت الراوتر السابق.')
        values.update({"name": name, "host": host, "port": port, "username": username, "ftp_port": ftp_port})
        if "password" in values:
            values["password"] = _protect_secret(values["password"])
        for key in ("use_ssl", "verify_ssl", "is_default"):
            if key in values:
                values[key] = int(bool(values[key]))
        pairs = list(values.items())
        with self.conn:
            if values.get("is_default"):
                self.conn.execute("UPDATE routers SET is_default=0")
            sql = "UPDATE routers SET " + ",".join("%s=?" % key for key, _ in pairs) + " WHERE id=?"
            self.conn.execute(sql, [value for _, value in pairs] + [int(router_id)])

    def delete_router(self, router_id):
        with self.conn:
            self.conn.execute("UPDATE batches SET router_id=NULL WHERE router_id=?", (int(router_id),))
            self.conn.execute("DELETE FROM routers WHERE id=?", (int(router_id),))

    @staticmethod
    def _router_value(row):
        if not row:
            return None
        result = dict(row)
        result["password"] = _unprotect_secret(result.get("password", ""))
        return result

    def routers(self):
        rows = self.conn.execute("SELECT * FROM routers ORDER BY is_default DESC, id").fetchall()
        return [self._router_value(row) for row in rows]

    def router(self, router_id):
        row = self.conn.execute("SELECT * FROM routers WHERE id=?", (int(router_id),)).fetchone()
        return self._router_value(row)

    def default_router(self):
        row = self.conn.execute(
            "SELECT * FROM routers WHERE is_default=1 ORDER BY id LIMIT 1"
        ).fetchone()
        if not row:
            row = self.conn.execute("SELECT * FROM routers ORDER BY id LIMIT 1").fetchone()
        return self._router_value(row)

    def create_batch_with_cards(
        self,
        batch_no,
        router_id,
        server,
        profile,
        cards,
        price=0,
        validity_days="",
        limit_uptime="",
        limit_bytes=0,
        notes="",
        status="CREATED",
        package_id=None,
        print_settings=None,
        service="hotspot",
    ):
        cards = list(cards)
        now = datetime.now().isoformat(timespec="seconds")
        package = self.package(package_id)
        if package_id is not None and package is None:
            raise ValueError('الباقة المحددة لم تعد موجودة.')
        from .packages import package_snapshot
        snapshot = package_snapshot(print_settings or (package['settings'] if package else self.all_settings()))
        from .cards import MAX_BATCH_SIZE, MAX_CODE_LENGTH, validate_characters
        if not 1 <= len(cards) <= MAX_BATCH_SIZE:
            raise ValueError('عدد كروت الدفعة غير صالح.')
        if service not in ('hotspot', 'userman', 'ppp'):
            raise ValueError('نوع الخدمة غير صالح.')
        names = [str(card['username']) for card in cards]
        if len(set(names)) != len(names):
            raise ValueError('توجد أسماء مستخدمين مكررة داخل الدفعة؛ لم يُحفظ أي كرت.')
        for name in names:
            validate_characters(name, 'اسم المستخدم')
            if len(name) > MAX_CODE_LENGTH:
                raise ValueError('اسم المستخدم أطول من الحد المسموح.')
        with self.transaction() as conn:
            # Check under the same write lock as insertion: two app processes
            # must not reserve an offline name on different connection profiles.
            if router_id is not None and not conn.execute('SELECT 1 FROM routers WHERE id=?', (router_id,)).fetchone():
                raise ValueError('اتصال الراوتر لم يعد موجودًا.')
            duplicates = set(names) & self.reserved_usernames(router_id)
            if duplicates:
                raise ValueError('توجد أسماء مستخدمين محفوظة مسبقًا لهذا الراوتر؛ غيّر البادئة أو الطول.')
            if conn.execute('SELECT 1 FROM batches WHERE batch_no=?', (batch_no,)).fetchone():
                raise ValueError('رقم الدفعة محفوظ مسبقًا؛ أعد المحاولة برقم جديد.')
            cur = conn.execute(
                """INSERT INTO batches(batch_no,router_id,server,profile,quantity,price,created_at,notes,
                package_id,package_name,print_settings_json,service) VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                (batch_no, router_id, server, profile, len(cards), float(price or 0), now, str(notes or ""),
                 package_id, package['name'] if package else '', json.dumps(snapshot,ensure_ascii=False),service),
            )
            batch_id = cur.lastrowid
            router_column = ", router_id" if self._has_router_column() else ""
            router_placeholder = ", ?" if router_column else ""
            conn.executemany(
                """INSERT INTO cards(
                batch_id,sequence,username,password,price,profile,server,validity_days,
                limit_uptime,limit_bytes,created_at,status,comment,service%s)
                VALUES(?,?,?,?,?,?,?,?,?,?,?,?,?,?%s)""" % (router_column, router_placeholder),
                [
                    (
                        batch_id,
                        card["sequence"],
                        card["username"],
                        card.get("password", ""),
                        float(price or 0),
                        profile,
                        server,
                        str(validity_days or ""),
                        str(limit_uptime or ""),
                        int(limit_bytes or 0),
                        now,
                        status,
                        str(notes or ""),
                        service,
                    ) + ((router_id,) if router_column else ())
                    for card in cards
                ],
            )
        return batch_id

    def username_exists(self, username):
        return (
            self.conn.execute("SELECT 1 FROM cards WHERE username=? LIMIT 1", (username,)).fetchone()
            is not None
        )

    def username_exists_on_router(self, username, router_id=None):
        """Report whether a voucher name is already used for *this* router.

        One physical router must never receive the same voucher name twice, but
        two independent routers may legitimately use the same numbering (each
        router keeps its own Hotspot/User Manager tables).  Rows whose batch is
        not tied to any router stay globally reserved so a legacy/offline batch
        is still protected from an accidental collision.
        """
        name = str(username)
        if router_id is None or not self._has_router_column():
            # Before the per-router rebuild the single table cannot tell two
            # routers apart, so the historical global protection applies.
            return self.username_exists(name)
        row = self.conn.execute(
            "SELECT 1 FROM cards WHERE username=? AND (router_id IN (%s) OR router_id IS NULL "
            "OR router_id NOT IN (SELECT id FROM routers)) LIMIT 1" %
            ','.join('?' for _ in self._same_router_endpoint_ids(router_id)),
            (name,) + tuple(self._same_router_endpoint_ids(router_id)),
        ).fetchone()
        return row is not None

    def _same_router_endpoint_ids(self, router_id):
        rows = self.conn.execute('SELECT id,host,port,connection_mode FROM routers').fetchall()
        current = next((r for r in rows if r['id'] == int(router_id)), None)
        if current is None:return [int(router_id)]
        normalize = lambda value: str(value).strip('[]').rstrip('.').casefold()
        # Preserve legacy local aliases (API/API-SSL). A shared remote VPN
        # hostname can front many routers; its forwarded port is essential.
        return [r['id'] for r in rows if normalize(r['host']) == normalize(current['host'])
                and (r['port']==current['port'] or
                     r['connection_mode']==current['connection_mode']=='local')]

    def reserved_usernames(self, router_id=None):
        """Load one indexed name snapshot; keep orphaned/offline cards reserved."""
        if router_id is None or not self._has_router_column():
            rows = self.conn.execute('SELECT username FROM cards')
        else:
            ids = self._same_router_endpoint_ids(router_id)
            rows = self.conn.execute(
                'SELECT username FROM cards WHERE router_id IN (%s) OR router_id IS NULL '
                'OR router_id NOT IN (SELECT id FROM routers)' % ','.join('?' for _ in ids), ids)
        return {row[0] for row in rows}

    def max_numeric_sequence_on_router(self, router_id=None):
        """Highest numeric sequence already stored for one router's cards."""
        if router_id is None or not self._has_router_column():
            return self.max_numeric_sequence()
        ids = self._same_router_endpoint_ids(router_id)
        row = self.conn.execute(
            "SELECT COALESCE(MAX(CAST(sequence AS INTEGER)),0) FROM cards "
            "WHERE sequence<>'' AND sequence NOT GLOB '*[^0-9]*' "
            "AND (router_id IN (%s) OR router_id IS NULL "
            "OR router_id NOT IN (SELECT id FROM routers))" % ','.join('?' for _ in ids),
            ids,
        ).fetchone()
        return int(row[0])

    def cards(self, limit=500, batch_id=None, search=None, status=None, printed=None):
        sql = "SELECT * FROM cards"
        where = []
        params = []
        if batch_id is not None:
            where.append("batch_id=?")
            params.append(int(batch_id))
        if search:
            where.append("(username LIKE ? OR sequence LIKE ? OR profile LIKE ? OR comment LIKE ?)")
            token = "%%%s%%" % str(search)
            params.extend([token, token, token, token])
        if status and status != "ALL":
            where.append("status=?")
            params.append(status)
        if printed is not None:
            where.append("printed=?")
            params.append(int(bool(printed)))
        if where:
            sql += " WHERE " + " AND ".join(where)
        sql += " ORDER BY id DESC LIMIT ?"
        params.append(max(1, int(limit)))
        return self.conn.execute(sql, params).fetchall()

    def card(self, card_id):
        return self.conn.execute("SELECT * FROM cards WHERE id=?", (int(card_id),)).fetchone()

    def batches(self, limit=200, package_id=None, router_id=None, unassigned=False, service=None, profile=None):
        where, params = [], []
        if package_id is not None:
            where.append('b.package_id=?'); params.append(int(package_id))
        elif unassigned:
            where.append('b.package_id IS NULL')
        if router_id is not None:
            where.append('(b.router_id=? OR b.router_id IS NULL)'); params.append(int(router_id))
        if service is not None:
            where.append("COALESCE(b.service,'hotspot')=?"); params.append(str(service))
        if profile is not None:
            where.append('b.profile=?'); params.append(str(profile))
        params.append(max(1,int(limit)))
        return self.conn.execute(
            """SELECT b.*, r.name router_name FROM batches b
            LEFT JOIN routers r ON r.id=b.router_id """ +
            ('WHERE '+' AND '.join(where)+' ' if where else '') + 'ORDER BY b.id DESC LIMIT ?', params,
        ).fetchall()

    def batch(self, batch_id):
        return self.conn.execute("SELECT * FROM batches WHERE id=?", (int(batch_id),)).fetchone()

    def max_numeric_sequence(self):
        row = self.conn.execute(
            "SELECT COALESCE(MAX(CAST(sequence AS INTEGER)),0) FROM cards "
            "WHERE sequence<>'' AND sequence NOT GLOB '*[^0-9]*'"
        ).fetchone()
        return int(row[0])

    def set_card_status(self, card_ids, status, error=""):
        card_ids = list(dict.fromkeys(int(card_id) for card_id in card_ids))
        if not card_ids:
            return 0
        with self.conn:
            cur = self.conn.executemany(
                "UPDATE cards SET status=?,last_error=? WHERE id=? AND status<>'SENDING'",
                ((str(status), str(error or "")[:1000], ident) for ident in card_ids),
            )
        return cur.rowcount

    def mark_printed(self, card_ids):
        card_ids = list(dict.fromkeys(int(card_id) for card_id in card_ids))
        if not card_ids:
            return
        with self.conn:
            self.conn.executemany(
                "UPDATE cards SET printed=1,print_count=print_count+1 WHERE id=?",
                ((ident,) for ident in card_ids),
            )

    def delete_cards(self, card_ids):
        card_ids = list(dict.fromkeys(int(card_id) for card_id in card_ids))
        if not card_ids:
            return 0
        with self.transaction() as conn:
            for start in range(0, len(card_ids), 800):
                group = card_ids[start:start + 800]
                blocked = conn.execute(
                    "SELECT 1 FROM cards WHERE status IN ('SENDING','UNKNOWN') AND id IN (%s) LIMIT 1" %
                    ','.join('?' for _ in group), group).fetchone()
                if blocked:
                    raise ValueError('لا يمكن حذف سجل إرسال جارٍ أو غير مؤكد؛ احتفظ به للتحقق من الكرت على الراوتر.')
            cur = conn.executemany("DELETE FROM cards WHERE id=?", ((ident,) for ident in card_ids))
        return cur.rowcount

    def delete_batch(self, batch_id):
        with self.transaction() as conn:
            if conn.execute("SELECT 1 FROM cards WHERE batch_id=? AND status IN ('SENDING','UNKNOWN') LIMIT 1",
                            (int(batch_id),)).fetchone():
                raise ValueError('الدفعة تحتوي إرسالًا جاريًا أو غير مؤكد؛ احتفظ بسجلها للتحقق من الراوتر.')
            conn.execute("DELETE FROM cards WHERE batch_id=?", (int(batch_id),))
            conn.execute("DELETE FROM batches WHERE id=?", (int(batch_id),))

    def record_remote_user_action(self, router_id, service, action, usernames):
        """Keep confirmed local cards in step with successful router actions."""
        statuses = {'enable': 'SENT', 'disable': 'DISABLED', 'remove': 'REMOTE_DELETED'}
        if action not in statuses:
            return 0
        ids = self._same_router_endpoint_ids(router_id)
        names = list(dict.fromkeys(str(name) for name in usernames))
        with self.conn:
            cur = self.conn.executemany(
                "UPDATE cards SET status=?,last_error='' WHERE username=? AND service=? "
                "AND status IN ('SENT','DISABLED') AND batch_id IN "
                "(SELECT id FROM batches WHERE router_id IN (%s))" % ','.join('?' for _ in ids),
                ((statuses[action], name, service, *ids) for name in names))
        return cur.rowcount

    def stats(self):
        total = self.conn.execute("SELECT COUNT(*) c FROM cards").fetchone()["c"]
        printed = self.conn.execute("SELECT COUNT(*) c FROM cards WHERE printed=1").fetchone()["c"]
        sent = self.conn.execute("SELECT COUNT(*) c FROM cards WHERE status='SENT'").fetchone()["c"]
        failed = self.conn.execute("SELECT COUNT(*) c FROM cards WHERE status='FAILED'").fetchone()["c"]
        batches = self.conn.execute("SELECT COUNT(*) c FROM batches").fetchone()["c"]
        return {
            "total": total,
            "printed": printed,
            "unprinted": total - printed,
            "batches": batches,
            "sent": sent,
            "failed": failed,
        }

    def dashboard_stats(self, router_id=None):
        """Return local voucher lifecycle totals, split by router service."""
        where = ""
        params = []
        if router_id is not None:
            where = " WHERE b.router_id=?"
            params.append(int(router_id))
        rows = self.conn.execute(
            "SELECT COALESCE(c.service,'hotspot') service,c.status,COUNT(*) c "
            "FROM cards c LEFT JOIN batches b ON b.id=c.batch_id" + where +
            " GROUP BY COALESCE(c.service,'hotspot'),c.status", params
        ).fetchall()
        result = {service:{"total":0,"valid":0,"expired":0,"pending":0}
                  for service in ("hotspot","userman","ppp")}
        expired_statuses = {"DISABLED","REMOTE_DELETED"}
        for row in rows:
            service = row["service"] if row["service"] in result else "hotspot"
            count = int(row["c"] or 0);status = str(row["status"] or "")
            result[service]["total"] += count
            if status == "SENT":result[service]["valid"] += count
            elif status in expired_statuses:result[service]["expired"] += count
            else:result[service]["pending"] += count
        total = {key:sum(values[key] for values in result.values())
                 for key in ("total","valid","expired","pending")}
        return {"total":total, "services":result}

    def dashboard_analytics(self, router_id=None, days=15):
        """Small local analytics set for the visual dashboard.

        The database records voucher creation, not real cash collection. Values
        below therefore represent the face value of cards produced by this
        program. Keeping that distinction explicit avoids presenting generated
        cards as confirmed sales.
        """
        days=max(2,min(60,int(days or 15)))
        today=datetime.now().date()
        first=today.replace(day=1).isoformat()
        start=(today-timedelta(days=days-1)).isoformat()
        where=""
        params=[]
        if router_id is not None:
            where=" AND b.router_id=?";params.append(int(router_id))
        def aggregate(prefix):
            row=self.conn.execute(
                "SELECT COUNT(*) c,COALESCE(SUM(c.price),0) value FROM cards c "
                "LEFT JOIN batches b ON b.id=c.batch_id WHERE c.created_at LIKE ?"+where,
                [prefix+'%']+params).fetchone()
            return {'count':int(row['c'] or 0),'value':float(row['value'] or 0)}
        daily_rows=self.conn.execute(
            "SELECT substr(c.created_at,1,10) day,COUNT(*) c,COALESCE(SUM(c.price),0) value "
            "FROM cards c LEFT JOIN batches b ON b.id=c.batch_id "
            "WHERE substr(c.created_at,1,10)>=?"+where+" GROUP BY substr(c.created_at,1,10)",
            [start]+params).fetchall()
        by_day={str(row['day']):{'count':int(row['c'] or 0),'value':float(row['value'] or 0)} for row in daily_rows}
        series=[]
        for offset in range(days):
            day=today-timedelta(days=days-1-offset);key=day.isoformat();item=by_day.get(key,{'count':0,'value':0.0})
            series.append({'day':key,'label':day.strftime('%d/%m'),'count':item['count'],'value':item['value']})
        return {'today':aggregate(today.isoformat()),'month':aggregate(first[:7]),'series':series}

    def generated_usernames(self, service, router_id=None):
        """Names produced by this program for one service/router."""
        sql = ("SELECT c.username FROM cards c LEFT JOIN batches b ON b.id=c.batch_id "
               "WHERE COALESCE(c.service,'hotspot')=?")
        params=[str(service)]
        if router_id is not None:
            sql += " AND b.router_id=?";params.append(int(router_id))
        return {str(row["username"]) for row in self.conn.execute(sql,params).fetchall()}

    def log(self, category, action, status="SUCCESS", details=""):
        with self.conn:
            self.conn.execute(
                "INSERT INTO activity(ts,category,action,status,details) VALUES(?,?,?,?,?)",
                (
                    datetime.now().isoformat(timespec="seconds"),
                    str(category or "GENERAL")[:50],
                    str(action or "")[:150],
                    str(status or "INFO")[:30],
                    str(details or "")[:4000],
                ),
            )

    def activities(self, limit=200, search=None, category=None):
        sql = "SELECT * FROM activity"
        where = []
        params = []
        if search:
            token = "%%%s%%" % str(search)
            where.append("(action LIKE ? OR details LIKE ? OR status LIKE ?)")
            params.extend([token, token, token])
        if category and category != "ALL":
            where.append("category=?")
            params.append(category)
        if where:
            sql += " WHERE " + " AND ".join(where)
        sql += " ORDER BY id DESC LIMIT ?"
        params.append(max(1, int(limit)))
        return self.conn.execute(sql, params).fetchall()

    def integrity_check(self):
        row = self.conn.execute("PRAGMA quick_check").fetchone()
        result = str(row[0]) if row else "unknown"
        if result == 'ok' and self.conn.execute("PRAGMA foreign_key_check").fetchone():
            return 'توجد علاقات مفقودة بين سجلات قاعدة البيانات.'
        return result

    def backup_to(self, destination):
        destination = Path(destination)
        if destination.exists() and os.path.samefile(str(destination), str(self.path)):
            raise ValueError("اختر ملفًا مختلفًا عن قاعدة البيانات الحالية للنسخ الاحتياطي.")
        destination.parent.mkdir(parents=True, exist_ok=True)
        with tempfile.TemporaryDirectory(prefix=".nidi-backup-", dir=str(destination.parent)) as folder:
            staged_path = Path(folder) / "backup.db"
            target = sqlite3.connect(str(staged_path))
            try:
                self.conn.backup(target)
                target.commit()
                check = target.execute("PRAGMA quick_check").fetchone()
                if not check or check[0] != "ok":
                    raise sqlite3.DatabaseError("فشل فحص سلامة النسخة الاحتياطية.")
            finally:
                target.close()
            os.replace(str(staged_path), str(destination))
        return destination

    @staticmethod
    def validate_backup(path):
        path = Path(path)
        if not path.is_file():
            raise FileNotFoundError(path)
        uri = path.resolve().as_uri() + "?mode=ro"
        conn = sqlite3.connect(uri, uri=True)
        try:
            check = conn.execute("PRAGMA quick_check").fetchone()
            if not check or check[0] != "ok":
                raise sqlite3.DatabaseError("ملف النسخة الاحتياطية تالف.")
            if conn.execute("PRAGMA foreign_key_check").fetchone():
                raise sqlite3.DatabaseError("النسخة الاحتياطية تحتوي سجلات مرتبطة ببيانات مفقودة؛ لم تُستعد البيانات.")
            tables = {row[0] for row in conn.execute("SELECT name FROM sqlite_master WHERE type='table'")}
            required = {"settings", "cards", "batches", "activity"}
            if not required.issubset(tables):
                raise sqlite3.DatabaseError("الملف ليس نسخة صالحة لبرنامج حضرموت.")
            columns = {'settings': {'key','value'},
                       'cards': {'id','batch_id','username','password','sequence','price','profile','server',
                                 'validity_days','limit_uptime','limit_bytes','status','created_at',
                                 'printed','print_count','comment'},
                       'batches': {'id','batch_no','router_id','server','profile','quantity','price','created_at','notes'},
                       'activity': {'id','ts','category','action','status','details'}}
            for table, needed in columns.items():
                present = {row[1] for row in conn.execute('PRAGMA table_info(%s)' % table)}
                if not needed.issubset(present):
                    raise sqlite3.DatabaseError("بنية النسخة الاحتياطية ناقصة: " + table)
            if 'meta' in tables:
                row = conn.execute("SELECT value FROM meta WHERE key='schema_version'").fetchone()
                if row and int(row[0]) > SCHEMA_VERSION:
                    raise sqlite3.DatabaseError("هذه النسخة تحتاج إصدارًا أحدث من البرنامج.")
        finally:
            conn.close()
        return True

    def restore_from(self, source_path):
        source_path = Path(source_path)
        if source_path.exists() and os.path.samefile(str(source_path), str(self.path)):
            raise ValueError("اختر نسخة احتياطية مختلفة عن قاعدة البيانات الحالية.")
        self.validate_backup(source_path)
        # Migrate and validate a separate copy first. An incompatible backup
        # must never replace the live database before an error is discovered.
        with tempfile.TemporaryDirectory(prefix="nidi-restore-") as folder:
            staged_path = Path(folder) / "validated.db"
            source = sqlite3.connect(source_path.resolve().as_uri() + "?mode=ro", uri=True)
            target = sqlite3.connect(str(staged_path))
            try:
                source.backup(target)
            finally:
                target.close()
                source.close()
            # Recheck the exact copied bytes, not only the source path that
            # may have changed between validation and the SQLite backup.
            self.validate_backup(staged_path)
            staged = Database(staged_path)
            try:
                if staged.integrity_check() != 'ok':
                    raise sqlite3.DatabaseError("فشل فحص النسخة بعد الترقية.")
                staged.conn.backup(self.conn)
                self.conn.commit()
            finally:
                staged.close()
        self._configure()
        self._router_scoped = "router_id" in self._columns('cards')
        return True

    def close(self):
        if self._closed:
            return
        try:
            self.conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            self.conn.commit()
        finally:
            self.conn.close()
            self._closed = True
