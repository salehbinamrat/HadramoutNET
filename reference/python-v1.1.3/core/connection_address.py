"""Parse router addresses without connecting or inferring a forwarded API port.

A paid WinBox endpoint may forward a different service. The API port entered
by the operator is always required and authoritative, even if a link has a port.
HTTP(S) and winbox prefixes describe pasted links, not the API transport.
"""
from ipaddress import ip_address, IPv6Address
import re
import unicodedata
from urllib.parse import urlsplit


def normalize_address(address, api_port, mode='local'):
    if mode not in ('local', 'remote'):
        raise ValueError('اختر نوع الاتصال: محلي أو عن بُعد.')
    host = str(address or '').strip()
    if not host or len(host)>1024 or any(
            ch.isspace() or unicodedata.category(ch) in ('Cc','Cf','Cs') for ch in host):
        raise ValueError('عنوان الراوتر غير صالح؛ احذف المسافات وأحرف التحكم.')
    try:
        port = int(str(api_port).strip())
        if not 1<=port<=65535:raise ValueError
    except (ValueError, TypeError):
        if mode=='remote':
            raise ValueError('أدخل منفذ API الخارجي من مزود الرابط (1–65535)، وليس منفذ WinBox تلقائياً.') from None
        raise ValueError('منفذ API يجب أن يكون رقمًا من 1 إلى 65535.') from None
    if mode=='local':
        if '[' in host or ']' in host:
            try:
                if not (host.startswith('[') and host.endswith(']')):raise ValueError
                host=str(IPv6Address(host[1:-1]))
            except ValueError:
                raise ValueError('عنوان IPv6 غير صالح؛ أدخل المنفذ في حقل منفذ API.') from None
        if any(ch in host for ch in '/\\?#@'):
            raise ValueError('أدخل عنوان IP أو اسم نطاق الراوتر فقط، بدون http:// أو مسار صفحة.')
        if ':' in host:
            try:
                host = str(IPv6Address(host))
            except ValueError:
                raise ValueError('عنوان IPv6 غير صالح أو العنوان يتضمن منفذًا؛ أدخل منفذ API في حقله المنفصل.') from None
        return host, port
    if any(ch in host for ch in '\\?#@%'):
        raise ValueError('أدخل عنوان الاتصال فقط، بدون بيانات دخول أو استعلام أو مسار صفحة.')
    # Unbracketed IPv6 is an address, never an ambiguous host:port split.
    try:
        return str(ip_address(host)), port
    except ValueError:
        pass
    try:
        parsed=urlsplit(host if '://' in host else '//'+host)
        if parsed.scheme.lower() not in ('','http','https','winbox'):
            raise ValueError
        if parsed.path not in ('','/') or parsed.query or parsed.fragment:
            raise ValueError
        # Access validates numeric/range syntax; it NEVER selects the API port.
        if parsed.port is not None and parsed.port<1:raise ValueError
        if parsed.netloc.endswith(':'):raise ValueError
        name=parsed.hostname
        if not name:raise ValueError
        try:
            name=str(ip_address(name))
        except ValueError:
            name=name.encode('idna').decode('ascii').lower()
            if len(name.rstrip('.'))>253:raise ValueError
            labels=name.rstrip('.').split('.')
            if any(not re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', label) for label in labels):
                raise ValueError
    except (ValueError, UnicodeError):
        raise ValueError('الرابط غير صالح. استخدم اسم نطاق أو IP، ويمكن إضافة المنفذ أو بادئة winbox:// بدون مسار صفحة.') from None
    return name, port


def endpoint_text(host, port):
    """Unambiguous display only; never includes username or password."""
    return ('[%s]:%s' if ':' in host else '%s:%s') % (host,port)
