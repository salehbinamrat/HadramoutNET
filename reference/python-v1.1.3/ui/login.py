"""The only screen shown until RouterOS confirms the login."""
from __future__ import annotations

import tkinter as tk
import webbrowser
from tkinter import font as tkfont, ttk

from .arabic import ui_text as AR
from .layout import PageFrame, fit_table
from .theme import COLORS as THEME_COLORS


class DeveloperPanel(tk.Canvas):
    """One compact, selectable credit below the unchanged application logo."""
    def __init__(self, parent, colors):
        name_font=tkfont.Font(parent,family='Arial',size=12,weight='bold')
        contact_font=tkfont.Font(parent,family='Arial',size=10)
        height=max(name_font.metrics('linespace'),contact_font.metrics('linespace'))*2+28
        super().__init__(parent,bg=colors['bg'],width=1,height=height,highlightthickness=0,bd=0)
        self._fonts=(name_font,contact_font)
        self._border=self.create_polygon(0,0,0,0,fill=colors['panel'],outline=colors['line'],smooth=True,width=1)
        self.text=tk.Text(self,height=2,width=1,font=name_font,bg=colors['panel'],fg=colors['navy'],
                          relief='flat',bd=0,highlightthickness=0,padx=0,pady=0,wrap='none',
                          cursor='arrow',exportselection=False,takefocus=True)
        self.text.tag_configure('name',justify='center',font=name_font,spacing3=4)
        self.text.tag_configure('contact',justify='center',font=contact_font)
        self.text.tag_configure('telegram',foreground=colors['accent'],underline=True)
        self.text.insert('end','برمجة وتطوير / صالح بن عمرات\n','name')
        # A separate LTR paragraph prevents the phone and URL from reversing.
        self.text.insert('end','\u200e774660770  •  ','contact')
        self.text.insert('end','t.me/Salehbinzain',('contact','telegram'))
        self.text.tag_bind('telegram','<ButtonRelease-1>',self._open_telegram)
        self.text.tag_bind('telegram','<Enter>',lambda _e:self.text.configure(cursor='hand2'))
        self.text.tag_bind('telegram','<Leave>',lambda _e:self.text.configure(cursor='arrow'))
        self.text.configure(state='disabled')
        self._content=self.create_window(12,10,anchor='nw',window=self.text)
        self.bind('<Configure>',self._resize)

    def _resize(self,event):
        w=max(24,event.width-1);h=max(24,event.height-1);r=9
        self.coords(self._border,r,1,w-r,1,w,1,w,r,w,h-r,w,h,w-r,h,r,h,1,h,1,h-r,1,r,1,1)
        self.itemconfigure(self._content,width=max(1,w-24),height=max(1,h-20))

    def _open_telegram(self,_event=None):
        # Only the fixed HTTPS contact is opened; no user input enters a command.
        if not self.text.tag_ranges('sel'):
            try:webbrowser.open('https://t.me/Salehbinzain',new=2)
            except (OSError,webbrowser.Error):pass


