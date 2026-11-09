"""Windows GDI voucher printing. No PDF association or external viewer required."""
import ctypes as C
import platform
import struct
import math

from PIL import ImageDraw


PDF_DESTINATION = 'حفظ كملف PDF'


def pdf_destination(name):
    return str(name or '').strip().casefold() in (
        PDF_DESTINATION.casefold(), 'microsoft print to pdf')


def configure_devmode(buffer, settings):
    """Modify the public DEVMODEW prefix, preserving driver-private bytes."""
    if len(buffer) < 96 or struct.unpack_from('<H', buffer, 68)[0] < 96:
        raise RuntimeError('تعريف الطابعة أعاد إعدادات غير صالحة.')
    fields = struct.unpack_from('<I', buffer, 72)[0]
    # Explicit paper size takes precedence over a saved custom width/length/form.
    fields = (fields & ~(0x4 | 0x8 | 0x10000)) | 0x1 | 0x2 | 0x10 | 0x100 | 0x1000
    struct.pack_into('<I', buffer, 72, fields)
    struct.pack_into('<hh', buffer, 76,
                     2 if settings.get('print.orientation') == 'landscape' else 1,
                     8 if settings.get('print.paper') == 'A3' else 9)
    struct.pack_into('<h', buffer, 84, 100)  # dmScale: actual size
    struct.pack_into('<h', buffer, 86, 1)    # dmCopies: avoid inherited multiple copies
    struct.pack_into('<h', buffer, 94,
                     {'single': 1, 'long-edge': 2, 'short-edge': 3}[settings.get('print.duplex', 'single')])


def validate_devmode(buffer, settings):
    """Validate what the driver accepted, not just the values we requested."""
    if len(buffer)<96 or struct.unpack_from('<H',buffer,68)[0]<96:
        raise RuntimeError('تعريف الطابعة أعاد إعدادات غير صالحة.')
    fields=struct.unpack_from('<I',buffer,72)[0]
    if fields & 0x100 and struct.unpack_from('<h',buffer,86)[0]!=1:
        raise RuntimeError('تعريف الطابعة لم يؤكد نسخة واحدة؛ لم تبدأ الطباعة لتجنب تكرار الكروت.')
    if fields & 0x10 and struct.unpack_from('<h',buffer,84)[0]!=100:
        raise RuntimeError('تعريف الطابعة لم يقبل المقاس الفعلي 100%؛ لم تبدأ الطباعة.')
    requested={'single':1,'long-edge':2,'short-edge':3}[settings.get('print.duplex','single')]
    actual=struct.unpack_from('<h',buffer,94)[0]
    # dmDuplex is meaningful only when the driver advertises DM_DUPLEX.
    if (requested!=1 and not fields & 0x1000) or (fields & 0x1000 and actual!=requested):
        raise RuntimeError('الطابعة لا تدعم نمط الوجهين المختار. اختر وجهًا واحدًا أو احفظ PDF.')


class DocInfo(C.Structure):
    _fields_ = [('cbSize', C.c_int), ('lpszDocName', C.c_wchar_p),
                ('lpszOutput', C.c_wchar_p), ('lpszDatatype', C.c_wchar_p),
                ('fwType', C.c_uint32)]


def _bind(dll, name, result, args):
    fn = getattr(dll, name);fn.restype = result;fn.argtypes = args
    return fn


