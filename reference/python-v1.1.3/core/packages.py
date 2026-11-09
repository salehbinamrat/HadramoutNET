"""Portable package presets: card art, creation defaults and print layout."""
import base64
import io
import json
import math
from pathlib import Path

from PIL import Image


PREFIXES = ('card.', 'create.', 'print.')
MAX_IMAGE_BYTES = 15 * 1024 * 1024
CARD_IMAGE_FORMATS = ('PNG', 'JPEG', 'BMP', 'WEBP', 'GIF')
CARD_FONT_LIMITS = (1, 300)
CARD_BORDER_LIMITS = (0, 100)


def _verify_image(content):
    if len(content) > MAX_IMAGE_BYTES:
        raise ValueError('صورة القالب أكبر من 15 ميجابايت.')
    try:
        with Image.open(io.BytesIO(content), formats=CARD_IMAGE_FORMATS) as check:
            if check.width * check.height > 32_000_000:
                raise ValueError('أبعاد صورة القالب كبيرة جدًا؛ اختر صورة أصغر من 32 مليون بكسل.')
            check.verify()
    except (OSError, SyntaxError) as exc:
        raise ValueError('صورة القالب تالفة أو غير مدعومة.') from exc


def package_snapshot(settings):
    from .db import DEFAULT_SETTINGS
    result = {key: value for key, value in DEFAULT_SETTINGS.items() if key.startswith(PREFIXES)}
    result.update({key: value for key, value in settings.items() if key.startswith(PREFIXES)})
    # Support dense A4 layouts while preserving the existing larger templates:
    # positive dimensions now accept width 40 mm and height below 15 mm.
    for key, low, high in (('card.width_mm',1,300), ('card.height_mm',1,300),
                           ('card.dpi',72,600), ('create.price',0,1e9),
                           ('create.validity_days',0,100000), ('create.limit_bytes',0,1e9),
                           ('create.limit_uptime_h',0,1e6), ('create.limit_uptime_m',0,59),
                           ('print.rows',1,1000), ('print.columns',1,1000),
                           ('print.gap_h_mm',0,300), ('print.gap_v_mm',0,300),
                           ('print.page_margin_mm',0,300)):
        try:number = float(result[key])
        except (TypeError, ValueError):raise ValueError('قيمة رقمية غير صالحة: '+key)
        if not math.isfinite(number) or not low <= number <= high:
            raise ValueError('قيمة غير صالحة للإعداد %s؛ النطاق %s إلى %s.' % (key,low,high))
        result[key] = number
    for key, low, high in (('create.code_length',1,64),('create.password_length',1,64),
                           ('create.quantity',1,10000),('create.start_sequence',0,2147483647),
                           ('create.validity_days',0,100000),('create.limit_uptime_h',0,1000000),
                           ('create.limit_uptime_m',0,59),('card.dpi',72,600),
                           ('card.font_size',*CARD_FONT_LIMITS),('card.border_width',*CARD_BORDER_LIMITS),
                           ('print.rows',1,1000),('print.columns',1,1000)):
        try:number = float(result[key])
        except (TypeError, ValueError):raise ValueError('قيمة رقمية غير صالحة: '+key)
        if not math.isfinite(number) or not number.is_integer() or not low <= number <= high:
            raise ValueError('يجب إدخال عدد صحيح بين %s و%s للإعداد %s.' % (low,high,key))
        result[key] = int(number)
    if result['print.paper'] not in ('A4','A3') or result['print.orientation'] not in ('portrait','landscape'):
        raise ValueError('حجم الورق أو اتجاهه غير صالح.')
    from .cards import CARD_FORMATS, UNIT_MULTIPLIERS, validate_characters, validate_username_prefix
    if result['create.limit_unit'] not in UNIT_MULTIPLIERS:
        raise ValueError('وحدة السعة غير صحيحة.')
    if result['create.card_format'] not in CARD_FORMATS:
        raise ValueError('صيغة الكرت غير صحيحة.')
    validate_characters(result['create.username_chars'],'أحرف اسم المستخدم')
    result['create.username_prefix']=validate_username_prefix(
        result.get('create.username_prefix',''),result['create.code_length'],result['create.username_chars'])
    if result['create.card_format']=='code_password':
        validate_characters(result['create.password_chars'],'أحرف كلمة المرور')
    if result['print.order'] not in ('left_to_right','right_to_left','snake','column_left','column_right'):
        raise ValueError('ترتيب الكروت غير صحيح.')
    if result.get('print.duplex','single') not in ('single','long-edge','short-edge'):
        raise ValueError('نمط طباعة الوجهين غير صحيح.')
    if str(result['card.background_mode']).lower() not in ('fit','fill','stretch','crop','tile'):
        raise ValueError('طريقة عرض صورة القالب غير صحيحة.')
    if result.get('card.qr_mode') not in ('username','credentials','login_url'):
        raise ValueError('محتوى QR غير صالح.')
    if result.get('card.qr_mode') == 'login_url':
        from urllib.parse import urlsplit
        url = str(result.get('card.qr_login_url','')).strip()
        parts = urlsplit(url)
        if len(url)>2048 or parts.scheme not in ('http','https') or not parts.netloc:
            raise ValueError('رابط QR يجب أن يكون رابط http أو https صالحًا.')
    positions=result.get('card.positions')
    if not isinstance(positions,dict):raise ValueError('مواقع عناصر القالب غير صحيحة.')
    for value in positions.values():
        if not isinstance(value,(list,tuple)) or len(value)!=2 or any(
                not isinstance(n,(int,float)) or not math.isfinite(n) for n in value):
            raise ValueError('مواقع عناصر القالب يجب أن تحتوي إحداثيات رقمية صحيحة.')
    path = str(result.get('card.background_path') or '')
    if path:
        source = Path(path)
        if not source.is_file():
            from .db import DATA_DIR, BUNDLE_DIR
            candidate = BUNDLE_DIR/'assets'/'default_card.png' if source.name=='default_card.png' else DATA_DIR/'designs'/source.name
            if candidate.is_file():source=candidate
        if not source.is_file():
            raise ValueError('صورة القالب غير موجودة. اخترها مجددًا من صفحة التصميم: '+source.name)
        if source.stat().st_size > MAX_IMAGE_BYTES:
            raise ValueError('صورة القالب أكبر من 15 ميجابايت.')
        content = source.read_bytes()
        _verify_image(content)
        result['card.background_data'] = base64.b64encode(content).decode('ascii')
        result['card.background_name'] = source.name
        result['card.background_path'] = ''
    elif result.get('card.background_data'):
        encoded=result['card.background_data']
        if not isinstance(encoded,str) or len(encoded)>MAX_IMAGE_BYTES*4//3+4:
            raise ValueError('بيانات صورة القالب غير صالحة أو كبيرة جدًا.')
        try:content=base64.b64decode(encoded,validate=True)
        except (ValueError,TypeError) as exc:raise ValueError('بيانات صورة القالب تالفة.') from exc
        _verify_image(content)
    # The copy prevents positions/lists from leaking between package drafts.
    return json.loads(json.dumps(result, ensure_ascii=False, allow_nan=False))


