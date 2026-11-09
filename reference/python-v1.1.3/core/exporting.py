from __future__ import annotations

import csv
import os
import tempfile
from contextlib import contextmanager
from pathlib import Path


CARD_HEADERS = [
    "sequence",
    "username",
    "password",
    "profile",
    "server",
    "limit_uptime",
    "limit_bytes",
    "validity_days",
    "price",
    "status",
    "created_at",
    "comment",
    "service",
]


@contextmanager
def _staged_export(destination):
    """Publish only complete output; keep an existing export on any failure."""
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='.nidi-export-', dir=str(destination.parent)) as folder:
        staged = Path(folder) / destination.name
        yield staged
        with staged.open('r+b') as handle:
            os.fsync(handle.fileno())
        os.replace(str(staged), str(destination))


def _value(card, key, default=""):
    try:
        return card[key]
    except (KeyError, TypeError, IndexError):
        getter = getattr(card, "get", None)
        return getter(key, default) if getter else default


def mikrotik_quote(value):
    """Quote user data for a RouterOS .rsc script without creating commands."""
    text = str(value or "")
    escaped = []
    for char in text:
        if char == "\\":
            escaped.append("\\\\")
        elif char == '"':
            escaped.append('\\"')
        elif char == "$":
            escaped.append("\\$")
        elif char == "\r":
            escaped.append("\\r")
        elif char == "\n":
            escaped.append("\\n")
        elif char == "\t":
            escaped.append("\\t")
        elif ord(char) < 32 or ord(char) == 127:
            escaped.append("\\%02X" % ord(char))
        else:
            escaped.append(char)
    return '"%s"' % "".join(escaped)



def routeros_user_line(card):
    service=_value(card,'service','hotspot') or 'hotspot'
    if service=='ppp':
        return '/ppp secret add service=pppoe name=%s password=%s profile=%s comment=%s' % tuple(
            mikrotik_quote(_value(card,key)) for key in ('username','password','profile','comment'))
    if service=='userman':
        version=int(_value(card,'um_version',0) or 0)
        name=mikrotik_quote(_value(card,'username'));password=mikrotik_quote(_value(card,'password'));profile=mikrotik_quote(_value(card,'profile'))
        if version==7:return '/user-manager user add name=%s password=%s\n/user-manager user-profile add user=%s profile=%s' % (name,password,name,profile)
        owner=_value(card,'customer','')
        if version==6 and owner:
            customer=mikrotik_quote(owner)
            return ('/tool user-manager user add username=%s password=%s customer=%s\n'
                    ':local voucherId [/tool user-manager user find where username=%s]\n'
                    ':if ([:len $voucherId] != 1) do={ :error "Cannot identify new voucher; stopped" }\n'
                    '/tool user-manager user create-and-activate-profile numbers=$voucherId customer=%s profile=%s') % (name,password,customer,name,customer,profile)
        raise ValueError('لتصدير سكربت مدير المستخدمين، اتصل براوتر الدفعة واقرأ الباقة وحساب مديرها أولًا.')
    if service!='hotspot':raise ValueError('نوع خدمة غير معروف.')
    parts = ["/ip hotspot user add", "name=" + mikrotik_quote(_value(card, "username"))]
    password = _value(card, "password")
    if password:
        parts.append("password=" + mikrotik_quote(password))
    server = _value(card, "server", "all")
    if server and server != "all":
        parts.append("server=" + mikrotik_quote(server))
    profile = _value(card, "profile", "default")
    if profile:
        parts.append("profile=" + mikrotik_quote(profile))
    uptime = _value(card, "limit_uptime")
    if uptime:
        parts.append("limit-uptime=" + mikrotik_quote(uptime))
    limit_bytes = _value(card, "limit_bytes", 0)
    if limit_bytes:
        parts.append("limit-bytes-total=%d" % int(limit_bytes))
    comment = _value(card, "comment")
    if comment:
        parts.append("comment=" + mikrotik_quote(comment))
    return " ".join(parts)


