from __future__ import annotations

import ctypes
import os
import shutil
import tempfile
import platform
import math
from pathlib import Path
from PIL import Image

from reportlab.lib.pagesizes import A3, A4, landscape
from reportlab.lib.utils import ImageReader
from reportlab.pdfgen import canvas
from reportlab.pdfbase import pdfmetrics, pdfdoc

from .render import card_dimensions_mm, render_card, render_dimensions, prepare_card_base


MM = 72.0 / 25.4


class LayoutError(ValueError):
    pass


def _layout_number(value, label, minimum, maximum, integer=False):
    try:
        result = float(value)
    except (TypeError, ValueError, OverflowError):
        raise LayoutError('قيمة غير صالحة: ' + label) from None
    if (not math.isfinite(result) or not minimum <= result <= maximum or
            (integer and not result.is_integer())):
        raise LayoutError('قيمة غير صالحة: ' + label)
    return int(result) if integer else result


def _layout_spacing(gap_h_mm, gap_v_mm, page_margin_mm):
    return (_layout_number(gap_h_mm, 'الفاصل الأفقي', 0, 300),
            _layout_number(gap_v_mm, 'الفاصل الرأسي', 0, 300),
            _layout_number(page_margin_mm, 'هامش الورقة', 0, 300))


def unique_print_cards(cards):
    """Refuse an accidental duplicate voucher before opening a printer job."""
    cards = list(cards)
    names = set()
    for card in cards:
        name = str(card['username'])
        if not name.strip() or name in names:
            raise LayoutError('توجد أسماء مستخدمين فارغة أو مكررة في طلب الطباعة؛ لم تبدأ الطباعة.')
        names.add(name)
    return cards


def page_size(name="A4", orientation="portrait"):
    if str(name).upper() not in ('A4', 'A3') or orientation not in ('portrait', 'landscape'):
        raise LayoutError('حجم الورق أو اتجاهه غير صالح.')
    selected = A3 if str(name).upper() == "A3" else A4
    return landscape(selected) if orientation == "landscape" else selected