def load_card_template(path, settings):
    """Validate a portable template completely before changing current settings."""
    path=Path(path)
    if path.stat().st_size > 22*1024*1024:
        raise ValueError('ملف القالب أكبر من 22 ميجابايت.')
    try:data=json.loads(path.read_text(encoding='utf-8-sig'))
    except (ValueError,UnicodeError) as exc:raise ValueError('تعذر قراءة ملف القالب؛ تأكد أنه ملف قالب حضرموت.') from exc
    if not isinstance(data,dict) or not any(key.startswith('card.') for key in data):
        raise ValueError('لا يحتوي الملف على إعدادات قالب كرت صالحة.')
    merged={key:value for key,value in settings.items() if not key.startswith('card.')}
    merged.update({key:value for key,value in data.items() if key.startswith('card.')})
    if merged.get('card.background_data'):merged['card.background_path']=''
    elif str(merged.get('card.background_path','')).replace('\\','/').startswith('//'):
        raise ValueError('لا يُسمح للقالب المستورد بتحميل صورة من مشاركة شبكة. اختر الصورة محليًا.')
    return package_snapshot(merged)


def sample_card(settings):
    from datetime import datetime
    from .cards import bytes_total, uptime_string
    prefix=str(settings.get('create.username_prefix','') or '')
    length=max(1,int(settings.get('create.code_length',8) or 8))
    username_chars=str(settings.get('create.username_chars','0123456789') or '1')
    sample_char='1' if '1' in username_chars else username_chars[0]
    username=(prefix+(sample_char*max(0,length-len(prefix))))[:length]
    card_format=settings.get('create.card_format','code_only')
    password=''
    if card_format=='code_same_password':password=username
    elif card_format=='code_password':
        chars=str(settings.get('create.password_chars','0123456789'))
        password=chars[0]*int(settings.get('create.password_length',6))
        if password==username:
            different=next((ch for ch in chars if ch!=chars[0]),None)
            if different is not None:password=different+password[1:]
            else:
                other_user_char=next((ch for ch in username_chars if ch!=chars[0]),None)
                if other_user_char is None or len(prefix)>=length:
                    raise ValueError('هذه الأحرف والطول لا تسمح بكلمة مرور مختلفة عن اسم المستخدم.')
                username=username[:-1]+other_user_char
    return dict(sequence='0001',username=username,password=password,
                created_at=datetime.now().isoformat(timespec='seconds'),
                price=settings.get('create.price',0),profile=settings.get('create.profile','default'),
                server=settings.get('create.server','all'),validity_days=settings.get('create.validity_days',0),
                limit_bytes=bytes_total(settings.get('create.limit_bytes',0),settings.get('create.limit_unit','MB')),
                limit_uptime=uptime_string(settings.get('create.limit_uptime_h',0),settings.get('create.limit_uptime_m',0)))
