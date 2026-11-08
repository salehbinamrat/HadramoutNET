"""Explicitly authorized router maintenance screens.

All RouterOS reads and writes run through app.run_network (serialized, async).
No scheduled task, page refresh, card creation or cleanup calls these writes.
"""
from __future__ import annotations

import tkinter as tk
from tkinter import ttk, messagebox

from .theme import COLORS


def _text(parent,text='',*,size=11,bold=True,color=None,bg=None,**kwargs):
    return tk.Label(parent,text=text,anchor='e',justify='right',
                    bg=bg or parent.cget('bg'),fg=color or COLORS['text'],
                    font=('Arial',size,'bold' if bold else 'normal'),**kwargs)




class RouterMaintenancePage(tk.Frame):
    """Unified network control screen. No write occurs without two confirmations."""
    def __init__(self,parent,app):
        super().__init__(parent,bg=COLORS['bg'])
        self.app=app;self.busy=False;self.loaded=False;self.servers=[];self.sessions=[];self.neighbors=[]
        self.known_version=None
        self.router=lambda:app.session.router_row()
        from core.db import DATA_DIR
        self.data_dir=DATA_DIR
        header=tk.Frame(self,bg=COLORS['navy'],height=67)
        header.pack(fill='x',padx=9,pady=(7,3));header.pack_propagate(False)
        _text(header,'التحكم بالشبكة والصيانة',size=18,color='white').pack(side='right',padx=12,pady=8)
        _text(header,'إدارة السيرفرات • قاعدة البيانات • الأجهزة المتصلة',size=10,color='#CEE6FB').pack(side='right',padx=4)
        self.state_var=tk.StringVar(value='لم تُقرأ بيانات السيرفرات بعد.')
        _text(self,textvariable=self.state_var,size=10,color='#163B5A').pack(fill='x',padx=16,pady=(2,6))
        self.tabs=ttk.Notebook(self)
        self.tabs.pack(fill='both',expand=True,padx=10,pady=(2,10))
        self.servers_tab=tk.Frame(self.tabs,bg=COLORS['panel'])
        self.db_tab=tk.Frame(self.tabs,bg=COLORS['panel'])
        self.neighbors_tab=tk.Frame(self.tabs,bg=COLORS['panel'])
        self.sessions_tab=tk.Frame(self.tabs,bg=COLORS['panel'])
        for tab,title in ((self.servers_tab,'سيرفرات الهوتسبوت'),(self.db_tab,'قاعدة البيانات والراوتر'),
                          (self.neighbors_tab,'الأجهزة المتصلة'),(self.sessions_tab,'الجلسات')):
            self.tabs.add(tab,text=title)
        self._build_servers();self._build_db();self._build_neighbors();self._build_sessions()
        self.tabs.bind('<<NotebookTabChanged>>',self._tab_changed)

    def _button(self,parent,text,fn,style='Accent.TButton'):
        button=ttk.Button(parent,text=text,command=fn,style=style)
        button.pack(side='right',padx=3,pady=5)
        return button

    def _tree(self,parent,headings,height=9):
        wrap=tk.Frame(parent,bg=COLORS['panel']);wrap.pack(fill='both',expand=True,padx=8,pady=5)
        columns=tuple(k for k,_,_ in headings)
        tree=ttk.Treeview(wrap,columns=columns,show='headings',height=height,selectmode='extended')
        for key,title,width in headings:
            tree.heading(key,text=title)
            tree.column(key,width=width,anchor='center',stretch=True)
        scroll=ttk.Scrollbar(wrap,orient='vertical',command=tree.yview)
        scroll.pack(side='left',fill='y');tree.pack(side='right',fill='both',expand=True)
        tree.configure(yscrollcommand=scroll.set)
        return tree

    def _build_servers(self):
        tab=self.servers_tab
        _text(tab,'تعطيل المصادقة قد يقطع المشتركين أو يغيّر الوصول للشبكة؛ لا يضمن إنترنت مجانيًا.',
              size=10,color='#8B2E18').pack(fill='x',padx=12,pady=(8,1))
        controls=tk.Frame(tab,bg=COLORS['panel']);controls.pack(fill='x',padx=8,pady=(2,1))
        self._button(controls,'تحديث السيرفرات',self.read_servers)
        self._button(controls,'تحديد الكل',lambda:self.server_tree.selection_set(self.server_tree.get_children()),'Dark.TButton')
        self._button(controls,'إلغاء التحديد',lambda:self.server_tree.selection_remove(self.server_tree.selection()),'Dark.TButton')
        actions=tk.Frame(tab,bg=COLORS['panel']);actions.pack(fill='x',padx=8,pady=1)
        self._button(actions,'تفعيل المحدد',lambda:self.change_servers(False),'Green.TButton')
        self._button(actions,'تعطيل المحدد',lambda:self.change_servers(True),'Danger.TButton')
        self._button(actions,'بدء وضع الصيانة المؤقت',self.start_mode,'Dark.TButton')
        self.restore_button=self._button(actions,'استعادة الحالات الأصلية',self.restore_mode,'Green.TButton')
        self.restore_button.configure(state='disabled')
        self.server_tree=self._tree(tab,[('select','اختيار',55),('name','اسم السيرفر',160),('interface','الواجهة',135),
                    ('profile','البروفايل',130),('status','الحالة',100)],height=9)
        self.server_tree.bind('<<TreeviewSelect>>',lambda _event:self._show_server_checks())
        self.server_tree.bind('<Button-1>',self._toggle_server_check)
        self._make_sortable(self.server_tree)
        self.servers_info=tk.StringVar(value='لم تُقرأ قائمة Hotspot بعد.')
        _text(tab,textvariable=self.servers_info,size=10,color=COLORS['navy']).pack(fill='x',padx=15,pady=(1,5))

    def _make_sortable(self,tree):
        def sorter(column):
            items=list(tree.get_children(''))
            items.sort(key=lambda iid: str(tree.set(iid,column)).casefold())
            for index,iid in enumerate(items):tree.move(iid,'',index)
        for column in tree['columns']:
            if column!='select':
                tree.heading(column,text=tree.heading(column,'text'),command=lambda c=column:sorter(c))

    def _show_server_checks(self):
        selected=set(self.server_tree.selection())
        for iid in self.server_tree.get_children():
            self.server_tree.set(iid,'select','☑' if iid in selected else '☐')

    def _toggle_server_check(self,event):
        if self.server_tree.identify_column(event.x)!='#1':return
        item=self.server_tree.identify_row(event.y)
        if not item:return
        selected=list(self.server_tree.selection())
        if item in selected:self.server_tree.selection_remove(item)
        else:self.server_tree.selection_add(item)
        self._show_server_checks()
        return 'break'

    def _build_db(self):
        tab=self.db_tab
        _text(tab,'هوية الراوتر وإجراءات مدير المستخدمين',size=14,color=COLORS['navy']).pack(fill='x',padx=13,pady=(10,7))
        self.db_status=tk.StringVar(value='اضغط «قراءة حالة قاعدة البيانات» لمعرفة الإصدار والأوامر المتاحة.')
        _text(tab,textvariable=self.db_status,size=11,color=COLORS['text'],wraplength=940).pack(fill='x',padx=15,pady=8)
        self.backup_var=tk.BooleanVar(value=False)
        ttk.Checkbutton(tab,text='أؤكد أن لدي نسخة احتياطية موثوقة من مدير المستخدمين',
                        variable=self.backup_var).pack(anchor='e',padx=15,pady=7)
        controls=tk.Frame(tab,bg=COLORS['panel']);controls.pack(fill='x',padx=10,pady=7)
        self._button(controls,'قراءة حالة قاعدة البيانات',self.read_db)
        self.optimize_button=self._button(controls,'تحسين قاعدة البيانات (v7)',self.optimize_db,'Dark.TButton')
        self.optimize_button.configure(state='disabled')
        self._button(controls,'إعادة تشغيل الراوتر',self.reboot,'Danger.TButton')
        _text(tab,'إعادة بناء قاعدة البيانات في RouterOS 6: معطلة عبر API حتى يتم التحقق من الموافقة التفاعلية وإتمام النسخ الاحتياطي.\n'
              'تحسين قاعدة البيانات في RouterOS 7 يختلف عن إعادة بنائها. لن تُحذف قواعد البيانات أو الباقات.',
              size=10,color='#82471B',wraplength=900).pack(fill='x',padx=14,pady=11)
        self.db_result=tk.StringVar(value='لم تُنفذ أي صيانة.')
        _text(tab,textvariable=self.db_result,size=10,color=COLORS['navy'],wraplength=850).pack(fill='x',padx=14,pady=7)

    def _build_neighbors(self):
        tab=self.neighbors_tab
        controls=tk.Frame(tab,bg=COLORS['panel']);controls.pack(fill='x',padx=8,pady=6)
        self._button(controls,'تحديث الأجهزة',self.read_neighbors)
        self._button(controls,'نسخ IP/MAC المحدد',self.copy_neighbor,'Dark.TButton')
        self.search_var=tk.StringVar()
        ttk.Entry(controls,textvariable=self.search_var,width=28,justify='right').pack(side='right',padx=7,pady=6)
        self.search_var.trace_add('write',lambda *_:self.show_neighbors())
        _text(controls,'بحث:').pack(side='right')
        self.neighbor_tree=self._tree(tab,[('interface','الواجهة',100),('address','عنوان IP',135),
              ('mac','MAC',145),('identity','الجهاز',150),('platform','المنصة',90),
              ('version','الإصدار',105)],height=10)
        self._make_sortable(self.neighbor_tree)
        self.neighbors_info=tk.StringVar(value='قائمة للقراءة فقط، باستخدام اكتشاف الراوتر الموجود.')
        _text(tab,textvariable=self.neighbors_info,size=10,color=COLORS['navy']).pack(fill='x',padx=15,pady=5)

    def _build_sessions(self):
        tab=self.sessions_tab
        controls=tk.Frame(tab,bg=COLORS['panel']);controls.pack(fill='x',padx=8,pady=5)
        self._button(controls,'قراءة الجلسات الحالية',self.read_sessions)
        self._button(controls,'فصل الجلسات المحددة',self.disconnect_sessions,'Danger.TButton')
        self.sessions_tree=self._tree(tab,[('service','النوع',95),('user','المستخدم',220),
                   ('address','IP',170),('id','المعرف',90)],height=10)
        self.sessions_info=tk.StringVar(value='الفصل اختياري ولا يُنفّذ بعد الطباعة تلقائياً.')
        _text(tab,textvariable=self.sessions_info,size=10,color=COLORS['navy']).pack(fill='x',padx=15,pady=7)

    def _tab_changed(self,event=None):
        if self.busy:return
        tab=self.tabs.select()
        if tab==str(self.neighbors_tab) and not self.neighbors:self.read_neighbors()
        if tab==str(self.db_tab) and self.known_version is None:self.read_db()

    def _run(self,worker,done,*,write=False):
        if self.busy:return
        self.busy=True
        def okay(result):
            if not self.winfo_exists():return
            self.busy=False
            done(result)
        def failed(exc):
            if not self.winfo_exists():return
            self.busy=False
            self.state_var.set('تعذرت العملية: '+str(exc)[:220])
            self.app.db.log('ROUTER','التحكم بالشبكة والصيانة','ERROR',str(exc)[:160])
            messagebox.showerror('التحكم بالشبكة والصيانة',str(exc),parent=self)
            self._update_mode()
        self.app.run_network(worker,okay,failed,read_only=not write,affects_inventory=False)

    def _router(self):
        router=self.router()
        if not router:raise ValueError('لا يوجد اتصال موثّق بالراوتر.')
        return router

    def _update_mode(self):
        try:
            from core.maintenance_state import file_for,read
            state=read(file_for(self.data_dir,self._router()))
            active=bool(state and state.get('status')!='restored')
            count=len(state['servers']) if active else 0
            self.restore_button.configure(state='normal' if active else 'disabled')
            text=('وضع الصيانة نشط/بانتظار الاستعادة (%s سيرفر)' % count if active else 'وضع الصيانة غير نشط')
            return active,text
        except Exception as exc:
            self.restore_button.configure(state='disabled')
            return True,'ملف استعادة غير قابل للقراءة: '+str(exc)

    def read_servers(self):
        if self.busy:return
        self.state_var.set('جارٍ قراءة السيرفرات من الراوتر...')
        def work(gateway):
            rows=gateway.hotspot_servers()
            try:
                facts=gateway.api.command('/system/resource/print')
                version=facts[0].get('version','') if facts else ''
            except Exception:version=''
            return rows,version
        def done(payload):
            rows,version=payload
            self.servers=rows
            self.server_tree.delete(*self.server_tree.get_children())
            for i,row in enumerate(rows):
                self.server_tree.insert('','end',iid=str(i),values=('☐',row['name'],row['interface'],row['profile'],
                   'معطّل' if row['disabled'] else 'مفعّل'))
            active,status=self._update_mode()
            self.servers_info.set('%s سيرفر | %s' % (len(rows),status))
            self.state_var.set('الراوتر: %s | %s | RouterOS %s | %s سيرفر' %
                  (self.app.session.identity,self._router()['host'],version or 'غير معروف',len(rows)))
            self.loaded=True
        self._run(work,done)

    def _selected_servers(self):
        return [self.servers[int(i)] for i in self.server_tree.selection()]

    def _confirm(self,title,body):
        if not messagebox.askyesno(title,body,parent=self,icon='warning'):return False
        return messagebox.askyesno('تأكيد إداري نهائي','تأكيد إرسال العملية المطلوبة إلى الراوتر المتصل الآن؟',
                                   parent=self,icon='warning')

    def change_servers(self,disabled):
        if self.busy:return
        rows=self._selected_servers()
        if not rows:return messagebox.showinfo('السيرفرات','حدد السيرفرات أولًا.',parent=self)
        active,_=self._update_mode()
        if active:return messagebox.showwarning('وضع الصيانة','استعد الحالات الأصلية أولًا؛ التعديل اليدوي محظور أثناء وضع الصيانة.',parent=self)
        action='تعطيل' if disabled else 'تفعيل'
        names='، '.join(r['name'] for r in rows[:15]);more=' ...' if len(rows)>15 else ''
        if not self._confirm(action+' السيرفرات','ستتم محاولة %s %s سيرفر:\n%s%s\nقد يتأثر اتصال العملاء.' %
                             (action,len(rows),names,more)):return
        ids=[r['id'] for r in rows]
        self.state_var.set('جارٍ التحقق من عملية '+action)
        def done(result):
            self.app.db.log('ROUTER','Hotspot '+action,'SUCCESS','servers=%s' % result['requested'])
            self.state_var.set('تم التحقق: %s سيرفر؛ تغيرت حالة %s.' % (result['requested'],result['changed']))
            self.read_servers()
        router=dict(self._router());data_dir=self.data_dir
        self._run(lambda gateway:gateway.change_hotspot_servers(ids,disabled,router,data_dir,expected=rows),done,write=True)

    def start_mode(self):
        if self.busy:return
        rows=self._selected_servers()
        if not rows:return messagebox.showinfo('وضع الصيانة','حدد السيرفرات المستهدفة أولًا.',parent=self)
        active,status=self._update_mode()
        if active:return messagebox.showwarning('وضع الصيانة','لا يمكن بدء وضع جديد؛ '+status,parent=self)
        names='، '.join(r['name'] for r in rows[:16]);more=' ...' if len(rows)>16 else ''
        if not self._confirm('وضع الصيانة المؤقت',
           'سيُحفَظ وضع كل سيرفر ثم تُعطّل السيرفرات المفعّلة من %s محددة:\n%s%s\n\n'
           'هذه العملية لا تضمن إنترنت مجانيًا وقد توقف تسجيل دخول المشتركين. '
           'لن نعيد تشغيل الراوتر قبل استعادة الحالة. تأكد من توفر وصول إدارة بديل.'%
           (len(rows),names,more)):return
        ids=[r['id'] for r in rows];router=dict(self._router());data_dir=self.data_dir
        self.state_var.set('جارٍ حفظ حالات السيرفرات ثم التعديل...')
        def done(result):
            self.app.db.log('ROUTER','تفعيل وضع الصيانة','SUCCESS','changed=%s' % result['modified'])
            self.state_var.set('وضع الصيانة نشط؛ عُطلت %s من %s سيرفر. الاستعادة اليدوية مطلوبة.'%
                               (result['modified'],result['selected']))
            self.read_servers()
        self._run(lambda g:g.start_temporary_hotspot_mode(ids,router,data_dir,expected=rows),done,write=True)

    def restore_mode(self):
        if self.busy:return
        active,status=self._update_mode()
        if not active:return
        if not self._confirm('استعادة الهوتسبوت',
                             'ستعود الحالات الأصلية المحفوظة؛ السيرفرات التي كانت معطلة ستظل معطلة.\n'+status):return
        router=dict(self._router());data_dir=self.data_dir
        self.state_var.set('جارٍ استعادة الحالات الأصلية والتحقق من كل سيرفر...')
        def done(result):
            self.app.db.log('ROUTER','استعادة سيرفرات Hotspot','SUCCESS','servers=%s' % result['restored'])
            self.state_var.set('تمت الاستعادة والتحقق من %s سيرفر.'%result['restored'])
            self.read_servers()
        self._run(lambda g:g.restore_temporary_hotspot_mode(router,data_dir),done,write=True)

    def read_db(self):
        if self.busy:return
        self.db_status.set('جارٍ فحص معلومات مدير المستخدمين...')
        def done(data):
            self.known_version=data['version']
            self.optimize_button.configure(state='normal' if data['version']==7 and data['status']=='متاحة' else 'disabled')
            self.db_status.set('مدير المستخدمين (الإصدار %s) | %s | حجم القاعدة: %s | المساحة المتاحة: %s' %
                (str(data['version']).translate(str.maketrans('67','٦٧')),data['status'],data.get('db_size') or 'غير معروف',data.get('free_space') or 'غير معروف'))
        self._run(lambda g:g.database_maintenance_status(),done)

    def optimize_db(self):
        if self.busy:return
        if self.known_version!=7:
            return messagebox.showerror('قاعدة البيانات','تحسين الإصدار ٧ غير متاح أو لم يتم التحقق منه.',parent=self)
        if not self.backup_var.get():
            return messagebox.showwarning('نسخة احتياطية','أكد وجود نسخة احتياطية موثوقة قبل المتابعة.',parent=self)
        if not self._confirm('تحسين مدير المستخدمين',
            'سيُنفّذ أمر optimize-db على مدير المستخدمين (الإصدار ٧)، دون إعادة بناء القاعدة. '
            'قد يتأخر ردّ الراوتر. لا تغلق البرنامج. هل تتابع؟'):return
        def done(result):
            self.db_result.set('نتيجة الأمر: '+ ' | '.join(result))
            self.app.db.log('ROUTER','تحسين مدير المستخدمين (الإصدار ٧)','SUCCESS','command accepted')
            self.read_db()
        self._run(lambda g:g.perform_maintenance([('optimize',None)]),done,write=True)

    def reboot(self):
        if self.busy:return
        active,warning=self._update_mode()
        if active:
            return messagebox.showerror('إعادة التشغيل ممنوعة','يجب استعادة السيرفرات قبل إعادة تشغيل الراوتر.\n'+warning,parent=self)
        if not self._confirm('إعادة تشغيل الراوتر',
          'سيتم قطع الشبكة وكل الجلسات مؤقتاً. قد يتعذر إعادة الاتصال؛ '
          'يجب التحقق من عودة الخدمات يدوياً بعد إعادة تشغيل الراوتر.'):return
        self.db_result.set('جارٍ إرسال طلب إعادة التشغيل...')
        def done(result):
            self.db_result.set(' | '.join(result))
            self.app.db.log('ROUTER','إعادة تشغيل الراوتر','INFO','reboot requested')
            self.app._schedule_heartbeat(15000)
        self._run(lambda g:g.perform_maintenance([('reboot',None)]),done,write=True)

    def read_neighbors(self):
        if self.busy:return
        self.neighbors_info.set('جارٍ القراءة من اكتشاف الراوتر، دون مسح الشبكة...')
        def done(rows):
            self.neighbors=rows;self.show_neighbors()
        self._run(lambda g:g.neighbor_devices(),done)

    def show_neighbors(self):
        if not hasattr(self,'neighbor_tree'):return
        query=self.search_var.get().strip().lower()
        rows=[r for r in self.neighbors if not query or query in ' '.join(r.values()).lower()]
        self.neighbor_tree.delete(*self.neighbor_tree.get_children())
        for idx,row in enumerate(rows):
            self.neighbor_tree.insert('','end',iid=str(idx),values=(row['interface'],
                row.get('address4') or row['address'],row['mac-address'],row['identity'],
                row['platform'],row['version']))
        self.neighbors_info.set('%s جهاز من أصل %s | للقراءة فقط.'%(len(rows),len(self.neighbors)))

    def copy_neighbor(self):
        selected=self.neighbor_tree.selection()
        if not selected:return
        address,mac=self.neighbor_tree.item(selected[0],'values')[1:3]
        self.clipboard_clear();self.clipboard_append('IP: %s | MAC: %s'%(address,mac))
        self.neighbors_info.set('تم نسخ عنوان الجهاز المحدد.')

    def read_sessions(self):
        if self.busy:return
        self.sessions_info.set('جارٍ قراءة جلسات Hotspot وPPPoE...')
        def done(rows):
            self.sessions=rows;self.sessions_tree.delete(*self.sessions_tree.get_children())
            for i,row in enumerate(rows):
                self.sessions_tree.insert('','end',iid=str(i),values=(row['service'],row['user'],row['address'],row['id']))
            self.sessions_info.set('تمت قراءة %s جلسة. لا يتم فصل أي جلسة دون تأكيد.'%len(rows))
        self._run(lambda g:g.maintenance_sessions(),done)

    def disconnect_sessions(self):
        if self.busy:return
        rows=[self.sessions[int(i)] for i in self.sessions_tree.selection()]
        if not rows:return messagebox.showinfo('الجلسات','اختر جلسة واحدة أو أكثر.',parent=self)
        if not self._confirm('فصل الجلسات','سيُقطع اتصال %s جلسة. لا يمكن التراجع عن ذلك.'%len(rows)):return
        actions=[('disconnect',r) for r in rows]
        def done(result):
            self.app.db.log('ROUTER','فصل جلسات','SUCCESS','sessions=%s'%len(result))
            self.sessions_info.set('تم تنفيذ %s أمر فصل.'%len(result));self.read_sessions()
        self._run(lambda g:g.perform_maintenance(actions),done,write=True)

    def select_action(self,action):
        if action in ('disable','restore'):
            self.tabs.select(self.servers_tab)
        else:
            self.tabs.select(self.db_tab)
        if action=='restore':self._update_mode()
        if action in ('disable','restore') and not self.loaded:self.read_servers()

    def refresh(self):
        self._update_mode()
        if not self.loaded:self.read_servers()


def offer_optional_maintenance(app,reason):
    """A navigation prompt, never an execution prompt."""
    if messagebox.askyesno('الصيانة الاختيارية',
        '%s.\n\nهل تريد فتح نافذة صيانة الراوتر؟\n'
        'لن تُنفّذ أي عملية إلا بعد اختيارها وتأكيدها مرتين.' % reason,parent=app):
        app.show_page('maintenance')