def auto_grid(settings, paper="A4", orientation="portrait", gap_h_mm=0.5, gap_v_mm=0.5, page_margin_mm=0.5):
    gap_h_mm, gap_v_mm, page_margin_mm = _layout_spacing(gap_h_mm, gap_v_mm, page_margin_mm)
    page_width, page_height = page_size(paper, orientation)
    card_width_mm, card_height_mm = card_dimensions_mm(settings)
    available_width = page_width / MM - 2 * float(page_margin_mm)
    available_height = page_height / MM - 2 * float(page_margin_mm)
    columns = int((available_width + float(gap_h_mm)) // (card_width_mm + float(gap_h_mm)))
    rows = int((available_height + float(gap_v_mm)) // (card_height_mm + float(gap_v_mm)))
    return max(1, rows), max(1, columns)


def calculate_layout(
    settings,
    rows=15,
    columns=4,
    paper="A4",
    orientation="portrait",
    gap_h_mm=0.5,
    gap_v_mm=0.5,
    page_margin_mm=0.5,
):
    rows = _layout_number(rows, 'عدد الصفوف', 1, 1000, integer=True)
    columns = _layout_number(columns, 'عدد الأعمدة', 1, 1000, integer=True)
    gap_h_mm, gap_v_mm, page_margin_mm = _layout_spacing(gap_h_mm, gap_v_mm, page_margin_mm)
    page_width, page_height = page_size(paper, orientation)
    card_width_mm, card_height_mm = card_dimensions_mm(settings)
    card_width = card_width_mm * MM
    card_height = card_height_mm * MM
    gap_x = gap_h_mm * MM
    gap_y = gap_v_mm * MM
    page_margin = page_margin_mm * MM
    grid_width = columns * card_width + (columns - 1) * gap_x
    grid_height = rows * card_height + (rows - 1) * gap_y
    available_width = page_width - 2 * page_margin
    available_height = page_height - 2 * page_margin
    if grid_width > available_width + 0.1 or grid_height > available_height + 0.1:
        suggested_rows, suggested_columns = auto_grid(
            settings, paper, orientation, gap_h_mm, gap_v_mm, page_margin_mm
        )
        raise LayoutError(
            "التخطيط %d×%d أكبر من مساحة ورقة %s. المقترح لهذه الأبعاد: %d صف × %d عمود."
            % (rows, columns, str(paper).upper(), suggested_rows, suggested_columns)
        )
    start_x = (page_width - grid_width) / 2.0
    top_y = page_height - (page_height - grid_height) / 2.0
    return {
        "page_size": (page_width, page_height),
        "rows": rows,
        "columns": columns,
        "card_width": card_width,
        "card_height": card_height,
        "gap_x": gap_x,
        "gap_y": gap_y,
        "start_x": start_x,
        "top_y": top_y,
        "per_page": rows * columns,
        "grid_width": grid_width,
        "grid_height": grid_height,
    }


def print_plan(
    settings,
    quantity,
    rows=15,
    columns=4,
    paper="A4",
    orientation="portrait",
    gap_h_mm=0.5,
    gap_v_mm=0.5,
    page_margin_mm=0.5,
    duplex="single",
):
    """Return physical-sheet capacity/count and generated PDF page count.

    Keeping this calculation in the printing core guarantees that the UI, PDF
    preview and final print path all agree on the same layout mathematics.
    """
    _validate_print_modes(duplex=duplex)
    try:
        quantity = max(0, int(quantity or 0))
    except (TypeError, ValueError):
        raise LayoutError("عدد الكروت غير صالح.")
    layout = calculate_layout(
        settings, rows, columns, paper, orientation, gap_h_mm, gap_v_mm, page_margin_mm
    )
    per_sheet = int(layout["per_page"])
    sheets = (quantity + per_sheet - 1) // per_sheet if quantity else 0
    pdf_pages = sheets * (2 if str(duplex or "single") != "single" else 1)
    return {
        "layout": layout,
        "per_sheet": per_sheet,
        "sheets": sheets,
        "pdf_pages": pdf_pages,
    }


def _slot(layout, index, order):
    columns = layout["columns"]
    row = index // columns
    column = index % columns
    if order in ('column_left','column_right'):
        column=index//layout['rows'];row=index%layout['rows']
        if order=='column_right':column=columns-1-column
    if order == "right_to_left":
        column = columns - 1 - column
    elif order == "snake" and row % 2 == 1:
        column = columns - 1 - column
    x = layout["start_x"] + column * (layout["card_width"] + layout["gap_x"])
    y = layout["top_y"] - layout["card_height"] - row * (
        layout["card_height"] + layout["gap_y"]
    )
    return x, y


def _validate_print_modes(order='left_to_right', duplex='single'):
    if order not in ('left_to_right', 'right_to_left', 'snake', 'column_left', 'column_right'):
        raise LayoutError('ترتيب الكروت غير صالح.')
    if duplex not in ('single', 'long-edge', 'short-edge'):
        raise LayoutError('إعداد الطباعة على الوجهين غير صالح.')


def card_pages(cards,layout,order,duplex,orientation):
    """Single source for PDF/native page order and duplex slot mirroring."""
    _validate_print_modes(order, duplex)
    for start in range(0,len(cards),layout['per_page']):
        group=cards[start:start+layout['per_page']]
        for back in range(2 if duplex!='single' else 1):
            page=[]
            for i,card in enumerate(group):
                x,y=_slot(layout,i,order)
                if back:
                    if (duplex=='long-edge')==(orientation=='portrait'):
                        x=layout['page_size'][0]-x-layout['card_width']
                    else:y=layout['page_size'][1]-y-layout['card_height']
                page.append((card,x,y))
            yield page


def export_settings_pdf(settings,cards,path,progress=None):
    """Export one captured settings snapshot without repeating UI argument maps."""
    return export_cards_pdf(settings,cards,path,settings['print.rows'],settings['print.columns'],
        settings['print.paper'],settings['print.orientation'],settings['print.gap_h_mm'],
        settings['print.gap_v_mm'],settings['print.order'],settings.get('print.show_borders',True),
        settings['print.page_margin_mm'],settings.get('print.cut_marks',False),progress=progress)


def _draw_cut_marks(pdf, x, y, width, height, length=3 * MM):
    pdf.setLineWidth(0.25)
    # Bottom left / right and top left / right, outside the card edge.
    for corner_x in (x, x + width):
        pdf.line(corner_x - length, y, corner_x + length, y)
        pdf.line(corner_x - length, y + height, corner_x + length, y + height)
    for corner_y in (y, y + height):
        pdf.line(x, corner_y - length, x, corner_y + length)
        pdf.line(x + width, corner_y - length, x + width, corner_y + length)


class _UnicodeCredentialText:
    """Map one invisible glyph to an exact Unicode credential, per document.

    A whole-field ToUnicode mapping keeps Arabic digits and mixed scripts from
    being reordered as individually drawn glyphs during text extraction. The
    underlying glyph is never painted (render mode 3). Standard Helvetica
    supplies consistent selection heights without additional font files.
    No credentials persist between exports.
    """
    def __init__(self):
        self.values = []
        self.tokens = {}

    def token(self, value, glyph_width):
        key = (value, glyph_width)
        if key not in self.tokens:
            index = len(self.values)
            self.values.append(key)
            self.tokens[key] = ('NidiUC%d' % (index // 250 + 1), index % 250 + 1)
        return self.tokens[key]

    def add_fonts(self, pdf):
        doc = pdf._doc
        for start in range(0, len(self.values), 250):
            values = self.values[start:start + 250]
            name = 'NidiUC%d' % (start // 250 + 1)
            cmap = [
                '/CIDInit /ProcSet findresource begin', '12 dict begin', 'begincmap',
                '/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def',
                '/CMapName /%s def' % name, '/CMapType 2 def',
                '1 begincodespacerange', '<01> <FF>', 'endcodespacerange',
            ]
            for offset in range(0, len(values), 100):
                group = values[offset:offset + 100]
                cmap.append('%d beginbfchar' % len(group))
                cmap.extend('<%02X> <%s>' % (offset + i + 1, value.encode('utf-16-be').hex().upper())
                            for i, (value, glyph_width) in enumerate(group))
                cmap.append('endbfchar')
            cmap.extend(['endcmap', 'CMapName currentdict /CMap defineresource pop', 'end', 'end'])
            cmap_stream = pdfdoc.PDFStream(content='\n'.join(cmap).encode('ascii'),
                                           filters=[pdfdoc.PDFZCompress])
            font = pdfdoc.PDFDictionary({
                'Type': pdfdoc.PDFName('Font'), 'Subtype': pdfdoc.PDFName('Type1'),
                'Name': pdfdoc.PDFName(name), 'BaseFont': pdfdoc.PDFName('Helvetica'),
                'Encoding': pdfdoc.PDFDictionary({
                    'Type': pdfdoc.PDFName('Encoding'),
                    'Differences': pdfdoc.PDFArray([1] + [pdfdoc.PDFName('A')] * len(values)),
                }),
                'FirstChar': 1, 'LastChar': len(values),
                'Widths': pdfdoc.PDFArray([width for value, width in values]),
                'ToUnicode': doc.Reference(cmap_stream),
            })
            # This is the same document font-resource integration used by
            # ReportLab's TTFont.addObjects in the bundled 4.0.9 runtime.
            doc.idToObject['BasicFonts'].dict[name] = doc.Reference(font)


def validate_credential_layout(layers, image_size):
    """Use the same credential safety rules for preview, PDF and GDI output."""
    width, height = image_size
    qr_bounds = []
    credential_bounds=[]
    for layer in layers:
        if layer['name'] == 'qr' and layer['image'] is not None:
            px, py = layer['position']
            ox, oy = layer['offset']
            qr_bounds.append((px + ox, py + oy, px + ox + layer['image'].width,
                              py + oy + layer['image'].height))
    for layer in layers:
        if layer['name'] not in ('username', 'password'):
            continue
        value = layer['text']
        image = layer['image']
        bounds = layer['full_bounds']
        if (image is None or not value.strip() or bounds[0] < 0 or bounds[1] < 0 or
                bounds[2] > width or bounds[3] > height):
            label = 'رقم الكرت' if layer['name'] == 'username' else 'كلمة المرور'
            raise LayoutError('%s خارج حدود الكرت أو غير ظاهر بالكامل؛ عدّل موضعه أو حجم الخط ثم احفظ PDF مجددًا.' % label)
        px, py = layer['position']
        ox, oy = layer['offset']
        left, top = px + ox, py + oy
        if any(left < qr[2] and left + image.width > qr[0] and
               top < qr[3] and top + image.height > qr[1] for qr in qr_bounds):
            raise LayoutError('رمز QR يغطي رقم الكرت أو كلمة المرور؛ عدّل موضعه أو مقاسه ثم احفظ PDF مجددًا.')
        if any(bounds[0] < other[2] and bounds[2] > other[0] and
               bounds[1] < other[3] and bounds[3] > other[1] for other in credential_bounds):
            raise LayoutError('رقم الكرت وكلمة المرور متداخلان؛ افصل موضعيهما أو عدّل حجم الخط قبل الطباعة أو حفظ PDF.')
        credential_bounds.append(bounds)
    if not any(layer['name']=='username' for layer in layers):
        raise LayoutError('رقم الكرت غير ظاهر؛ لم تبدأ الطباعة.')


def preflight_cards(settings, cards, prepared_base=None, progress=None):
    """Validate every card before opening a physical print job; bounded memory."""
    base=prepared_base if prepared_base is not None else prepare_card_base(settings,render_dimensions(settings))
    for index,card in enumerate(cards,1):
        layers=[]
        render_card(settings,dict(card),prepared_base=base,layers=layers)
        validate_credential_layout(layers,base.size)
        if progress and (index % 50 == 0 or index==len(cards)):
            progress('preflight',index,len(cards))


def _draw_copyable_credentials(pdf, layers, image_size, layout, x, y, unicode_text):
    """Add selectable text over the validated raster credential rectangles."""
    validate_credential_layout(layers,image_size)
    width,height=image_size
    sx,sy=layout['card_width']/width,layout['card_height']/height
    for layer in layers:
        if layer['name'] not in ('username','password'):
            continue
        value=layer['text'];image=layer['image']
        px,py=layer['position'];ox,oy=layer['offset']
        left,top=px+ox,py+oy
        bottom_y = y + (height - top - image.height) * sy
        pdf.saveState()
        try:
            if all(32 <= ord(char) < 127 for char in value):
                ascent, descent = pdfmetrics.getAscentDescent('Helvetica')
                size = image.height * sy * 1000.0 / (ascent - descent)
                text_width = pdfmetrics.stringWidth(value, 'Helvetica', size)
                text = pdf.beginText(left * sx + x, bottom_y - descent * size / 1000.0)
                text.setFont('Helvetica', size)
                text.setHorizScale(image.width * sx * 100.0 / text_width)
                text.setTextRenderMode(3)  # selectable/searchable, no painted glyphs
                # Explicit text-line boundaries keep username-only cards on
                # the same physical row separate in clipboard/extraction.
                text.textLine(value)
                pdf.drawText(text)
            else:
                ascent, descent = pdfmetrics.getAscentDescent('Helvetica')
                size = image.height * sy * 1000.0 / (ascent - descent)
                glyph_width = image.width * sx * 1000.0 / size
                name, code = unicode_text.token(value, glyph_width)
                pdf.addLiteral('BT /%s %.8f Tf %.8f TL 100 Tz 3 Tr 1 0 0 1 %.8f %.8f Tm <%02X> Tj T* ET' %
                               (name, size, size * 1.2, left * sx + x,
                                bottom_y - descent * size / 1000.0, code))
        finally:
            pdf.restoreState()


def export_cards_pdf(
    settings,
    cards,
    output_path,
    rows=15,
    columns=4,
    paper="A4",
    orientation="portrait",
    margin_h_mm=0.5,
    margin_v_mm=0.5,
    order="left_to_right",
    show_borders=True,
    page_margin_mm=0.5,
    cut_marks=False,
    progress=None,
):
    cards = unique_print_cards(cards)
    if not cards:
        raise LayoutError("لا توجد كروت لتصديرها.")
    _validate_print_modes(order, settings.get('print.duplex', 'single'))
    layout = calculate_layout(
        settings,
        rows,
        columns,
        paper,
        orientation,
        margin_h_mm,
        margin_v_mm,
        page_margin_mm,
    )
    output_path = Path(output_path)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix=".nidi-pdf-", dir=str(output_path.parent)) as folder:
        staged_path = Path(folder) / "cards.pdf"
        prepared_base = prepare_card_base(settings, render_dimensions(settings))
        background = ImageReader(prepared_base)
        pdf = canvas.Canvas(str(staged_path), pagesize=layout["page_size"], pageCompression=1)
        pdf.setTitle("Hadramout Network Cards")
        pdf.setAuthor("Hadramout Network Cards")
        pdf.setSubject("MikroTik voucher cards")
        unicode_text = _UnicodeCredentialText()

        def draw(card,x,y):
            # Share the photographic template across the whole document. Only
            # sparse text/QR pixels differ per voucher; never embed the complete
            # background again for every username in a large batch.
            layers=[]
            render_card(settings, dict(card), prepared_base=prepared_base,layers=layers)
            pdf.drawImage(
                background,
                x,
                y,
                width=layout["card_width"],
                height=layout["card_height"],
                preserveAspectRatio=False,
                mask="auto",
            )
            overlay=Image.new('RGBA',prepared_base.size,(0,0,0,0))
            for layer in layers:
                image=layer['image']
                if image is not None:
                    px,py=layer['position'];ox,oy=layer['offset']
                    overlay.alpha_composite(image.convert('RGBA'),(px+ox,py+oy))
            bounds = overlay.getbbox()
            if bounds:
                left, top, right, bottom = bounds
                sx, sy = layout['card_width']/overlay.width, layout['card_height']/overlay.height
                pdf.drawImage(ImageReader(overlay.crop(bounds)),x+left*sx,
                    y+(overlay.height-bottom)*sy,width=(right-left)*sx,
                    height=(bottom-top)*sy,preserveAspectRatio=False,mask='auto')
            _draw_copyable_credentials(pdf, layers, prepared_base.size, layout, x, y, unicode_text)
            if show_borders:
                pdf.setLineWidth(0.35)
                pdf.rect(x, y, layout["card_width"], layout["card_height"], stroke=1, fill=0)
            if cut_marks:
                _draw_cut_marks(pdf, x, y, layout["card_width"], layout["card_height"])
        for number,page in enumerate(card_pages(cards,layout,order,settings.get('print.duplex','single'),orientation)):
            if number:pdf.showPage()
            for card,x,y in page:draw(card,x,y)
            if progress:
                total=(len(cards)+layout['per_page']-1)//layout['per_page']
                progress('pdf',number+1,total*(2 if settings.get('print.duplex','single')!='single' else 1))
        unicode_text.add_fonts(pdf)
        pdf.save()
        os.replace(str(staged_path), str(output_path))
    return str(output_path)


def save_pdf_copy(source_path, destination_path):
    """Atomically save a user-selected copy of an already rendered PDF."""
    source = Path(source_path).resolve()
    destination = Path(destination_path).expanduser().resolve()
    if not source.is_file():
        raise FileNotFoundError("ملف PDF الناتج غير موجود.")
    if source == destination:
        return str(destination)
    destination.parent.mkdir(parents=True, exist_ok=True)
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(
            prefix=".nidi-print-", suffix=".pdf.tmp", dir=str(destination.parent), delete=False
        ) as stream:
            temporary = Path(stream.name)
            with source.open("rb") as original:
                shutil.copyfileobj(original, stream, length=1024 * 1024)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(str(temporary), str(destination))
    finally:
        if temporary is not None and temporary.exists():
            try:
                temporary.unlink()
            except OSError:
                pass
    return str(destination)


def windows_printers():
    from .windows_printing import PDF_DESTINATION
    if platform.system() != "Windows":
        return [PDF_DESTINATION]
    try:
        from ctypes import wintypes

        winspool = ctypes.WinDLL("winspool.drv")
        winspool.EnumPrintersW.argtypes = [wintypes.DWORD,wintypes.LPWSTR,wintypes.DWORD,
            ctypes.c_void_p,wintypes.DWORD,ctypes.POINTER(wintypes.DWORD),ctypes.POINTER(wintypes.DWORD)]
        winspool.EnumPrintersW.restype = wintypes.BOOL
        flags = 2 | 4  # PRINTER_ENUM_LOCAL | PRINTER_ENUM_CONNECTIONS
        needed = wintypes.DWORD()
        returned = wintypes.DWORD()
        winspool.EnumPrintersW(
            flags, None, 4, None, 0, ctypes.byref(needed), ctypes.byref(returned)
        )
        if not needed.value:
            return [PDF_DESTINATION]
        buffer = (ctypes.c_byte * needed.value)()
        ok = winspool.EnumPrintersW(
            flags,
            None,
            4,
            ctypes.cast(buffer, ctypes.POINTER(ctypes.c_byte)),
            needed.value,
            ctypes.byref(needed),
            ctypes.byref(returned),
        )
        if not ok:
            return [PDF_DESTINATION]

        class PrinterInfo4(ctypes.Structure):
            _fields_ = [
                ("pPrinterName", wintypes.LPWSTR),
                ("pServerName", wintypes.LPWSTR),
                ("Attributes", wintypes.DWORD),
            ]

        array = ctypes.cast(buffer, ctypes.POINTER(PrinterInfo4))
        names = [array[i].pPrinterName for i in range(returned.value) if array[i].pPrinterName]
        return [PDF_DESTINATION]+sorted(set(names), key=str.lower)
    except Exception:
        return [PDF_DESTINATION]