def export_text(cards, path):
    destination = Path(path)
    with _staged_export(destination) as staged:
        with staged.open("w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.writer(handle, lineterminator="\n")
            for card in cards:
                writer.writerow((_value(card, "username"), _value(card, "password")))
    return str(destination)


def _spreadsheet_text(value):
    """Neutralize formulas in CSV while leaving ordinary voucher numbers alone."""
    if isinstance(value, str):
        probe = value.lstrip()
        if probe.startswith(("=", "+", "-", "@")) or value.startswith(("\t", "\r", "\n")):
            return "'" + value
    return value


def export_csv(cards, path):
    destination = Path(path)
    with _staged_export(destination) as staged:
        with staged.open("w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.writer(handle)
            writer.writerow(CARD_HEADERS)
            for card in cards:
                writer.writerow([_spreadsheet_text(_value(card, key)) for key in CARD_HEADERS])
    return str(destination)


def export_script(cards, path):
    lines=[];seen=set()
    for card in cards:
        name=str(_value(card,'username'))
        if not name.strip() or name in seen:
            raise ValueError('أسماء مستخدمين فارغة أو مكررة؛ لم يُنشأ السكربت.')
        seen.add(name)
        service=_value(card,'service','hotspot') or 'hotspot'
        menu,field=('/ip hotspot user','name')
        if service=='ppp':menu='/ppp secret'
        elif service=='userman':
            if int(_value(card,'um_version',0) or 0)==6:menu,field='/tool user-manager user','username'
            else:menu='/user-manager user'
        line=routeros_user_line(card)
        # The profile assignment belongs inside the same guarded block as add.
        # Re-importing a file must not attach another package to an old account.
        lines.append(':if ([:len [%s find where %s=%s]] = 0) do={\n%s\n} else={ :error "Voucher already exists; stopped" }' %
                     (menu,field,mikrotik_quote(name),line))
    destination = Path(path)
    with _staged_export(destination) as staged:
        with staged.open("w", encoding="utf-8-sig", newline="\n") as handle:
            handle.write("# Hadramout Network Cards - RouterOS 6/7\n")
            handle.write("# Generated offline; review the target profile before import.\n")
            for line in lines:
                handle.write(line + "\n")
    return str(destination)


def export_excel(cards, path, title="Cards"):
    from openpyxl import Workbook
    from openpyxl.styles import Alignment, Font, PatternFill
    from openpyxl.utils import get_column_letter

    destination = Path(path)
    workbook = Workbook()
    sheet = workbook.active
    sheet.title = ''.join(c for c in str(title or "Cards") if c not in '\\/*?:[]')[:31] or "Cards"
    sheet.sheet_view.rightToLeft = True
    sheet.freeze_panes = "A2"
    sheet.append(CARD_HEADERS)
    for row_index, card in enumerate(cards, 2):
        values = [_value(card, key) for key in CARD_HEADERS]
        sheet.append(values)
        # Exact credentials (including leading zeroes and '=' passwords) must
        # remain text, never an executable spreadsheet formula.
        for column, value in enumerate(values, 1):
            if isinstance(value, str):
                sheet.cell(row_index, column).data_type = 's'

    fill = PatternFill("solid", fgColor="008F78")
    for cell in sheet[1]:
        cell.fill = fill
        cell.font = Font(color="FFFFFF", bold=True)
        cell.alignment = Alignment(horizontal="center")
    sheet.auto_filter.ref = sheet.dimensions
    widths = {
        "sequence": 12,
        "username": 20,
        "password": 18,
        "profile": 18,
        "server": 15,
        "limit_uptime": 15,
        "limit_bytes": 16,
        "validity_days": 14,
        "price": 12,
        "status": 14,
        "created_at": 22,
        "comment": 28,
    }
    for index, key in enumerate(CARD_HEADERS, 1):
        sheet.column_dimensions[get_column_letter(index)].width = widths.get(key, 15)
    with _staged_export(destination) as staged:
        workbook.save(str(staged))
    return str(destination)
