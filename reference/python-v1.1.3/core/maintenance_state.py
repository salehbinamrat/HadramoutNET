"""Crash-resilient, password-free state ledger for explicit Hotspot maintenance.

Journal is written BEFORE each router mutation.  Recovery is manually triggered;
no automatic network changes occur when the application starts.
"""
from __future__ import annotations
import hashlib
import json
import re
import os
from contextlib import contextmanager
from pathlib import Path
from datetime import datetime
import tempfile

def _unique_pairs(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError('حقل مكرر في ملف حالة الصيانة: ' + key)
        result[key] = value
    return result

def _atomic_write(path, data, make_parent=False):
    """Replace maintenance journal atomically; old journal survives IO errors."""
    path = Path(path)
    if path.is_symlink():
        raise ValueError('لا يمكن الكتابة على رابط رمزي لحالة الصيانة.')
    if make_parent:
        path.parent.mkdir(parents=True, exist_ok=True)
    if not isinstance(data, bytes):
        raise TypeError('Maintenance journal must be bytes')
    temp_path = None
    try:
        with tempfile.NamedTemporaryFile(mode='wb', prefix='.' + path.name + '.',
                                         suffix='.tmp', dir=path.parent, delete=False) as stream:
            temp_path = Path(stream.name)
            stream.write(data)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temp_path, path)
    finally:
        if temp_path is not None:
            temp_path.unlink(missing_ok=True)


@contextmanager
def operation_lock(path):
    """OS-released advisory lock: two app instances cannot replace pre-states."""
    lock_path=Path(path).with_suffix('.lock')
    lock_path.parent.mkdir(parents=True,exist_ok=True)
    with lock_path.open('a+b') as stream:
        if stream.tell()==0:stream.write(b'\0');stream.flush()
        stream.seek(0)
        try:
            if os.name=='nt':
                import msvcrt
                msvcrt.locking(stream.fileno(),msvcrt.LK_NBLCK,1)
            else:
                import fcntl
                fcntl.flock(stream.fileno(),fcntl.LOCK_EX|fcntl.LOCK_NB)
        except OSError:
            raise ValueError('توجد عملية صيانة أخرى لهذا الراوتر؛ انتظر اكتمالها.') from None
        try:yield
        finally:
            stream.seek(0)
            if os.name=='nt':msvcrt.locking(stream.fileno(),msvcrt.LK_UNLCK,1)
            else:fcntl.flock(stream.fileno(),fcntl.LOCK_UN)


def _now():
    return datetime.now().astimezone().isoformat(timespec='seconds')


def file_for(data_dir, router):
    host = str(router.get('host', '')).strip()
    port = str(router.get('port', '')).strip()
    if not host or not port:
        raise ValueError('اتصال الراوتر غير محدد.')
    identity = host.lower() + ':' + port
    digest = hashlib.sha256(identity.encode('utf-8')).hexdigest()[:24]
    return Path(data_dir) / 'maintenance' / (digest + '.json')


def read(path):
    path = Path(path)
    if not path.exists():
        return None
    if path.is_symlink():
        raise ValueError('مسار الاستعادة رابط رمزي؛ لم يُعتمد.')
    with path.open('rb') as stream:
        raw=stream.read(1024*1024+1)
    if len(raw)>1024*1024:
        raise ValueError('ملف الاستعادة أكبر من الحد المسموح.')
    result=json.loads(raw.decode('utf-8'),object_pairs_hook=_unique_pairs)
    if (not isinstance(result, dict) or type(result.get('version')) is not int or result['version']!=1
            or result.get('status') not in ('pending','active','restoring','restored','completed')
            or not isinstance(result.get('router_host'),str) or not result['router_host']
            or not isinstance(result.get('router_port'),str) or not result['router_port'].isdigit()
            or not 1<=int(result['router_port'])<=65535
            or not isinstance(result.get('servers'),list) or not 1<=len(result['servers'])<=500):
        raise ValueError('ملف الاستعادة غير صالح. توقف عن التعديل حتى يُراجَع.')
    seen=set()
    for row in result['servers']:
        if (not isinstance(row,dict) or not re.fullmatch(r'\*[0-9A-Fa-f]+',str(row.get('id','')))
                or not isinstance(row.get('name'),str) or not row['name']
                or not isinstance(row.get('interface'),str)
                or type(row.get('disabled_before')) is not bool
                or row['id'] in seen):
            raise ValueError('حالات السيرفرات المحفوظة تالفة؛ لم ينفذ أي تعديل.')
        seen.add(row['id'])
    return result


def write(path, record):
    path = Path(path)
    raw = json.dumps(record, ensure_ascii=False, sort_keys=True, indent=2)
    _atomic_write(path,raw.encode('utf-8'),True)


def make_record(router, server_rows):
    return {'version': 1,
            'router_host': str(router['host']),
            'router_port': str(router['port']),
            'status': 'pending', 'created': _now(), 'updated': _now(),
            'servers': [{'id': r['id'], 'name': r['name'], 'interface': r['interface'],
                         'disabled_before': bool(r['disabled'])} for r in server_rows]}


def set_status(path,record,status):
    record['status'] = status
    record['updated'] = _now()
    write(path,record)
