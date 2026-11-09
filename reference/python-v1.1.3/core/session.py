"""Router authentication state, independent of the desktop UI."""
from __future__ import annotations

from .connection_address import normalize_address
from .mikrotik import connect_from_row


def login_settings(values):
    row = dict(values)
    mode = row.get('connection_mode', 'local')
    host, port = normalize_address(row.get('host', ''), row.get('port', ''), mode)
    username = str(row.get('username', '')).strip()
    if not username:
        raise ValueError('أدخل اسم مستخدم الراوتر.')
    if any(ord(c) < 32 for c in username):
        raise ValueError('اسم المستخدم يحتوي على أحرف تحكم غير صالحة.')
    row.update(host=host, port=port, username=username,
               password=str(row.get('password', '')), use_ssl=bool(row.get('use_ssl')),
               verify_ssl=bool(row.get('verify_ssl')), ftp_port=row.get('ftp_port', 21),
               connection_mode=mode)
    return row


def authenticate_router(row):
    """Return a live, authenticated client. A TCP connection alone is not login."""
    api = connect_from_row(row)
    try:
        api.connect()
        identity = api.identity()
        return api, identity or row['host']
    except Exception:
        api.close()
        raise


def remember_connection(db, row, identity, save_password=False):
    """Persist a profile only after authentication; keep unsaved secrets in memory."""
    same = next((dict(saved) for saved in db.routers()
                 if saved['host']==row['host'] and saved['port']==row['port']
                 and saved['username']==row['username']
                 and bool(saved['use_ssl'])==bool(row['use_ssl'])
                 and saved.get('connection_mode','local')==row.get('connection_mode','local')), None)
    values = dict(name=(same['name'] if same else identity), host=row['host'],
                  port=row['port'], username=row['username'],
                  password=row['password'] if save_password else '',
                  use_ssl=row['use_ssl'], verify_ssl=row['verify_ssl'],
                  ftp_port=same['ftp_port'] if same else row.get('ftp_port',21),
                  connection_mode=row.get('connection_mode','local'))
    if same:
        db.update_router(same['id'], **values)
        router_id = same['id']
    else:
        router_id = db.add_router(**values, is_default=not bool(db.routers()))
    result = dict(db.router(router_id))
    result['password'] = row['password']
    return result


class RouterSession:
    def __init__(self):
        self.generation = 0
        self.state = 'locked'
        self.router = None
        self.api = None
        self.identity = ''

    @property
    def authenticated(self):
        return self.state == 'authenticated' and self.api is not None

    def begin(self):
        self.lock()
        self.state = 'connecting'
        return self.generation

    def accept(self, generation, row, api, identity):
        if generation != self.generation or self.state != 'connecting':
            api.close()
            return False
        self.router = dict(row)
        self.api = api
        self.identity = identity
        self.state = 'authenticated'
        return True

    def lock(self):
        self.generation += 1
        api, self.api = self.api, None
        self.router = None
        self.identity = ''
        self.state = 'locked'
        if api is not None:
            api.close()

    def router_row(self):
        return dict(self.router) if self.authenticated else None