class WindowsPrinter:
    """One local printer DC. Abort on failure; never retry an uncertain job."""
    def __init__(self, name, settings):
        if platform.system() != 'Windows':
            raise RuntimeError('الطباعة المباشرة متاحة على Windows. استخدم حفظ PDF على هذا النظام.')
        self.dc = None;self.active = False
        self.spool = C.WinDLL('winspool.drv', use_last_error=True)
        self.gdi = C.WinDLL('gdi32', use_last_error=True)
        ptr=C.c_void_p;integer=C.c_int;word=C.c_uint32;wide=C.c_wchar_p
        _bind(self.spool,'GetDefaultPrinterW',integer,[wide,C.POINTER(word)])
        _bind(self.spool,'OpenPrinterW',integer,[wide,C.POINTER(ptr),ptr])
        _bind(self.spool,'ClosePrinter',integer,[ptr])
        _bind(self.spool,'DocumentPropertiesW',C.c_int32,[ptr,ptr,wide,ptr,ptr,word])
        _bind(self.gdi,'CreateDCW',ptr,[wide,wide,wide,ptr])
        for fn in ('DeleteDC','StartPage','EndPage','EndDoc','AbortDoc'):
            _bind(self.gdi,fn,integer,[ptr])
        _bind(self.gdi,'StartDocW',integer,[ptr,C.POINTER(DocInfo)])
        _bind(self.gdi,'GetDeviceCaps',integer,[ptr,integer])
        _bind(self.gdi,'SetStretchBltMode',integer,[ptr,integer])
        _bind(self.gdi,'StretchDIBits',integer,
              [ptr]+[integer]*8+[ptr,ptr,word,word])
        _bind(self.gdi,'MoveToEx',integer,[ptr,integer,integer,ptr])
        _bind(self.gdi,'LineTo',integer,[ptr,integer,integer])
        self.name = str(name or '').strip() or self.default_name()
        if pdf_destination(self.name):return
        handle=ptr()
        if not self.spool.OpenPrinterW(self.name,C.byref(handle),None):
            raise self.error('تعذر فتح الطابعة «%s». تحقق من اسمها واتصالها.' % self.name)
        try:
            size=self.spool.DocumentPropertiesW(None,handle,self.name,None,None,0)
            if size < 96 or size > 1024*1024:raise self.error('تعذر قراءة إعدادات الطابعة.')
            mode=C.create_string_buffer(size)
            if self.spool.DocumentPropertiesW(None,handle,self.name,mode,None,2) != 1:
                raise self.error('تعذر تحميل إعدادات الطابعة.')
            configure_devmode(mode,settings)
            if self.spool.DocumentPropertiesW(None,handle,self.name,mode,mode,10) != 1:
                raise self.error('تعذر تطبيق حجم الورق واتجاهه على الطابعة.')
            validate_devmode(mode,settings)
            self.dc=self.gdi.CreateDCW('WINSPOOL',self.name,None,mode)
            if not self.dc:raise self.error('تعذر فتح محرك الطباعة.')
        finally:
            self.spool.ClosePrinter(handle)

    @staticmethod
    def error(message):
        code=C.get_last_error()
        return RuntimeError('%s (Windows %s)' % (message,code))

    def default_name(self):
        size=C.c_uint32()
        self.spool.GetDefaultPrinterW(None,C.byref(size))
        if not 0 < size.value < 32768:
            raise RuntimeError('لا توجد طابعة افتراضية. اختر طابعة مثبتة أو «حفظ كملف PDF».')
        name=C.create_unicode_buffer(size.value)
        if not self.spool.GetDefaultPrinterW(name,C.byref(size)):
            raise self.error('تعذر قراءة اسم الطابعة الافتراضية.')
        return name.value

    def geometry(self):
        # dpi, physical page, hardware origin, printable extent (device pixels).
        return tuple(self.gdi.GetDeviceCaps(self.dc,k) for k in (88,90,110,111,112,113,8,10))

    def start(self):
        doc=DocInfo(C.sizeof(DocInfo),'Hadramout voucher cards',None,None,0)
        if self.gdi.StartDocW(self.dc,C.byref(doc)) <= 0:
            raise self.error('تعذر بدء مهمة الطباعة؛ تحقق من خدمة الطباعة وتعريف الطابعة.')
        self.active=True

    def begin_page(self):
        if self.gdi.StartPage(self.dc) <= 0:raise self.error('تعذر بدء صفحة الطباعة.')
        self.gdi.SetStretchBltMode(self.dc,3)  # COLORONCOLOR: crisp QR edges

    def draw(self,image,box):
        width,height=image.size
        stride=(width*3+3)&~3
        pixels=image.convert('RGB').tobytes('raw','BGR',stride,-1)
        bitmap=struct.pack('<IiiHHIIiiII',40,width,height,1,24,0,len(pixels),0,0,0,0)
        data=C.create_string_buffer(pixels);info=C.create_string_buffer(bitmap)
        x,y,w,h=box
        copied=self.gdi.StretchDIBits(self.dc,x,y,w,h,0,0,width,height,data,info,0,0x00CC0020)
        if copied in (0,-1):raise self.error('تعذر رسم الكرت على صفحة الطابعة.')

    def line(self,x1,y1,x2,y2):
        if not self.gdi.MoveToEx(self.dc,x1,y1,None) or not self.gdi.LineTo(self.dc,x2,y2):
            raise self.error('تعذر رسم علامات القص.')

    def end_page(self):
        if self.gdi.EndPage(self.dc) <= 0:raise self.error('تعذر تسليم صفحة الطباعة.')

    def finish(self):
        if self.gdi.EndDoc(self.dc) <= 0:raise self.error('تعذر تأكيد تسليم مهمة الطباعة.')
        self.active=False

    def close(self):
        if self.dc:
            try:
                if self.active:self.gdi.AbortDoc(self.dc)
            finally:
                self.gdi.DeleteDC(self.dc);self.dc=None;self.active=False


def _validate_geometry(page,geometry):
    dx,dy,pw,ph,ox,oy,rw,rh=geometry
    if (not all(math.isfinite(v) for v in geometry) or
            min(dx,dy,pw,ph,rw,rh) <= 0 or min(ox,oy) < 0 or
            ox+rw>pw+1 or oy+rh>ph+1):
        raise RuntimeError('تعريف الطابعة أعاد أبعادًا غير صالحة.')
    expected_w,expected_h=page
    if abs(pw*72/dx-expected_w)>6 or abs(ph*72/dy-expected_h)>6:
        raise RuntimeError('تعريف الطابعة لم يقبل حجم الورق أو اتجاهه. راجع الورق المختار وإعدادات الطابعة.')


