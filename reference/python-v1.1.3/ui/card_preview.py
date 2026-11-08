"""Shared preview: move existing canvas layers; render only after a drop."""
from copy import deepcopy
from PIL import ImageTk

from core.render import render_card, _background_path
from core.printing import LayoutError, validate_credential_layout
from .arabic import ui_text as AR


ELEMENT_NAMES = {'username':'رقم الكرت','password':'كلمة المرور','sequence':'التسلسل',
                 'title':'العنوان','price':'السعر','capacity':'السعة','duration':'المدة',
                 'validity':'الصلاحية','date':'التاريخ','phone':'الهاتف','qr':'QR',
                 'other':'أخرى 1','other2':'أخرى 2'}


def preview_geometry(canvas, width_mm, height_mm, padding=8):
    """Fit the card without stretching its aspect ratio on narrow previews."""
    width=max(1,canvas.winfo_width());height=max(1,canvas.winfo_height())
    available_w=max(1,width-2*padding);available_h=max(1,height-2*padding)
    ratio=float(width_mm)/float(height_mm)
    w=max(1,min(available_w,int(available_h*ratio)))
    h=max(1,int(w/ratio))
    return (width-w)/2,(height-h)/2,w,h


class CardPreview:
    def __init__(self, canvas):
        self.canvas = canvas
        self.geometry = None
        self.items = {}
        self.photos = []
        self.selected = None
        self.drag = None
        self._last_draw = None

    def draw(self, settings, card, geometry):
        if self.drag is not None:
            return
        source = _background_path(settings.get('card.background_path', ''))
        try:
            stamp = source.stat() if source is not None else None
            source_key = (str(source), stamp.st_mtime_ns, stamp.st_size) if stamp else None
        except OSError:
            source_key = None
        key = (settings, dict(card or {}), tuple(geometry), self.selected, source_key)
        if self._last_draw == key:
            return
        self._last_draw = None
        x0,y0,width,height = geometry
        layers = []
        background = render_card(settings, card, target_size=(width,height), layers=layers)
        try:
            validate_credential_layout(layers,background.size)
            self.validation_error=''
        except LayoutError as exc:
            # Keep the layers editable so the user can repair the positions.
            self.validation_error=str(exc)
        # Use the actual raster dimensions, including the minimum image size.
        width,height = background.size
        self.geometry = x0,y0,width,height
        self.items = {}
        self.photos = [ImageTk.PhotoImage(background)]
        self.canvas.delete('all')
        self.canvas.create_image(x0,y0,image=self.photos[0],anchor='nw',tags=('card-preview',))
        for layer in layers:
            name = layer['name'];tag = 'card-element:'+name
            px,py = layer['position'];ox,oy = layer['offset'];image = layer['image']
            iw,ih = image.size if image is not None else (26,13)
            if image is not None:
                photo = ImageTk.PhotoImage(image);self.photos.append(photo)
                self.canvas.create_image(x0+px+ox,y0+py+oy,image=photo,anchor='nw',tags=(tag,))
            extent = (max(1,iw+ox),max(1,ih+oy))
            handle_w,handle_h = max(26,extent[0]),max(13,extent[1])
            rectangle = self.canvas.create_rectangle(x0+px,y0+py,x0+px+handle_w,y0+py+handle_h,
                outline='#F0A12B' if name==self.selected else '#2D76D6',width=1,dash=(3,2),tags=(tag,))
            self.items[name] = {'tag':tag, 'rectangle':rectangle,
                                'anchor':(x0+px,y0+py), 'extent':extent}
            if name==self.selected:
                self.canvas.create_text(x0+px,y0+py-2,text=AR(ELEMENT_NAMES.get(name,name)),
                    anchor='sw',fill='#956013',font=('Arial',9,'bold'),tags=(tag,))
        self._last_draw = deepcopy(key)

    def hit(self, x, y):
        # Use rectangle coordinates, never bbox(): bbox includes outline pixels
        # and caused the old one-pixel drift on every click/drop.
        candidates = []
        for name,item in self.items.items():
            coords = self.canvas.coords(item['rectangle'])
            if len(coords)!=4:continue
            left,top,right,bottom = coords
            if left-3<=x<=right+3 and top-3<=y<=bottom+3:
                candidates.append(((right-left)*(bottom-top), name))
        # Small elements remain selectable when a large title or QR overlaps.
        return min(candidates)[1] if candidates else None

    def start(self, event):
        self._last_draw = None
        self.selected = self.hit(event.x,event.y)
        self.drag = None
        if self.selected is None or self.geometry is None:return False
        item = self.items[self.selected]
        self.drag = {'pointer':(event.x,event.y), 'origin':item['anchor']}
        self.canvas.tag_raise(item['tag'])
        for name,other in self.items.items():
            self.canvas.itemconfigure(other['rectangle'],outline='#F0A12B' if name==self.selected else '#2D76D6',width=1)
        self.canvas.configure(cursor='fleur')
        self.canvas.focus_set()
        return True

    def move(self, event):
        if self.drag is None:return
        item = self.items[self.selected]
        x0,y0,width,height = self.geometry
        origin_x,origin_y = self.drag['origin'];click_x,click_y = self.drag['pointer']
        # Keep the original click offset and constrain the content to the card.
        x = max(x0,min(x0+max(0,width-item['extent'][0]),origin_x+event.x-click_x))
        y = max(y0,min(y0+max(0,height-item['extent'][1]),origin_y+event.y-click_y))
        current_x,current_y = item['anchor']
        self.canvas.move(item['tag'],x-current_x,y-current_y)
        item['anchor'] = x,y

    def finish(self, event):
        if self.drag is None:return None
        # A click without motion must never change saved coordinates, including
        # an existing oversized template whose text is partly outside the card.
        if (event.x,event.y)==self.drag['pointer']:
            self.drag=None;self.canvas.configure(cursor='');return None
        self.move(event)
        item = self.items[self.selected]
        old = self.drag['origin'];self.drag = None
        self.canvas.configure(cursor='')
        if item['anchor']==old:return None
        x0,y0,width,height = self.geometry
        x,y = item['anchor']
        return self.selected,[round((x-x0)/width*280,2),round((y-y0)/height*130,2)]
