"""Compact router workspace following the supplied desktop reference layout."""
from __future__ import annotations

import json
import math
import re
import time
from datetime import datetime
from pathlib import Path
import tkinter as tk
from PIL import Image, ImageTk
from tkinter import ttk, filedialog, messagebox, colorchooser, font as tkfont

from core.network import SERVICES, COLORS, truth, byte_count, remote_password_available
from core.github_license import format_license_countdown
from core.cards import generate_cards, new_batch_no, readable_bytes, bytes_total, uptime_string, MAX_BATCH_SIZE, lowercase_english
from core.delivery import send_cards
from core.packages import package_snapshot, sample_card, CARD_FONT_LIMITS, CARD_BORDER_LIMITS
from core.printing import export_settings_pdf, save_pdf_copy, windows_printers, auto_grid, calculate_layout, print_plan
from core.windows_printing import print_cards
from core.exporting import export_excel, export_csv, export_text
from .arabic import ui_text as AR
from .layout import PageFrame, fit_table, fit_buttons
from .card_preview import CardPreview, ELEMENT_NAMES, preview_geometry
from .theme import COLORS as THEME_COLORS


APP_BG=THEME_COLORS['bg'];SURFACE=THEME_COLORS['panel'];SURFACE_ALT=THEME_COLORS['panel2']
INK=THEME_COLORS['text'];PRIMARY=THEME_COLORS['accent'];ACCENT=THEME_COLORS['accent2']
LINE=THEME_COLORS['line'];PALE=THEME_COLORS['pale'];DANGER=THEME_COLORS['red'];MUTED=THEME_COLORS['muted']


def safe_int(value, default=0):
    """Return an integer for RouterOS values without letting one bad field break a page."""
    if value is None or value == '':
        return default
    try:
        return int(value)
    except (TypeError, ValueError, OverflowError):
        try:
            return int(float(str(value).strip()))
        except (TypeError, ValueError, OverflowError):
            return default


def choose_pdf_destination(parent,filename):
    output=Path(parent.db.get_setting('paths.output'))
    initial_dir=Path(parent.db.get_setting('paths.print_copy',str(output)))
    if not initial_dir.is_dir():initial_dir=output
    return filedialog.asksaveasfilename(
        parent=parent,title='حفظ ملف الكروت PDF',initialdir=str(initial_dir),
        initialfile=Path(filename).name,defaultextension='.pdf',filetypes=[('ملف PDF','*.pdf')])


def card_pdf_filename(profile_name,count,when=None):
    """Return a readable Windows-safe PDF name for a saved card batch.

    Example: ``(200R) 500 كرت 23-09-2026.pdf``.  A numeric token in the
    RouterOS profile is treated as the package price; non-numeric profile
    names are preserved without inventing a price.
    """
    raw=str(profile_name or '').strip()
    numeric=re.search(r'\d+(?:[.,]\d+)?',raw)
    if numeric:
        token=numeric.group(0).replace(',','.')
        if token.endswith('.0'):token=token[:-2]
        package='(%sR)' % token
    else:
        safe=re.sub(r'[<>:"/\\|?*\x00-\x1f]+','-',raw).strip(' .-_') or 'Package'
        package='(%s)' % safe[:48]
    stamp=(when or datetime.now()).strftime('%d-%m-%Y')
    filename='%s %s كرت %s.pdf' % (package,int(count),stamp)
    filename=re.sub(r'[<>:"/\\|?*\x00-\x1f]+','-',filename).strip(' .')
    return filename