class LoginPage(PageFrame):
    def __init__(self, parent, app, colors, app_name, version):
        super().__init__(parent, bg='#F8EBD1')
        self.app = app
        self.colors = dict(THEME_COLORS)
        self.colors.update({key: value for key, value in colors.items() if key not in self.colors})
        # Login-only palette. Do not change the shared theme or other program pages.
        self.colors.update({'bg':'#F8EBD1','panel':'#FFFBF5','line':'#D8BD88',
                            'muted':'#6E5D49','navy':'#0B2749','title_bg':'#0B2749',
                            'title_text':'#FFFFFF','accent':'#B78234'})
        surface=self.colors['panel']; page_bg=self.colors['bg']; line=self.colors['line']
        self.busy = False
        self.trial_opted_in = False
        self.saved_rows = {}
        self.vars = {
            'host': tk.StringVar(value='192.168.88.1'),
            'port': tk.StringVar(value='8728'),
            'username': tk.StringVar(value='admin'),
            'password': tk.StringVar(),
            'use_ssl': tk.BooleanVar(value=False),
            'verify_ssl': tk.BooleanVar(value=True),
            'connection_mode': tk.StringVar(value='local'),
        }
        self._display_mode='local'
        self._address_fields=('host','port','use_ssl','verify_ssl')
        self._address_drafts={'local':('192.168.88.1','8728',False,True),'remote':('','',False,True)}
        self.saved_var = tk.StringVar()
        self.show_password = tk.BooleanVar(value=False)
        self.save_password = tk.BooleanVar(value=False)
        self.message_var = tk.StringVar(value=AR('سجّل الدخول بالبيانات الخاصة براوتر MikroTik.'))
        # A stable, image-free three-column composition. Only ui/login.py is
        # modified; the actual login, activation and database callbacks remain.
        self.configure(bg=page_bg)
        self.inner.configure(bg=page_bg)
        self.inner.columnconfigure(0,weight=4,minsize=268)
        self.inner.columnconfigure(1,weight=6,minsize=480)
        self.inner.columnconfigure(2,weight=0,minsize=182)
        self.inner.rowconfigure(0,weight=1)
        self.inner.rowconfigure(1,weight=0)

        brand_wrap=tk.Frame(self.inner,bg=page_bg)
        brand_wrap.grid(row=0,column=0,sticky='nsew',padx=(16,7),pady=(12,8))
        brand_card=tk.Frame(brand_wrap,bg=surface,highlightbackground=line,highlightthickness=1)
        brand_card.pack(fill='both',expand=True,pady=(0,11))
        tk.Frame(brand_card,bg='#C9A263',height=5).pack(fill='x')
        brand_body=tk.Frame(brand_card,bg=surface)
        brand_body.pack(fill='both',expand=True,padx=15,pady=12)
        brand_center=tk.Frame(brand_body,bg=surface)
        brand_center.place(relx=.5,rely=.47,anchor='center',relwidth=.95)
        tk.Label(brand_center,text='حضرموت',bg=surface,fg=self.colors['navy'],
                 font=('Arial',32,'bold')).pack(fill='x',pady=(0,5))
        tk.Frame(brand_center,bg='#C9A263',height=2).pack(fill='x',padx=45,pady=(1,13))
        tk.Label(brand_center,text='لإدارة الشبكات وطباعة الكروت',bg=surface,
                 fg='#58452C',font=('Arial',14,'bold'),wraplength=310,
                 justify='center').pack(fill='x',pady=(0,12))
        tk.Label(brand_center,text='إدارة اتصالات MikroTik والباقات والمستخدمين،\n'
                 'وإنشاء الكروت ومعاينتها وطباعتها من واجهة واحدة.',bg=surface,
                 fg=self.colors['muted'],font=('Arial',11),wraplength=320,
                 justify='center').pack(fill='x',pady=(0,14))
        tk.Label(brand_center,text='الإصدار  v'+version,bg='#F2E5CC',fg=self.colors['navy'],
                 font=('Arial',11,'bold'),padx=13,pady=6).pack()
        tk.Label(brand_card,text='RouterOS V6 / V7   |   هوتسبوت • يوزرمنجر • برودباند',
                 bg=surface,fg=self.colors['muted'],font=('Arial',9),
                 wraplength=320,justify='center').pack(fill='x',padx=9,pady=(0,14))
        self.developer_panel=DeveloperPanel(brand_wrap,self.colors)
        self.developer_panel.pack(fill='x',pady=(0,4))

        # The stack keeps its dimensions while only the visible section changes.
        center_wrap=tk.Frame(self.inner,bg=page_bg)
        center_wrap.grid(row=0,column=1,sticky='nsew',padx=(7,8),pady=(12,8))
        center_wrap.rowconfigure(0,weight=1)
        center_wrap.columnconfigure(0,weight=1)
        center_wrap.grid_propagate(False)
        self.section_stack=tk.Frame(center_wrap,bg=page_bg)
        self.section_stack.grid(row=0,column=0,sticky='nsew')
        self.section_stack.grid_rowconfigure(0,weight=1)
        self.section_stack.grid_columnconfigure(0,weight=1)
        self.section_stack.grid_propagate(False)
        side=tk.Frame(self.section_stack,bg=surface,highlightbackground=line,
                      highlightthickness=1,padx=10,pady=9)
        side.grid(row=0,column=0,sticky='nsew')
        self.section_frames={'login':side}

        nav_bg=self.colors['navy']
        sidebar=tk.Frame(self.inner,bg=nav_bg,width=184)
        sidebar.grid(row=0,column=2,sticky='nsew',padx=(0,13),pady=(12,8))
        sidebar.grid_propagate(False)
        tk.Frame(sidebar,bg='#C9A263',height=4).pack(fill='x')
        tk.Label(sidebar,text='حضرموت',bg=nav_bg,fg='#F6DCAB',
                 font=('Arial',19,'bold')).pack(fill='x',pady=(21,1))
        tk.Label(sidebar,text='القائمة الرئيسية',bg=nav_bg,fg='#C5D3E4',
                 font=('Arial',10)).pack(fill='x',pady=(0,22))
        tk.Frame(sidebar,bg='#31516B',height=1).pack(fill='x',padx=13,pady=(0,12))
        self.section_buttons={}
        for key,title in (('login','تسجيل الدخول'),('license','الترخيص'),
                          ('about','حول البرنامج'),('support','خدمة العملاء')):
            button=tk.Button(sidebar,text=title,bg='#15395E',fg='#FFFFFF',
                             activebackground='#D5AF70',activeforeground=nav_bg,
                             font=('Arial',12,'bold'),relief='flat',bd=0,highlightthickness=0,
                             cursor='hand2',anchor='e',padx=13,pady=13,
                             command=lambda section=key:self.show_section(section))
            button.pack(fill='x',padx=9,pady=4)
            self.section_buttons[key]=button
        tk.Label(sidebar,text='v'+version,bg=nav_bg,fg='#C6A66B',
                 font=('Arial',10,'bold')).pack(side='bottom',fill='x',pady=17)

        title_band=tk.Frame(side,bg=self.colors['title_bg'],height=47)
        title_band.pack(fill='x',pady=(0,7));title_band.pack_propagate(False)
        tk.Frame(title_band,bg='#C9A263',width=5).pack(side='right',fill='y')
        tk.Label(title_band,text='تسجيل الدخول',bg=self.colors['title_bg'],
                 fg=self.colors['title_text'],font=('Arial',15,'bold'),anchor='e').pack(fill='both',padx=14)
        form=tk.Frame(side,bg=surface,highlightbackground=line,highlightthickness=1,padx=9,pady=4)
        form.pack(fill='x');form.columnconfigure(2,weight=1)
        def label(text,row,column):
            widget=tk.Label(form,text=text,bg=surface,fg=self.colors['navy'],font=('Arial',11,'bold'))
            widget.grid(row=row,column=column,sticky='e',padx=5,pady=3)
            return widget
        mode_bar=tk.Frame(form,bg=surface);mode_bar.grid(row=0,column=0,columnspan=4,sticky='ew')
        self.mode_buttons=[]
        for text,value in (('محلي','local'),('عن بُعد — رابط VPN','remote')):
            button=ttk.Radiobutton(mode_bar,text=text,variable=self.vars['connection_mode'],value=value,command=self.switch_connection_mode)
            button.pack(side='right',padx=7,pady=3);self.mode_buttons.append(button)
        self.host_label=label('عنوان الراوتر :',1,3);self.port_label=label('API :',1,1)
        self.host_entry=ttk.Entry(form,textvariable=self.vars['host'],width=23,justify='center')
        self.host_entry.grid(row=1,column=2,sticky='ew',padx=3,pady=2)
        self.port_entry=ttk.Entry(form,textvariable=self.vars['port'],width=7,justify='center')
        self.port_entry.grid(row=1,column=0,padx=3,pady=2)
        label('اسم الدخول :',2,3)
        self.username_entry=ttk.Entry(form,textvariable=self.vars['username'],justify='center')
        self.username_entry.grid(row=2,column=2,sticky='ew',padx=3,pady=2)
        self.ssl_check=ttk.Checkbutton(form,text='API-SSL',variable=self.vars['use_ssl'],command=self.toggle_ssl)
        self.ssl_check.grid(row=2,column=0,columnspan=2,sticky='e')
        label('كلمة المرور :',3,3)
        self.password_entry=ttk.Entry(form,textvariable=self.vars['password'],show='*',justify='center')
        self.password_entry.grid(row=3,column=2,sticky='ew',padx=3,pady=2)
        self.show_check=ttk.Checkbutton(form,text='إظهار',variable=self.show_password,command=self.toggle_password)
        self.show_check.grid(row=3,column=0,columnspan=2,sticky='e')
        options=tk.Frame(form,bg=surface);options.grid(row=4,column=0,columnspan=4,sticky='ew',pady=2)
        self.save_check=ttk.Checkbutton(options,text='حفظ بيانات الدخول',variable=self.save_password)
        self.save_check.pack(side='right',padx=4)
        self.verify_check=ttk.Checkbutton(options,text='التحقق من شهادة SSL',variable=self.vars['verify_ssl'])
        self.verify_check.pack(side='left',padx=4)
        self.saved_combo=ttk.Combobox(form,textvariable=self.saved_var,state='readonly',width=39)
        self.saved_combo.grid(row=5,column=0,columnspan=4,sticky='ew',padx=3,pady=1)
        self.saved_combo.bind('<<ComboboxSelected>>',self.select_saved)
        ttk.Button(form,text='⌄ محفوظات الاتصالات',command=self.toggle_history).grid(row=6,column=0,columnspan=4,sticky='ew',padx=3)
        self.connection_hint=tk.Label(form,text='أدخل منفذ API الخارجي من مزود الرابط في خانة «API خارجي». منفذ WinBox داخل الرابط لا يُستخدم تلقائياً.',bg=surface,fg=self.colors['muted'],font=('Arial',9),wraplength=450,anchor='e',justify='right')
        self.connection_hint.grid(row=7,column=0,columnspan=4,sticky='ew',padx=3,pady=3)
        self.history=tk.Frame(form,bg=surface)
        self.history.columnconfigure(0,weight=1)
        self.history_tree=ttk.Treeview(self.history,columns=('host','user','port','mode'),show='headings',height=5)
        for key,title,width in [('host','عنوان الراوتر',160),('user','اسم الدخول',95),('port','المنفذ',65),('mode','الاتصال',70)]:
            self.history_tree.heading(key,text=title);self.history_tree.column(key,width=width,anchor='center')
        self.history_tree.grid(row=0,column=0,sticky='ew')
        fit_table(self.history_tree)
        self.history_tree.bind('<<TreeviewSelect>>',self.select_history)
        self.history_visible=False
        # Retain the original 20-day trial alongside signed online paid licensing.
        self.serial_var=tk.StringVar()
        self.renewal_open=False
        self.license_message_var=tk.StringVar(value='جارٍ التحقق من رمز الترخيص...')
        self.license_area=tk.Frame(side,bg=surface,highlightbackground=line,highlightthickness=1,padx=8,pady=3)
        self.license_area.pack(fill='x',before=form,pady=(0,5))
        license_header=tk.Frame(self.license_area,bg=surface)
        license_header.pack(fill='x',pady=(0,3))
        ttk.Button(license_header,text='التفاصيل',width=7,command=self.show_license_details).pack(side='left',padx=(0,4))
        self.license_status_label=tk.Label(license_header,textvariable=self.license_message_var,
            bg=surface,fg=self.colors['navy'],font=('Arial',10,'bold'),anchor='e',justify='right',wraplength=500)
        self.license_status_label.pack(side='right',fill='x',expand=True)
        license_header.bind('<Configure>',lambda e:self.license_status_label.configure(wraplength=max(180,e.width-80)))
        self.trial_explanation=tk.Label(self.license_area,
            text='التجربة المجانية لمدة 20 يوماً (480 ساعة) تبدأ بعد أول اتصال ناجح بالراوتر.',
            bg=surface,fg=self.colors['muted'],font=('Arial',9,'bold'),anchor='e',justify='right',wraplength=510)
        self.trial_explanation.pack(fill='x',pady=(0,3))
        self.trial_button=ttk.Button(self.license_area,text='بدء التجربة المجانية — 20 يوماً',
                                     style='Accent.TButton',command=self.choose_trial)
        self.trial_button.pack(fill='x',pady=(0,4))
        self.activation_hint=tk.Label(self.license_area,
            text='أدخل رمز الترخيص؛ عند أول تفعيل تُرسل بصمة Windows تلقائياً دون طلبها مسبقاً:',
            bg=surface,fg=self.colors['muted'],font=('Arial',9,'bold'),anchor='e',justify='right',wraplength=510)
        self.activation_hint.pack(fill='x',pady=(0,4))
        self.activation_row=tk.Frame(self.license_area,bg=surface)
        self.activation_row.pack(fill='x')
        self.renew_license_button=ttk.Button(self.license_area,text='تجديد الترخيص',
                                             style='Accent.TButton',command=self.open_renewal_entry)
        self.activate_button=ttk.Button(self.activation_row,text='تحقق وتفعيل',style='Accent.TButton',
                                        command=self.submit_activation)
        self.activate_button.pack(side='left',padx=2)
        self.serial_entry=ttk.Entry(self.activation_row,textvariable=self.serial_var,justify='center',width=33,
                                    font=('Consolas',10,'bold'),show='●')
        self.serial_entry.pack(side='left',fill='x',expand=True,padx=4)
        tk.Label(self.activation_row,text='رمز الترخيص:',bg=surface,fg=self.colors['navy'],
                 font=('Arial',10,'bold')).pack(side='right',padx=3)
        self.serial_entry.bind('<Return>',lambda _e:self.submit_activation())
        self.serial_entry.bind('<Control-v>', self._paste_serial)
        self.serial_entry.bind('<Control-V>', self._paste_serial)
        self.serial_entry.bind('<Shift-Insert>', self._paste_serial)
        self.serial_entry.bind('<Control-a>', self._select_serial)
        self.serial_entry.bind('<Control-A>', self._select_serial)
        self.serial_context=tk.Menu(self.serial_entry,tearoff=False)
        self.serial_context.add_command(label='لصق رقم الترخيص',command=self._paste_serial)
        self.serial_context.add_command(label='نسخ',command=lambda:self.serial_entry.event_generate('<<Copy>>'))
        self.serial_context.add_command(label='تحديد الكل',command=self._select_serial)
        self.serial_entry.bind('<Button-3>', self._serial_context_menu)
        buttons=tk.Frame(side,bg=surface,highlightbackground=line,highlightthickness=1)
        buttons.pack(fill='x',pady=2)
        # Dedicated login style: the shared Accent buttons elsewhere remain untouched.
        connect_style=ttk.Style(self)
        connect_style.configure('LoginConnect.TButton',background='#D7192B',foreground='#FFFFFF',
                                font=('Arial',18,'bold'),padding=(24,10),borderwidth=0)
        connect_style.map('LoginConnect.TButton',
                          background=[('disabled','#BD4853'),('pressed','#A90F20'),('active','#E62A3C')],
                          foreground=[('disabled','#FFFFFF'),('active','#FFFFFF')])
        self.login_button=ttk.Button(buttons,text='اتصال',style='LoginConnect.TButton',command=self.submit)
        self.login_button.pack(side='right',fill='x',expand=True,padx=5,pady=4)
        self.cancel_button=ttk.Button(buttons,text='إغلاق',style='Dark.TButton',command=self.cancel)
        self.cancel_button.pack(side='left',padx=5,pady=2)
        self.progress=ttk.Progressbar(side,mode='indeterminate');self.progress.pack(fill='x',pady=1)
        self.message_label=tk.Label(side,textvariable=self.message_var,bg=surface,fg=self.colors['muted'],font=('Arial',11,'bold'),wraplength=520,justify='right',anchor='e')
        self.message_label.pack(fill='x',pady=2)
        # Secondary sections are created once and layered on top of the login
        # pane. Raising a pane does not resize, forget or destroy login widgets.
        self._build_information_sections(version)
        self.show_section('login')
        footer=tk.Frame(self.inner,bg=page_bg,height=26)
        footer.grid(row=1,column=0,columnspan=3,sticky='ew',padx=20,pady=(0,4))
        footer.grid_propagate(False)
        tk.Label(footer,text='حضرموت  •  إدارة الشبكات وطباعة الكروت',bg=page_bg,
                 fg='#755C36',font=('Arial',10,'bold'),anchor='e').pack(side='right')
        tk.Label(footer,text='v'+version,bg=page_bg,fg='#755C36',
                 font=('Arial',10)).pack(side='left')
        self.controls = (self.port_entry, self.host_entry, self.username_entry,
                         self.password_entry, self.show_check, self.ssl_check,
                         self.verify_check, self.save_check, self.login_button)+tuple(self.mode_buttons)
        self.bind('<Return>', lambda _e: self.submit())
        for widget in (self.host_entry, self.port_entry, self.username_entry, self.password_entry):
            widget.bind('<Return>', lambda _e: self.submit())
        self.refresh_saved()
        self.toggle_ssl(update_port=False)
        self._refresh_connection_mode()

    def show_section(self, section):
        """Navigate without rebuilding widgets, images or the layout geometry."""
        pane=self.section_frames.get(section)
        if pane is None:
            return
        pane.tkraise()
        for key,button in self.section_buttons.items():
            selected=key==section
            button.configure(bg='#D5AF70' if selected else '#15395E',
                             fg='#0B2749' if selected else '#FFFFFF')
        self._visible_section=section

    def _build_information_sections(self, version):
        """Compact RTL information cards; login and licensing actions are unchanged."""
        surface=self.colors['panel'];line=self.colors['line'];navy=self.colors['navy']
        gold='#C9A263';pale='#F8F1E4'

        def panel(key,title):
            frame=tk.Frame(self.section_stack,bg=surface,highlightbackground=line,
                           highlightthickness=1,padx=13,pady=12)
            frame.grid(row=0,column=0,sticky='nsew')
            self.section_frames[key]=frame
            banner=tk.Frame(frame,bg=navy,height=53)
            banner.pack(fill='x',pady=(0,12));banner.pack_propagate(False)
            tk.Frame(banner,bg=gold,width=5).pack(side='right',fill='y')
            tk.Label(banner,text=title,bg=navy,fg='white',anchor='e',
                     font=('Arial',15,'bold')).pack(fill='both',padx=12)
            return frame

        def scroll_content(parent):
            """Scrollable, width-constrained content without rebuilding on navigation."""
            area=tk.Frame(parent,bg=surface)
            area.pack(fill='both',expand=True)
            scroller=tk.Canvas(area,bg=surface,bd=0,highlightthickness=0)
            scrollbar=ttk.Scrollbar(area,orient='vertical',command=scroller.yview)
            scroller.configure(yscrollcommand=scrollbar.set)
            scrollbar.pack(side='left',fill='y')
            scroller.pack(side='right',fill='both',expand=True)
            content=tk.Frame(scroller,bg=surface)
            item=scroller.create_window((0,0),window=content,anchor='nw')
            content.bind('<Configure>',lambda e:scroller.configure(scrollregion=scroller.bbox('all')))
            scroller.bind('<Configure>',lambda e:scroller.itemconfigure(item,width=max(1,e.width)))
            return content

        def heading(parent,text):
            tk.Label(parent,text=text,bg=surface,fg=navy,font=('Arial',12,'bold'),
                     anchor='e',justify='right').pack(fill='x',pady=(8,6))

        def bullet(parent,text):
            row=tk.Frame(parent,bg=pale,highlightbackground='#E2D3B9',highlightthickness=1)
            row.pack(fill='x',pady=3)
            tk.Label(row,text='●',bg=pale,fg='#B5863D',font=('Arial',11,'bold'),
                     width=2,anchor='n').pack(side='right',padx=(2,9),pady=(10,5))
            label=tk.Label(row,text=text,bg=pale,fg=navy,font=('Arial',11),
                           anchor='e',justify='right',wraplength=400)
            label.pack(side='right',fill='x',expand=True,padx=(10,2),pady=9)
            # Wrap according to the actual available row width at different DPI.
            row.bind('<Configure>',lambda e,l=label:l.configure(wraplength=max(160,e.width-60)))
            return row

        def back_button(parent):
            ttk.Button(parent,text='العودة إلى تسجيل الدخول',
                       command=lambda:self.show_section('login')).pack(side='bottom',fill='x',pady=(10,0))

        license_pane=panel('license','الترخيص الإلكتروني')
        back_button(license_pane)
        license_content=scroll_content(license_pane)
        for item in (
            'الترخيص مخصص لجهاز كمبيوتر واحد، ويمكن إدارة عدة راوترات بنفس الترخيص.',
            'يُصدر المورّد الرمز دون بصمة؛ أدخله في البرنامج لتُسجَّل بصمة أول جهاز تلقائياً عبر خدمة التفعيل الآمنة.',
            'أدخل الرمز مرة واحدة؛ بعدها يتحقق البرنامج عبر الإنترنت عند كل تشغيل وكل 30 دقيقة.',
            'التجربة المجانية المحلية 20 يوماً (480 ساعة) تبدأ فقط بعد أول دخول ناجح للراوتر.',
            'يُحفظ وقت بداية التجربة على Windows ولا يتجدد عند إغلاق البرنامج أو تثبيته مجدداً.',
            'بعد انتهاء التجربة يلزم تفعيل رمز الترخيص؛ التراخيص المدفوعة تتطلب الإنترنت للتحقق.',
            'لا نستخدم ملف license.dat القديم لإصدار الترخيص المدفوع.',
            'يستطيع المورّد تجديد الترخيص وإيقافه ونقله إلى جهاز آخر عبر خدمة إدارة التراخيص.',
            'قد يتغير رقم الجهاز بعد إعادة تثبيت Windows، وعندها يلزم نقل الترخيص.',
        ):
            bullet(license_content,item)

        about_pane=panel('about','حول البرنامج')
        tk.Label(about_pane,text='حضرموت لإدارة الشبكات وطباعة الكروت',
                 bg=surface,fg=navy,font=('Arial',15,'bold'),
                 wraplength=450,justify='center').pack(fill='x',pady=(0,2))
        tk.Label(about_pane,text='الإصدار  v'+version,bg=surface,fg='#946A2F',
                 font=('Arial',10,'bold')).pack(fill='x',pady=(0,5))
        back_button(about_pane)
        about_content=scroll_content(about_pane)
        heading(about_content,'أهم خدمات البرنامج')
        for item in (
            'إدارة الاتصالات المحلية والبعيدة بأجهزة MikroTik، مع حفظ بيانات الاتصالات واستدعائها.',
            'العمل مع خدمات الهوتسبوت واليوزرمنجر والبرودباند، بحسب إمكانات الراوتر المتصل.',
            'عرض بيانات الراوتر ومتابعة المستخدمين والمتصلين وحالة الكروت.',
            'إنشاء الكروت ومعاينتها، وتخصيص القوالب والخطوط والألوان والأبعاد وعناصر الكرت.',
            'تجهيز دفعات الكروت للطباعة، وضبط المقاسات والهوامش وتخطيط الصفحات.',
            'تنظيم الباقات وإدارة المستخدمين، والاستفادة من أدوات الشبكة والتشخيص والصيانة.',
        ):
            bullet(about_content,item)
        heading(about_content,'إعدادات مستقلة لكل باقة')
        bullet(about_content,
            'عند حفظ إعدادات الباقة، يحتفظ البرنامج باختيارات القالب والتوليد والطباعة المرتبطة بها، '
            'ويستعيدها عند اختيار الباقة مجددًا؛ لتقليل تكرار الضبط والحفاظ على اتساق الكروت المطبوعة.')
        heading(about_content,'سهولة الاستخدام')
        bullet(about_content,
            'واجهة عربية منظمة تسهّل الوصول إلى الأدوات وتنفيذ المهام اليومية بوضوح وسلاسة.')

        support_pane=panel('support','خدمة العملاء')
        back_button(support_pane)
        support_content=scroll_content(support_pane)
        heading(support_content,'معلومات المطوّر والتواصل')
        bullet(support_content,'برمجة وتطوير: صالح بن عمرات')
        bullet(support_content,'رقم التواصل: 774660770')
        telegram_row=tk.Frame(support_content,bg=pale,highlightbackground='#E2D3B9',
                              highlightthickness=1)
        telegram_row.pack(fill='x',pady=3)
        tk.Label(telegram_row,text='●',bg=pale,fg='#B5863D',font=('Arial',11,'bold'),
                 width=2,anchor='n').pack(side='right',padx=(2,9),pady=(11,5))
        tk.Label(telegram_row,text='تيليجرام:',bg=pale,fg=navy,
                 font=('Arial',11,'bold')).pack(side='right',pady=11)
        tk.Button(telegram_row,text='t.me/Salehbinzain',bg=pale,fg='#174D82',
                  activebackground=pale,activeforeground='#174D82',relief='flat',
                  font=('Arial',11,'bold','underline'),cursor='hand2',
                  command=lambda:webbrowser.open('https://t.me/Salehbinzain',new=2)
                  ).pack(side='right',padx=9,pady=8)

    def _refresh_connection_mode(self):
        remote=self.vars['connection_mode'].get()=='remote'
        self.host_label.configure(text='رابط الاتصال :' if remote else 'عنوان الراوتر :')
        self.port_label.configure(text='API خارجي :' if remote else 'API :')
        if remote:self.connection_hint.grid()
        else:self.connection_hint.grid_remove()

    def switch_connection_mode(self):
        if self.busy:return
        self._address_drafts[self._display_mode]=tuple(self.vars[key].get() for key in self._address_fields)
        self._display_mode=self.vars['connection_mode'].get()
        for key,value in zip(self._address_fields,self._address_drafts[self._display_mode]):self.vars[key].set(value)
        self.toggle_ssl(update_port=False)
        self._refresh_connection_mode()
        self.set_message('أدخل رابط الوصول ومنفذ API الخارجي، ثم استخدم حساب الراوتر نفسه.' if self._display_mode=='remote' else 'أدخل عنوان الراوتر المحلي ومنفذ API.')

    def toggle_history(self):
        self.history_visible=not self.history_visible
        if self.history_visible:self.history.grid(row=8,column=0,columnspan=4,sticky='ew',padx=3,pady=4)
        else:self.history.grid_remove()

    def select_history(self,_event=None):
        if self.busy:return
        selected=self.history_tree.selection()
        if not selected:return
        key=next((k for k,row in self.saved_rows.items() if row and str(row['id'])==selected[0]),None)
        if key:self.saved_var.set(key);self.select_saved()

    def refresh_saved(self):
        new = AR('اتصال جديد')
        self.saved_rows = {new: None}
        for row in self.app.db.routers():
            title = '%s — %s:%s [%s]' % (AR(row['name']), row['host'], row['port'], row['id'])
            if row.get('connection_mode')=='remote':title=AR('عن بُعد')+' | '+title
            self.saved_rows[title] = dict(row)
        for item in self.history_tree.get_children():self.history_tree.delete(item)
        for row in self.saved_rows.values():
            if row:self.history_tree.insert('','end',iid=str(row['id']),values=(row['host'],row['username'],row['port'],'عن بُعد' if row.get('connection_mode')=='remote' else 'محلي'))
        self.saved_combo['values'] = list(self.saved_rows)
        if self.saved_var.get() not in self.saved_rows:
            self.saved_var.set(new)

    def select_saved(self, _event=None):
        if self.busy:
            return
        row = self.saved_rows.get(self.saved_var.get())
        defaults = {'host': '192.168.88.1', 'port': '8728', 'username': 'admin',
                    'password': '', 'use_ssl': False, 'verify_ssl': True,'connection_mode':'local'}
        for key, variable in self.vars.items():
            variable.set(row.get(key, defaults[key]) if row else defaults[key])
        self.save_password.set(bool(row and row.get('password')))
        self.show_password.set(False)
        self.toggle_password()
        self.toggle_ssl(update_port=False)
        self._display_mode=self.vars['connection_mode'].get()
        self._address_drafts={'local':('192.168.88.1','8728',False,True),'remote':('','',False,True)}
        self._address_drafts[self._display_mode]=tuple(self.vars[key].get() for key in self._address_fields)
        self._refresh_connection_mode()
        self.set_message('اضغط تسجيل الدخول للتحقق من البيانات المحفوظة.')

    def toggle_password(self):
        self.password_entry.configure(show='' if self.show_password.get() else '*')

    def toggle_ssl(self, update_port=True):
        use_ssl = self.vars['use_ssl'].get()
        if update_port and self.vars['connection_mode'].get()=='local' and self.vars['port'].get().strip() in ('8728', '8729'):
            self.vars['port'].set('8729' if use_ssl else '8728')
        self.verify_check.configure(state='normal' if use_ssl and not self.busy else 'disabled')

    def set_message(self, message, error=False):
        self.message_var.set(AR(message))
        self.message_label.configure(fg=self.colors['red'] if error else self.colors['muted'])

    def set_busy(self, busy):
        self.busy = busy
        for widget in self.controls:
            widget.configure(state='disabled' if busy else 'normal')
        self.saved_combo.configure(state='disabled' if busy else 'readonly')
        self.cancel_button.configure(text=AR('إلغاء الاتصال' if busy else 'إغلاق'))
        self.toggle_ssl(update_port=False)
        if busy:
            self.progress.start(15)
        else:
            self.progress.stop()
        access=self.app.current_license_access()
        trial_allowed=access.mode in ('ready','trial') and self.trial_opted_in
        self.login_button.configure(state=('normal' if not busy and (access.mode=='active' or trial_allowed) else 'disabled'))
        self.trial_button.configure(state=('disabled' if busy else 'normal'))

    def _paste_serial(self, _event=None):
        try:
            copied=self.serial_entry.clipboard_get().strip()
        except tk.TclError:
            self.set_message('حافظة النسخ فارغة أو يتعذر الوصول إليها. انسخ الرقم من أداة التراخيص أولاً.', True)
            return 'break'
        try:
            first=self.serial_entry.index('sel.first')
            self.serial_entry.delete('sel.first','sel.last')
        except tk.TclError:
            first=self.serial_entry.index('insert')
        self.serial_entry.insert(first,copied)
        self.serial_entry.icursor(first+len(copied))
        self.serial_entry.focus_set()
        return 'break'

    def _select_serial(self, _event=None):
        self.serial_entry.focus_set()
        self.serial_entry.selection_range(0,'end')
        self.serial_entry.icursor('end')
        return 'break'

    def _serial_context_menu(self, event):
        self.serial_entry.focus_set()
        try:
            self.serial_context.tk_popup(event.x_root,event.y_root)
        finally:
            self.serial_context.grab_release()
        return 'break'

    def copy_device_code(self):
        try:
            code=self.app.license_device_code()
            self.clipboard_clear()
            self.clipboard_append(code)
            self.set_message('تم نسخ بصمة الجهاز للدعم أو طلب نقل ترخيص قائم؛ لا تحتاجها للتفعيل الأول.')
        except tk.TclError:
            self.set_message('تعذر نسخ رقم الجهاز.',True)

    def choose_trial(self):
        """Opt in to the original trial; starts only after RouterOS login succeeds."""
        if self.busy:
            return
        access=self.app.current_license_access()
        if access.mode not in ('ready','trial'):
            self.set_message(access.message,True)
            return
        self.trial_opted_in=True
        self.set_message('تم اختيار التجربة المجانية. تبدأ الـ 20 يوماً عند أول دخول ناجح إلى الراوتر.')
        self.show_license_status(access)

    def _show_activation_controls(self):
        self.renew_license_button.pack_forget()
        if not self.activation_hint.winfo_manager():
            self.activation_hint.pack(fill='x',pady=(0,4))
        if not self.activation_row.winfo_manager():
            self.activation_row.pack(fill='x')

    def _hide_activation_controls(self):
        self.activation_hint.pack_forget()
        self.activation_row.pack_forget()
        if not self.renew_license_button.winfo_manager():
            self.renew_license_button.pack(fill='x',pady=(0,3))

    def open_renewal_entry(self):
        if self.busy:
            return
        self.renewal_open=True
        self.serial_var.set('')
        self._show_activation_controls()
        self.serial_entry.focus_set()

    def show_license_status(self, access):
        self.license_message_var.set(access.message)
        self.license_status_label.configure(
            fg=self.colors['red'] if access.mode in ('locked','clock_error') else self.colors['navy'])
        trial_available=access.mode in ('ready','trial')
        if access.mode=='active':
            self.trial_opted_in=False
            self.trial_button.pack_forget()
            self.trial_explanation.pack_forget()
            if self.renewal_open:
                self._show_activation_controls()
            else:
                self._hide_activation_controls()
        else:
            self._show_activation_controls()
            if not self.trial_explanation.winfo_manager():
                self.trial_explanation.pack(fill='x',before=self.activation_hint,pady=(0,3))
            if trial_available:
                self.trial_explanation.configure(
                    text='التجربة المجانية لمدة 20 يوماً تبدأ عند أول دخول ناجح؛ لن تُعاد المدة بعد انتهائها.')
                self.trial_button.configure(
                    text=('✓ التجربة محددة — اضغط اتصال بالراوتر' if self.trial_opted_in
                          else ('متابعة التجربة المجانية' if access.mode=='trial'
                                else 'بدء التجربة المجانية — 20 يوماً')),
                    state='disabled' if self.busy else 'normal')
                if not self.trial_button.winfo_manager():
                    self.trial_button.pack(fill='x',before=self.activation_hint,pady=(0,4))
            else:
                self.trial_opted_in=False
                self.trial_button.pack_forget()
                self.trial_explanation.configure(text=(
                    'انتهت فترة التجربة المجانية؛ يلزم إدخال رمز الترخيص.'
                    if access.reason=='trial_expired' else 'يمكنك تفعيل رمز الترخيص من هذا الحقل.'))
        allowed=access.mode=='active' or (trial_available and self.trial_opted_in)
        self.login_button.configure(state='normal' if allowed and not self.busy else 'disabled')

    def show_license_details(self):
        access=self.app.current_license_access()
        lines=[access.message,'','رقم الجهاز:',self.app.license_device_code(),
               '','رمز الترخيص:',access.license_id or 'لم يتم التفعيل',
               'تاريخ الانتهاء:',access.expires or 'غير محدد',
               '', ('التجربة المجانية محلية لمدة 20 يوماً؛ رمز الترخيص يتطلب الإنترنت.'
                    if access.mode in ('ready','trial') else
                    'رمز الترخيص يتطلب الاتصال بالإنترنت كل 30 دقيقة.')]
        dialog=tk.Toplevel(self)
        dialog.title('تفاصيل رمز الترخيص')
        dialog.transient(self.winfo_toplevel())
        panel=tk.Text(dialog,width=68,height=min(16,len(lines)+2),wrap='word',
                      font=('Arial',11),padx=12,pady=10)
        panel.pack(fill='both',expand=True,padx=8,pady=8)
        panel.tag_configure('details',justify='right')
        panel.insert('1.0','\n'.join(lines),'details')
        panel.configure(state='disabled')
        ttk.Button(dialog,text='نسخ بصمة الجهاز للدعم أو نقل الترخيص',
                   command=self.copy_device_code).pack(pady=(0,4))
        ttk.Button(dialog,text='إغلاق',command=dialog.destroy).pack(pady=(0,8))
        dialog.bind('<Escape>',lambda _e:dialog.destroy())

    def submit_activation(self):
        if not self.busy:
            self.app.activate_license_code(self.serial_var.get())

    def submit(self):
        if self.busy:
            return
        row = dict(self.saved_rows.get(self.saved_var.get()) or {})
        row.update({key: variable.get() for key, variable in self.vars.items()})
        self.app.start_login(row, self.save_password.get())

    def cancel(self):
        if self.busy:
            self.app.cancel_login()
        else:
            self.app.on_close()

    def reset(self, message='', error=False):
        self.show_section('login')
        self.set_busy(False)
        # Do not carry an unsubmitted activation code across login/logout.
        self.serial_var.set('')
        self.renewal_open=False
        self.show_license_status(self.app.current_license_access())
        self.vars['password'].set('')
        self.show_password.set(False)
        self.toggle_password()
        self.refresh_saved()
        self.set_message(message or 'سجّل الدخول بالبيانات الخاصة براوتر MikroTik.', error)
        self.password_entry.focus_set()
