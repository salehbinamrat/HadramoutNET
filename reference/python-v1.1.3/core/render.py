from __future__ import annotations

import os
import platform
import math
import unicodedata
from functools import lru_cache
from pathlib import Path
from urllib.parse import parse_qsl, quote, urlencode, urlsplit, urlunsplit

import qrcode
from PIL import Image, ImageDraw, ImageFont, ImageOps
from .packages import CARD_IMAGE_FORMATS, CARD_FONT_LIMITS, CARD_BORDER_LIMITS


APP_DIR = Path(__file__).resolve().parents[1]
BUNDLE_DIR = Path(getattr(__import__("sys"), "_MEIPASS", APP_DIR))
DEFAULT_BACKGROUND = BUNDLE_DIR / "assets" / "default_card.png"
REF_W, REF_H = 280.0, 130.0
MAX_RENDER_PIXELS = 32_000_000


class CardRenderError(ValueError):
    pass


def _number(value, fallback, minimum=None, maximum=None):
    try:
        result = float(value)
    except (TypeError, ValueError):
        result = float(fallback)
    if not math.isfinite(result):
        result = float(fallback)
    if minimum is not None:
        result = max(float(minimum), result)
    if maximum is not None:
        result = min(float(maximum), result)
    return result


def card_dimensions_mm(settings):
    # Keep dimensions positive but allow compact cards for dense A4 sheets.
    width = _number(settings.get("card.width_mm"), 50.0, 1, 300)
    height = _number(settings.get("card.height_mm"), 19.0, 1, 300)
    return width, height


def render_dimensions(settings, dpi=None, scale=None, target_size=None):
    width_mm, height_mm = card_dimensions_mm(settings)
    if target_size:
        width = max(1, int(target_size[0]))
        height = max(1, int(target_size[1]))
    elif scale is not None:
        width = max(64, int(REF_W * float(scale)))
        height = max(64, int(width * height_mm / width_mm))
    else:
        effective_dpi = _number(dpi or settings.get("card.dpi"), 300, 72, 600)
        width = max(64, int(round(width_mm / 25.4 * effective_dpi)))
        height = max(64, int(round(height_mm / 25.4 * effective_dpi)))
    if width * height > MAX_RENDER_PIXELS:
        raise CardRenderError("أبعاد الكرت وDPI ينتجان صورة كبيرة جدًا. خفّض DPI أو الأبعاد.")
    return width, height


@lru_cache(maxsize=16)
def _font_path(family="Arial", bold=False):
    family = str(family or "Arial")
    if platform.system() == "Windows":
        fonts = Path(os.environ.get("WINDIR", "C:/Windows")) / "Fonts"
        lowered = family.lower()
        if "tahoma" in lowered:
            candidates = ["tahomabd.ttf" if bold else "tahoma.ttf", "arial.ttf"]
        elif "segoe" in lowered:
            candidates = ["segoeuib.ttf" if bold else "segoeui.ttf", "arial.ttf"]
        else:
            candidates = ["arialbd.ttf" if bold else "arial.ttf", "tahoma.ttf"]
        for candidate in candidates:
            path = fonts / candidate
            if path.exists():
                return str(path)
    candidates = [
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"
        if bold
        else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/liberation2/LiberationSans-Bold.ttf"
        if bold
        else "/usr/share/fonts/truetype/liberation2/LiberationSans-Regular.ttf",
    ]
    for candidate in candidates:
        if Path(candidate).exists():
            return candidate
    return None


@lru_cache(maxsize=48)
def _font(size, family="Arial", bold=False):
    path = _font_path(family, bold)
    if path:
        try:
            return ImageFont.truetype(path, max(1, int(size)))
        except OSError:
            pass
    return ImageFont.load_default()


def _hex(color, fallback="#FFFFFF"):
    value = str(color or "")
    if len(value) == 9 and value.startswith("#"):
        # EasyVouch settings sometimes use #AARRGGBB.
        value = "#" + value[-6:]
    if len(value) in (4, 7) and value.startswith("#"):
        try:
            int(value[1:], 16)
            return value
        except ValueError:
            pass
    return fallback