def printer_transform(layout,geometry,cut_marks=False):
    _validate_geometry(layout['page_size'],geometry)
    dx,dy,pw,ph,ox,oy,rw,rh=geometry
    expected_w,expected_h=layout['page_size']
    margin=3*72/25.4 if cut_marks else 0
    left=(layout['start_x']-margin)*dx/72-ox
    top=(expected_h-layout['top_y']-margin)*dy/72-oy
    right=left+(layout['grid_width']+2*margin)*dx/72
    bottom=top+(layout['grid_height']+2*margin)*dy/72
    if left < -1 or top < -1 or right > rw+1 or bottom > rh+1:
        raise RuntimeError('التخطيط المحفوظ %s أعمدة × %s صفوف يتجاوز مساحة الطابعة الفعلية عند موضعه المحدد.\n'
            'مساحة شبكة الكروت مع علامات القص: %.1f × %.1f مم؛ مساحة الطابعة: %.1f × %.1f مم.\n'
            'لم يتغير عدد الصفوف أو الأعمدة أو مقاس الكرت، ولم تبدأ الطباعة. '
            'اختر ورقًا أو اتجاهًا مناسبًا، أو عدّل الأبعاد والفواصل بنفسك ثم احفظ الإعدادات.' %
            (layout['columns'],layout['rows'],(layout['grid_width']+2*margin)*25.4/72,
             (layout['grid_height']+2*margin)*25.4/72,rw*25.4/dx,rh*25.4/dy))
    def transform(x,y,w,h):
        return (round(x*dx/72-ox),round((expected_h-y-h)*dy/72-oy),
                max(1,round(w*dx/72)),max(1,round(h*dy/72)))
    return transform


def print_cards(settings,cards,printer_name='',*,pdf_path=None,details=None,progress=None):
    """Return True only after EndDoc; False means the selected PDF destination."""
    from .printing import calculate_layout, card_pages, _draw_cut_marks, export_settings_pdf, unique_print_cards, preflight_cards
    from .render import render_card, prepare_card_base, render_dimensions
    cards=unique_print_cards(cards)
    if not cards:raise ValueError('لا توجد كروت للطباعة.')
    settings=dict(settings);printer=None;pdf_written=False;pages_submitted=0;started=False
    try:
        if not pdf_destination(printer_name):printer=WindowsPrinter(printer_name,settings)
        physical=printer is not None and not pdf_destination(printer.name)
        if physical:
            geometry=printer.geometry()
        layout=calculate_layout(settings,settings['print.rows'],settings['print.columns'],
            settings['print.paper'],settings['print.orientation'],settings['print.gap_h_mm'],
            settings['print.gap_v_mm'],settings['print.page_margin_mm'])
        if physical:transform=printer_transform(layout,geometry,settings.get('print.cut_marks',False))
        if details is not None:
            details.update({'per_sheet':layout['per_page'],
                'sheets':(len(cards)+layout['per_page']-1)//layout['per_page']})
        if pdf_path is not None:
            export_settings_pdf(settings,cards,pdf_path,progress=progress);pdf_written=True
        if not physical:return False
        # Bounded raster size; physical size always comes from the mm layout.
        rendering=dict(settings);rendering['card.dpi']=min(300,int(settings.get('card.dpi',300)))
        base=prepare_card_base(rendering,render_dimensions(rendering))
        preflight_cards(rendering,cards,prepared_base=base,progress=progress)
        class CutMarks:
            def setLineWidth(self,_width):pass
            def line(self,x1,y1,x2,y2):
                a,b,_,_=transform(x1,y1,0,0);c,d,_,_=transform(x2,y2,0,0)
                printer.line(a,b,c,d)
        printer.start();started=True
        total_pages=((len(cards)+layout['per_page']-1)//layout['per_page'])*(2 if settings.get('print.duplex','single')!='single' else 1)
        for page in card_pages(cards,layout,settings['print.order'],settings.get('print.duplex','single'),settings['print.orientation']):
            printer.begin_page()
            for card,x,y in page:
                w,h=layout['card_width'],layout['card_height']
                image=render_card(rendering,dict(card),prepared_base=base)
                if settings.get('print.show_borders',True):
                    ImageDraw.Draw(image).rectangle((0,0,image.width-1,image.height-1),
                        outline='black',width=max(1,round(.35*rendering['card.dpi']/72)))
                printer.draw(image,transform(x,y,w,h))
                if settings.get('print.cut_marks',False):_draw_cut_marks(CutMarks(),x,y,w,h)
            printer.end_page()
            pages_submitted+=1
            if progress:progress('printer',pages_submitted,total_pages)
        printer.finish()
        return True
    except Exception as print_error:
        # Preserve an export even if opening/validating the driver failed.
        if pdf_path is not None and not pdf_written:
            try:export_settings_pdf(settings,cards,pdf_path)
            except Exception as pdf_error:
                raise RuntimeError('%s\nوتعذر حفظ PDF: %s' % (print_error,pdf_error)) from print_error
        if started:
            raise RuntimeError('%s\n\nتوقفت مهمة الطباعة بعد تسليم %s صفحة إلى Windows. قد تكون بعض الصفحات طُبعت؛ راجع الورق وقائمة انتظار الطابعة قبل إعادة المحاولة لتجنب التكرار.' % (print_error,pages_submitted)) from print_error
        raise
    finally:
        if printer is not None:printer.close()