def show_saved_pdf_dialog(parent,path,count):
    """Show the successful-save dialog with explicit OK and preview actions."""
    path=Path(path)
    # Headless test doubles do not own a Tcl interpreter.  The real desktop
    # page always has ``tk``; preserving this fallback also keeps non-GUI
    # automation and diagnostics safe.
    if not hasattr(parent,'tk'):
        messagebox.showinfo('تم حفظ PDF','حُفظ ملف %s كرت، وأرقام الكروت وكلمات المرور قابلة للتحديد والنسخ:\n%s' % (count,path))
        return
    dialog=tk.Toplevel(parent)
    dialog.title('تم حفظ PDF')
    dialog.configure(bg=THEME_COLORS['panel'])
    dialog.resizable(False,False)
    dialog.transient(parent.winfo_toplevel())
    dialog.grab_set()
    shell=tk.Frame(dialog,bg=THEME_COLORS['panel'],highlightbackground=THEME_COLORS['accent'],highlightthickness=2,padx=18,pady=16)
    shell.pack(fill='both',expand=True,padx=2,pady=2)
    head=tk.Frame(shell,bg=THEME_COLORS['title_bg'],height=48)
    head.pack(fill='x',pady=(0,14));head.pack_propagate(False)
    tk.Frame(head,bg=THEME_COLORS['accent'],width=6).pack(side='right',fill='y')
    tk.Label(head,text='تم حفظ ملف PDF بنجاح',bg=THEME_COLORS['title_bg'],fg=THEME_COLORS['white'],font=('Arial',15,'bold'),anchor='e').pack(fill='both',padx=12)
    msg='حُفظ ملف %s كرت، ويمكنك طباعته أو تحديد رقم الكرت وكلمة المرور ونسخهما.\n\n%s' % (count,path)
    tk.Label(shell,text=msg,bg=THEME_COLORS['panel'],fg=THEME_COLORS['text'],font=('Arial',11,'bold'),justify='right',anchor='e',wraplength=620).pack(fill='x',padx=8,pady=(0,16))
    buttons=tk.Frame(shell,bg=THEME_COLORS['panel']);buttons.pack(fill='x')
    def close():
        try:dialog.grab_release()
        except tk.TclError:pass
        dialog.destroy()
    def preview():
        try:
            import os,subprocess
            if os.name=='nt':os.startfile(str(path))
            else:subprocess.Popen(['xdg-open',str(path)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        except Exception as exc:
            messagebox.showerror('معاينة PDF','تعذر فتح ملف PDF:\n'+str(exc),parent=dialog)
            return
        close()
    ttk.Button(buttons,text='موافق ومعاينة',style='Accent.TButton',command=preview).pack(side='right',padx=(6,0),ipadx=12)
    ttk.Button(buttons,text='موافق',style='Dark.TButton',command=close).pack(side='right',ipadx=16)
    dialog.protocol('WM_DELETE_WINDOW',close)
    dialog.update_idletasks()
    root=parent.winfo_toplevel();x=root.winfo_rootx()+max(0,(root.winfo_width()-dialog.winfo_width())//2);y=root.winfo_rooty()+max(0,(root.winfo_height()-dialog.winfo_height())//2)
    dialog.geometry('+%d+%d' % (x,y))
    dialog.wait_window()


def offer_print_copy(parent,result):
    """Offer the same post-dispatch PDF copy dialog in live and archive printing."""
    destination=choose_pdf_destination(parent,result)
    if not destination:return None
    saved=Path(save_pdf_copy(result,destination))
    parent.db.set_setting('paths.print_copy',str(saved.parent))
    return saved


def printing_progress(app):
    """Post only one UI update per page; Tk widgets stay on the UI thread."""
    generation=app.session.generation
    def update(stage,current,total):
        title={'pdf':'تجهيز PDF','preflight':'فحص تخطيط الكروت'}.get(stage,'تسليم الصفحات للطابعة')
        message=AR('%s: %s / %s' % (title,current,total))
        app.post_ui(lambda:app.status_var.set(message),generation=generation)
    return update


def label(parent, text='', **kw):
    return tk.Label(parent,text=text,bg=kw.pop('bg',SURFACE),fg=kw.pop('fg',INK),font=kw.pop('font',('Arial',11)),**kw)


def icon_label_widget(app,parent,icon,bg,size=(24,24),fg='#FFFFFF',font=('Arial',11,'bold'),anchor='center'):
    """Render either a local PNG asset or a text fallback without network access."""
    if isinstance(icon,str) and icon.lower().endswith('.png') and getattr(app,'load_asset_icon',None):
        photo=app.load_asset_icon(icon,size)
        widget=tk.Label(parent,image=photo,bg=bg,anchor=anchor)
        widget.image=photo
        return widget
    return label(parent,icon,bg=bg,fg=fg,font=font,anchor=anchor)


def box(parent, title='', accent=None):
    border=accent or LINE
    if title:
        return tk.LabelFrame(parent,text=AR(title),bg=SURFACE,fg=accent or PRIMARY,font=('Arial',11,'bold'),
                             bd=0,relief='flat',highlightbackground=border,highlightcolor=border,
                             highlightthickness=1,padx=6,pady=4)
    return tk.Frame(parent,bg=SURFACE,highlightbackground=border,highlightthickness=1)


def section_card(parent,title,accent,soft,header_fg='#FFFFFF',action=None,icon=None,strong_title=False):
    """A compact visual section with a colored edge and a distinct title strip.

    The body remains a normal Frame so existing Tk controls and callbacks stay
    untouched.  This is intentionally presentation-only.
    """
    outer=tk.Frame(parent,bg=SURFACE,highlightbackground=accent,highlightthickness=1,bd=0,relief='flat')
    head=tk.Frame(outer,bg=soft,height=38 if action else 24);head.pack(fill='x');head.pack_propagate(False)
    if action:
        # Local styling only; keep the exact original command and workflow.
        save_button=tk.Button(head,text=AR(action[0]),command=action[1],
                  font=('Arial',13,'bold'),fg='#FFFFFF',bg='#CB0920',
                  activeforeground='#FFFFFF',activebackground='#99071A',
                  relief='flat',bd=0,highlightthickness=0,
                  cursor='hand2',padx=26,pady=2)
        save_button.pack(side='left',fill='y',padx=(0,5))
    if icon:
        label(head,icon,bg=soft,fg=header_fg,font=('Segoe UI Symbol',12,'bold')).pack(side='left',padx=(7,2))
    header_title=label(head,AR(title),bg=soft,fg=header_fg,
                       font=('Arial',11,'bold'),anchor='e')
    header_title.pack(side='right',fill='both',expand=True,padx=3 if action else 8)
    if action:
        def fit_save_header(event):
            # Fit the longer caption without hiding the adjacent section title.
            compact=event.width<440
            save_button.configure(font=('Arial',10 if compact else 13,'bold'),
                                  padx=8 if compact else 26)
            header_title.configure(font=('Arial',9 if compact else 11,'bold'))
        head.bind('<Configure>',fit_save_header,add='+')
    body=tk.Frame(outer,bg=SURFACE,padx=5,pady=2);body.pack(fill='both',expand=True)
    return outer,body


def field(parent, title, variable, row, column=0, choices=None, width=15, readonly=True):
    label(parent,title,anchor='e',font=('Arial',10)).grid(row=row,column=column*2+1,sticky='e',padx=3,pady=2)
    if choices is None:widget=ttk.Entry(parent,textvariable=variable,width=width,justify='center')
    else:widget=ttk.Combobox(parent,textvariable=variable,values=choices,width=width,state='readonly' if readonly else 'normal',justify='center')
    widget.grid(row=row,column=column*2,sticky='ew',padx=3,pady=2)
    parent.columnconfigure(column*2,weight=1)
    return widget


def mini_field(parent, title, variable, column, choices=None, width=10, readonly=True, row=0):
    """Compact field with readable typography; space is saved around controls, not in fonts."""
    cell=tk.Frame(parent,bg=parent.cget('bg'))
    cell.grid(row=row,column=column,sticky='ew',padx=3,pady=1)
    parent.columnconfigure(column,weight=1)
    label(cell,title,anchor='e',font=('Arial',10,'bold'),fg=MUTED,bg=cell.cget('bg')).pack(fill='x',pady=(0,0))
    if choices is None:widget=ttk.Entry(cell,textvariable=variable,width=width,justify='center')
    else:
        from .choices import LocalizedChoice
        choice_type=LocalizedChoice if isinstance(choices,dict) else ttk.Combobox
        widget=choice_type(cell,textvariable=variable,values=choices,width=width,state='readonly' if readonly else 'normal',justify='center')
    widget.pack(fill='x')
    return widget


def inline_field(parent,title,variable,column,choices=None,width=6,readonly=True,row=0):
    """Single-line field used where vertical space matters, without reducing font size."""
    cell=tk.Frame(parent,bg=parent.cget('bg'))
    cell.grid(row=row,column=column,sticky='ew',padx=3,pady=1)
    parent.columnconfigure(column,weight=1)
    label(cell,title,anchor='e',font=('Arial',10,'bold'),fg=MUTED,bg=cell.cget('bg')).pack(side='right',padx=(3,1))
    if choices is None:widget=ttk.Entry(cell,textvariable=variable,width=width,justify='center')
    else:widget=ttk.Combobox(cell,textvariable=variable,values=choices,width=width,state='readonly' if readonly else 'normal',justify='center')
    widget.pack(side='left',fill='x',expand=True)
    return widget


def table(parent, columns, height=8):
    frame=box(parent);frame.pack(fill='both',expand=True,pady=2)
    tree=ttk.Treeview(frame,columns=[c[0] for c in columns],show='headings',height=height,selectmode='extended')
    for name,title,width in columns:
        tree.heading(name,text=title);tree.column(name,width=width,minwidth=45,anchor='center')
    fit_table(tree)
    tree.grid(row=0,column=0,sticky='nsew')
    frame.columnconfigure(0,weight=1);frame.rowconfigure(0,weight=1)
    return tree


class Brand(tk.Canvas):
    def __init__(self,parent,width=280,height=230):
        super().__init__(parent,width=width,height=height,bg=APP_BG,highlightthickness=0,bd=0)
        self._asset_path=Path(__file__).resolve().parent.parent/'assets'/'login_hero.png'
        self._photo=None
        self._source_image=None
        self.bind('<Configure>',self.draw)

    def draw(self,_event=None):
        self.delete('all')
        w=max(40,self.winfo_width());h=max(40,self.winfo_height())
        x1,y1,x2,y2=6,6,w-6,h-6
        self.create_rectangle(x1+10,y1+14,x2-10,y2-4,fill='#B7EFFF',outline='',stipple='gray25')
        self.create_rectangle(x1,y1,x2,y2,fill=THEME_COLORS['navy_soft'],outline=THEME_COLORS['line'],width=1)
        self.create_rectangle(x1+8,y1+8,x2-8,y2-8,fill=THEME_COLORS['panel'],outline=THEME_COLORS['accent'],width=2)
        self.create_rectangle(x1+18,y1+18,x2-18,y2-18,outline=THEME_COLORS['accent2'],width=1,dash=(8,5))
        if self._asset_path.exists():
            try:
                if self._source_image is None:
                    with Image.open(self._asset_path, formats=('PNG',)) as source:
                        self._source_image=source.convert('RGBA')
                image=self._source_image.copy()
                max_w=max(60,w-42);max_h=max(60,h-42)
                image.thumbnail((max_w,max_h),Image.LANCZOS)
                self._photo=ImageTk.PhotoImage(image)
                self.create_image(w/2,h/2,image=self._photo)
                return
            except Exception:
                self._photo=None
        r=min(w*.40,h*.42);cx=w/2;cy=h/2
        self.create_oval(cx-r,cy-r,cx+r,cy+r,fill=THEME_COLORS['navy'],outline=THEME_COLORS['accent'],width=2)
        self.create_polygon(cx-r*.9,cy-r*.53,cx+r*.76,cy-r*.75,cx+r*.92,cy+r*.53,cx-r*.74,cy+r*.75,fill=THEME_COLORS['pale'],outline=THEME_COLORS['white'],width=3)
        self.create_text(cx,cy-r*.18,text='حضرموت',fill=INK,font=('Arial',max(20,int(r*.42)),'bold'))
        self.create_text(cx,cy+r*.3,text='Cards',fill=INK,font=('Arial',max(15,int(r*.25)),'bold'))
        self.create_text(cx+r*.52,cy-r*.72,text='Mini',fill=THEME_COLORS['accent2'],font=('Arial',max(12,int(r*.2)),'bold'))


class ServiceStrip(tk.Frame):
    def __init__(self,parent,app):
        super().__init__(parent,bg=SURFACE,highlightbackground=LINE,highlightthickness=1,padx=2,pady=2)
        self.app=app;self.buttons={}
        for service in SERVICES:
            b=tk.Button(self,text=SERVICES[service],bg=SURFACE,fg=COLORS[service],font=('Arial',11,'bold'),
                        relief='solid',bd=1,padx=6,pady=4,command=lambda key=service:app.select_service(key))
            b.pack(side='right',fill='x',expand=True,padx=2);self.buttons[service]=b
        ttk.Button(self,text='تحديث من الراوتر',command=app.refresh_network).pack(side='left',padx=2,pady=1)
        self.refresh()

    def refresh(self):
        for key,b in self.buttons.items():
            b.configure(bg=THEME_COLORS['accent_soft'] if self.app.active_service==key else SURFACE,relief='sunken' if self.app.active_service==key else 'solid')


class NetworkPage(tk.Frame):
    def __init__(self,parent,app,services=True):
        super().__init__(parent,bg=APP_BG);self.app=app;self.db=app.db
        self.strip=ServiceStrip(self,app) if services else None
        if self.strip:self.strip.pack(fill='x',padx=4,pady=(2,1))
        self.notice=label(self,'',bg=APP_BG,anchor='e',justify='right',wraplength=900,font=('Arial',9))
        self.notice.pack(fill='x',padx=6,pady=(0,1))
        self.viewport=PageFrame(self,APP_BG)
        self.viewport.pack(fill='both',expand=True,padx=4,pady=(0,2));self.body=self.viewport.inner

    def router_row(self):return self.app.session.router_row()

    def state(self):return self.app.network_catalog.get('services',{}).get(self.app.active_service,{})

    def refresh(self):
        if self.strip:self.strip.refresh()
        state=self.state()
        text=('جارٍ قراءة الباقات من الراوتر...' if self.app.catalog_busy else state.get('error',''))
        if not text:
            text='الباقات من الراوتر: %s | %s' % (self.app.session.identity,len(state.get('profiles',[])))
            if not state.get('profiles'):text+=' — لا توجد باقات في هذا القسم. اختر نوع الخدمة الصحيح أو أضف باقة.'
            if state.get('warnings'):text+=' | بعض الإحصاءات غير متاحة؛ افتح التشخيص.'
        self.notice.configure(text=AR(text),fg=DANGER if state.get('error') else INK)


class RoundedPanel(tk.Frame):
    """Lightweight bordered panel wrapper.

    Tk does not provide true rounded corners for native widgets, and the canvas
    simulation can introduce rendering artifacts on some Windows systems.  This
    wrapper keeps the same API (including ``inner`` and ``set_panel_fill``)
    while prioritizing clean, stable rendering.
    """
    def __init__(self,parent,fill,border,radius=16,padding=1,canvas_bg=None,**kwargs):
        super().__init__(parent,bg=border,bd=0,highlightthickness=0,relief='flat',**kwargs)
        pad=max(1,int(padding))
        self.inner=tk.Frame(self,bg=fill,bd=0,highlightthickness=0,relief='flat')
        self.inner.pack(fill='both',expand=True,padx=pad,pady=pad)

    def set_panel_fill(self,fill):
        self.inner.configure(bg=fill)


class DashboardMetricCard(RoundedPanel):
    """Colour-coded, lightweight Tk metric card with cached local icon."""
    def __init__(self,parent,app,title,icon,color,subtitle='',command=None,icon_size=(30,30),panel_height=122):
        super().__init__(parent,fill=color,border=color,radius=15,padding=1,
                         canvas_bg=parent.cget('bg'),height=panel_height)
        self.app=app;self.color=color;self.command=command
        self.default_fill=color;self.hover_fill=color
        self.inner.configure(bg=color)
        head=tk.Frame(self.inner,bg=color,height=39)
        head.pack(fill='x',padx=8,pady=(4,0));head.pack_propagate(False)
        icon_box=tk.Frame(head,bg=color,width=43,height=36)
        icon_box.pack(side='right',padx=(4,2));icon_box.pack_propagate(False)
        self.icon_label=icon_label_widget(app,icon_box,icon,bg=color,size=icon_size,
                                          fg='#FFFFFF',font=('Arial',12,'bold'))
        self.icon_label.pack(fill='both',expand=True)
        self.title=label(head,AR(title),bg=color,fg='#FFFFFF',font=('Arial',11,'bold'),anchor='e')
        self.title.pack(side='right',fill='both',expand=True,padx=(1,2))
        body=tk.Frame(self.inner,bg=color)
        body.pack(fill='both',expand=True,padx=12,pady=(0,7))
        self.value=label(body,'—',bg=color,fg='#FFFFFF',font=('Arial',26,'bold'),anchor='e')
        self.value.pack(fill='x')
        self.subtitle=label(body,AR(subtitle),bg=color,fg='#EDF3FF',font=('Arial',8,'bold'),anchor='e')
        self.subtitle.pack(fill='x',pady=(1,0))
        if command:
            for widget in (self,head,icon_box,self.icon_label,self.title,body,self.value,self.subtitle):
                widget.configure(cursor='hand2')
                widget.bind('<Button-1>',lambda _event:self.command())

    def set_value(self,value,subtitle=None):
        if isinstance(value,float) and value.is_integer():value=int(value)
        self.value.configure(text='—' if value is None else ('{:,}'.format(value) if isinstance(value,(int,float)) else AR(str(value))))
        if subtitle is not None:self.subtitle.configure(text=AR(str(subtitle)))


class DashboardResourcePair(RoundedPanel):
    """CPU and memory in one compact, left-aligned dashboard card.

    Values are read from the existing router cache by NetworkHome.refresh;
    these controls do not schedule polling or modify router data.
    """
    def __init__(self,parent,app,panel_height=126):
        super().__init__(parent,fill=SURFACE,border='#E44D80',radius=15,padding=2,
                         canvas_bg=parent.cget('bg'),height=panel_height)
        self.app=app;self.values={};self.bars={};self.percentages={}
        for key,title,icon,tone in (
                ('cpu','المعالج','cpu_usage.png','#1668DC'),
                ('memory','الذاكرة','memory_usage.png','#119A56')):
            row=tk.Frame(self.inner,bg=SURFACE)
            row.pack(fill='both',expand=True,padx=7,pady=4)
            icon_box=tk.Frame(row,bg=SURFACE,width=48,height=44)
            icon_box.pack(side='left',padx=(0,6));icon_box.pack_propagate(False)
            icon_label_widget(app,icon_box,icon,bg=SURFACE,size=(36,36),
                              fg=tone,font=('Arial',12,'bold')).pack(fill='both',expand=True)
            data=tk.Frame(row,bg=SURFACE)
            data.pack(side='left',fill='both',expand=True)
            info=tk.Frame(data,bg=SURFACE)
            info.pack(fill='x')
            label(info,title,bg=SURFACE,fg=INK,font=('Arial',10,'bold'),anchor='e').pack(side='right',padx=3)
            value=label(info,'—',bg=SURFACE,fg=tone,font=('Arial',15,'bold'),anchor='w')
            value.pack(side='left',padx=2);self.values[key]=value
            bar=tk.Canvas(data,height=14,bd=0,highlightthickness=0,bg='#D7DADE')
            bar.pack(fill='x',pady=(2,1))
            self.bars[key]=(bar,tone);self.percentages[key]=None
            bar.bind('<Configure>',lambda _event,k=key:self._draw_bar(k))

    def _draw_bar(self,key):
        bar,tone=self.bars[key]
        width=max(1,bar.winfo_width());height=max(1,bar.winfo_height())
        bar.delete('usage')
        value=self.percentages[key]
        if value is not None and value>0:
            bar.create_rectangle(0,0,int(width*min(100,value)/100),height,
                                 fill=tone,outline='',tags='usage')

    def set_values(self,cpu=None,memory=None):
        for key,raw in (('cpu',cpu),('memory',memory)):
            if raw in (None,''):
                self.values[key].configure(text='—');self.percentages[key]=None
            else:
                self.values[key].configure(text=AR('%s%%' % raw))
                try:self.percentages[key]=max(0,min(100,float(raw)))
                except (TypeError,ValueError,OverflowError):self.percentages[key]=None
            self._draw_bar(key)




class VoucherStatusWindow(tk.Toplevel):
    """Read-only list behind the active/expired dashboard counters."""
    PAGE_SIZE=300
    TITLES={'active':'الكروت النشطة','expired':'الكروت المنتهية'}

    def __init__(self,parent,app,status,color):
        super().__init__(parent);self.app=app;self.status=status;self.color=color
        self.rows=[];self.filtered=[];self.page=0;self._search_job=None
        self.title(AR('حالة الكروت - '+self.TITLES.get(status,status)));self.configure(bg=APP_BG)
        self.geometry('1040x650');self.minsize(820,500);self.transient(app)
        head=tk.Frame(self,bg=color,height=46);head.pack(fill='x');head.pack_propagate(False)
        label(head,self.TITLES.get(status,status),bg=color,fg='#FFFFFF',font=('Arial',15,'bold'),anchor='e').pack(side='right',fill='both',expand=True,padx=12)
        self.total_label=label(head,'جارٍ القراءة...',bg=color,fg='#FFFFFF',font=('Arial',10,'bold'),anchor='w')
        self.total_label.pack(side='left',padx=12)
        filters=tk.Frame(self,bg=SURFACE,highlightbackground=LINE,highlightthickness=1);filters.pack(fill='x',padx=8,pady=7)
        self.search=tk.StringVar();label(filters,'بحث بالرقم أو الباقة',bg=SURFACE,fg=MUTED,font=('Arial',10,'bold'),anchor='e').pack(side='right',padx=(8,4),pady=6)
        entry=ttk.Entry(filters,textvariable=self.search,justify='center');entry.pack(side='right',fill='x',expand=True,padx=4,pady=6)
        entry.bind('<KeyRelease>',self._schedule_filter)
        ttk.Button(filters,text='إغلاق',style='Dark.TButton',command=self.destroy).pack(side='left',padx=6,pady=5)
        ttk.Button(filters,text='تحديث القائمة',style='Dark.TButton',command=self._load).pack(side='left',padx=4,pady=5)
        if self.status=='expired':
            ttk.Button(filters,text='حذف وتنظيف المنتهية',style='Danger.TButton',command=self._cleanup_expired).pack(side='left',padx=4,pady=5)
        self.tree=table(self,[('name','اسم المستخدم',145),('profile','الباقة',145),('reason','الحالة',190),
            ('seen','آخر ظهور',155),('uptime','الاستخدام',105),('download','تنزيل',100),('upload','رفع',100),('sessions','جلسات',65)],15)
        footer=tk.Frame(self,bg=APP_BG);footer.pack(fill='x',padx=8,pady=(2,8))
        self.page_label=label(footer,'',bg=APP_BG,fg=MUTED,font=('Arial',9,'bold'),anchor='center');self.page_label.pack(side='right',fill='x',expand=True)
        self.prev=ttk.Button(footer,text='السابق',style='Dark.TButton',command=lambda:self._page(-1));self.prev.pack(side='left',padx=3)
        self.next=ttk.Button(footer,text='التالي',style='Dark.TButton',command=lambda:self._page(1));self.next.pack(side='left',padx=3)
        self._load()

    def _load(self):
        def done(rows):
            if not self.winfo_exists():return
            self.rows=list(rows or []);self._apply_filter()
        def failed(exc):
            if not self.winfo_exists():return
            self.total_label.configure(text=AR('تعذرت القراءة'));messagebox.showerror('حالة الكروت',str(exc),parent=self)
        # Show the counter the dashboard already holds before the rows arrive, so
        # the window never sits on an empty "جارٍ القراءة..." for several seconds.
        self._show_known_count()
        self.app.run_network(lambda g:g.voucher_status_rows(self.status),done,failed,read_only=True)
    def _show_known_count(self):
        """Display the last known counter immediately when one is available."""
        try:
            life=(self.app.network_catalog.get('services',{}).get('userman',{}) or {}).get('lifecycle',{}) or {}
            known=life.get(self.status)
            if known is None:return
            self.total_label.configure(text=AR('%s كرت' % f'{int(known):,}'))
        except (tk.TclError,TypeError,ValueError):
            return

    def _cleanup_expired(self):
        if self.status!='expired':return
        rows=list(self.rows or [])
        if not rows:
            return messagebox.showinfo('تنظيف المنتهية','لا توجد كروت منتهية في القراءة الحالية.',parent=self)
        count=len(rows)
        if not messagebox.askyesno('حذف وتنظيف الكروت المنتهية',
                'سيتم حذف %s كرت منتهي من مدير المستخدمين فقط.\nلن يتم حذف الكروت النشطة.\n\nمتابعة؟' % f'{count:,}',parent=self):
            return
        if not messagebox.askyesno('تأكيد نهائي',
                'هذه العملية نهائية ولا يمكن التراجع عنها.\nهل تريد حذف وتنظيف الكروت المنتهية الآن؟',parent=self):
            return
        self.total_label.configure(text=AR('جارٍ حذف %s كرت...' % f'{count:,}'))
        def done(result):
            if not self.winfo_exists():return
            removed=int(result.get('removed',0));failed=int(result.get('failed',0));skipped=int(result.get('skipped',0))
            self.app.db.log('USERMAN','حذف وتنظيف الكروت المنتهية','PARTIAL' if failed else 'SUCCESS',
                            'removed=%s failed=%s skipped=%s' % (removed,failed,skipped))
            text='تم حذف %s كرت منتهي.' % f'{removed:,}'
            if failed:text+=' فشل حذف %s.' % f'{failed:,}'
            if skipped:text+=' تم تجاوز %s سجل لم تتأكد صلاحية حذفه.' % f'{skipped:,}'
            if failed:
                errors=result.get('errors') or []
                if errors:text+='\nأول خطأ: '+str(errors[0].get('error',''))
                messagebox.showwarning('نتيجة التنظيف',text,parent=self)
            else:messagebox.showinfo('نتيجة التنظيف',text,parent=self)
            # Do not trigger a third 20k+ User Manager inventory immediately
            # after the mandatory pre-delete scan. The user can press Refresh.
            self.rows=[];self.filtered=[];self.page=0;self._render()
            self.total_label.configure(text=AR('اكتمل التنظيف — اضغط تحديث القائمة لقراءة جديدة'))
            dashboard=self.app.page_frames.get('dashboard')
            if dashboard is not None and hasattr(dashboard,'refresh'):dashboard.refresh()
            if removed and not failed:self.app.offer_maintenance('اكتمال حذف الكروت المنتهية')
        def failed(exc):
            if self.winfo_exists():
                self.total_label.configure(text=AR('%s كرت' % len(self.rows)))
                messagebox.showerror('تنظيف المنتهية',str(exc),parent=self)
        self.app.run_network(lambda g:g.purge_expired_vouchers(rows),done,failed,read_only=False)

    def _schedule_filter(self,_event=None):
        if self._search_job:
            try:self.after_cancel(self._search_job)
            except tk.TclError:pass
        self._search_job=self.after(180,self._apply_filter)

    def _apply_filter(self):
        self._search_job=None;query=self.search.get().strip().casefold();self.page=0
        if query:
            self.filtered=[row for row in self.rows if query in ('%s %s %s' % (row.get('_name',''),row.get('_profile',''),row.get('_reason',''))).casefold()]
        else:self.filtered=list(self.rows)
        self._render()

    def _page(self,delta):
        pages=max(1,(len(self.filtered)+self.PAGE_SIZE-1)//self.PAGE_SIZE)
        self.page=max(0,min(pages-1,self.page+delta));self._render()

    def _render(self):
        for item in self.tree.get_children():self.tree.delete(item)
        start=self.page*self.PAGE_SIZE;end=min(start+self.PAGE_SIZE,len(self.filtered))
        for index,row in enumerate(self.filtered[start:end],start):
            self.tree.insert('','end',iid=str(index),values=(row.get('_name',''),row.get('_profile',''),AR(row.get('_reason','')),
                row.get('last-seen',''),row.get('uptime-used',''),readable_bytes(byte_count(row.get('download-used',0))),
                readable_bytes(byte_count(row.get('upload-used',0))),row.get('active-sessions','0')))
        pages=max(1,(len(self.filtered)+self.PAGE_SIZE-1)//self.PAGE_SIZE)
        self.total_label.configure(text=AR('%s كرت' % len(self.rows)))
        self.page_label.configure(text=AR('عرض %s–%s من %s  •  صفحة %s من %s' % (start+1 if self.filtered else 0,end,len(self.filtered),self.page+1,pages)))
        self.prev.configure(state='normal' if self.page>0 else 'disabled')
        self.next.configure(state='normal' if self.page+1<pages else 'disabled')


class QuickActionCard(RoundedPanel):
    """Dashboard shortcut with a compact mode for small windows."""
    def __init__(self,parent,app,key,icon,title,subtitle,color):
        super().__init__(parent,fill=SURFACE,border=color,radius=16,padding=1,canvas_bg=parent.cget('bg'),height=104)
        self.app=app;self.default_fill=SURFACE;self.hover_fill='#EEF5F8'
        self.configure(cursor='hand2')
        self.command=lambda:app.show_page(key)
        wrap=tk.Frame(self.inner,bg=self.default_fill)
        wrap.pack(fill='both',expand=True,padx=4,pady=4)
        icon_box=tk.Frame(wrap,bg=color,width=40,height=40)
        icon_box.pack(side='right',fill='y');icon_box.pack_propagate(False)
        icon_label=icon_label_widget(app,icon_box,icon,bg=color,size=(28,28),fg='#FFFFFF',font=('Arial',16,'bold'),anchor='center')
        icon_label.pack(fill='both',expand=True)
        text=tk.Frame(wrap,bg=self.default_fill)
        text.pack(side='right',fill='both',expand=True,padx=5,pady=2)
        title_label=label(text,AR(title),bg=self.default_fill,fg=INK,font=('Arial',10,'bold'),anchor='e',justify='right')
        title_label.pack(fill='x')
        sub_label=label(text,AR(subtitle),bg=self.default_fill,fg=MUTED,font=('Arial',8),anchor='e',justify='right')
        sub_label.pack(fill='x',pady=(2,0))
        self.minimum_width=60+max(title_label.winfo_reqwidth(),sub_label.winfo_reqwidth())
        for widget in (self,wrap,icon_box,icon_label,text,title_label,sub_label):
            widget.bind('<Button-1>',lambda _event:self.command())
            widget.bind('<Enter>',lambda _event:self._hover(True))
            widget.bind('<Leave>',lambda _event:self._hover(False))
        self._parts=(wrap,icon_box,icon_label,text,title_label,sub_label)
        def resize(event):
            if event.widget is not self:return
            compact=event.height<52
            wrap.pack_configure(pady=2 if compact else 4)
            text.pack_configure(pady=0 if compact else 2)
            if compact:sub_label.pack_forget()
            elif not sub_label.winfo_manager():sub_label.pack(fill='x',pady=(2,0))
        self.bind('<Configure>',resize,add='+')

    def _hover(self,active):
        bg=self.hover_fill if active else self.default_fill
        self.set_panel_fill(bg)
        self._parts[0].configure(bg=bg);self._parts[3].configure(bg=bg);self._parts[4].configure(bg=bg);self._parts[5].configure(bg=bg)


class NetworkHome(NetworkPage):
    """Modern home dashboard inspired by the supplied network-manager references.

    Only existing cached/router-safe reads are shown here.  Expensive user/session
    histories remain on their dedicated pages so the home screen stays fast.
    """
    DASH=APP_BG;PANEL=SURFACE;PANEL2=SURFACE_ALT;TEXT=INK;SUB=MUTED

    def __init__(self,parent,app):
        super().__init__(parent,app,services=False)
        self.notice.pack_forget()
        self.viewport.configure(bg=self.DASH);self.body.configure(bg=self.DASH)

        hero=tk.Frame(self.body,bg=THEME_COLORS['navy'],height=62)
        hero.pack(fill='x',padx=7,pady=(6,4));hero.pack_propagate(False)
        brand_icon=app.load_asset_icon('hadramout_mark.png',(46,46))
        mark=tk.Label(hero,image=brand_icon,bg=THEME_COLORS['navy'],bd=0)
        mark.image=brand_icon
        mark.pack(side='right',padx=(8,12),pady=6)
        hero_text=tk.Frame(hero,bg=THEME_COLORS['navy'])
        label(hero_text,'لوحة التحكم الرئيسية',bg=THEME_COLORS['navy'],fg='#FFFFFF',font=('Arial',14,'bold'),anchor='e').pack(fill='x')
        label(hero_text,'حضرموت لإدارة الشبكات وطباعة الكروت',bg=THEME_COLORS['navy'],fg=THEME_COLORS['title_muted'],font=('Arial',9,'bold'),anchor='e').pack(fill='x',pady=(2,0))
        self.health=tk.Frame(hero,bg='#E3F8EC',width=185,height=42);self.health.pack(side='left',padx=8,pady=13);self.health.pack_propagate(False)
        self.health_icon=label(self.health,'✓',bg='#E3F8EC',fg=THEME_COLORS['green'],font=('Arial',18,'bold'),width=2)
        self.health_icon.pack(side='right',padx=(6,2),pady=4)
        self.health_text=label(self.health,'النظام يعمل طبيعيًا',bg='#E3F8EC',fg='#0C6C3C',font=('Arial',10,'bold'),anchor='e')
        self.health_text.pack(side='right',fill='both',expand=True,pady=4)

        # Keep the top of the dashboard free of duplicated router facts.  The
        # detailed router identity/status/version/resources are consolidated in
        # the single «بيانات الراوتر» panel below.
        sync=tk.Frame(hero,bg=THEME_COLORS['navy'],height=46)
        sync.pack(side='left',padx=(4,8),pady=10)
        ttk.Button(sync,text='تحديث البيانات',style='Teal.TButton',command=lambda:app.refresh_dashboard(force=True)).pack(fill='x',padx=3,pady=(0,2))
        self.date_label=label(sync,'',bg=THEME_COLORS['navy'],fg=THEME_COLORS['title_muted'],font=('Arial',8,'bold'),anchor='center')
        self.date_label.pack(fill='x',padx=3)
        hero_text.pack(side='right',fill='both',expand=True,pady=8)

        # Always-visible local licensing badge: updated by the existing 5s monitor.
        # Does not make router API calls or add a second background worker.
        self.license_banner=tk.Frame(self.body,bg='#E9F4FF',highlightbackground=THEME_COLORS['line'],highlightthickness=1,height=36)
        self.license_banner.pack(fill='x',padx=7,pady=(0,4))
        self.license_banner.pack_propagate(False)
        self.license_banner_text=tk.StringVar(value='حالة البرنامج: جارٍ التحقق...')
        self._license_clock_job = None
        self._license_clock_end = None
        self._license_clock_mode = ''
        self.license_banner_label=label(self.license_banner,'',bg='#E9F4FF',fg=THEME_COLORS['navy'],
                                        textvariable=self.license_banner_text,font=('Arial',10,'bold'),anchor='e')
        self.license_banner_label.pack(side='right',fill='both',expand=True,padx=12)
        self.show_license_status(self.app.current_license_access())

        # Four unified statistics cards; heavy lifecycle reads are requested once
        # after login and thereafter only by the explicit manual button.
        metrics=tk.Frame(self.body,bg=self.DASH)
        metrics.pack(fill='x',padx=7,pady=(0,3))
        # Four existing metric cards remain unchanged.  The CPU/RAM pair sits
        # at the user's far-left in ONE shared white card, as in the reference.
        for col in range(1,5):metrics.columnconfigure(col,weight=5,uniform='dashboard-metrics')
        metrics.columnconfigure(0,weight=6,uniform='dashboard-metrics')
        specs=(
            ('إجمالي الكروت','total_cards.png','#6545C1','مدير المستخدمين',None),
            ('المتصلون الآن','connected_users.png','#1473BD','Hotspot + PPPoE',None),
            ('الكروت النشطة','active_cards.png','#079260','مفعلة وصالحة','active'),
            ('الكروت المنتهية','expired_cards.png','#C92E53','انتهت الباقة','expired'),
        )
        self.cards=[];self.lifecycle_cards={}
        for index,(title,icon,tone,subtitle,status) in enumerate(specs):
            command=(lambda k=status,c=tone:VoucherStatusWindow(self,self.app,k,c)) if status else None
            metric=DashboardMetricCard(metrics,app,title,icon,tone,subtitle,command=command,panel_height=126)
            metric.grid(row=0,column=4-index,sticky='nsew',padx=3,pady=1)
            if status:self.lifecycle_cards[status]=metric
            else:self.cards.append(metric)
        self.resource_pair=DashboardResourcePair(metrics,app,panel_height=126)
        self.resource_pair.grid(row=0,column=0,sticky='nsew',padx=3,pady=1)
        note=tk.Frame(self.body,bg=THEME_COLORS['navy_soft'],height=35)
        note.pack(fill='x',padx=10,pady=(1,5));note.pack_propagate(False)
        ttk.Button(note,text='تحديث النشطة والمنتهية',style='Small.Dark.TButton',
                   command=lambda:app.refresh_lifecycle(force=True)).pack(side='left',padx=5,pady=3)
        self.lifecycle_note=label(note,'حالة الكروت: قراءة عند الدخول ثم تحديث يدوي فقط',
            bg=THEME_COLORS['navy_soft'],fg='#17345A',font=('Arial',9,'bold'),anchor='e')
        self.lifecycle_note.pack(side='right',fill='both',expand=True,padx=7)

        content=tk.Frame(self.body,bg=self.DASH,height=246)
        content.pack(fill='both',expand=True,padx=7,pady=(0,5));content.pack_propagate(False);content.grid_propagate(False)
        content.grid_columnconfigure(0,weight=5,uniform='dashboard-lower')
        content.grid_columnconfigure(1,weight=5,uniform='dashboard-lower')
        content.grid_columnconfigure(2,weight=5,uniform='dashboard-lower')
        content.grid_rowconfigure(0,weight=1)

        # Quick actions — only high-frequency commands, matching the supplied modern reference.
        quick=RoundedPanel(content,fill=self.PANEL,border=THEME_COLORS['accent'],radius=20,padding=1,canvas_bg=self.DASH)
        quick.grid(row=0,column=2,sticky='nsew',padx=(3,0))
        qhead=tk.Frame(quick.inner,bg=THEME_COLORS['accent2'],height=31);qhead.pack(fill='x');qhead.pack_propagate(False)
        label(qhead,'إجراءات سريعة',bg=THEME_COLORS['accent2'],fg='#FFFFFF',font=('Arial',11,'bold'),anchor='e').pack(side='right',padx=(8,4),pady=3)
        actions=tk.Frame(quick.inner,bg=self.PANEL)
        for col in range(2):actions.columnconfigure(col,weight=1,uniform='quick')
        for row in range(2):actions.rowconfigure(row,weight=1,uniform='quick-row')
        shortcuts=(
            ('create','create_cards.png','إنشاء كروت','تجهيز وطباعة دفعة',THEME_COLORS['green']),
            ('manage','manage_cards.png','إدارة الكروت','بحث وتعديل وطباعة',THEME_COLORS['blue']),
            ('monitor','connected_users.png','مراقبة الجلسات','Hotspot والمستخدمون',THEME_COLORS['yellow']),
            ('packages','packages.png','إدارة الباقات','السرعة والوقت والسعة','#6B4FA1'),
        )
        quick_actions=[]
        for index,item in enumerate(shortcuts):
            action=QuickActionCard(actions,app,*item)
            action.grid(row=index//2,column=1-index%2,sticky='nsew',padx=3,pady=3)
            quick_actions.append(action)
        def resize_shortcuts(event):
            if event.widget is not actions or event.width<2:return
            columns=2 if event.width>=2*max(card.minimum_width for card in quick_actions)+12 else 1
            rows=len(quick_actions)//columns
            for column in range(2):actions.columnconfigure(column,weight=1 if column<columns else 0,uniform='quick' if column<columns else '')
            for row in range(4):actions.rowconfigure(row,weight=1 if row<rows else 0,uniform='quick-row' if row<rows else '')
            for index,card in enumerate(quick_actions):card.grid(row=index//columns,column=columns-1-index%columns)
        actions.bind('<Configure>',resize_shortcuts,add='+')
        quick_footer=tk.Frame(quick.inner,bg='#EFF5F8');quick_footer.pack(side='bottom',fill='x',padx=6,pady=(0,6))
        footer_buttons=[ttk.Button(quick_footer,text=title,style='Small.Dark.TButton',command=lambda k=key:app.show_page(k))
                        for title,key in [('الإعدادات','settings'),('الأرشيف','archive'),('التقارير','reports')]]
        fit_buttons(quick_footer,footer_buttons,(3,2,1),padx=2,pady=2)
        actions.pack(fill='both',expand=True,padx=6,pady=6)

        activity=RoundedPanel(content,fill=self.PANEL,border=THEME_COLORS['violet'],radius=20,padding=1,canvas_bg=self.DASH)
        activity.grid(row=0,column=1,sticky='nsew',padx=3)
        ahead=tk.Frame(activity.inner,bg=THEME_COLORS['violet'],height=34);ahead.pack(fill='x');ahead.pack_propagate(False)
        label(ahead,'آخر النشاط',bg=THEME_COLORS['violet'],fg='#FFFFFF',font=('Arial',11,'bold'),anchor='e').pack(side='right',padx=8,pady=3)
        ttk.Button(ahead,text='السجل',style='Indigo.TButton',command=lambda:app.show_page('reports')).pack(side='left',padx=4,pady=2)
        self.activity_rows=tk.Frame(activity.inner,bg=self.PANEL);self.activity_rows.pack(fill='both',expand=True,padx=6,pady=6)

        live=RoundedPanel(content,fill=self.PANEL,border=THEME_COLORS['accent2'],radius=20,padding=1,canvas_bg=self.DASH)
        live.grid(row=0,column=0,sticky='nsew',padx=(0,3))
        live_head=tk.Frame(live.inner,bg=THEME_COLORS['accent2'],height=34);live_head.pack(fill='x');live_head.pack_propagate(False)
        label(live_head,'بيانات الراوتر',bg=THEME_COLORS['accent2'],fg='#FFFFFF',font=('Arial',11,'bold'),anchor='e').pack(side='right',padx=(8,4),pady=3)

        # Router facts stay in ONE authoritative panel: five compact rows,
        # right-side icons in a single vertical column, never duplicate data.
        info_list=tk.Frame(live.inner,bg=self.PANEL)
        info_list.pack(fill='x',padx=7,pady=(5,2))
        self.router_data_labels={}
        data_specs=(
            ('type','نوع الجهاز','▣',THEME_COLORS['navy']),
            ('name','اسم الراوتر','▤',THEME_COLORS['blue']),
            ('version','RouterOS','◈',THEME_COLORS['violet']),
            ('host','عنوان الراوتر','◉',THEME_COLORS['blue']),
            ('uptime','مدة تشغيل الراوتر','◷',THEME_COLORS['navy']),
        )
        for key,title,symbol,tone in data_specs:
            cell=tk.Frame(info_list,bg=THEME_COLORS['pale'],
                          highlightbackground=THEME_COLORS['line'],highlightthickness=1,height=37)
            cell.pack(fill='x',pady=1);cell.pack_propagate(False)
            icon_box=tk.Frame(cell,bg=THEME_COLORS['pale'],width=36,height=34)
            icon_box.pack(side='right',padx=(4,3),pady=1);icon_box.pack_propagate(False)
            tk.Label(icon_box,text=symbol,bg=THEME_COLORS['pale'],fg=tone,
                     font=('Segoe UI Symbol',16,'bold')).pack(fill='both',expand=True)
            text=tk.Frame(cell,bg=THEME_COLORS['pale'])
            text.pack(side='right',fill='both',expand=True,padx=(5,2),pady=1)
            label(text,title,bg=THEME_COLORS['pale'],fg=MUTED,
                  font=('Arial',8,'bold'),anchor='e').pack(fill='x')
            value=label(text,'—',bg=THEME_COLORS['pale'],fg=INK,
                        font=('Arial',10,'bold'),anchor='e')
            value.pack(fill='x')
            if key=='uptime':self.uptime_value=value
            else:self.router_data_labels[key]=value

        self.traffic_rows=tk.Frame(live.inner,bg=self.PANEL);self.traffic_rows.pack(fill='x',padx=6,pady=(1,5))
        banner=tk.Frame(self.body,bg=THEME_COLORS['navy'],height=92)
        banner.pack(fill='x',padx=7,pady=(0,4));banner.pack_propagate(False)
        banner_path=Path(__file__).resolve().parent.parent/'assets'/'router_banner.jpg'
        try:
            with Image.open(banner_path) as original:
                banner_photo=ImageTk.PhotoImage(original.convert('RGB').crop((0,0,min(270,original.width),original.height)))
            router_art=tk.Label(banner,image=banner_photo,bg=THEME_COLORS['navy'],bd=0)
            router_art.image=banner_photo
            router_art.pack(side='left',fill='y')
        except (OSError,tk.TclError):pass
        slogan=tk.Frame(banner,bg=THEME_COLORS['navy'])
        slogan.pack(side='right',fill='both',expand=True,padx=(3,12),pady=6)
        # Keep the four existing maintenance callbacks and widths; give the
        # buttons the space freed by removing the section heading.
        banner_actions=tk.Frame(slogan,bg=THEME_COLORS['navy'])
        banner_actions.pack(fill='both',expand=True)
        for title,action,color in (
                ('تعطيل الهوتسبوت','disable','#6654A5'),
                ('إعادة بناء / تحسين','optimize','#2779B8'),
                ('إعادة تشغيل الراوتر','reboot','#D47F24'),
                ('استعادة الهوتسبوت','restore','#187B5B')):
            action_button=tk.Button(banner_actions,text=title,command=lambda a=action:app.open_maintenance(a),
                      font=('Arial',12,'bold'),bg=color,fg='white',activebackground=color,
                      activeforeground='white',bd=0,highlightthickness=0,padx=5,pady=6,
                      cursor='hand2',justify='center')
            action_button.pack(side='right',expand=True,fill='both',padx=2)
            # Keep the larger writing legible without widening any button at
            # the original 940px window size; wrap only when space is tight.
            action_button.bind('<Configure>',lambda event,button=action_button:
                               button.configure(wraplength=max(50,event.width-15)))

    @staticmethod
    def _pct(used,total):
        try:
            total=float(total or 0);used=float(used or 0)
            return int(round(100*used/total)) if total>0 else None
        except (TypeError,ValueError,OverflowError):return None

    def _render_traffic(self,interfaces):
        for child in self.traffic_rows.winfo_children():child.destroy()
        rows=sorted((interfaces or []),key=lambda r:safe_int(r.get('rx',0))+safe_int(r.get('tx',0)),reverse=True)[:2]
        if not rows:return
        head=tk.Frame(self.traffic_rows,bg=THEME_COLORS['title_bg']);head.pack(fill='x',pady=(1,1))
        for text,width in [('الواجهة',12),('تنزيل',11),('رفع',11)]:label(head,text,bg=THEME_COLORS['title_bg'],fg='#FFFFFF',font=('Arial',8,'bold'),width=width).pack(side='right')
        for item in rows:
            row=tk.Frame(self.traffic_rows,bg=THEME_COLORS['pale']);row.pack(fill='x',pady=1)
            name=item.get('name','—')+(' ●' if item.get('running') else '')
            label(row,name,bg=THEME_COLORS['pale'],fg=INK,font=('Arial',8,'bold'),width=12,anchor='e').pack(side='right',padx=2)
            label(row,readable_bytes(item.get('rx',0)),bg=THEME_COLORS['pale'],fg=THEME_COLORS['blue'],font=('Arial',7),width=11).pack(side='right')
            label(row,readable_bytes(item.get('tx',0)),bg=THEME_COLORS['pale'],fg=THEME_COLORS['red'],font=('Arial',7),width=11).pack(side='right')

    def _render_activity(self):
        for child in self.activity_rows.winfo_children():child.destroy()
        rows=list(self.db.activities(limit=6))
        if not rows:
            label(self.activity_rows,'لا توجد عمليات مسجلة بعد.',bg=self.PANEL,fg=MUTED,font=('Arial',9),anchor='center').pack(fill='both',expand=True,pady=20)
            return
        tones={'SUCCESS':THEME_COLORS['green'],'PARTIAL':THEME_COLORS['yellow'],'ERROR':THEME_COLORS['red'],'INFO':THEME_COLORS['blue']}
        for item in rows:
            row=tk.Frame(self.activity_rows,bg=THEME_COLORS['pale'],highlightbackground=THEME_COLORS['line'],highlightthickness=1,height=29)
            row.pack(fill='x',pady=1);row.pack_propagate(False)
            status=str(item['status'] or 'INFO').upper();tone=tones.get(status,THEME_COLORS['blue'])
            tk.Frame(row,bg=tone,width=5).pack(side='right',fill='y')
            action=str(item['action'] or item['category'] or 'عملية')
            label(row,AR(action[:34]),bg=THEME_COLORS['pale'],fg=INK,font=('Arial',8,'bold'),anchor='e').pack(side='right',fill='both',expand=True,padx=6)
            stamp=str(item['ts'] or '')[11:16] or '—'
            label(row,stamp,bg=THEME_COLORS['pale'],fg=MUTED,font=('Arial',8),anchor='w',width=6).pack(side='left',padx=4)

    def show_license_status(self, access):
        """Synchronize the lightweight clock with the existing 5-second license check."""
        self._license_clock_mode = access.mode
        self._license_clock_term = access.remaining_term
        self._license_clock_message = access.message
        self._license_clock_remaining = access.remaining_seconds
        self._license_clock_end = (time.monotonic() + access.remaining_seconds
                                   if access.remaining_seconds >= 0 else None)
        self._paint_license_clock()
        if self._license_clock_job is None:
            self._license_clock_job = self.after(1000, self._license_tick)

    def _paint_license_clock(self):
        # Only changes a StringVar and colors; never revalidates license, opens
        # files, or sends requests to the router on the one-second tick.
        mode = self._license_clock_mode
        remaining = (-1 if self._license_clock_end is None else
                     max(0, math.ceil(self._license_clock_end - time.monotonic())))
        if mode == 'active':
            term = ('  |  المدة: ' + self._license_clock_term) if self._license_clock_term else ''
            text = 'مفعّل ✓  |  العدّاد: %s%s' % (format_license_countdown(remaining), term)
            bg, fg = '#E3F8EC', '#116139'
        elif mode == 'trial':
            text = 'التجربة المجانية — المتبقي: %s' % format_license_countdown(remaining)
            bg, fg = '#FFF4DC', '#805700'
        else:
            text = self._license_clock_message
            bg, fg = '#FDE8E8', '#9C2727'
        self.license_banner_text.set(text)
        self.license_banner.configure(bg=bg)
        self.license_banner_label.configure(bg=bg, fg=fg)

    def _license_tick(self):
        self._license_clock_job = None
        if not self.winfo_exists():
            return
        self._paint_license_clock()
        self._license_clock_job = self.after(1000, self._license_tick)

    def destroy(self):
        job = getattr(self, '_license_clock_job', None)
        if job is not None:
            try:
                self.after_cancel(job)
            except tk.TclError:
                pass
            self._license_clock_job = None
        super().destroy()

    def refresh(self):
        self.show_license_status(self.app.current_license_access())
        catalog=self.app.network_catalog;router=self.router_row()
        um=catalog.get('services',{}).get('userman',{})
        live_src=catalog.get('active_clients_sources',{})
        online=catalog.get('active_clients')
        total_users=um.get('users')
        r=catalog.get('resource',{})
        cpu=r.get('cpu-load')
        uptime=r.get('uptime','—')
        interfaces=catalog.get('interfaces',[]) or []
        self.cards[0].set_value(total_users,'مدير المستخدمين')
        online_note='Hotspot %s + PPPoE %s' % (live_src.get('hotspot','—'),live_src.get('ppp','—'))
        if catalog.get('active_clients_partial'):online_note+=' | قراءة جزئية'
        self.cards[1].set_value(online,online_note)

        life=um.get('lifecycle',{}) or {}
        if life.get('complete'):
            for key in ('active','expired'):
                self.lifecycle_cards[key].set_value(safe_int(life.get(key,0)))
            life_time=str(life.get('updated_at') or catalog.get('inventory_updated_at') or '').replace('T',' ')
            note='آخر قراءة: '+(life_time or 'الآن')
            if life.get('server_side'):
                note+='  •  RouterOS 6: عدّ مباشر داخل الراوتر'
            elif life.get('legacy'):
                note+='  •  RouterOS 6: قراءة مباشرة من حالة المستخدم وحدود الباقة'
            if safe_int(life.get('unclassified',0))>0:
                note+='  •  غير مصنفة: %s' % f"{safe_int(life.get('unclassified',0)):,}"
            if life.get('stale'):note+='  •  تُعرض آخر قراءة ناجحة'
            self.lifecycle_note.configure(text=AR(note),fg='#17345A')
        else:
            for key in ('active','expired'):self.lifecycle_cards[key].set_value('—')
            if self.app.lifecycle_busy:
                amount=um.get('users')
                reason=('جارٍ فحص %s كرت مدير المستخدمين في الخلفية...' % f'{safe_int(amount):,}'
                        if amount not in (None,'') else 'جارٍ فحص كروت مدير المستخدمين في الخلفية...')
            else:
                reason=life.get('reason') or um.get('lifecycle_error') or 'اضغط «تحديث حالة الكروت» لقراءة النشط والمنتهي'
            self.lifecycle_note.configure(text=AR(str(reason)[:125]),fg='#8B2E18' if reason else '#17345A')
        if router:
            host=router.get('host','—');port=router.get('port','—');identity=self.app.session.identity or 'الراوتر المتصل'
            board=r.get('board-name',r.get('platform','—'));version=r.get('version','—')
            ram_used=safe_int(r.get('total-memory',0))-safe_int(r.get('free-memory',0));ram=self._pct(ram_used,r.get('total-memory'))
            router_values={
                'name':identity,
                'host':'%s:%s' % (host,port),
                'version':version,
                'type':board,
            }
            for key,value in router_values.items():
                self.router_data_labels[key].configure(text=AR(str(value or '—')))
            self.uptime_value.configure(text=AR(str(uptime or '—')))
            self.resource_pair.set_values(cpu,ram)
            self.health.configure(bg='#DDF7F0');self.health_icon.configure(bg='#E3F8EC',fg=THEME_COLORS['green'],text='✓');self.health_text.configure(bg='#E3F8EC',fg='#0C6C3C',text=AR('الراوتر متصل'))
            if getattr(self.app.session.api,'sock',None) is None or catalog.get('resource_stale'):
                self.health.configure(bg='#FFF1E6');self.health_icon.configure(bg='#FFF1E6',fg=THEME_COLORS['yellow'],text='!')
                self.health_text.configure(bg='#FFF1E6',fg=THEME_COLORS['yellow'],text=AR('قراءة قديمة / اتصال مؤقت'))
        else:
            for value in self.router_data_labels.values():value.configure(text='—')
            self.uptime_value.configure(text='—')
            self.resource_pair.set_values(None,None)
            self.health.configure(bg='#FFF1E6');self.health_icon.configure(bg='#FFF1E6',fg=THEME_COLORS['yellow'],text='!');self.health_text.configure(bg='#FFF1E6',fg=THEME_COLORS['yellow'],text=AR('الراوتر غير متصل'))
        self._render_traffic(interfaces)
        self._render_activity()
        self.date_label.configure(text=AR('التاريخ: '+datetime.now().strftime('%d/%m/%Y')))

class NetworkProfilesPage(NetworkPage):
    def __init__(self,parent,app):
        super().__init__(parent,app);self.editing=None;self._rows={};self._service=None
        self.tree=table(self.body,[('name','اسم الباقة',170),('rate','الرفع / التنزيل',135),('capacity','السعة',95),
                        ('validity','الصلاحية',90),('uptime','الوقت / الجلسة',100),('shared','المشتركون',80),('price','السعر',75)],6)
        self.tree.bind('<<TreeviewSelect>>',self.select_row)
        form_outer,form=section_card(self.body,'بيانات الباقة على الراوتر',THEME_COLORS['blue'],'#315C8B')
        form_outer.pack(fill='x',pady=3)
        self.vars={key:tk.StringVar() for key in ('name','rate','shared','uptime','validity','price','owner','local-address','remote-address','only-one','starts')}
        self.inputs={}
        rows=[('name','اسم الباقة',None),('rate','الرفع / التنزيل مثل 512k/2M',None),('shared','المشتركون (off لإلغاء الحد)',None),
              ('uptime','مدة الجلسة مثل 1h30m',None),('validity','الصلاحية مثل 3d',None),('price','السعر / سعر الطباعة',None),
              ('owner','مدير المستخدمين (الإصدار ٦)',None),('local-address','عنوان PPP المحلي',None),('remote-address','عنوان العميل / مجموعة العناوين',None),
              ('only-one','جلسة PPP واحدة',{'default':'حسب الإعداد الافتراضي','yes':'نعم','no':'لا'}),
              ('starts','بداية صلاحية مدير المستخدمين',{'first-auth':'عند أول دخول','assigned':'عند إسناد الباقة'})]
        for i,(key,title,choices) in enumerate(rows):self.inputs[key]=mini_field(form,title,self.vars[key],i%4,choices,width=12,row=i//4)
        footer_outer,footer=section_card(self.body,'إجراءات الباقة',THEME_COLORS['accent2'],'#176F62')
        footer_outer.pack(fill='x',pady=4)
        footer_buttons=[]
        for title,command,style in [('إضافة / حفظ على الراوتر',self.save,'Accent.TButton'),('جديد',self.new,'Dark.TButton'),
                ('إنشاء كروت الباقة',self.use,'Green.TButton'),('محددات مدير المستخدمين',self.edit_limits,'Dark.TButton'),('حذف المحدد',self.remove,'Danger.TButton')]:
            footer_buttons.append(ttk.Button(footer,text=title,command=command,style=style))
        fit_buttons(footer,list(reversed(footer_buttons)),(5,3,2,1),pady=5)
        self.new()

    def new(self):
        self.editing=None
        defaults={'shared':'1','uptime':'0s','validity':'unlimited','price':'0','only-one':'default','starts':'first-auth'}
        for key,var in self.vars.items():var.set(defaults.get(key,''))
        self.tree.selection_remove(self.tree.selection())

    def refresh(self):
        super().refresh();service=self.app.active_service
        if service!=self._service:self.new();self._service=service
        self._rows={str(row['id']):row for row in self.state().get('profiles',[])}
        for ident in self.tree.get_children():self.tree.delete(ident)
        for ident,row in self._rows.items():
            if service!='userman':
                saved=self.db.saved_router_preset(self.router_row()['id'],service,row['id'])
                if saved:row['price']=str(saved['settings'].get('create.price',0))
            self.tree.insert('','end',iid=ident,values=(row['name'],row['rate'] or '—',readable_bytes(row['capacity']) if row['capacity'] else '—',
                         row['validity'] or '—',row['uptime'] or '—',row['shared'] or '—',row['price'] or '—'))
        enabled={'name','price'}
        enabled.update({'rate','shared','uptime'} if service=='hotspot' else {'rate','uptime','local-address','remote-address','only-one'} if service=='ppp' else {'shared','validity','owner','starts'})
        for key,entry in self.inputs.items():entry.configure(state=('readonly' if key in ('only-one','starts') else 'normal') if key in enabled else 'disabled')

    def select_row(self,_event=None):
        selected=self.tree.selection()
        if not selected:return
        row=self._rows[selected[0]];self.editing=row
        raw=row['raw']
        for key in self.vars:self.vars[key].set(str(row.get(key,raw.get(key,''))))
        self.vars['owner'].set(raw.get('owner',raw.get('customer','')))
        self.vars['starts'].set(raw.get('starts-when','first-auth' if raw.get('starts-at','logon')=='logon' else 'assigned'))
        self.vars['only-one'].set(raw.get('only-one','default'))
        if not self.vars['price'].get():self.vars['price'].set('0')
        if not self.vars['shared'].get():self.vars['shared'].set('1')

    def save(self):
        if not self.state().get('available'):return messagebox.showerror('الراوتر',self.state().get('error') or 'انتظر قراءة الباقات.')
        values={key:var.get().strip() for key,var in self.vars.items()};service=self.app.active_service;existing=self.editing
        if not values['name']:return messagebox.showerror('الباقة','أدخل اسم الباقة.')
        if not messagebox.askyesno('حفظ على الراوتر','سيتم %s الباقة «%s» على الراوتر المتصل. متابعة؟' % ('تعديل' if existing else 'إضافة',values['name'])):return
        try:
            price=float(values['price'] or 0)
            if not math.isfinite(price) or not 0<=price<=1e9:raise ValueError('السعر غير صالح.')
        except ValueError as exc:return messagebox.showerror('السعر',str(exc))
        def done(ident):
            if service!='userman' and ident:
                profile={'id':ident,'name':values['name'],'price':price}
                saved=self.db.router_preset(self.router_row()['id'],service,profile)
                settings=dict(saved['settings']);settings['create.price']=price;settings['create.profile']=values['name']
                self.db.save_package(saved['name'],settings,saved['id'])
                if saved['id']==self.app.active_package_id:self.db.set_setting('create.price',price)
            self.editing=None;self.app.refresh_network();messagebox.showinfo('تم','قبل الراوتر بيانات الباقة. جارٍ تحديث القائمة.')
        self.app.run_network(lambda g:g.save_profile(service,values,existing),done,affects_inventory=service=='userman')

    def use(self):
        if not self.editing:return messagebox.showinfo('الباقات','حدد باقة من الجدول.')
        if self.app.select_router_profile(self.editing['id']):self.app.show_page('create')

    def remove(self):
        if not self.editing:return
        row=self.editing;service=self.app.active_service
        if not messagebox.askyesno('حذف من الراوتر','حذف الباقة «%s» من الراوتر؟ قد تكون مرتبطة بمستخدمين.' % row['name']):return
        self.app.run_network(lambda g:g.remove_profile(service,row),lambda _r:self.app.refresh_network(),affects_inventory=service=='userman')

    def edit_limits(self):
        if self.app.active_service!='userman' or not self.editing:return messagebox.showinfo('محددات الباقة','اختر باقة مدير المستخدمين من الجدول.')
        LimitationEditor(self,self.app,self.editing)


class LimitationEditor(tk.Toplevel):
    def __init__(self,parent,app,profile):
        super().__init__(parent);self.app=app;self.profile=profile;self.rows={};self.editing=None;self.busy=False
        self.title(AR('محددات باقة: '+profile['name']));self.configure(bg=SURFACE);self.transient(app)
        self.geometry('760x470');self.minsize(620,380)
        label(self,'المحدد قد تستخدمه عدة باقات؛ تعديله يؤثر في كل الباقات المرتبطة به.',wraplength=700).pack(fill='x',pady=5)
        self.tree=table(self,[('name','المحدد',200),('capacity','السعة',130),('time','الوقت',120),('linked','مرتبط بهذه الباقة',145)],5)
        self.tree.bind('<<TreeviewSelect>>',self.select)
        form=box(self);form.pack(fill='x',padx=4)
        self.vars={k:tk.StringVar(value='') for k in ('name','transfer-limit','uptime-limit','rate-limit-rx','rate-limit-tx','owner')}
        for i,(key,title) in enumerate((('name','الاسم'),('transfer-limit','السعة بالبايت (0 مفتوح)'),('uptime-limit','الوقت مثل 2h'),
                                      ('rate-limit-rx','سرعة الرفع'),('rate-limit-tx','سرعة التنزيل'),('owner','مدير المستخدمين (الإصدار ٦)'))):mini_field(form,title,self.vars[key],i%3,width=15,row=i//3)
        buttons=tk.Frame(self,bg=SURFACE);buttons.pack(fill='x',pady=7)
        for title,command in [('جديد',self.new),('حفظ المحدد',self.save),('ربط بالباقة',self.link),('فك الربط',self.unlink),('إغلاق',self.destroy)]:ttk.Button(buttons,text=title,command=command).pack(side='right',padx=4)
        self.new();self.refresh()

    def _alive(self):
        try:return bool(self.winfo_exists())
        except tk.TclError:return False

    def _failed(self,exc):
        if not self._alive():return
        self.busy=False
        messagebox.showerror('محددات الباقة',str(exc),parent=self)

    def _finished(self,refresh_network=True):
        if self._alive():
            self.busy=False;self.refresh()
        if refresh_network:self.app.refresh_network()

    def refresh(self):
        def done(rows):
            if not self._alive():return
            self.rows={r['.id']:r for r in rows}
            for item in self.tree.get_children():self.tree.delete(item)
            for ident,row in self.rows.items():
                linked=any(link.get('limitation') in (row.get('name'),ident) for link in self.profile.get('links',[]))
                self.tree.insert('','end',iid=ident,values=(row.get('name',''),readable_bytes(row.get('transfer-limit',0)),row.get('uptime-limit',''),AR('نعم' if linked else 'لا')))
        self.app.run_network(lambda g:g.limitations(),done,read_only=True,affects_inventory=False)

    def new(self):
        self.editing=None
        for key,var in self.vars.items():var.set('0' if key=='transfer-limit' else '0s' if key=='uptime-limit' else self.profile['raw'].get('owner','') if key=='owner' else '')

    def select(self,_event=None):
        selected=self.tree.selection()
        if selected:
            self.editing=self.rows[selected[0]]
            for key,var in self.vars.items():var.set(self.editing.get(key,''))

    def save(self):
        if self.busy or not self._alive():return
        values={k:v.get() for k,v in self.vars.items()};existing=self.editing
        if not messagebox.askyesno('حفظ المحدد','حفظ هذا المحدد على الراوتر؟',parent=self):return
        self.busy=True
        self.app.run_network(lambda g:g.save_limitation(values,existing),lambda _r:self._finished(),self._failed,affects_inventory=True)

    def link(self):
        if self.busy or not self._alive() or not self.editing:return
        item=self.editing
        aliases={str(item.get('.id','') or ''),str(item.get('name','') or '')};aliases.discard('')
        if any(str(link.get('limitation','') or '') in aliases for link in self.profile.get('links',[])):
            return messagebox.showinfo('ربط المحدد','هذا المحدد مرتبط بهذه الباقة مسبقًا.',parent=self)
        def done(_r):
            if self._alive() and not any(str(link.get('limitation','') or '') in aliases for link in self.profile.get('links',[])):
                self.profile.setdefault('links',[]).append({'limitation':item.get('name') or item.get('.id')})
            self._finished()
        self.busy=True
        self.app.run_network(lambda g:g.link_limitation(self.profile,item),done,self._failed,affects_inventory=True)

    def unlink(self):
        if self.busy or not self._alive() or not self.editing:return
        link=next((r for r in self.profile.get('links',[]) if r.get('limitation') in (self.editing['.id'],self.editing['name'])),None)
        if not link or not link.get('.id'):return messagebox.showinfo('تحديث','أغلق النافذة وحدّث الباقات لقراءة معرف الربط الحالي.',parent=self)
        if not messagebox.askyesno('فك الربط','فك هذا المحدد عن الباقة؟',parent=self):return
        def done(_r):
            if link in self.profile.get('links',[]):self.profile['links'].remove(link)
            self._finished()
        self.busy=True
        self.app.run_network(lambda g:g.link_limitation(self.profile,self.editing,link['.id']),done,self._failed,affects_inventory=True)


class VoucherWorkspace(NetworkPage):
    ORDERS={'متعرج':'snake','أفقي من اليمين':'right_to_left','أفقي من اليسار':'left_to_right',
            'عمودي من اليمين':'column_right','عمودي من اليسار':'column_left'}
    FORMATS={'اسم المستخدم فقط':'code_only','متطابقتان':'code_same_password','مختلفتان':'code_password'}
    # Preserve the two original selectors and all their callbacks.
    # English letters are lowercase in every newly selected preset.
    CHARSET_MODES=('أرقام فقط','أحرف فقط','مختلطة')
    CHARSET_PRESETS={
        'أرقام فقط':'0123456789',
        'أحرف فقط':'abcdefghijklmnopqrstuvwxyz',
        'مختلطة':'0123456789abcdefghijklmnopqrstuvwxyz',
    }
    MAP={'quantity':'create.quantity','code_length':'create.code_length','password_length':'create.password_length',
         'username_chars':'create.username_chars','password_chars':'create.password_chars','prefix':'create.username_prefix','start':'create.start_sequence',
         'price':'create.price','capacity':'create.limit_bytes','unit':'create.limit_unit','hours':'create.limit_uptime_h',
         'minutes':'create.limit_uptime_m','days':'create.validity_days','server':'create.server',
         'title':'card.title_text','phone':'card.phone_text','width':'card.width_mm','height':'card.height_mm',
         'font':'card.font_size','font_family':'card.font_family',
         'border_width':'card.border_width','qr_size':'card.qr_size_mm','mode':'card.background_mode',
         'rows':'print.rows','cols':'print.columns','gap_h':'print.gap_h_mm','gap_v':'print.gap_v_mm',
         'margin':'print.page_margin_mm','paper':'print.paper','printer':'print.default_printer'}
    INTEGER={'quantity','code_length','password_length','start','hours','minutes','days','font','border_width','qr_size','rows','cols'}
    DECIMAL={'price','capacity','width','height','gap_h','gap_v','margin'}
    LAYOUT_INPUTS=frozenset(('width','height','paper','orientation','gap_h','gap_v','margin'))

    @staticmethod
    def permitted_choices(low,high,step=1):
        """Only values accepted by the existing settings/printing validators.

        0 is legal for margins, gaps and borders; a zero-sized voucher,
        password, username or font is not a valid printable setting.
        """
        scale=round(1/step)
        return [str(n//scale) if n%scale==0 else ('%g' % (n/scale))
                for n in range(round(low*scale),round(high*scale)+1)]

    FIELD_CHOICES={
        'code_length':permitted_choices.__func__(1,64),
        'password_length':permitted_choices.__func__(1,64),
        'width':permitted_choices.__func__(1,300,.5),
        'height':permitted_choices.__func__(1,300,.5),
        'font':permitted_choices.__func__(*CARD_FONT_LIMITS),
        'border_width':permitted_choices.__func__(*CARD_BORDER_LIMITS),
        'gap_h':permitted_choices.__func__(0,300,.5),
        'gap_v':permitted_choices.__func__(0,300,.5),
        'margin':permitted_choices.__func__(0,300,.5),
    }

    def __init__(self,parent,app):
        super().__init__(parent,app);self._loading=True;self._loaded=None;self._preview_job=None;self._batches={};self._cards_cache={};self._batches_dirty=False
        self.remote_cards=None;self.busy=False;self._bg_data={}
        self._position_overrides={}
        self._selected_element=None
        self.vars={key:tk.StringVar(value='') for key in list(self.MAP)+['profile','format','orientation','order','batch','customer','serial','duplex','username_mode','password_mode']}
        self.elements={key:tk.BooleanVar(value=False) for key in ('title','sequence','price','capacity','duration','date','phone','qr')}
        self.card_bold=tk.BooleanVar(value=True)
        self.reset_sequence=tk.BooleanVar(value=True)
        self.export_vars={key:tk.BooleanVar() for key in ('excel','csv','text')}
        self.total=tk.StringVar()
        self.page_count=tk.StringVar(value='—')
        self._sync_counts=False
        self._count_source='quantity'
        self._choice_widgets={}
        self.page_count.trace_add('write',lambda *_:self._pages_changed())
        self.batch_total=tk.StringVar()

        area=tk.Frame(self.body,bg=APP_BG);area.pack(fill='both',expand=True,padx=2,pady=2)
        # Screenshot composition: 42% credential/print column, 58% card editor.
        # Row weights, instead of fixed panel heights, preserve the existing
        # fixed-size desktop window on narrower Windows displays.
        area.columnconfigure(0,weight=42);area.columnconfigure(1,weight=58)
        area.rowconfigure(0,weight=1)
        left=tk.Frame(area,bg=APP_BG);left.grid(row=0,column=0,sticky='nsew',padx=(0,4),pady=0)
        center=tk.Frame(area,bg=APP_BG);center.grid(row=0,column=1,sticky='nsew',padx=(4,0),pady=0)
        left.grid_propagate(False);center.grid_propagate(False)
        left.columnconfigure(0,weight=1);center.columnconfigure(0,weight=1)
        for row,weight in ((0,2),(1,1),(2,3)):left.rowconfigure(row,weight=weight)
        center.rowconfigure(1,weight=1)

        # ----- Card/network data (right) -----
        route_outer,routing=section_card(center,'الباقة ومسار الإرسال',PRIMARY,'#0B3D71',action=('حفظ إعدادات الباقة',app.save_active_package),strong_title=True)
        route_outer.grid(row=0,column=0,sticky='ew',pady=(0,5))
        self.profile_combo=mini_field(routing,'الباقة من الراوتر',self.vars['profile'],0,choices=[],width=10)
        self.profile_combo.bind('<<ComboboxSelected>>',self.choose_profile)
        self.server_combo=mini_field(routing,'السيرفر',self.vars['server'],1,choices=['all'],width=10)
        self.customer_combo=mini_field(routing,'مدير المستخدمين (الإصدار ٦)',self.vars['customer'],1,choices=[],readonly=False,width=9,row=1)
        self.customer_combo.master.grid_configure(column=0,columnspan=2)
        self.profile_note=label(routing,'',wraplength=500,justify='right',font=('Arial',9,'bold'),anchor='e',fg=MUTED)
        self.profile_note.grid(row=2,column=0,columnspan=2,sticky='ew',padx=4,pady=(2,0))
        routing.bind('<Configure>',lambda event:self.profile_note.configure(wraplength=max(120,event.width-16)))

        basics_outer,basics=section_card(left,'بيانات التوليد الأساسية',THEME_COLORS['blue'],'#0B3D71',icon='⚙',strong_title=True)
        basics_outer.grid(row=1,column=0,sticky='nsew',pady=(0,5))
        basics.rowconfigure(0,weight=1)
        # All five generation fields share one horizontal row, including the
        # editable number of physical sheets next to quantity.
        for i,(key,title) in enumerate((('quantity','عدد الكروت'),('pages','عدد الصفحات'),
                                        ('prefix','بادئة الرقم'),('code_length','طول المستخدم'),
                                        ('password_length','طول المرور'))):
            var=self.page_count if key=='pages' else self.vars[key]
            widget=mini_field(basics,title,var,i,choices=self.FIELD_CHOICES.get(key),width=5,row=0)
            if key in self.FIELD_CHOICES:self._choice_widgets[key]=widget
            if key=='pages':self.page_count_widget=widget
            if key=='password_length':self.password_length_widget=widget
        identity_outer,identity_body=section_card(left,'اسم المستخدم وكلمة المرور',THEME_COLORS['blue'],'#0B3D71',icon='●',strong_title=True)
        identity_outer.grid(row=0,column=0,sticky='nsew',pady=(0,5))
        credential_row=tk.Frame(identity_body,bg=SURFACE)
        credential_row.pack(fill='x',pady=(0,2))
        # The existing create.card_format is still the single source of truth.
        # These visible controls are an alternative presentation, not a new
        # card-generation mode or a new persistent setting.
        self.credential_mode=tk.StringVar(value='اسم مستخدم فقط')
        self._last_password_relation='مختلفتان'
        self.relation_different=tk.BooleanVar(value=False)
        self.relation_same=tk.BooleanVar(value=False)
        self.format_combo=ttk.Combobox(credential_row,textvariable=self.credential_mode,
            values=('اسم مستخدم فقط','اسم مستخدم وكلمة مرور'),state='readonly',
            justify='right',width=15)
        self.format_combo.bind('<<ComboboxSelected>>',self.change_credential_mode)
        self.different_checkbox=ttk.Checkbutton(credential_row,text='مختلفتين',
            variable=self.relation_different,command=lambda:self.choose_password_relation('مختلفتان'))
        self.same_checkbox=ttk.Checkbutton(credential_row,text='متطابقتين',
            variable=self.relation_same,command=lambda:self.choose_password_relation('متطابقتان'))
        # One RTL row at every supported window width: selector on the right,
        # its two existing exclusive options immediately to its left.
        credential_row.columnconfigure(0,weight=0)
        credential_row.columnconfigure(1,weight=0)
        credential_row.columnconfigure(2,weight=1)
        self.same_checkbox.grid(row=0,column=0,sticky='w',padx=(0,2),pady=1)
        self.different_checkbox.grid(row=0,column=1,sticky='w',padx=(0,2),pady=1)
        self.format_combo.grid(row=0,column=2,sticky='ew',padx=(0,2),pady=1)
        identity=tk.Frame(identity_body,bg=SURFACE)
        mini_field(identity,'بداية التسلسل',self.vars['start'],0,width=6)
        username_mode=mini_field(identity,'نوع المستخدم',self.vars['username_mode'],1,choices=list(self.CHARSET_MODES),width=9)
        password_mode=mini_field(identity,'نوع المرور',self.vars['password_mode'],2,choices=list(self.CHARSET_MODES),width=9)
        self.password_mode_widget=password_mode
        username_mode.bind('<<ComboboxSelected>>',lambda _e:self.apply_charset_mode('username'))
        password_mode.bind('<<ComboboxSelected>>',lambda _e:self.apply_charset_mode('password'))
        reset_cell=tk.Frame(identity,bg=SURFACE);reset_cell.grid(row=0,column=3,sticky='ew',padx=3,pady=2);identity.columnconfigure(3,weight=1)
        label(reset_cell,'إعادة التسلسل',font=('Arial',10,'bold'),fg=MUTED,anchor='e').pack(fill='x',pady=(0,1))
        reset_button=ttk.Checkbutton(reset_cell,text='لكل دفعة',variable=self.reset_sequence)
        reset_button.pack(anchor='e',pady=3)
        chars=tk.Frame(identity_body,bg=SURFACE);chars.pack(side='bottom',fill='x',pady=(1,0))
        identity.pack(fill='both',expand=True);identity.rowconfigure(0,weight=1)
        inline_field(chars,'أحرف المستخدم',self.vars['username_chars'],0,width=7)
        self.password_chars_widget=inline_field(chars,'أحرف المرور',self.vars['password_chars'],1,width=7)

        # Capacity, duration and price are retained in the settings contract
        # for old packages and generated cards, but are intentionally not shown
        # because prepared card artwork now carries those values.
        self.limit_inputs={}

        # Five compact areas; only this editor uses the approved champagne theme.
        # Printing colours, dimensions and the shared renderer are untouched.
        design_bg='#F8EFD9';design_panel='#FFFDF8';design_line='#D2AA61';design_ink='#0B2749'
        design_style=ttk.Style(self)
        for name in ('CardDesign.TCombobox','CardDesign.TEntry'):
            design_style.configure(name,fieldbackground='#FFFFFF',background='#FFFFFF',
                foreground=design_ink,arrowcolor=design_ink,bordercolor=design_line,
                lightcolor=design_line,darkcolor=design_line,padding=(3,3),arrowsize=12)
        design_style.map('CardDesign.TCombobox',fieldbackground=[('readonly','#FFFFFF')],
            foreground=[('readonly',design_ink)],selectbackground=[('readonly','#FFFFFF')],
            selectforeground=[('readonly',design_ink)])
        design_style.configure('CardDesign.TCheckbutton',background=design_panel,
            foreground=design_ink,font=('Arial',9),padding=0)
        design_style.map('CardDesign.TCheckbutton',background=[('active',design_bg)])
        for style in ('CardDesign.Teal.TButton','CardDesign.Purple.TButton'):
            design_style.configure(style,padding=(5,3),font=('Arial',10,'bold'))

        def design_header(parent,title,icon=None):
            head=tk.Frame(parent,bg=design_ink)
            if icon:
                label(head,icon,bg=design_ink,fg='#FFFFFF',font=('Segoe UI Symbol',10,'bold')).pack(side='left',padx=3)
            label(head,title,bg=design_ink,fg='#FFFFFF',font=('Arial',11,'bold'),
                  anchor='e',padx=4,pady=2).pack(side='right',fill='x',expand=True)
            return head

        self.preview_outer=tk.Frame(center,bg=design_bg,highlightbackground=design_line,
                                    highlightthickness=1,bd=0)
        self.preview_outer.grid(row=1,column=0,sticky='nsew',pady=(0,5))
        design_header(self.preview_outer,'معاينة وتصميم الكرت','◈').pack(fill='x')
        editor=tk.Frame(self.preview_outer,bg=design_bg)
        editor.pack(fill='both',expand=True,padx=2,pady=2)
        editor.columnconfigure(0,weight=1);editor.rowconfigure(1,weight=1)

        # Original actions, together at the reader's right, above the image.
        self.design_toolbar=tk.Frame(editor,bg=design_bg)
        self.design_toolbar.grid(row=0,column=0,sticky='ew',pady=(0,2))
        self.template_button=ttk.Button(self.design_toolbar,text='تغيير القالب',
            style='CardDesign.Teal.TButton',command=self.choose_background)
        self.template_button.pack(side='right',padx=(2,0))
        self.advanced_design_button=ttk.Button(self.design_toolbar,text='تعديلات متقدمة',
            style='CardDesign.Purple.TButton',command=lambda:app.show_page('design'))
        self.advanced_design_button.pack(side='right',padx=(2,0))

        self.preview_region=tk.Frame(editor,bg=design_bg)
        self.preview_region.grid(row=1,column=0,sticky='nsew')
        self.preview_region.columnconfigure(0,weight=1)
        self.preview_region.columnconfigure(1,weight=0)
        self.preview_region.rowconfigure(0,weight=1)
        self.canvas=tk.Canvas(self.preview_region,bg=design_panel,width=1,height=100,
                              highlightthickness=0,bd=0)
        self.canvas.grid(row=0,column=0,sticky='nsew',padx=(0,2))
        self._preview_editor=CardPreview(self.canvas)
        self.canvas.bind('<ButtonPress-1>',self._preview_drag_start)
        self.canvas.bind('<B1-Motion>',self._preview_drag_move)
        self.canvas.bind('<ButtonRelease-1>',self._preview_drag_end)
        self.canvas.bind('<MouseWheel>',self._preview_wheel)
        self.canvas.bind('<Button-4>',self._preview_wheel)
        self.canvas.bind('<Button-5>',self._preview_wheel)
        self.canvas.bind('<Configure>',lambda _e:self.schedule_preview())

        self.appearance_panel=tk.Frame(self.preview_region,bg=design_panel,
                                      highlightbackground=design_line,highlightthickness=1)
        self.appearance_panel.grid(row=0,column=1,sticky='ns')
        appearance_heading=label(self.appearance_panel,'خيارات مظهر الكرت',bg=design_ink,fg='#FFFFFF',
              font=('Arial',10,'bold'),anchor='e',padx=3,pady=2)
        appearance_heading.pack(fill='x')
        self.appearance_grid=tk.Frame(self.appearance_panel,bg=design_panel)
        self.appearance_grid.pack(fill='both',expand=True,padx=2,pady=1)
        # A single caption column and control column determine ALL four edges.
        # No row-specific offsets; column 0 is sized by the widest real control.
        self.appearance_grid.columnconfigure(0,weight=1)
        self.appearance_grid.columnconfigure(1,weight=0)
        self.appearance_controls={};self.appearance_labels={};self._card_color_swatches={}
        for row,(key,title) in enumerate((('font','حجـــم الخــــــط'),
                                         ('font_family','نــــوع الخـــــط'),
                                         ('font_color','لـــون الخـــــط'),
                                         ('number_color','لون رقم الكرت'))):
            self.appearance_grid.rowconfigure(row,weight=1,uniform='appearance')
            caption=label(self.appearance_grid,title,bg=design_panel,fg=design_ink,
                font=('Arial',9,'bold'),anchor='w',padx=0,pady=0)
            caption.grid(row=row,column=1,sticky='w',padx=(2,0),pady=1)
            self.appearance_labels[key]=caption
            if key in ('font','font_family'):
                choices=self.FIELD_CHOICES['font'] if key=='font' else ('Arial','Tahoma')
                widget=ttk.Combobox(self.appearance_grid,textvariable=self.vars[key],
                    values=choices,state='readonly',width=7,justify='center',
                    style='CardDesign.TCombobox',font=('Arial',9))
                if key=='font':self._choice_widgets[key]=widget
                else:self.font_family_combo=widget
            else:
                setting,fallback,command=(('card.font_color','#E7FFF7',self.choose_card_font_color)
                    if key=='font_color' else ('card.color.username','#000000',self.choose_card_number_color))
                widget=tk.Button(self.appearance_grid,text='تغيير',command=command,
                    font=('Arial',9,'bold'),relief='solid',bd=1,highlightthickness=0,
                    padx=3,pady=3,cursor='hand2')
                self._card_color_swatches[setting]=widget
                self._paint_color_swatch(widget,self.db.get_setting(setting,fallback))
            widget.grid(row=row,column=0,sticky='ew',padx=0,pady=1)
            self.appearance_controls[key]=widget

        # Reserve the real requested row heights: short windows must not squash
        # the four appearance controls. Only the lower options scroll if needed.
        minimum_preview_height=appearance_heading.winfo_reqheight()+4*(max(
            widget.winfo_reqheight() for widget in self.appearance_controls.values())+2)+4
        editor.rowconfigure(1,minsize=minimum_preview_height)
        lower=tk.Frame(editor,bg=design_bg)
        lower.grid(row=2,column=0,sticky='ew',pady=(2,0));lower.columnconfigure(0,weight=1)
        self.design_options_canvas=tk.Canvas(lower,bg=design_bg,bd=0,highlightthickness=0,width=1,height=1)
        self.design_options_canvas.grid(row=0,column=0,sticky='ew')
        self.design_options_scrollbar=ttk.Scrollbar(lower,orient='vertical',command=self.design_options_canvas.yview)
        self.design_options_canvas.configure(yscrollcommand=self.design_options_scrollbar.set)
        lower_content=tk.Frame(self.design_options_canvas,bg=design_bg)
        lower_window=self.design_options_canvas.create_window(0,0,window=lower_content,anchor='nw')
        lower_content.columnconfigure(0,weight=1)
        def fit_lower_options(_event=None):
            wanted=lower_content.winfo_reqheight()
            available=max(36,editor.winfo_height()-self.design_toolbar.winfo_reqheight()-minimum_preview_height-4)
            height=min(wanted,available)
            if int(self.design_options_canvas.cget('height'))!=height:
                self.design_options_canvas.configure(height=height)
            scrolling=wanted>height
            if scrolling and not self.design_options_scrollbar.winfo_manager():
                self.design_options_scrollbar.grid(row=0,column=1,sticky='ns')
            elif not scrolling and self.design_options_scrollbar.winfo_manager():
                self.design_options_scrollbar.grid_remove()
                self.design_options_canvas.yview_moveto(0)
            self.design_options_canvas.configure(scrollregion=(0,0,lower_content.winfo_width(),wanted))
        def fit_lower_width(event):
            self.design_options_canvas.itemconfigure(lower_window,width=event.width)
        self.design_options_canvas.bind('<Configure>',fit_lower_width)
        lower_content.bind('<Configure>',fit_lower_options)
        editor.bind('<Configure>',fit_lower_options,add='+')

        # The same five existing measurement/colour controls, immediately below.
        self.dimension_row=tk.Frame(lower_content,bg=design_bg)
        self.dimension_row.grid(row=0,column=0,sticky='ew',pady=(0,1))
        self.dimension_controls={}
        dimension_cells=[]
        for key,title in (('width','العرض (مم)'),('height','الطول (مم)'),
                          ('qr_size','حجم QR (مم)'),('border_width','سمك الحدود')):
            widget=mini_field(self.dimension_row,title,self.vars[key],len(dimension_cells),
                              choices=self.FIELD_CHOICES.get(key),width=5)
            widget.configure(style='CardDesign.TCombobox' if isinstance(widget,ttk.Combobox) else 'CardDesign.TEntry',
                             font=('Arial',10))
            for caption in widget.master.winfo_children():
                if isinstance(caption,tk.Label):caption.configure(fg=design_ink)
            if key in self.FIELD_CHOICES:self._choice_widgets[key]=widget
            self.dimension_controls[key]=widget
            dimension_cells.append(widget.master)
        border_cell=tk.Frame(self.dimension_row,bg=design_bg)
        label(border_cell,'لون الحدود',bg=design_bg,fg=design_ink,
              font=('Arial',10,'bold'),anchor='e',padx=0).pack(fill='x')
        border_button=tk.Button(border_cell,text='تغيير',command=self.choose_card_border_color,
            font=('Arial',10,'bold'),relief='solid',bd=1,highlightthickness=0,padx=3,pady=3,cursor='hand2')
        border_button.pack(fill='x')
        self._card_color_swatches['card.border_color']=border_button
        self._paint_color_swatch(border_button,self.db.get_setting('card.border_color','#00C98D'))
        self.dimension_controls['border_color']=border_button
        dimension_cells.append(border_cell)
        # Real requested sizes (including Windows DPI), not a pixel breakpoint.
        self._fit_design_rows(self.dimension_row,dimension_cells,(5,3,2,1),rtl=True)

        elements_outer=tk.Frame(lower_content,bg=design_panel,highlightbackground=design_line,highlightthickness=1)
        elements_outer.grid(row=1,column=0,sticky='ew',pady=(1,0))
        design_header(elements_outer,'عناصر الكرت').pack(fill='x')
        self.element_grid=tk.Frame(elements_outer,bg=design_panel)
        self.element_grid.pack(fill='x',padx=2,pady=1)
        self.element_buttons={}
        for key,title in (('capacity','السعة'),('phone','الهاتف'),('qr','QR'),
                          ('duration','الوقت'),('date','التاريخ'),('title','العنوان'),
                          ('sequence','التسلسل'),('price','السعر')):
            button=ttk.Checkbutton(self.element_grid,text=title,variable=self.elements[key],
                style='CardDesign.TCheckbutton',command=self.schedule_preview)
            self.element_buttons[key]=button
        self._fit_design_rows(self.element_grid,list(self.element_buttons.values()),(8,4,2,1),rtl=True,padx=0)

        print_outer,print_body=section_card(left,'إعداد الطباعة والدفعة',PRIMARY,'#0B3D71',icon='▣',strong_title=True)
        print_outer.grid(row=2,column=0,sticky='nsew',pady=(0,5))
        paper=tk.Frame(print_body,bg=SURFACE);paper.pack(fill='both',expand=True)
        # Two full-width rows: the second contains only three spacing fields.
        # Sharing all available width removes the empty fourth cell entirely.
        for col,(key,title,choices) in enumerate((
                ('paper','الورق',['A4','A3']),('orientation','الاتجاه',['عمودي','أفقي']),
                ('rows','الصفوف',None),('cols','الأعمدة',None))):
            control=mini_field(paper,title,self.vars[key],col,choices,width=5)
            if key in ('rows','cols'):
                # Counts are computed by the printing engine, never handwritten.
                control.configure(state='readonly')
        spacing=tk.Frame(paper,bg=SURFACE)
        spacing.grid(row=1,column=0,columnspan=4,sticky='ew',pady=(1,0))
        for col,(key,title) in enumerate((('gap_h','أفقي (مم)'),
                                          ('gap_v','رأسي (مم)'),('margin','هامش (مم)'))):
            control=mini_field(spacing,title,self.vars[key],col,
                choices=self.FIELD_CHOICES[key],width=6)
            self._choice_widgets[key]=control
        for row in range(2):paper.rowconfigure(row,weight=1)

        ttk.Separator(print_body,orient='horizontal').pack(fill='x',pady=3)
        selection=tk.Frame(print_body,bg=SURFACE);selection.pack(fill='x')
        self.batch_combo=inline_field(selection,'الدفعة',self.vars['batch'],0,choices=[],width=6)
        self.batch_combo.bind('<<ComboboxSelected>>',self.select_batch)
        inline_field(selection,'الطابعة',self.vars['printer'],1,choices=windows_printers(),width=5)
        export=tk.Frame(selection,bg=SURFACE,highlightbackground=LINE,highlightthickness=1)
        export.grid(row=1,column=0,columnspan=2,sticky='ew',padx=2,pady=(4,1))
        # Export labels have a short caption and adequate space for a heavier face.
        # Keep the existing variables, handlers and control positions unchanged.
        ttk.Style(self).configure('VoucherExport.TCheckbutton',font=('Arial',9,'bold'),padding=(0,0))
        for key,title in (('excel','Excel'),('csv','CSV'),('text','نصي')):
            ttk.Checkbutton(export,text=title,variable=self.export_vars[key],
                            style='VoucherExport.TCheckbutton').pack(side='right',padx=1,pady=2)
        label(export,'تصدير إضافي مع الإرسال',fg=MUTED,font=('Arial',9,'bold')).pack(side='left',padx=5)
        batch_label=label(selection,textvariable=self.batch_total,anchor='e',fg=MUTED,font=('Arial',10,'bold'),wraplength=420)
        batch_label.grid(
            row=2,column=0,columnspan=2,sticky='ew',padx=3,pady=(2,0))
        selection.bind('<Configure>',lambda event:batch_label.configure(wraplength=max(120,event.width-12)))

        # The action bar remains outside the flexible option area.
        self.viewport.pack_forget()
        footer=tk.Frame(self,bg=SURFACE,highlightbackground=LINE,highlightthickness=1)
        footer.pack(side='bottom',fill='x',padx=6,pady=(2,4))
        self.viewport.pack(side='top',fill='both',expand=True,padx=6,pady=0)
        total_label=label(footer,textvariable=self.total,anchor='e',font=('Arial',11,'bold'),fg=INK,bg='#FFFFFF',wraplength=900)
        total_label.pack(fill='x',padx=7,pady=(4,1))
        footer.bind('<Configure>',lambda event:total_label.configure(wraplength=max(120,event.width-16)))
        actions=tk.Frame(footer,bg='#FFFFFF');actions.pack(fill='x',padx=6,pady=(2,5))
        self.create_buttons=[]
        primary=[('إنشاء وإرسال وحفظ',lambda:self.create(True),'Green.TButton'),
                 ('إنشاء وإرسال',lambda:self.create(False),'Accent.TButton'),
                 ('معاينة قبل الإنشاء',self.preview_before_create,'Dark.TButton')]
        secondary=[('طباعة الدفعة',lambda:self.print_batch(True),'Dark.TButton'),
                   ('معاينة PDF',lambda:self.print_batch(False,True),'Dark.TButton'),
                   ('حفظ PDF',lambda:self.print_batch(False,choose_destination=True),'Dark.TButton')]
        action_specs=secondary + list(reversed(primary))
        for col,(title,command,style) in enumerate(action_specs):
            actions.columnconfigure(col,weight=1)
            b=ttk.Button(actions,text=title,command=command,style=style)
            b.grid(row=0,column=col,sticky='ew',padx=3,pady=1,ipady=2)
            self.create_buttons.append(b)
        fit_buttons(actions,self.create_buttons,(6,3,2,1),ipady=2)
        # Keep all five captions legible in one row even in the compact window.
        basic_labels=[next(child for child in cell.winfo_children() if isinstance(child,tk.Label))
                      for cell in basics.winfo_children() if isinstance(cell,tk.Frame)]
        def fit_basic_labels(event):
            point_size=9 if event.width<440 else 10
            for caption in basic_labels:caption.configure(font=('Arial',point_size,'bold'))
        basics.bind('<Configure>',fit_basic_labels,add='+')
        for key,var in self.vars.items():
            if key not in ('profile','batch','customer'):var.trace_add('write',lambda *_,name=key:self.setting_changed(name))
        self.vars['batch'].trace_add('write',lambda *_:self.update_totals() if not self._loading else None)
        self._style_options(area)
        identity.columnconfigure(3,weight=0,minsize=reset_button.winfo_reqwidth()+6)
        self._loading=False

    @staticmethod
    def _paint_color_swatch(button,color):
        """Keep the original selected colour visible and its caption readable."""
        red,green,blue=button.winfo_rgb(str(color))
        foreground='#0B2749' if .299*red+.587*green+.114*blue>32767 else '#FFFFFF'
        button.configure(bg=str(color),fg=foreground,activebackground=str(color),activeforeground=foreground)

    @staticmethod
    def _fit_design_rows(frame,widgets,candidates,rtl=False,padx=2):
        """Reflow only when the column count changes; no timers or redraw loop."""
        current=[None]
        def arrange(event):
            if event.widget is not frame or event.width<2:return
            count=1
            for candidate in candidates:
                widths=[max((widget.winfo_reqwidth()+2*padx for index,widget in enumerate(widgets)
                             if index%candidate==col),default=0) for col in range(candidate)]
                if sum(widths)<=event.width:
                    count=candidate;break
            if current[0]==count:return
            current[0]=count
            for col in range(max(candidates)):
                frame.columnconfigure(col,weight=1 if col<count else 0)
            for index,widget in enumerate(widgets):
                column=count-1-index%count if rtl else index%count
                widget.grid(row=index//count,column=column,sticky='ew',padx=padx,pady=1)
        frame.bind('<Configure>',arrange,add='+')
        # Establish natural geometry without waiting for the first resize.
        for index,widget in enumerate(widgets):
            widget.grid(row=0,column=len(widgets)-1-index if rtl else index,sticky='ew',padx=padx,pady=1)

    def _style_options(self,parent):
        """Emphasize creation-page captions without resizing constrained fields."""
        for widget in parent.winfo_children():
            if widget is getattr(self,'preview_outer',None):continue
            if isinstance(widget,(ttk.Entry,ttk.Combobox)):
                # Existing input size and baseline are deliberately preserved.
                widget.configure(font=('Arial',11,'bold'))
            elif isinstance(widget,ttk.Checkbutton):
                if not widget.cget('style'):widget.configure(style='Create.TCheckbutton')
            elif isinstance(widget,tk.Label):
                original=str(widget.cget('font'))
                size=tkfont.Font(font=original).actual('size')
                # Use the same Arial family as the rest of the program.
                # Do not enlarge crowded five-column fields.
                widget.configure(font=('Arial',max(10,size),'bold'))
            self._style_options(widget)

    def refresh(self):
        super().refresh()
        profiles=self.state().get('profiles',[]);self.profile_ids={r['name']:r['id'] for r in profiles}
        self.profile_combo.configure(values=list(self.profile_ids))
        selected=self.app.current_profile()
        self.vars['profile'].set(selected['name'] if selected else '')
        ident=(self.app.active_service,self.app.active_package_id)
        package_changed=ident!=self._loaded
        batches_changed=package_changed or bool(getattr(self,'_batches_dirty',False))
        if package_changed:
            self._cards_cache.clear();self._position_overrides={}
            self._loaded=ident;self.remote_cards=None;self._loading=True;self._count_source='quantity'
            s=self.db.all_settings()
            for key,setting in self.MAP.items():
                value=s.get(setting,'')
                if key in ('username_chars','password_chars'):
                    value=self.lowercase_english(value)
                self.vars[key].set(value)
            self.vars['format'].set(next((k for k,v in self.FORMATS.items() if v==s.get('create.card_format')),'اسم المستخدم فقط'))
            if 'username_mode' in self.vars:self.vars['username_mode'].set(self.charset_mode(s.get('create.username_chars','')))
            if 'password_mode' in self.vars:self.vars['password_mode'].set(self.charset_mode(s.get('create.password_chars','')))
            self.vars['orientation'].set('أفقي' if s.get('print.orientation')=='landscape' else 'عمودي')
            self.vars['order'].set(next((k for k,v in self.ORDERS.items() if v==s.get('print.order')),'متعرج'))
            self.vars['serial'].set(s.get('create.sequence_mode','0000'))
            self.vars['duplex'].set(s.get('print.duplex','single'))
            self.reset_sequence.set(bool(s.get('create.reset_sequence_each_batch',True)))
            for key,var in self.elements.items():var.set(bool(s.get('card.add_'+key,False if key=='date' else True)))
            self.card_bold.set(bool(s.get('card.font_bold',True)))
            for color_key,swatch in self._card_color_swatches.items():
                self._paint_color_swatch(swatch,s.get(color_key,self.db.get_setting(color_key,'#163050')))
            self._bg_data={k:v for k,v in s.items() if k.startswith('card.background_')}
            customer=s.get('create.userman_customer')
            self.vars['customer'].set(customer if customer is not None else
                (selected['raw'].get('owner',selected['raw'].get('customer','')) if selected else ''))
            for key,var in self.__dict__.get('export_vars',{}).items():var.set(bool(s.get('create.export_'+key,False)))
            # Allow precise existing custom values even if they are between
            # the dropdown's half-millimetre presets; do not round old plans.
            for key,widget in self._choice_widgets.items():
                current=self.vars[key].get()
                values=list(self.FIELD_CHOICES[key])
                if current and current not in values:values.append(current)
                widget.configure(values=values)
            self._loading=False
            self.auto_fit(quiet=True)
        self.update_password_controls()
        self.server_combo.configure(values=['all']+self.app.network_catalog.get('servers',[]))
        service=self.app.active_service
        self.server_combo.configure(state='readonly' if service=='hotspot' else 'disabled')
        self.customer_combo.configure(state='normal' if service=='userman' and self.state().get('um_version')==6 else 'disabled')
        for key,widget in self.limit_inputs.items():
            disabled=service in ('userman','ppp') and key not in ('price','days')
            widget.configure(state='disabled' if disabled else 'readonly' if key=='unit' else 'normal')
        note=''
        if selected:
            note='السرعة: %s | المشتركون: %s' % (selected['rate'] or 'حسب البروفايل',selected['shared'] or 'حسب البروفايل')
            if service=='userman':note+=' | السعة والمدة من محددات الباقة على الراوتر.'
            elif service=='ppp':note+=' | حدود الجلسة والسرعة من بروفايل PPP.'
            else:note+=' | السعة والوقت من إعدادات الباقة المحفوظة.'
        self.profile_note.configure(text=AR(note))
        if batches_changed:
            self._cards_cache.clear()
            old=self._batches.get(self.vars['batch'].get(),{}).get('id')
            router=self.router_row()
            self._batches={}
            if router and selected:
                # Filter in SQLite only when the service/package or batch list
                # changed; returning to the page reuses the existing list.
                for b in self.db.batches(2000,router_id=router['id'],service=service,profile=selected['name']):
                    self._batches['%s | %s %s' % (b['batch_no'],b['quantity'],AR('كرت'))]=dict(b)
            self.batch_combo.configure(values=list(self._batches))
            if self.remote_cards is None:
                self.vars['batch'].set(next((k for k,v in self._batches.items() if v['id']==old),next(iter(self._batches),'')))
            self._batches_dirty=False
        self.update_totals()
        self.schedule_preview()

    def _persist_package_settings(self,settings,package_id):
        """Persist the exact operation snapshot without depending on later UI refreshes."""
        snapshot=package_snapshot(settings)
        if package_id is not None:
            saved=self.db.package(package_id)
            if saved is None:raise ValueError('الباقة المحددة لم تعد موجودة؛ حدّث الباقات ثم اخترها مجددًا.')
            self.db.save_package(saved['name'],snapshot,saved['id'])
        self.db.set_settings(snapshot)
        return snapshot

    def _offer_print_copy(self,result):
        return offer_print_copy(self,result)

    def operation_settings(self,source=None):
        # Printing and creation consume exactly the captured/visible settings.
        return package_snapshot(self.draft_settings() if source is None else source)

    @staticmethod
    def lowercase_english(value):
        """Normalize English alphabet choices without altering digits/symbols."""
        return lowercase_english(value)

    @staticmethod
    def charset_mode(chars):
        """Classify existing saved character sets into the three selectors."""
        chars=VoucherWorkspace.lowercase_english(chars)
        if chars and all(ch.isdigit() for ch in chars):return 'أرقام فقط'
        if chars and all('a'<=ch<='z' for ch in chars):return 'أحرف فقط'
        return 'مختلطة'

    def apply_charset_mode(self,target):
        """Update the existing editable alphabet, with no extra UI fields."""
        self.vars[target+'_chars'].set(self.CHARSET_PRESETS[self.vars[target+'_mode'].get()])
        self.schedule_preview()

    def draft_settings(self):
        settings=self.db.all_settings()
        overrides=getattr(self,'_position_overrides',{})
        if overrides:
            settings['card.positions']=dict(settings.get('card.positions',{}),**overrides)
        for key,setting in self.MAP.items():
            value=self.vars[key].get()
            if key in self.INTEGER:
                number=float(value)
                if not number.is_integer():raise ValueError('أدخل عددًا صحيحًا: '+key)
                settings[setting]=int(number)
            else:settings[setting]=float(value) if key in self.DECIMAL else value
        # Enforce lowercase English letters even if a legacy preset or a
        # manually edited alphabet contains uppercase characters. Do not
        # mutate older stored packages until the user explicitly saves.
        for setting in ('create.username_chars','create.password_chars'):
            settings[setting]=self.lowercase_english(settings.get(setting,''))
        settings.update(self._bg_data)
        # The mode has its own editable control.
        settings['card.background_mode']=self.vars['mode'].get()
        for key,var in self.elements.items():settings['card.add_'+key]=var.get()
        settings['card.font_bold']=self.card_bold.get()
        settings['create.card_format']=self.FORMATS[self.vars['format'].get()]
        username_mode=(self.vars['username_mode'].get() if 'username_mode' in self.vars else self.charset_mode(settings.get('create.username_chars','')))
        password_mode=(self.vars['password_mode'].get() if 'password_mode' in self.vars else self.charset_mode(settings.get('create.password_chars','')))
        if username_mode=='أرقام فقط' and not str(settings.get('create.username_chars','')).isdigit():
            raise ValueError('عند اختيار أرقام فقط يجب أن تحتوي أحرف اسم المستخدم على أرقام فقط.')
        if password_mode=='أرقام فقط' and settings['create.card_format']=='code_password' and not str(settings.get('create.password_chars','')).isdigit():
            raise ValueError('عند اختيار أرقام فقط يجب أن تحتوي أحرف كلمة المرور على أرقام فقط.')
        for mode,key,enabled in ((username_mode,'create.username_chars',True),
                (password_mode,'create.password_chars',settings['create.card_format']=='code_password')):
            if enabled and mode=='أحرف فقط' and (not settings[key] or any(
                    not 'a'<=ch<='z' for ch in settings[key])):
                raise ValueError('عند اختيار أحرف فقط يجب أن تحتوي مجموعة الأحرف على أحرف إنجليزية فقط.')
        prefix=self.lowercase_english(settings.get('create.username_prefix',''))
        settings['create.username_prefix']=prefix
        if username_mode=='أرقام فقط' and prefix and not prefix.isdigit():
            raise ValueError('بادئة اسم المستخدم يجب أن تكون أرقاماً عند اختيار أرقام فقط.')
        if username_mode=='أحرف فقط' and any(not 'a'<=ch<='z' for ch in prefix):
            raise ValueError('عند اختيار أحرف فقط يجب أن تحتوي البادئة على أحرف إنجليزية فقط.')
        settings['create.sequence_mode']=self.vars['serial'].get()
        settings['create.reset_sequence_each_batch']=self.reset_sequence.get()
        settings['create.userman_customer']=self.vars['customer'].get().strip()
        for key,var in self.__dict__.get('export_vars',{}).items():settings['create.export_'+key]=var.get()
        settings['print.orientation']='landscape' if self.vars['orientation'].get()=='أفقي' else 'portrait'
        settings['print.auto_pages']=True  # Layout now always follows real paper/card measurements.
        settings['print.order']=self.ORDERS[self.vars['order'].get()]
        settings['print.duplex']=self.vars['duplex'].get() or 'single'
        # Prevent an invalid/unresolved pages entry from silently generating
        # the old card quantity. The printing engine remains authoritative.
        if self._count_source=='pages':
            raw=self.page_count.get()
            try:pages=float(raw)
            except (ValueError,TypeError,OverflowError):raise ValueError('عدد الصفحات غير صالح.') from None
            if not math.isfinite(pages) or not pages.is_integer() or pages<1:
                raise ValueError('عدد الصفحات غير صالح.')
            capacity=print_plan(settings=settings,quantity=1,rows=settings['print.rows'],
                columns=settings['print.columns'],paper=settings['print.paper'],
                orientation=settings['print.orientation'],gap_h_mm=settings['print.gap_h_mm'],
                gap_v_mm=settings['print.gap_v_mm'],page_margin_mm=settings['print.page_margin_mm'],
                duplex=settings['print.duplex'])['per_sheet']
            required=int(pages)*capacity
            if required>MAX_BATCH_SIZE or required!=settings['create.quantity']:
                raise ValueError('عدد الصفحات لا يتطابق مع عدد الكروت أو يتجاوز الحد الأقصى.')
        profile=self.app.current_profile()
        if profile:settings['create.profile']=profile['name']
        return {k:v for k,v in settings.items() if k.startswith(('card.','create.','print.'))}

    def choose_profile(self,_event=None):
        if not self.app.select_router_profile(self.profile_ids.get(self.vars['profile'].get())):
            selected=self.app.current_profile()
            self.vars['profile'].set(selected['name'] if selected else '')

    def _pick_card_color(self,key,fallback):
        color=colorchooser.askcolor(self.db.get_setting(key,fallback),parent=self)[1]
        if color:
            if key=='card.color.username':
                self.db.set_settings({'card.color.username':color,'card.color.password':color})
            else:
                self.db.set_setting(key,color)
            swatch=getattr(self,'_card_color_swatches',{}).get(key)
            if swatch is not None:self._paint_color_swatch(swatch,color)
            self.schedule_preview()

    def choose_card_number_color(self):
        self._pick_card_color('card.color.username','#000000')

    def choose_card_font_color(self):
        self._pick_card_color('card.font_color','#E7FFF7')

    def choose_card_border_color(self):
        self._pick_card_color('card.border_color','#00C98D')

    def choose_background(self):
        path=filedialog.askopenfilename(filetypes=[('صور الكروت','*.png *.jpg *.jpeg *.bmp')])
        if not path:return
        try:
            s=self.draft_settings();s['card.background_path']=path;s=package_snapshot(s)
            self._bg_data={k:v for k,v in s.items() if k.startswith('card.background_')}
            self.schedule_preview()
        except Exception as exc:messagebox.showerror('القالب',str(exc))

    def cards(self):
        if self.remote_cards is not None:return self.remote_cards
        batch=self._batches.get(self.vars['batch'].get())
        if not batch:return []
        if batch['id'] in self._cards_cache:return self._cards_cache[batch['id']]
        rows=[dict(row) for row in self.db.cards(100000,batch['id'])]
        # Keep the configured start number/prefix stable on paper rather than
        # inheriting the database's newest-first retrieval order.
        rows=sorted(rows,key=lambda row:(safe_int(str(row.get('sequence','')).strip(),2**63)
                                           if str(row.get('sequence','')).strip().isdigit() else 2**63,
                                           safe_int(row.get('id',0))))
        # Keep only the selected batch; opening old batches must not retain an
        # ever-growing copy of the voucher archive (and its passwords) in RAM.
        self._cards_cache.clear()
        self._cards_cache[batch['id']]=rows
        return rows

    def select_batch(self,_event=None):
        self.remote_cards=None;self.update_totals();self.schedule_preview()

    def schedule_preview(self):
        if self._loading or getattr(getattr(self,'_preview_editor',None),'drag',None) is not None:return
        if self._preview_job:self.after_cancel(self._preview_job)
        self._preview_job=self.after(180,self.preview)

    def setting_changed(self,key):
        if self._loading or self._sync_counts:return
        if key=='format':self.update_password_controls()
        if key=='quantity':self._count_source='quantity'
        if key in self.LAYOUT_INPUTS:
            self.auto_fit(quiet=True)
        if key in ('quantity','width','height','paper','orientation','gap_h','gap_v','margin','rows','cols','duplex'):
            self.update_totals()
        self.schedule_preview()

    def _pages_changed(self):
        if self._loading or self._sync_counts:return
        self._count_source='pages'
        self.update_totals()
        self.schedule_preview()

    def _set_count(self,var,value):
        # Updating the counterpart must not create a recursive edit event.
        self._sync_counts=True
        try:var.set(str(value))
        finally:self._sync_counts=False

    def layout_options(self):
        return {'settings':{'card.width_mm':self.vars['width'].get(),'card.height_mm':self.vars['height'].get()},
                'paper':self.vars['paper'].get(),
                'orientation':'landscape' if self.vars['orientation'].get()=='أفقي' else 'portrait',
                'gap_h_mm':self.vars['gap_h'].get(),'gap_v_mm':self.vars['gap_v'].get(),
                'page_margin_mm':self.vars['margin'].get()}

    def update_totals(self):
        """Bidirectional, physical-sheet-safe quantity planning.

        Editing quantity keeps the exact requested card count and displays the
        required ceil-divided sheet count. Editing sheets generates a FULL grid
        per sheet and sets quantity = sheets * current validated grid capacity.
        The latest edited control remains authoritative after layout changes.
        """
        try:
            options=dict(rows=self.vars['rows'].get(),columns=self.vars['cols'].get(),
                         duplex=self.vars['duplex'].get() or 'single',**self.layout_options())
            # The same validated layout engine is used by PDF and printing.
            capacity=print_plan(quantity=1,**options)['per_sheet']
            if self._count_source=='pages':
                raw=self.page_count.get()
                try:pages=float(raw)
                except (ValueError,TypeError,OverflowError):raise ValueError('أدخل عدد صفحات صحيحًا أكبر من صفر.') from None
                if not math.isfinite(pages) or not pages.is_integer() or pages<1:
                    raise ValueError('أدخل عدد صفحات صحيحًا أكبر من صفر.')
                quantity=int(pages)*capacity
                if quantity>MAX_BATCH_SIZE:
                    raise ValueError('هذا العدد من الصفحات يحتاج %s كرت، والحد الأقصى %s كرت.' % (quantity,MAX_BATCH_SIZE))
                if self.vars['quantity'].get()!=str(quantity):
                    self._set_count(self.vars['quantity'],quantity)
            else:
                error='أدخل عدد كروت صحيحًا من 1 إلى %s.' % MAX_BATCH_SIZE
                try:quantity=float(self.vars['quantity'].get())
                except (ValueError,TypeError,OverflowError):raise ValueError(error) from None
                if not math.isfinite(quantity) or not quantity.is_integer() or not 1<=quantity<=MAX_BATCH_SIZE:
                    raise ValueError(error)
                quantity=int(quantity)
            plan=print_plan(quantity=quantity,**options)
            if self._count_source!='pages' and self.page_count.get()!=str(plan['sheets']):
                self._set_count(self.page_count,plan['sheets'])
            remain=quantity % capacity
            note=(' | الصفحة الأخيرة: %s من %s كرت (غير ممتلئة)' % (remain,capacity)) if remain else ' | الصفحات ممتلئة'
            duplex_note=(' | PDF: %s صفحة للوجهين' % plan['pdf_pages']) if plan['pdf_pages']!=plan['sheets'] else ''
            self.total.set(AR('الإنشاء الحالي: %s كرت | السعة: %s كرت/ورقة | عدد الصفحات: %s%s%s' %
                (quantity,capacity,plan['sheets'],note,duplex_note)))
            batch=self._batches.get(self.vars['batch'].get())
            count=len(self.remote_cards) if self.remote_cards is not None else safe_int(batch.get('quantity')) if batch else 0
            if count:
                sheets=(count+capacity-1)//capacity
                self.batch_total.set(AR('المحدد للطباعة: %s كرت | %s ورقة' % (count,sheets)))
            else:self.batch_total.set(AR('حدد دفعة محفوظة للطباعة بعد الإنشاء.'))
        except (ValueError,TypeError,OverflowError) as exc:
            if self._count_source!='pages' and self.page_count.get()!='—':
                self._set_count(self.page_count,'—')
            self.total.set(AR(str(exc)))
            self.batch_total.set('')

    def change_credential_mode(self,_event=None):
        # Preserve the last explicit matching choice when toggling between modes.
        if self.credential_mode.get()=='اسم مستخدم وكلمة مرور':
            self.vars['format'].set(self._last_password_relation)
        else:
            self.vars['format'].set('اسم المستخدم فقط')
        self.update_password_controls()

    def choose_password_relation(self,relation):
        if self.credential_mode.get()!='اسم مستخدم وكلمة مرور':
            self.update_password_controls()
            return
        # Checkbuttons look like the requested tick boxes but are mutually
        # exclusive: one, and only one, password relationship can be active.
        self.vars['format'].set(relation)
        self.update_password_controls()

    def update_password_controls(self):
        current=self.vars['format'].get()
        enabled=current!='اسم المستخدم فقط'
        if enabled:
            self._last_password_relation=current
        self.credential_mode.set('اسم مستخدم وكلمة مرور' if enabled else 'اسم مستخدم فقط')
        self.relation_different.set(current=='مختلفتان')
        self.relation_same.set(current=='متطابقتان')
        for button in (self.different_checkbox,self.same_checkbox):
            button.configure(state='normal' if enabled else 'disabled')
        different=self.FORMATS.get(current)=='code_password'
        self.password_length_widget.configure(state='readonly' if different else 'disabled')
        self.password_chars_widget.configure(state='normal' if different else 'disabled')
        self.password_mode_widget.configure(state='readonly' if different else 'disabled')

    def preview(self):
        self._preview_job=None
        if getattr(getattr(self,'_preview_editor',None),'drag',None) is not None:return
        self.update_totals()
        try:
            s=self.draft_settings();cards=self.cards()
            x0,y0,w,h=preview_geometry(self.canvas,s['card.width_mm'],s['card.height_mm'])
            self._preview_editor.draw(s,cards[0] if cards else sample_card(s),(x0,y0,w,h))
            error=getattr(self._preview_editor,'validation_error','')
            if error:self.app.status_var.set(AR('المعاينة: '+error))
            elif getattr(self,'_preview_layout_error',False):self.app.status_var.set(AR('المعاينة جاهزة'))
            self._preview_layout_error=bool(error)
        except Exception as exc:
            self.app.status_var.set(AR('المعاينة: '+str(exc)))

    def _direct_hit(self,x,y):
        return self._preview_editor.hit(x,y)

    def _preview_drag_start(self,event):
        if self.busy:return 'break'
        if self._preview_job:self.after_cancel(self._preview_job);self._preview_job=None
        if self._preview_editor.start(event):
            self._selected_element=self._preview_editor.selected
            self.app.status_var.set(AR('تحريك '+ELEMENT_NAMES[self._selected_element]+' — أفلت الماوس للحفظ'))
        return 'break'

    def _preview_drag_move(self,event):
        self._preview_editor.move(event)
        return 'break'

    def _preview_drag_end(self,event):
        result=self._preview_editor.finish(event)
        if result:
            name,value=result
            self._position_overrides[name]=value
            positions=dict(self.db.get_setting('card.positions',{}) or {});positions[name]=value
            self.db.set_setting('card.positions',positions)
            self.app.status_var.set(AR('تم حفظ موقع '+ELEMENT_NAMES[name]))
        self.preview()
        return 'break'

    def _preview_wheel(self,event):
        if self.busy or self._preview_editor.drag is not None:return 'break'
        element=self._direct_hit(event.x,event.y) or self._selected_element
        if not element:return 'break'
        direction=1 if getattr(event,'delta',0)>0 or getattr(event,'num',0)==4 else -1
        self._selected_element=element;self._preview_editor.selected=element
        if element=='qr':
            try:current=float(self.vars['qr_size'].get())
            except (ValueError,tk.TclError):current=24.0
            maximum=max(1.0,min(float(self.vars['width'].get()),float(self.vars['height'].get())))
            self.vars['qr_size'].set(round(max(1.0,min(maximum,current+direction)),2))
        else:
            key='card.size.'+element;current=float(self.db.get_setting(key,1.0) or 1.0)
            self.db.set_setting(key,round(max(.4,min(4.0,current+direction*.1)),2))
            self.preview()
        return 'break'

    def auto_fit(self,quiet=False):
        try:
            opts=self.layout_options()
            r,c=auto_grid(**opts)
            # max(1, ...) from auto_grid alone does not guarantee an oversized
            # card fits; check the exact PDF layout before accepting counts.
            calculate_layout(opts['settings'],r,c,opts['paper'],opts['orientation'],
                             opts['gap_h_mm'],opts['gap_v_mm'],opts['page_margin_mm'])
            self._loading=True
            try:self.vars['rows'].set(r);self.vars['cols'].set(c)
            finally:self._loading=False
            self.update_totals()
            self.schedule_preview()
        except Exception as exc:
            if not quiet:messagebox.showerror('الورق',str(exc))

    def preview_before_create(self):
        """Build the PDF off the Tk thread without creating router/local users."""
        if self.busy:return
        try:
            s=self.operation_settings()
            calculate_layout(s,s['print.rows'],s['print.columns'],s['print.paper'],
                             s['print.orientation'],s['print.gap_h_mm'],s['print.gap_v_mm'],s['print.page_margin_mm'])
            out=Path(self.db.get_setting('paths.output'))
            path=out/('Hadramout_preview_'+datetime.now().strftime('%Y%m%d_%H%M%S_%f')+'.pdf')
        except Exception as exc:return messagebox.showerror('المعاينة قبل الإنشاء',str(exc))
        self.set_busy(True);self.app.status_var.set(AR('جارٍ إعداد معاينة PDF في الخلفية دون الاتصال بالراوتر...'))
        def work():
            class PreviewNames:
                def username_exists(_,name):return False
            generated=generate_cards(PreviewNames(),s['create.quantity'],s['create.code_length'],s['create.username_chars'],
                s['create.card_format'],s['create.password_length'],s['create.password_chars'],s['create.sequence_mode'],
                True,s['create.start_sequence'],s.get('create.username_prefix',''))
            template=sample_card(s);cards=[dict(template,**row) for row in generated]
            out.mkdir(parents=True,exist_ok=True)
            export_settings_pdf(s,cards,path)
            return len(cards)
        def done(count):
            self.set_busy(False)
            import os,subprocess
            try:
                if os.name=='nt':os.startfile(str(path))
                else:subprocess.Popen(['xdg-open',str(path)])
            except OSError:messagebox.showinfo('تم حفظ المعاينة',str(path))
            self.update_totals()
            self.app.status_var.set(AR('اكتمل إعداد معاينة PDF لـ%s كرت دون إضافة مستخدمين إلى الراوتر.' % count))
        def failed(exc):
            self.set_busy(False);messagebox.showerror('المعاينة قبل الإنشاء',str(exc))
        self.app.safe_thread(work,done,failed)

    def set_busy(self,value):
        self.busy=value
        for b in self.create_buttons:b.configure(state='disabled' if value else 'normal')

    def create(self,save_after=False):
        if self.busy:return
        profile=self.app.current_profile();service=self.app.active_service;router=self.router_row()
        if not profile or not self.state().get('available'):return messagebox.showerror('الباقة','اختر باقة مقروءة من الراوتر أولًا.')
        try:
            s=self.operation_settings();customer=self.vars['customer'].get().strip()
            if save_after:calculate_layout(s,s['print.rows'],s['print.columns'],s['print.paper'],s['print.orientation'],s['print.gap_h_mm'],s['print.gap_v_mm'],s['print.page_margin_mm'])
            if service=='userman' and profile.get('limit_error'):raise ValueError('تعذرت قراءة محددات الباقة: '+profile['limit_error'])
            if service=='userman' and profile.get('um_version')==6 and not customer:raise ValueError('أدخل حساب مدير المستخدمين (الإصدار ٦) الظاهر في إعدادات الباقة.')
            destination=choose_pdf_destination(self,card_pdf_filename(profile.get('name',''),s['create.quantity'])) if save_after else None
            # Cancellation precedes every database/router mutation.
            if save_after and not destination:return
        except Exception as exc:return messagebox.showerror('بيانات الكرت',str(exc))
        package_id=self.app.active_package_id
        try:s=self._persist_package_settings(s,package_id)
        except Exception as exc:return messagebox.showerror('حفظ إعدادات الباقة',str(exc))
        self.set_busy(True)
        self.app.status_var.set(AR('جارٍ فحص أسماء المستخدمين على الراوتر لمنع التكرار...'))
        operation_generation=self.app.session.generation
        def failed(exc):self.set_busy(False);messagebox.showerror('إنشاء الكروت',str(exc))
        def names_ready(names):
            try:
                db=self.db
                router_id=router['id']
                reserved=names | db.reserved_usernames(router_id)
                class Names:
                    # Duplicates matter per router: the same voucher name on two
                    # different MikroTik devices is legitimate, while a repeat on
                    # the connected device must still be refused.
                    def username_exists(_,name):
                        return name in reserved
                start=s['create.start_sequence'] if s['create.reset_sequence_each_batch'] else max(
                    s['create.start_sequence'],db.max_numeric_sequence_on_router(router_id)+1)
                cards=generate_cards(Names(),s['create.quantity'],s['create.code_length'],s['create.username_chars'],s['create.card_format'],
                    s['create.password_length'],s['create.password_chars'],s['create.sequence_mode'],True,start,s.get('create.username_prefix',''))
                batch_no=new_batch_no()
                batch_id=db.create_batch_with_cards(batch_no,router['id'],s['create.server'],profile['name'],cards,s['create.price'],str(s['create.validity_days']),
                    uptime_string(s['create.limit_uptime_h'],s['create.limit_uptime_m']),bytes_total(s['create.limit_bytes'],s['create.limit_unit']),
                    status='CREATED',package_id=package_id,print_settings=s,service=service)
                rows=[dict(r) for r in db.cards(10000,batch_id)]
            except Exception as exc:return failed(exc)
            def send(g):
                def progress(n,total):
                    if n==1 or n%20==0 or n==total:self.app.post_ui(
                        lambda n=n:self.app.status_var.set(AR('إرسال الكروت: %s / %s' % (n,total))),
                        generation=operation_generation)
                return send_cards(g,rows,db.path,router['id'],{(service,profile['name']):profile},
                                  customer=customer,progress=progress)
            def send_failed(exc):
                self.set_busy(False);self.remote_cards=None;self._batches_dirty=True;self.refresh()
                self.vars['batch'].set(next((k for k,b in self._batches.items() if b['id']==batch_id),''))
                self.app.status_var.set(AR('تعذر الإرسال؛ حُفظت الدفعة محليًا ويمكن إعادة إرسالها من إدارة المستخدمين.'))
                messagebox.showerror('تعذر إرسال الدفعة','توقف إرسال الدفعة. النتائج المؤكدة محفوظة؛ راجع حالة بقية الكروت قبل الطباعة أو إعادة الإرسال:\n'+str(exc))
            def done(results):
                successful=sum(ok for _,ok,_ in results);self.remote_cards=None
                self._batches_dirty=True;self.refresh()
                self.vars['batch'].set(next((k for k,b in self._batches.items() if b['id']==batch_id),''))
                self.app.status_var.set(AR('تم إرسال %s من %s كرت. الدفعة محفوظة: %s' % (successful,len(rows),batch_no)))
                db.log('NETWORK','إنشاء وإرسال '+SERVICES[service],'SUCCESS' if successful==len(rows) else 'PARTIAL',batch_no)
                exported=sorted((dict(r) for r in db.cards(10000,batch_id) if r['status']=='SENT'),key=lambda c:c['id'])
                writers=[(writer,extension) for key,writer,extension in
                    (('excel',export_excel,'xlsx'),('csv',export_csv,'csv'),('text',export_text,'txt'))
                    if s.get('create.export_'+key,False) and exported]
                out=Path(db.get_setting('paths.output'))
                def export_files():
                    export_errors=[]
                    for writer,extension in writers:
                        try:
                            out.mkdir(parents=True,exist_ok=True)
                            writer(exported,out/(batch_no+'.'+extension))
                        except Exception as exc:export_errors.append('%s: %s' % (extension,exc))
                    return export_errors
                def exported_done(export_errors):
                    self.set_busy(False)
                    if successful<len(rows):messagebox.showwarning('إرسال جزئي','نجح %s وفشل %s. احتُفظ بكل النتائج في الدفعة؛ لا تطبع الكروت الفاشلة.\n%s' %
                             (successful,len(rows)-successful,next((error for _,ok,error in results if not ok),''))+
                             ('\nسيُحفظ في PDF عدد %s من الكروت المرسلة بنجاح فقط.' % successful if save_after and successful else ''))
                    if save_after and exported:self.print_batch(False,cards_override=exported,settings_override=s,package_id_override=package_id,save_destination=destination)
                    elif successful==len(rows):messagebox.showinfo('تم إنشاء وإرسال الدفعة','تم إنشاء وإرسال %s كرت بنجاح.\nالدفعة: %s' % (successful,batch_no))
                    if export_errors:messagebox.showwarning('تعذر بعض التصدير','تم الإرسال، لكن تعذر إنشاء بعض الملفات الاختيارية:\n'+'\n'.join(export_errors))
                    self.app.refresh_network()
                if writers:
                    self.app.status_var.set(AR('تم الإرسال؛ جارٍ تجهيز ملفات الدفعة في الخلفية...'))
                    self.app.safe_thread(export_files,exported_done,lambda exc:exported_done([str(exc)]))
                else:exported_done([])
            self.app.run_network(send,done,send_failed)
        self.app.run_network(lambda g:g.voucher_names(),names_ready,failed,read_only=True)

    def print_batch(self,do_print=False,open_after=False,*,cards_override=None,settings_override=None,package_id_override=None,save_destination=None,choose_destination=False):
        if self.busy:return
        cards=list(cards_override) if cards_override is not None else self.cards()
        if not cards:return messagebox.showinfo('الطباعة','اختر دفعة كروت أو حدد مستخدمين من الراوتر للطباعة.')
        failed=[c for c in cards if c.get('status')!='SENT']
        if failed:
            if not messagebox.askyesno('كروت غير مفعلة','توجد %s كروت لم ينجح إرسالها. طباعة الكروت التي أُرسلت بنجاح فقط؟' % len(failed)):return
            cards=[c for c in cards if c.get('status')=='SENT']
            if not cards:return
        try:
            s=self.operation_settings(settings_override);out=Path(self.db.get_setting('paths.output'))
            path=out/('Hadramout_'+datetime.now().strftime('%Y%m%d_%H%M%S_%f')+'.pdf')
            calculate_layout(s,s['print.rows'],s['print.columns'],s['print.paper'],s['print.orientation'],
                             s['print.gap_h_mm'],s['print.gap_v_mm'],s['print.page_margin_mm'])
            if choose_destination:
                current_profile=self.app.current_profile() or {}
                preferred=card_pdf_filename(current_profile.get('name',''),len(cards))
                save_destination=choose_pdf_destination(self,preferred)
                if not save_destination:return
            package_id=self.app.active_package_id if package_id_override is None else package_id_override
            s=self._persist_package_settings(s,package_id)
            local_ids=[c['id'] for c in cards] if cards_override is not None or self.remote_cards is None else []
        except Exception as exc:return messagebox.showerror('الطباعة',str(exc))
        self.set_busy(True);self.app.status_var.set(AR('جارٍ تجهيز PDF في الخلفية؛ لن تتجمد نافذة البرنامج...'))
        dispatch={'printed':False}
        progress=printing_progress(self.app)
        def work():
            out.mkdir(parents=True,exist_ok=True)
            if do_print:dispatch['printed']=print_cards(s,cards,s['print.default_printer'],pdf_path=path,details=dispatch,progress=progress)
            else:export_settings_pdf(s,cards,path,progress=progress)
            if save_destination and not do_print:
                return Path(save_pdf_copy(path,save_destination))
            return path
        def done(result):
            self.set_busy(False)
            if save_destination and not do_print:
                try:self.db.set_setting('paths.print_copy',str(Path(result).parent))
                except Exception as exc:messagebox.showwarning('حفظ المجلد المفضل','ملف PDF محفوظ، لكن تعذر تذكر المجلد للمرة القادمة:\n'+str(exc))
            if do_print:
                if local_ids and dispatch['printed']:
                    try:self.db.mark_printed(local_ids)
                    except Exception as exc:
                        messagebox.showwarning('سجل الطباعة','قُبلت مهمة الطباعة لكن تعذر تحديث السجل. لا تكرر الطباعة بسبب هذا التنبيه.\n'+str(exc))
                try:
                    saved_copy=self._offer_print_copy(result)
                    if saved_copy:messagebox.showinfo('تم الحفظ',('أُرسلت الدفعة إلى الطابعة، وحُفظت نسخة PDF في:\n' if dispatch['printed'] else 'حُفظ ملف PDF في:\n')+str(saved_copy))
                except Exception as exc:
                    messagebox.showwarning('حفظ نسخة PDF',('أُرسلت الدفعة إلى الطابعة.\n' if dispatch['printed'] else '')+'تعذر حفظ النسخة المختارة:\n'+str(exc)+'\n\nالنسخة التلقائية موجودة في:\n'+str(result))
            elif open_after:
                import os,subprocess
                try:
                    if os.name=='nt':os.startfile(str(result))
                    else:subprocess.Popen(['xdg-open',str(result)])
                except OSError:messagebox.showinfo('تم حفظ PDF',str(result))
            else:show_saved_pdf_dialog(self,result,len(cards))
            note=''
            if dispatch.get('per_sheet'):
                note=' بالتخطيط المحفوظ: %s أعمدة × %s صفوف؛ %s كرت/ورقة، %s ورقة.' % (s['print.columns'],s['print.rows'],dispatch['per_sheet'],dispatch['sheets'])
            self.app.status_var.set(AR('تم تجهيز PDF'+(' وتسليم الدفعة للطابعة.' if dispatch['printed'] else ': '+str(result))+note))
            self.schedule_preview()
            if do_print and dispatch.get('printed'):
                self.app.offer_maintenance('اكتمال إرسال الدفعة للطابعة')
        def failed(exc):
            self.set_busy(False)
            self.app.status_var.set(AR('تعذرت الطباعة؛ الدفعة وإعداداتها محفوظة.' if do_print else 'تعذر حفظ PDF؛ الدفعة وإعداداتها محفوظة.'))
            detail=str(exc)
            if path.is_file():detail+='\n\nملف PDF محفوظ في:\n'+str(path)
            detail+=('\n\nلا تُعد إنشاء الكروت. راجع الصفحات المطبوعة وقائمة الانتظار قبل إعادة طباعة الدفعة؛ يمكنك طباعة الصفحات الناقصة فقط من ملف PDF.' if do_print else '\n\nلا تُعد إنشاء الكروت أو إرسالها. اختر الدفعة ثم استخدم «حفظ PDF» لاختيار مجلد آخر.')
            messagebox.showerror('الطباعة' if do_print else 'حفظ PDF',detail)
        self.app.safe_thread(work,done,failed)

    def use_remote_cards(self,cards):
        self.remote_cards=list(cards);self.vars['batch'].set(AR('كروت مختارة من الراوتر'))
        self.update_totals();self.schedule_preview()

    def destroy(self):
        if self._preview_job:self.after_cancel(self._preview_job)
        super().destroy()


class NetworkUsersPage(NetworkPage):
    def __init__(self,parent,app):
        super().__init__(parent,app);self.rows=[];self.visible={};self.loaded=None;self.request=0;self.loading=False;self.busy=False
        self._cache={};self._loading_key=None;self._loaded_key=None;self._last_fetch_attempt={};self._cache_note=''
        self._cache_ttl=60.0;self._hard_refresh_floor=3.0
        self._search_job=None;self.page_size=250;self.page=0;self.filtered=[]
        filters_outer,bar=section_card(self.body,'البحث والتصفية',THEME_COLORS['blue'],'#284B63')
        filters_outer.pack(fill='x')
        self.search=tk.StringVar();self.filter=tk.StringVar(value='كل الباقات');self.active=tk.BooleanVar();self.passwords=tk.BooleanVar()
        self.search_entry=field(bar,'بحث',self.search,0,width=19)
        self.search_entry.bind('<Return>',lambda _e:self.populate())
        self.search_entry.bind('<KeyRelease>',self._schedule_filter)
        self.profiles=field(bar,'الباقة',self.filter,0,1,choices=['كل الباقات'],width=12)
        self.profiles.bind('<<ComboboxSelected>>',lambda _e:self.populate())
        ttk.Checkbutton(bar,text='المتصلون الآن',variable=self.active,command=lambda:self.fetch(True)).grid(row=1,column=3,sticky='e',padx=4)
        ttk.Checkbutton(bar,text='إظهار كلمات المرور',variable=self.passwords,command=self.populate).grid(row=1,column=2,sticky='e',padx=4)
        ttk.Button(bar,text='قراءة المستخدمين من الراوتر',command=lambda:self.fetch(True)).grid(row=1,column=0,columnspan=2,sticky='ew',padx=4,pady=4)
        self.tree=table(self.body,[('name','اسم المستخدم',145),('password','كلمة المرور',125),('profile','الباقة',165),
                ('address','العنوان',120),('uptime','مدة الاستخدام',120),('disabled','الحالة',85),('bytes','الاستهلاك',105)],10)
        self.count=label(self.body,'',anchor='e');self.count.pack(fill='x',padx=5)
        pager=tk.Frame(self.body,bg=APP_BG);pager.pack(fill='x',padx=5,pady=(1,3))
        self.page_label=label(pager,'',bg=APP_BG,fg=MUTED,font=('Arial',9),anchor='center')
        self.page_label.pack(side='right',fill='x',expand=True)
        self.prev_page=ttk.Button(pager,text='السابق',style='Dark.TButton',command=lambda:self._change_page(-1))
        self.prev_page.pack(side='left',padx=2)
        self.next_page=ttk.Button(pager,text='التالي',style='Dark.TButton',command=lambda:self._change_page(1))
        self.next_page.pack(side='left',padx=2)
        actions_outer,actions=section_card(self.body,'إجراءات المستخدمين',THEME_COLORS['navy'],'#263F5A')
        actions_outer.pack(fill='x',pady=5)
        action_specs=[('تفعيل',lambda:self.action('enable'),'Green.TButton'),
                ('تعطيل',lambda:self.action('disable'),'Amber.TButton'),
                ('حذف',lambda:self.action('remove'),'Danger.TButton'),
                ('فصل الجلسة',lambda:self.action('disconnect'),'Purple.TButton'),
                ('طباعة المحدد',self.print_selected,'Blue.TButton'),('تصدير CSV',self.export,'Teal.TButton'),
                ('الدفعات المحفوظة',lambda:app.show_page('archive'),'Indigo.TButton')]
        action_buttons=[ttk.Button(actions,text=title,command=command,style=style) for title,command,style in action_specs]
        fit_buttons(actions,action_buttons,(7,4,3,2,1),pady=5)

    def refresh(self):
        super().refresh()
        values=['كل الباقات']+[r['name'] for r in self.state().get('profiles',[])]
        self.profiles.configure(values=values)
        if self.filter.get() not in values:self.filter.set(values[0])
        key=self._cache_key()
        if self._loaded_key!=key or self._cache_rows(key) is None:
            self.fetch()
        elif self._cache_note.startswith('آخر قراءة'):
            self._cache_note=self._cache_age_note(key)

    def _cache_key(self,service=None,active=None):
        """Include the authenticated router generation so old sessions cannot leak rows."""
        service=self.app.active_service if service is None else service
        active=self.active.get() if active is None else bool(active)
        session=getattr(self.app,'session',None)
        generation=getattr(session,'generation',None)
        router=getattr(session,'router',None) or {}
        router_id=router.get('id') if isinstance(router,dict) else None
        identity=getattr(session,'identity','')
        return (generation,router_id,identity,service,bool(active))

    def _cache_entry(self,key):
        entry=self._cache.get(key)
        if isinstance(entry,dict) and 'rows' in entry:
            return entry
        # Tolerate entries made by older page instances/tests.
        if entry is not None:
            return {'rows':list(entry),'timestamp':0.0}
        return None

    def _cache_rows(self,key,allow_expired=False):
        entry=self._cache_entry(key)
        if entry is None:return None
        age=max(0.0,time.monotonic()-float(entry.get('timestamp',0.0) or 0.0))
        if not allow_expired and age>=self._cache_ttl:return None
        return list(entry.get('rows') or [])

    def _cache_age_note(self,key):
        entry=self._cache_entry(key)
        if entry is None:return ''
        age=max(0.0,time.monotonic()-float(entry.get('timestamp',0.0) or 0.0))
        if age < 1:return 'آخر قراءة محفوظة منذ لحظات'
        return 'آخر قراءة محفوظة منذ %s ثانية' % int(age)

    def invalidate_cache(self):
        """Drop router user data after a mutation or lifecycle invalidation."""
        self._cache.clear();self._loaded_key=None;self.loaded=None;self._cache_note=''
        self._last_fetch_attempt.clear()
        self._search_index=None

    @staticmethod
    def _safe_error(exc):
        text=str(exc or '').replace('\n',' ')
        for token in ('password=', 'passwd=', 'secret='):
            if token in text.lower():
                head=text.lower().find(token);end=text.find(' ',head)
                text=text[:head]+token+'***'+(text[end:] if end >= 0 else '')
        return text[:240]

    def _schedule_filter(self,_event=None):
        if self._search_job:self.after_cancel(self._search_job)
        self._search_job=self.after(220,self.populate)

    def _change_page(self,delta):
        total_pages=max(1,(len(self.filtered)+self.page_size-1)//self.page_size)
        self.page=max(0,min(total_pages-1,self.page+delta));self._render_page()

    def fetch(self,force=False):
        service=self.app.active_service;active=self.active.get();key=self._cache_key(service,active)
        if not self.state().get('available'):
            self.rows=[];self._loaded_key=None;self.loaded=service;self.populate();return
        if self.loading:
            # Never queue another router read while this page already has one.
            # On completion, read only the latest selected service/filter.
            if key!=self._loading_key:
                self.rows=[];self._loaded_key=None;self.loaded=service
                self._cache_note='جارٍ إنهاء القراءة السابقة قبل عرض الخدمة المحددة'
            else:self._cache_note='جارٍ تحميل القراءة الحالية'
            self.populate();return
        now=time.monotonic();last=self._last_fetch_attempt.get(key)
        if force and last is not None and now-last < self._hard_refresh_floor:
            self._cache_note='تم تجنب تحديث مكرر؛ انتظر قليلًا قبل القراءة القسرية'
            self.populate();return
        if not force:
            cached=self._cache_rows(key)
            if cached is not None:
                self.loaded=service;self._loaded_key=key;self.rows=cached;self.page=0
                self._cache_note=self._cache_age_note(key);self.populate();return
        self._last_fetch_attempt[key]=now
        self.request+=1;request=self.request;self.loaded=service;self._loaded_key=key
        self._loading_key=key;self.loading=True;self.page=0;self.rows=[];self._cache_note='جارٍ تحميل القراءة الحالية';self.populate()
        self.count.configure(text=AR('جارٍ قراءة المستخدمين...'))
        def latest():
            self.loading=False;self._loading_key=None
            if key==self._cache_key():return True
            self._loaded_key=None
            if getattr(self.app,'current_page',self) is self:self.fetch()
            return False
        def done(rows):
            if request!=self.request:return
            self._cache[key]={'rows':list(rows or []),'timestamp':time.monotonic()}
            if not latest():return
            self.rows=list(rows or []);self._loaded_key=key;self.page=0;self._cache_note='تم تحديث القراءة الآن';self.populate()
        def failed(exc):
            if request!=self.request or not latest():return
            cached=self._cache_rows(key,allow_expired=True)
            self.rows=list(cached) if cached is not None else []
            self._loaded_key=None
            self._cache.pop(key,None)
            self._cache_note=('تعذرت القراءة؛ آخر قائمة محفوظة وقد تكون قديمة' if cached is not None else 'تعذرت القراءة')
            self.populate();self.count.configure(text=AR('تعذرت القراءة: '+self._safe_error(exc)+' — '+self._cache_note))
        self.app.run_network(lambda g:g.users(service,active),done,failed,read_only=True)

    def populate(self):
        if self._search_job:
            try:self.after_cancel(self._search_job)
            except tk.TclError:pass
            self._search_job=None
        self.page=0
        query=self.search.get().strip().casefold();profile=self.filter.get()
        rows=self.rows
        cached=getattr(self,'_search_index',None)
        if cached is not None and cached[0] is not self.rows:
            self._search_index=None;cached=None
        if query:
            # Rows are replaced on every fetch/cache switch. Index only the
            # displayed searchable fields; credentials never enter this index.
            if cached is None or cached[0] is not self.rows or len(cached[1])!=len(self.rows):
                cached=(self.rows,[' '.join(str(r.get(k,'')) for k in
                    ('_name','_profile','_address')).casefold() for r in self.rows])
                self._search_index=cached
            rows=[r for r,text in zip(self.rows,cached[1]) if query in text]
        if profile!='كل الباقات':
            rows=[r for r in rows if profile in r.get('_profiles',[r.get('_profile','')])]
        self.filtered=rows
        self._render_page(query=query)

    def _render_page(self,query=None):
        for ident in self.tree.get_children():self.tree.delete(ident)
        self.visible={};start=self.page*self.page_size;end=min(start+self.page_size,len(self.filtered))
        for i,row in enumerate(self.filtered[start:end],start):
            ident=str(i);self.visible[ident]=row
            used=sum(safe_int(row.get(k,0)) for k in ('bytes-in','bytes-out'))
            password=row.get('password','') if self.passwords.get() else ('••••' if row.get('password') else '')
            self.tree.insert('','end',iid=ident,values=(row['_name'],password,row['_profile'],row['_address'],row['_uptime'],
                AR('متصل' if self.active.get() else 'معطل' if truth(row.get('disabled')) else 'مفعل'),readable_bytes(used)))
        total_pages=max(1,(len(self.filtered)+self.page_size-1)//self.page_size)
        self.page_label.configure(text=AR('صفحة %s من %s' % (self.page+1,total_pages)))
        self.prev_page.configure(state='normal' if self.page>0 else 'disabled')
        self.next_page.configure(state='normal' if self.page+1<total_pages else 'disabled')
        text='%s مستخدم في الصفحة | %s مطابق من %s' % (len(self.visible),len(self.filtered),len(self.rows))
        if self._cache_note:text+=' — '+self._cache_note
        self.count.configure(text=AR(text))

    def selected(self):return [self.visible[i] for i in self.tree.selection() if i in self.visible]

    def action(self,action):
        if self.busy:return
        service=self.app.active_service;current_key=self._cache_key(service,self.active.get())
        if self._loaded_key!=current_key or self.loading:
            return messagebox.showinfo('المستخدمون','تغيّرت الخدمة أو الجلسة؛ اقرأ المستخدمين من الراوتر قبل تنفيذ أي إجراء.')
        rows=self.selected()
        if not rows:return messagebox.showinfo('المستخدمون','حدد مستخدمًا من الجدول.')
        if self.active.get()!=(action=='disconnect'):
            return messagebox.showinfo('المستخدمون','فعّل «المتصلون الآن» لفصل جلسة، وألغِه لتعديل حساب المستخدم.')
        if not messagebox.askyesno('تعديل على الراوتر','تنفيذ «%s» على %s مستخدم محدد؟' %
                ({'enable':'تفعيل','disable':'تعطيل','remove':'حذف','disconnect':'فصل الجلسة'}[action],len(rows))):return
        self.busy=True
        router_id=self.router_row()['id']
        def work(g):
            results=[]
            for index,row in enumerate(rows):
                try:g.user_action(service,action,row);results.append('')
                except Exception as exc:
                    results.append(str(exc))
                    if getattr(g.api,'sock',True) is None:
                        results.extend('لم تُنفذ العملية بعد انقطاع الاتصال.' for _ in rows[index+1:])
                        break
            return results
        def done(results):
            self.busy=False
            try:
                self.db.record_remote_user_action(router_id,service,action,
                    [row['_name'] for row,error in zip(rows,results) if not error])
            except Exception as exc:
                messagebox.showwarning('السجل المحلي','نُفذت نتائج العملية على الراوتر، لكن تعذر حفظها محليًا. لا تكرر العملية بسبب هذا التنبيه.\n'+str(exc))
            errors=[r for r in results if r];self.invalidate_cache();self.fetch(True);self.app.refresh_network()
            self.db.log('NETWORK',action,'PARTIAL' if errors else 'SUCCESS','%s/%s' % (len(results)-len(errors),len(results)))
            if errors:messagebox.showwarning('نتيجة العملية','نجح %s وفشل %s.\n%s' % (len(results)-len(errors),len(errors),errors[0]))
        def failed(exc):
            self.busy=False;self.invalidate_cache()
            messagebox.showerror('نتيجة العملية','توقفت العملية؛ تحقق من المستخدمين قبل تكرار أي إجراء.\n'+str(exc))
        self.app.run_network(work,done,failed)

    def print_selected(self):
        if self.loading or self._loaded_key!=self._cache_key():
            return messagebox.showinfo('المستخدمون','حدّث قائمة الخدمة المحددة قبل الطباعة.')
        rows=self.selected()
        if self.active.get():return messagebox.showinfo('طباعة','ألغِ «المتصلون الآن» وحدد حسابات المستخدمين للطباعة.')
        if not rows:return messagebox.showinfo('طباعة','حدد المستخدمين المطلوب طباعتهم.')
        profile_names={r['_profile'] for r in rows}
        if len(profile_names)!=1:return messagebox.showinfo('طباعة','حدد مستخدمين من باقة واحدة ليُطبّق قالبها الصحيح.')
        name=next(iter(profile_names));profile=next((p for p in self.state().get('profiles',[]) if p['name']==name),None)
        if not profile:return messagebox.showerror('الباقة','لا يمكن تحديد باقة واحدة لهؤلاء المستخدمين. تحقق من ارتباطهم بالباقات على الراوتر.')
        if not self.app.select_router_profile(profile['id']):return
        s=self.db.all_settings();cards=[]
        unavailable=any(not remote_password_available(row) for row in rows)
        username_only=False
        if unavailable:
            if s.get('create.card_format')!='code_only':
                return messagebox.showerror('كلمة المرور','تعذر التأكد من كلمات المرور المقروءة من الراوتر. استخدم حسابًا يسمح بقراءة البيانات الحساسة أو اطبع الدفعة المحلية المحفوظة.')
            if not messagebox.askyesno('طباعة اسم المستخدم فقط','كلمة مرور واحدة أو أكثر غير متاحة أو قد تكون محجوبة. هل تريد طباعة أسماء المستخدمين فقط دون كلمات مرور؟'):
                return
            username_only=True
        for i,row in enumerate(rows):
            card=sample_card(s);card.update(username=row['_name'],password='' if username_only else row['password'],profile=name,sequence=str(i+1).zfill(4),status='SENT')
            if self.app.active_service=='hotspot':card.update(limit_bytes=safe_int(row.get('limit-bytes-total',0)),limit_uptime=row.get('limit-uptime',''))
            cards.append(card)
        self.app.show_page('create');self.app.page_frames['create'].use_remote_cards(cards)

    def export(self):
        if self.loading or self._loaded_key!=self._cache_key():
            return messagebox.showinfo('المستخدمون','حدّث قائمة الخدمة المحددة قبل التصدير.')
        # Pagination is display-only: export all filtered results if unselected.
        rows=self.selected() or list(self.filtered)
        if not rows:return
        path=filedialog.asksaveasfilename(defaultextension='.csv',filetypes=[('CSV','*.csv')])
        if not path:return
        cards=[dict(r,username=r['_name'],password=r.get('password','') if self.passwords.get() else '',profile=r['_profile'],service=self.app.active_service) for r in rows]
        try:export_csv(cards,path);messagebox.showinfo('التصدير','تم حفظ المستخدمين. كلمات المرور تُصدّر عند تفعيل إظهارها فقط.')
        except Exception as exc:messagebox.showerror('التصدير',str(exc))


class ActivityPage(NetworkPage):
    """Local audit/activity log. No router reads are performed on this page."""
    def __init__(self,parent,app):
        super().__init__(parent,app,services=False)
        self._refresh_job=None
        self.search=tk.StringVar();self.category=tk.StringVar(value='ALL')
        outer,bar=section_card(self.body,'النشاط وسجل العمليات',THEME_COLORS['violet'],'#4F3B78')
        outer.pack(fill='x',pady=(0,4))
        label(bar,'بحث',bg=SURFACE,fg=INK,font=('Arial',9,'bold'),anchor='e').pack(side='right',padx=(5,2))
        entry=ttk.Entry(bar,textvariable=self.search,justify='center');entry.pack(side='right',fill='x',expand=True,padx=3,pady=4)
        entry.bind('<KeyRelease>',lambda _e:self.schedule_refresh())
        ttk.Combobox(bar,textvariable=self.category,state='readonly',width=14,
                     values=['ALL','NETWORK','USERMAN','MIKROTIK','PRINT','EXPORT','GENERAL']).pack(side='right',padx=3,pady=4)
        ttk.Button(bar,text='تحديث',style='Indigo.TButton',command=self.refresh).pack(side='left',padx=4,pady=4)
        self.tree=table(self.body,[('time','الوقت',150),('category','القسم',100),('action','العملية',210),
                                   ('status','النتيجة',90),('details','التفاصيل',390)],14)
        self.count=label(self.body,'',bg=APP_BG,fg=MUTED,font=('Arial',9,'bold'),anchor='e')
        self.count.pack(fill='x',padx=4,pady=(2,4))

    def schedule_refresh(self):
        if self._refresh_job is not None:
            try:self.after_cancel(self._refresh_job)
            except tk.TclError:pass
        self._refresh_job=self.after(180,self.refresh)

    def refresh(self):
        self._refresh_job=None
        query=self.search.get().strip();category=self.category.get().strip() or 'ALL'
        rows=list(self.db.activities(limit=500,search=query or None,category=category))
        for item in self.tree.get_children():self.tree.delete(item)
        for i,row in enumerate(rows):
            ts=str(row['ts'] or '').replace('T',' ')
            self.tree.insert('','end',iid=str(i),values=(ts,row['category'] or '',AR(row['action'] or ''),row['status'] or '',row['details'] or ''))
        self.count.configure(text=AR('%s عملية معروضة — السجل محلي ولا يرسل طلبات للراوتر' % len(rows)))

    def destroy(self):
        if self._refresh_job is not None:
            try:self.after_cancel(self._refresh_job)
            except tk.TclError:pass
            self._refresh_job=None
        super().destroy()


class HotspotMonitorPage(NetworkPage):
    """Dedicated Hotspot live monitor inspired by the supplied active/host/binding screens.

    Reads are explicit and page-scoped. Mutations are limited to familiar RouterOS
    actions and always require confirmation.
    """
    def __init__(self,parent,app):
        super().__init__(parent,app,services=False)
        self.loaded=False;self.loading=False
        self.active_rows={};self.host_rows={};self.binding_rows={}

        header_outer,header=section_card(self.body,'المتصلون ومراقبة Hotspot',THEME_COLORS['green'],'#176A59')
        header_outer.pack(fill='x',pady=(0,4))
        self.summary_vars={name:tk.StringVar(value='—') for name in ('active','hosts','bindings')}
        specs=(('النشطون الآن','active',THEME_COLORS['green']),('الأجهزة المكتشفة','hosts',THEME_COLORS['blue']),('استثناءات الأجهزة','bindings','#6B4FA1'))
        for title,key,tone in specs:
            cell=tk.Frame(header,bg=THEME_COLORS['pale'],highlightbackground=tone,highlightthickness=1)
            cell.pack(side='right',fill='x',expand=True,padx=3,pady=4)
            label(cell,title,bg=tone,fg='#FFFFFF',font=('Arial',9,'bold'),anchor='e').pack(fill='x',ipady=2)
            label(cell,textvariable=self.summary_vars[key],bg=THEME_COLORS['pale'],fg=INK,font=('Arial',17,'bold'),anchor='center').pack(fill='x',pady=4)
        ttk.Button(header,text='تحديث القوائم',style='Teal.TButton',command=lambda:self.fetch(True)).pack(side='left',padx=4,pady=8)

        self.tabs=ttk.Notebook(self.body);self.tabs.pack(fill='both',expand=True,pady=2)
        self.active_tab=tk.Frame(self.tabs,bg=SURFACE);self.host_tab=tk.Frame(self.tabs,bg=SURFACE);self.binding_tab=tk.Frame(self.tabs,bg=SURFACE)
        self.tabs.add(self.active_tab,text='الجلسات النشطة')
        self.tabs.add(self.host_tab,text='الأجهزة المكتشفة')
        self.tabs.add(self.binding_tab,text='استثناءات الأجهزة')

        self.active_tree=table(self.active_tab,[('user','المستخدم',135),('address','IP',115),('mac','MAC',145),('server','السيرفر',100),('uptime','الوقت',100),('rx','تنزيل',95),('tx','رفع',95)],11)
        active_bar=tk.Frame(self.active_tab,bg=SURFACE);active_bar.pack(fill='x',pady=3)
        ttk.Button(active_bar,text='فصل الجلسة المحددة',style='Danger.TButton',command=self.disconnect_selected).pack(side='right',padx=4)
        ttk.Button(active_bar,text='تحديث',style='Blue.TButton',command=lambda:self.fetch(True)).pack(side='right',padx=4)

        self.host_tree=table(self.host_tab,[('address','IP',125),('to','العنوان الفعلي',125),('mac','MAC',150),('server','السيرفر',100),('uptime','الوقت',100),('state','الحالة',120)],11)
        host_bar=tk.Frame(self.host_tab,bg=SURFACE);host_bar.pack(fill='x',pady=3)
        ttk.Button(host_bar,text='استثناء الجهاز المحدد',style='Green.TButton',command=self.bypass_selected).pack(side='right',padx=4)
        ttk.Button(host_bar,text='تحديث',style='Blue.TButton',command=lambda:self.fetch(True)).pack(side='right',padx=4)

        self.binding_tree=table(self.binding_tab,[('address','IP',130),('mac','MAC',155),('server','السيرفر',105),('type','النوع',105),('disabled','الحالة',100),('comment','ملاحظة',180)],11)
        binding_bar=tk.Frame(self.binding_tab,bg=SURFACE);binding_bar.pack(fill='x',pady=3)
        ttk.Button(binding_bar,text='حذف الاستثناء المحدد',style='Danger.TButton',command=self.remove_binding).pack(side='right',padx=4)
        ttk.Button(binding_bar,text='تحديث',style='Blue.TButton',command=lambda:self.fetch(True)).pack(side='right',padx=4)

        help_outer,help_body=section_card(self.body,'ملاحظات التشغيل',THEME_COLORS['navy'],'#263F5A')
        help_outer.pack(fill='x',pady=(4,0))
        label(help_body,'القوائم تُقرأ مباشرة من Hotspot. الفصل ينهي الجلسة النشطة فقط، والاستثناء يسمح للجهاز بتجاوز صفحة الدخول بعد التأكيد.',bg=SURFACE,fg=MUTED,font=('Arial',9),anchor='e',justify='right').pack(fill='x',padx=4,pady=3)

    def refresh(self):
        if not self.loaded and not self.loading:self.fetch()

    @staticmethod
    def _status_host(row):
        if truth(row.get('bypassed')):return 'مستثنى من الدخول'
        if truth(row.get('authorized')):return 'مصرح'
        if truth(row.get('blocked')):return 'محظور'
        return 'غير مسجل'

    def fetch(self,force=False):
        if self.loading:return
        self.loading=True;self.notice.configure(text=AR('جارٍ قراءة الجلسات والأجهزة والاستثناءات من الراوتر...'),fg=INK)
        def work(g):
            active=g.api.command('/ip/hotspot/active/print')
            hosts=g.api.command('/ip/hotspot/host/print')
            bindings=g.api.command('/ip/hotspot/ip-binding/print')
            return active,hosts,bindings
        def done(result):
            self.loading=False;self.loaded=True
            active,hosts,bindings=result
            self._fill_active(active);self._fill_hosts(hosts);self._fill_bindings(bindings)
            self.summary_vars['active'].set(str(len(active)));self.summary_vars['hosts'].set(str(len(hosts)));self.summary_vars['bindings'].set(str(len(bindings)))
            self.notice.configure(text=AR('تم تحديث مراقبة Hotspot من الراوتر المتصل.'),fg=INK)
        def failed(exc):
            self.loading=False
            self.notice.configure(text=AR('تعذرت قراءة مراقبة Hotspot: '+str(exc)),fg=DANGER)
        self.app.run_network(work,done,failed,read_only=True)

    def _fill_active(self,rows):
        for item in self.active_tree.get_children():self.active_tree.delete(item)
        self.active_rows={}
        for index,row in enumerate(rows):
            iid=str(index);self.active_rows[iid]=row
            rx=safe_int(row.get('bytes-in',0));tx=safe_int(row.get('bytes-out',0))
            self.active_tree.insert('','end',iid=iid,values=(row.get('user',row.get('name','')),row.get('address',''),row.get('mac-address',''),row.get('server',''),row.get('uptime',''),readable_bytes(rx),readable_bytes(tx)))

    def _fill_hosts(self,rows):
        for item in self.host_tree.get_children():self.host_tree.delete(item)
        self.host_rows={}
        for index,row in enumerate(rows):
            iid=str(index);self.host_rows[iid]=row
            self.host_tree.insert('','end',iid=iid,values=(row.get('address',''),row.get('to-address',''),row.get('mac-address',''),row.get('server',''),row.get('uptime',''),AR(self._status_host(row))))

    def _fill_bindings(self,rows):
        for item in self.binding_tree.get_children():self.binding_tree.delete(item)
        self.binding_rows={}
        for index,row in enumerate(rows):
            iid=str(index);self.binding_rows[iid]=row
            self.binding_tree.insert('','end',iid=iid,values=(row.get('address',''),row.get('mac-address',''),row.get('server',''),row.get('type','regular'),AR('معطل' if truth(row.get('disabled')) else 'مفعل'),row.get('comment','')))

    def disconnect_selected(self):
        selected=self.active_tree.selection()
        if not selected:return messagebox.showinfo('المتصلون','حدد جلسة أو أكثر من جدول الجلسات النشطة.')
        rows=[self.active_rows[i] for i in selected if i in self.active_rows]
        if not messagebox.askyesno('فصل الجلسات','فصل %s جلسة Hotspot محددة؟ سيتم حذف الجلسة النشطة فقط.' % len(rows)):return
        def work(g):
            for row in rows:
                g.user_action('hotspot','disconnect',row)
            return len(rows)
        self.app.run_network(work,lambda _r:self.fetch(True))

    def bypass_selected(self):
        selected=self.host_tree.selection()
        if len(selected)!=1:return messagebox.showinfo('مستثنى من الدخول','حدد جهازًا واحدًا من جدول الأجهزة المكتشفة.')
        row=self.host_rows.get(selected[0],{})
        address=row.get('address') or row.get('to-address');mac=row.get('mac-address');server=row.get('server','all') or 'all'
        if not address and not mac:return messagebox.showerror('مستثنى من الدخول','لا يحتوي السجل على IP أو MAC صالح.')
        if not messagebox.askyesno('إضافة استثناء','السماح للجهاز المحدد بتجاوز صفحة دخول Hotspot؟'):return
        attrs={'type':'bypassed','server':server,'comment':'Network Manager bypass'}
        if address:attrs['address']=address
        if mac:attrs['mac-address']=mac
        def done(_r):self.app.db.log('NETWORK','إضافة استثناء دخول Hotspot','SUCCESS',address or mac);self.fetch(True)
        def work(g):
            g.validate_selected_record('/ip/hotspot/host',row,('address','to-address','mac-address','server'))
            return g.api.command('/ip/hotspot/ip-binding/add',attrs)
        self.app.run_network(work,done,affects_inventory=False)

    def remove_binding(self):
        selected=self.binding_tree.selection()
        if not selected:return messagebox.showinfo('استثناءات الأجهزة','حدد استثناءً واحدًا أو أكثر.')
        rows=[self.binding_rows[i] for i in selected if i in self.binding_rows]
        if not messagebox.askyesno('حذف الاستثناء','حذف %s استثناء محدد من الراوتر؟' % len(rows)):return
        def work(g):
            removed=0
            for row in rows:
                ident=g.validate_selected_record('/ip/hotspot/ip-binding',row,('address','mac-address','server','type'))
                g.api.command('/ip/hotspot/ip-binding/remove',{'.id':ident});removed+=1
            return removed
        def done(count):self.app.db.log('NETWORK','حذف استثناء Hotspot','SUCCESS',str(count));self.fetch(True)
        self.app.run_network(work,done)

class NetworkToolsPage(NetworkPage):
    MENUS={'واجهات الشبكة':'/interface','عناوين الشبكة':'/ip/address','عقود DHCP':'/ip/dhcp-server/lease',
           'سيرفرات Hotspot':'/ip/hotspot','خوادم RADIUS':'/radius','الموارد':'/system/resource'}
    def __init__(self,parent,app):
        super().__init__(parent,app,services=False);self.rows={};self.loaded=False
        tools_outer,bar=section_card(self.body,'أدوات التشخيص',THEME_COLORS['blue'],'#315C8B')
        tools_outer.pack(fill='x');self.kind=tk.StringVar(value='واجهات الشبكة')
        field(bar,'القسم',self.kind,0,choices=list(self.MENUS),width=25).bind('<<ComboboxSelected>>',lambda _e:self.fetch())
        ttk.Button(bar,text='تحديث',style='Teal.TButton',command=self.fetch).grid(row=0,column=2,padx=5)
        ttk.Button(bar,text='فحص اتصال وقراءة الباقات',style='Accent.TButton',command=self.diagnose).grid(row=0,column=3,padx=5)
        self.tree=table(self.body,[('name','الاسم / المعرف',160),('address','العنوان',190),('state','الحالة',130),('detail','البيانات',400)],8)
        links_outer,bar=section_card(self.body,'اختصارات أدوات الشبكة',THEME_COLORS['accent2'],'#176F62')
        links_outer.pack(fill='x',pady=(5,0))
        link_specs=[('صفحات دخول السيرفر','hotspot','Teal.TButton'),('الاتصالات المحفوظة','mikrotik','Blue.TButton'),
                    ('نسخ احتياطي وإعدادات','settings','Indigo.TButton'),('سجل العمليات','reports','Slate.TButton')]
        link_buttons=[ttk.Button(bar,text=title,style=style,command=lambda k=key:app.show_page(k))
                      for title,key,style in reversed(link_specs)]
        fit_buttons(bar,link_buttons,(4,2,1),padx=4,pady=5)
        output_outer,output_body=section_card(self.body,'نتيجة التشخيص',THEME_COLORS['navy'],'#263F5A')
        output_outer.pack(fill='both',expand=True,pady=5)
        self.output=tk.Text(output_body,height=5,wrap='word',bg='#F7FAFC',fg=INK,font=('Arial',10),relief='flat',bd=0)
        self.output.pack(fill='both',expand=True,padx=2,pady=2)
        self.output.configure(state='disabled')

    def refresh(self):
        if not self.loaded:self.fetch()

    def fetch(self):
        if getattr(self,'_fetch_loading',False):return
        kind=self.kind.get();path=self.MENUS[kind];self.loaded=False
        self._fetch_loading=True
        def current():
            self._fetch_loading=False
            if kind==self.kind.get():return True
            if getattr(self.app,'current_page',self) is self:self.fetch()
            return False
        def done(rows):
            if not current():return
            self.loaded=True
            for ident in self.tree.get_children():self.tree.delete(ident)
            for index,r in enumerate(rows):
                name=r.get('name',r.get('host-name',r.get('.id',kind)))
                state='معطل' if truth(r.get('disabled')) else 'يعمل' if truth(r.get('running')) else r.get('status','')
                detail=' | '.join('%s: %s' % (k,r[k]) for k in ('type','interface','mac-address','server','service','version','cpu-load','uptime','board-name') if k in r)
                self.tree.insert('','end',iid=str(index),values=(name,r.get('address',r.get('network','')),AR(state),detail))
            self.notice.configure(text=AR('%s — %s سجل من الراوتر المتصل' % (kind,len(rows))))
        def failed(exc):
            if not current():return
            self.loaded=False
            for ident in self.tree.get_children():self.tree.delete(ident)
            self.notice.configure(text=AR('تعذرت القراءة: '+str(exc)))
        self.app.run_network(lambda g:g.api.command(path+'/print'),done,failed,read_only=True,affects_inventory=False)

    def diagnose(self):
        self.notice.configure(text=AR('جارٍ تشخيص الاتصال والصلاحيات...'))
        def done(results):
            text='\n\n'.join('%s: %s\n%s' % (AR(title),AR(status),json.dumps(rows,ensure_ascii=False,indent=2)) for title,status,rows in results)
            self.output.configure(state='normal');self.output.delete('1.0','end');self.output.insert('1.0',text);self.output.configure(state='disabled')
            self.notice.configure(text=AR('اكتمل الفحص؛ يوضح التقرير الأقسام غير المثبتة أو الممنوعة بالصلاحيات.'))
        self.app.run_network(lambda g:g.diagnostics(),done,read_only=True,affects_inventory=False)