def _background_path(path):
    candidate = Path(str(path or ""))
    if candidate.is_file():
        return candidate
    if DEFAULT_BACKGROUND.is_file():
        return DEFAULT_BACKGROUND
    return None


def _paste_background(base, path, mode, embedded=''):
    import base64
    import binascii
    import io
    try:
        source = io.BytesIO(base64.b64decode(embedded,validate=True)) if embedded and not path else _background_path(path)
        if source is None:
            return base
        with Image.open(source if hasattr(source,'read') else str(source), formats=CARD_IMAGE_FORMATS) as opened:
            if opened.width * opened.height > MAX_RENDER_PIXELS:
                raise CardRenderError('صورة الخلفية أكبر من 32 مليون بكسل.')
            background = ImageOps.exif_transpose(opened).convert("RGB")
        width, height = base.size
        normalized = str(mode or "stretch").lower()
        if normalized == "stretch":
            background = background.resize((width, height), Image.Resampling.LANCZOS)
        elif normalized in ("fill", "crop"):
            background = ImageOps.fit(
                background, (width, height), Image.Resampling.LANCZOS, centering=(0.5, 0.5)
            )
        elif normalized == "fit":
            background.thumbnail((width, height), Image.Resampling.LANCZOS)
            canvas = Image.new("RGB", (width, height), "white")
            canvas.paste(background, ((width - background.width) // 2, (height - background.height) // 2))
            background = canvas
        elif normalized == "tile":
            tiled = Image.new("RGB", (width, height), "white")
            for y in range(0, height, background.height):
                for x in range(0, width, background.width):
                    tiled.paste(background, (x, y))
            background = tiled
        else:
            background = background.resize((width, height), Image.Resampling.LANCZOS)
        base.paste(background, (0, 0))
    except (OSError, ValueError, binascii.Error, Image.DecompressionBombError) as exc:
        raise CardRenderError('تعذر تحميل خلفية الكرت؛ اختر صورة سليمة قبل الطباعة.') from exc
    return base


def _rtl_text(draw, position, text, font, fill, anchor="la", direction="rtl"):
    value = str(text)
    options = {"direction": direction, "language": "ar"}
    try:
        bounds = draw.textbbox(position,value,font=font,anchor=anchor,**options)
    except (TypeError, KeyError, ValueError):
        try:
            from ui.arabic import rtl

            if direction == 'rtl':
                value = rtl(value)
        except Exception:
            pass
        options = {}
        bounds = draw.textbbox(position,value,font=font,anchor=anchor)
    # Large advertised font sizes remain usable, but never allocate an
    # unbounded glyph mask before the clipping/preflight checks can run.
    if max(0,bounds[2]-bounds[0])*max(0,bounds[3]-bounds[1]) > MAX_RENDER_PIXELS:
        raise CardRenderError('حجم النص كبير جدًا للرسم الآمن؛ خفّض حجم الخط أو طول النص.')
    draw.text(position,value,font=font,fill=fill,anchor=anchor,**options)
    return value,options


def _price(value):
    try:
        return "%g" % float(value or 0)
    except (TypeError, ValueError):
        return str(value or "")


def qr_payload(settings, card):
    username = str(card.get("username", ""))
    password = str(card.get("password", ""))
    mode = str(settings.get("card.qr_mode", "credentials")).lower()
    if mode == "username":
        return username
    if mode == "login_url":
        base = str(settings.get("card.qr_login_url", "http://hotspot.local/login")).strip()
        if "{username}" in base or "{password}" in base:
            try:
                return base.replace('{username}', quote(username, safe='')).replace('{password}', quote(password, safe=''))
            except (KeyError, ValueError):
                pass
        parts = urlsplit(base)
        data = [(key, value) for key, value in parse_qsl(parts.query, keep_blank_values=True)
                if key not in ('username', 'password')]
        data.append(('username', username))
        if password:
            data.append(('password', password))
        query = urlencode(data)
        return urlunsplit((parts.scheme, parts.netloc, parts.path, query, parts.fragment))
    return username + (("|" + password) if password else "")


def prepare_card_base(settings, size):
    """Prepare the identical background/border once for an entire print job."""
    width, height = size
    base = Image.new("RGB", (width, height), "#07151A")
    base = _paste_background(
        base,
        settings.get("card.background_path", ""),
        settings.get("card.background_mode", "stretch"),
        settings.get("card.background_data", ""),
    )
    draw = ImageDraw.Draw(base)

    border = _hex(settings.get("card.border_color"), "#00C98D")
    border_width = max(0, int(_number(settings.get("card.border_width"), 1, *CARD_BORDER_LIMITS) * width / REF_W))
    if border_width > min(width,height)//2:
        raise CardRenderError('سمك الإطار أكبر من المساحة المتاحة في الكرت؛ خفّض السمك أو كبّر أبعاد الكرت.')
    radius = max(0, int(_number(settings.get("card.border_radius"), 0, 0, 100) * width / REF_W))
    if border_width:
        box = (
            border_width // 2,
            border_width // 2,
            width - border_width // 2 - 1,
            height - border_width // 2 - 1,
        )
        if radius:
            draw.rounded_rectangle(box, radius=radius, outline=border, width=border_width)
        else:
            draw.rectangle(box, outline=border, width=border_width)

    return base


def render_card(settings: dict, card=None, scale=None, target_size=None, dpi=None, prepared_base=None, layers=None):
    card = card or {
        "sequence": "0001",
        "username": "58372914",
        "password": "",
        "price": 10.5,
        "profile": "default",
        "server": "all",
        "validity_days": "7",
        "limit_uptime": "1h",
        "limit_bytes": 512 * 1024 ** 2,
        "created_at": "2026-08-29 18:00",
        "comment": "",
    }
    width, height = render_dimensions(settings, dpi=dpi, scale=scale, target_size=target_size)
    width_mm, height_mm = card_dimensions_mm(settings)
    base = prepared_base.copy() if prepared_base is not None and prepared_base.size == (width, height) else prepare_card_base(settings, (width, height))
    draw = ImageDraw.Draw(base)

    default_color = _hex(settings.get("card.font_color"), "#E7FFF7")
    family = str(settings.get("card.font_family", "Arial"))
    bold = bool(settings.get("card.font_bold", True))
    # 14 at the legacy reference width remains visually equivalent, while the
    # result now scales correctly for arbitrary card dimensions and DPI.
    base_size = max(1, int(_number(settings.get("card.font_size"), 14, *CARD_FONT_LIMITS) * width / REF_W))
    font = _font(base_size, family, bold)
    big = _font(max(1, int(base_size * 1.35)), family, True)
    small = _font(max(1, int(base_size * 0.75)), family, False)
    positions = settings.get("card.positions", {})
    if not isinstance(positions, dict):
        positions = {}

    def xy(name):
        raw = positions.get(name, [10, 10])
        try:
            x, y = float(raw[0]), float(raw[1])
        except (TypeError, ValueError, IndexError):
            x, y = 10.0, 10.0
        return int(max(0, min(REF_W, x)) / REF_W * width), int(
            max(0, min(REF_H, y)) / REF_H * height
        )

    def draw_text(name, value, enabled=True, font_obj=None):
        if not enabled:
            return
        base_font = font_obj or font
        factor = _number(settings.get("card.size.%s" % name), 1.0, 0.4, 4.0)
        if abs(factor - 1.0) > 0.001:
            source_size = getattr(base_font, "size", base_size)
            if base_font is big:
                element_bold = True
            elif base_font is small:
                element_bold = False
            else:
                element_bold = bold
            base_font = _font(max(1, int(round(source_size * factor))), family, element_bold)
        element_default = "#000000" if name in ("username", "password") else default_color
        # The card-number color is the single source for both credentials,
        # including previews/prints made with previously saved package settings.
        color_key = "card.color.username" if name == "password" else "card.color.%s" % name
        color = _hex(settings.get(color_key), element_default)
        position = xy(name)
        direction = 'rtl'
        if name in ('username', 'password'):
            direction = 'rtl' if any(unicodedata.bidirectional(ch) in ('R', 'AL') for ch in str(value)) else 'ltr'
        if layers is None:
            _rtl_text(draw, position, value, base_font, color, anchor="la", direction=direction)
        else:
            layer = Image.new('RGBA', (width, height), (0,0,0,0))
            layer_draw = ImageDraw.Draw(layer)
            displayed, text_options = _rtl_text(layer_draw, position, value, base_font, color, anchor="la", direction=direction)
            bounds = layer.getbbox()
            item = {'name':name, 'position':position,
                    'offset':(bounds[0]-position[0], bounds[1]-position[1]) if bounds else (0,0),
                    'image':layer.crop(bounds) if bounds else None}
            if name in ('username', 'password'):
                # Keep the exact credential separate from its shaped raster
                # appearance. PDF selection must never use OCR or shaped text.
                item['text'] = str(value)
                item['full_bounds'] = layer_draw.textbbox(position, displayed, font=base_font,
                                                        anchor='la', **text_options)
            layers.append(item)

    draw_text("title", settings.get("card.title_text", "شبكة حضرموت"), settings.get("card.add_title", False), big)
    draw_text("sequence", "#%s" % card.get("sequence", ""), settings.get("card.add_sequence", False), small)
    draw_text("username", card.get("username", ""), True, big)
    if card.get("password"):
        draw_text("password", card.get("password", ""), True, big)
    draw_text("price", _price(card.get("price", 0)), settings.get("card.add_price", False), big)
    if settings.get("card.add_capacity", False):
        from .cards import readable_bytes

        draw_text("capacity", readable_bytes(card.get("limit_bytes", 0)), True, font)
    draw_text("duration", card.get("limit_uptime", ""), settings.get("card.add_duration", False), font)
    draw_text(
        "validity",
        "%s يوم" % card.get("validity_days", ""),
        settings.get("card.add_validity", False),
        font,
    )
    if settings.get("card.add_date", False):
        date = str(card.get("created_at", "")).split("T")[0].split(" ")[0]
        draw_text("date", date, True, small)
    draw_text("phone", settings.get("card.phone_text", "رقم الهاتف"), settings.get("card.add_phone", False), small)
    draw_text("other", settings.get("card.other_text", "أخرى 1"), settings.get("card.add_other", False), small)
    draw_text("other2", settings.get("card.other2_text", "أخرى 2"), settings.get("card.add_other2", False), small)

    if settings.get("card.add_qr", False):
        qr = qrcode.QRCode(
            version=None,
            error_correction=qrcode.constants.ERROR_CORRECT_M,
            box_size=6,
            border=1,
        )
        qr.add_data(qr_payload(settings, card))
        qr.make(fit=True)
        qr_image = qr.make_image(fill_color="black", back_color="white").convert("RGB")
        qr_mm = _number(settings.get("card.qr_size_mm"), 24, 1, min(width_mm, height_mm))
        qr_size = max(1, min(width, height, int(qr_mm / width_mm * width)))
        qr_image = qr_image.resize((qr_size, qr_size), Image.Resampling.NEAREST)
        qr_x, qr_y = xy("qr")
        qr_x = max(0, min(width - qr_size, qr_x))
        qr_y = max(0, min(height - qr_size, qr_y))
        if layers is None:
            base.paste(qr_image, (qr_x, qr_y))
        else:
            layers.append({'name':'qr', 'position':(qr_x,qr_y), 'offset':(0,0), 'image':qr_image})
    return base


def card_to_tk_image(settings, card, max_size=(650, 330)):
    from PIL import ImageTk

    width_mm, height_mm = card_dimensions_mm(settings)
    ratio = width_mm / height_mm
    target_width = min(int(max_size[0]), int(max_size[1] * ratio))
    target_height = max(1, int(target_width / ratio))
    image = render_card(settings, card, target_size=(target_width, target_height))
    return ImageTk.PhotoImage(image)
