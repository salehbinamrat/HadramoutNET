from __future__ import annotations
import os, sys, threading, shutil, json, logging, ctypes, subprocess, time
from ctypes import wintypes
from queue import Queue, Empty
from logging.handlers import RotatingFileHandler
from pathlib import Path
from datetime import datetime
import tkinter as tk
from tkinter import ttk, filedialog, messagebox, colorchooser, simpledialog
from PIL import Image, ImageTk

SOURCE_DIR=Path(__file__).resolve().parent
ROOT_DIR=Path(sys.executable).resolve().parent if getattr(sys,'frozen',False) else SOURCE_DIR
sys.path.insert(0,str(SOURCE_DIR))

from core.db import Database, DATA_DIR
from core.github_license import APP_VERSION, OnlineLicensing
from core.trial_runtime import ClockGuard, begin_trial, trial_access, choose_access
from core.cards import readable_bytes
from core.delivery import send_cards
from core.mikrotik import connect_from_row
from core.render import render_card, card_to_tk_image
from core.printing import (
    export_settings_pdf, windows_printers, calculate_layout, auto_grid, card_pages,
)
from core.windows_printing import print_cards
from core.hotspot import upload_folder, download_folder, validate_hotspot_template
from core.exporting import export_excel, export_csv, export_script
from ui.arabic import ui_text as AR
from core.session import RouterSession, authenticate_router, login_settings, remember_connection
from core.connection_address import endpoint_text
from ui.login import LoginPage
from ui.layout import PageFrame, fit_table, fit_buttons
from ui.card_preview import CardPreview, ELEMENT_NAMES, preview_geometry
from ui.choices import LocalizedChoice
from ui.packages import PackageBar
from core.packages import package_snapshot, sample_card, CARD_IMAGE_FORMATS
from core.network import NetworkGateway, SERVICES, profile_defaults
from core.snapshots import merge_snapshot
from ui.network import NetworkHome as DashboardPage, NetworkProfilesPage as PackagesPage, VoucherWorkspace as CreateCardsPage, NetworkUsersPage, NetworkToolsPage, HotspotMonitorPage, VoucherStatusWindow, ActivityPage
from ui.network import offer_print_copy, printing_progress
from ui.operations import RouterMaintenancePage, offer_optional_maintenance

APP_NAME='حضرموت لإدارة الشبكات وطباعة الكروت'

from ui.theme import COLORS, apply_style



def _enable_windows_dpi_awareness():
    if os.name != 'nt':
        return
    try:
        ctypes.windll.shcore.SetProcessDpiAwareness(1)
        return
    except Exception:
        pass
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass


_enable_windows_dpi_awareness()
LOG_DIR=DATA_DIR/'logs'
LOG_DIR.mkdir(parents=True,exist_ok=True)
LOGGER=logging.getLogger('hadramout')
LOGGER.setLevel(logging.INFO)
if not LOGGER.handlers:
    _log_handler=RotatingFileHandler(str(LOG_DIR/'hadramout.log'),maxBytes=2*1024*1024,backupCount=3,encoding='utf-8')
    _log_handler.setFormatter(logging.Formatter('%(asctime)s %(levelname)s %(name)s %(message)s'))
    LOGGER.addHandler(_log_handler)


def _open_path(path):
    path=str(Path(path).resolve())
    if os.name=='nt':os.startfile(path)
    else:subprocess.Popen(['xdg-open',path],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)



class HadramoutApp(tk.Tk):
    def __init__(self):
        super().__init__()
        # Bound display density on small laptop screens before creating fonts.
        self._ui_scale=float(self.tk.call('tk','scaling'))
        # Fit the *entire* fixed-size dashboard to the working screen. This
        # scales fonts and native widgets as a unit instead of clipping them.
        density_limit=max(0.90,min((self.winfo_screenwidth()-16)/1280,
                                    (self.winfo_screenheight()-72)/820))*(4/3)
        self._ui_scale=min(self._ui_scale,density_limit)
        self.tk.call('tk','scaling',self._ui_scale)
        self.db=Database()
        self.active_package_id=self.db.get_setting('packages.active_id')
        saved_package=self.db.package(self.active_package_id)
        if saved_package:self.db.replace_preset_settings(saved_package['settings'])
        else:self.active_package_id=None
        self.network_catalog={};self.catalog_busy=False
        self.active_service='hotspot';self.active_profile_id=None
        self.title(f'{AR(APP_NAME)}  v{APP_VERSION}')
        self.configure(bg=COLORS['bg'])
        self.resizable(False,False)  # Windows native minimize/close remain; maximize is disabled.
        try:
            self._icon=tk.PhotoImage(file=str(SOURCE_DIR/'assets'/'hadramout_icon.png'))
            self.iconphoto(True,self._icon)
            if os.name=='nt':self.iconbitmap(str(SOURCE_DIR/'assets'/'hadramout_icon.ico'))
        except tk.TclError:pass
        self.protocol('WM_DELETE_WINDOW',self.on_close)
        self._style()
        self.session=RouterSession()
        self._closing=False
        self._pending_jobs=0
        self._pending_lock_message=''
        self._results=Queue()
        self._heartbeat_job=None
        self._heartbeat_failures=0
        self._last_dashboard_refresh=0.0
        self._dashboard_refresh_min_interval=20.0
        self._dashboard_poll_job=None
        self._dashboard_poll_last={'clients':0.0,'total':0.0}
        self._dashboard_poll_interval={'clients':25.0,'total':90.0}
        self._dashboard_poll_busy=False
        self._dashboard_poll_failures=0
        self.workspace=None
        self.page_frames={}
        self.nav_buttons={}
        self.current_page=None
        self._asset_icon_cache={}
        self.router_status_var=tk.StringVar(value='غير متصل')
        self.status_var=tk.StringVar(value='جاهز')
        self._licensing=OnlineLicensing()
        self._trial_clock=ClockGuard()
        self._license_check_busy=False
        # Login and workspace share one fitted size; switching screens must
        # not resize or recenter the window after the user has positioned it.
        self.fit_window(1280,820,1008,648)
        self.login_page=LoginPage(self,self,COLORS,APP_NAME,APP_VERSION)
        self._show_login()
        # A removed/replaced/expired license invalidates the current session too.
        self._license_monitor_id = self.after(5000, self._monitor_license)

        self._dispatch_job=self.after(50,self._drain_results)
        self.after(150,self._check_license_online)
        LOGGER.info('Application started v%s; database=%s',APP_VERSION,self.db.path)


    def load_asset_icon(self,name,size):
        """Load a local PNG once and return a cached Tk image."""
        if isinstance(size,int):
            size=(size,size)
        width,height=size
        key=(name,int(width),int(height))
        cached=self._asset_icon_cache.get(key)
        if cached is not None:
            return cached
        path=SOURCE_DIR/'assets'/'dashboard_icons'/name
        with Image.open(path, formats=('PNG',)) as icon_image:
            icon_image=icon_image.convert('RGBA')
            icon_image.thumbnail((int(width),int(height)),Image.LANCZOS)
            canvas=Image.new('RGBA',(int(width),int(height)),(0,0,0,0))
            offset=((int(width)-icon_image.width)//2,(int(height)-icon_image.height)//2)
            canvas.paste(icon_image,offset,icon_image)
            photo=ImageTk.PhotoImage(canvas)
        self._asset_icon_cache[key]=photo
        return photo

    def _show_login(self,message='',error=False):
        self._stop_dashboard_polling()
        self._stop_initial_lifecycle()
        if self._heartbeat_job:
            self.after_cancel(self._heartbeat_job)
            self._heartbeat_job=None
        self.session.lock()
        self._network_lock=threading.RLock()
        self._network_gateway=None
        self._network_write_pending=False
        self._profiles_loaded=False
        self.lifecycle_busy=False
        self._last_lifecycle_refresh=0.0
        self._last_lifecycle_attempt=0.0
        self._initial_lifecycle_refresh_started=False
        self._last_dashboard_refresh=0.0
        self._dashboard_poll_last={'clients':0.0,'total':0.0}
        self._dashboard_poll_busy=False
        self._dashboard_poll_failures=0
        self._heartbeat_failures=0
        self._last_profile_attempt=0.0
        self.network_catalog={};self.catalog_busy=False;self.active_profile_id=None;self.active_package_id=None
        self._pending_lock_message=''
        if self.workspace:
            for page in self.page_frames.values():page.destroy()
            self.workspace.destroy()
            self.workspace=None
        self.page_frames.clear()
        self.nav_buttons.clear()
        self.current_page=None
        self.state('normal')
        self.title(f'{AR(APP_NAME)} — {AR("تسجيل الدخول")}  v{APP_VERSION}')
        self.router_status_var.set(AR('غير متصل'))
        self.login_page.pack(fill='both',expand=True)
        self.login_page.reset(message,error)
        self._refresh_license_panel()

    def fit_window(self,width,height,min_width=760,min_height=460):
        factor=self._ui_scale/(4/3)
        width,height=int(width*factor),int(height*factor)
        min_width,min_height=int(min_width*factor),int(min_height*factor)
        left=top=0
        available_w=self.winfo_screenwidth()
        available_h=self.winfo_screenheight()-40
        if os.name=='nt':
            workarea=wintypes.RECT()
            if ctypes.windll.user32.SystemParametersInfoW(0x30,0,ctypes.byref(workarea),0):
                left,top=workarea.left,workarea.top
                available_w=workarea.right-left
                available_h=workarea.bottom-top
        available_w=max(320,available_w-16)
        available_h=max(300,available_h-32)
        width=min(width,available_w);height=min(height,available_h)
        self.minsize(min(min_width,width),min(min_height,height))
        self.geometry('%dx%d+%d+%d' % (width,height,left+max(0,(available_w-width)//2),top+max(0,(available_h-height)//2)))
        self.resizable(False,False)

    def flush_page_settings(self):
        if isinstance(self.current_page,PrintPage):return
        if self.current_page and hasattr(self.current_page,'draft_settings'):
            self.db.set_settings(self.current_page.draft_settings())

    def refresh_package_views(self):
        for page in self.page_frames.values():
            page._loaded_package=object()
            if isinstance(page,CreateCardsPage):page._loaded=None
        self.refresh_all()

    def save_active_package(self,quiet=False):
        try:
            if not self.current_profile():raise ValueError('اختر باقة من الراوتر قبل حفظ قالبها.')
            self.flush_page_settings()
            current=self.db.package(self.active_package_id)
            name=current['name'] if current else simpledialog.askstring('حفظ باقة','اسم الباقة، مثال: باقة 200 ريال:',parent=self)
            if not name:return False
            self.active_package_id=self.db.save_package(name,self.db.all_settings(),self.active_package_id)
            self.db.replace_preset_settings(self.db.package(self.active_package_id)['settings'])
            if current is None and isinstance(self.current_page,PrintPage) and self.current_page._batch_id() is not None:
                self.db.assign_batch_package(self.current_page._batch_id(),self.active_package_id)
            self.db.set_setting('packages.active_id',self.active_package_id)
            self.refresh_package_views()
            self.status_var.set(AR('تم حفظ الباقة وقالبها وإعدادات الطباعة'))
            if not quiet:messagebox.showinfo('تم الحفظ','حُفظت إعدادات الباقة، وستظل متاحة بعد إغلاق البرنامج وفتحه.')
            return True
        except Exception as exc:
            messagebox.showerror('حفظ الباقة',str(exc));return False

    def select_package(self,package_id,check_changes=True):
        if package_id==self.active_package_id:return True
        package=self.db.package(package_id)
        if package_id is not None and package is None:return False
        try:
            self.flush_page_settings()
            old=self.db.package(self.active_package_id)
            if old and check_changes and package_snapshot(self.db.all_settings())!=old['settings']:
                answer=messagebox.askyesnocancel('تعديلات الباقة','حفظ التعديلات على «%s» قبل تبديل الباقة؟' % old['name'])
                if answer is None:return False
                if answer and not self.save_active_package(quiet=True):return False
            for page in self.page_frames.values():
                job=getattr(page,'_save_job',None)
                if job:page.after_cancel(job);page._save_job=None
            self.active_package_id=package_id
            self.db.replace_preset_settings(package['settings'] if package else {})
            self.db.set_setting('packages.active_id',package_id)
            self.refresh_package_views()
            return True
        except Exception as exc:
            messagebox.showerror('اختيار الباقة',str(exc));return False

    def current_license_access(self):
        online=self._licensing.status()
        if online.permitted:
            return online
        trial=trial_access(clock=self._trial_clock)
        return choose_access(online,trial,has_paid_code=bool(self._licensing.code))

    def license_device_code(self):
        return self._licensing.device_id

    def _refresh_license_panel(self):
        status=self.current_license_access()
        self.login_page.show_license_status(status)
        return status

    def _check_license_online(self, code=None):
        """Network verification stays off Tk's UI thread."""
        if self._closing or self._license_check_busy:
            return
        if code is None and not self._licensing.code:
            return
        self._license_check_busy=True
        if code is not None:
            self.login_page.set_message('جارٍ التحقق من رمز الترخيص...')
        def complete(status):
            self._license_check_busy=False
            if code is not None and status.permitted:
                self.login_page.renewal_open=False
            self._refresh_license_panel()
            if code is not None:
                if status.permitted:
                    self.login_page.serial_var.set('')
                    self.login_page.set_message(status.message)
                    messagebox.showinfo('تفعيل الترخيص',status.message,parent=self)
                else:
                    self.login_page.set_message(status.message,True)
        def failed(error):
            self._license_check_busy=False
            LOGGER.warning('Online license check failed: %s',type(error).__name__)
            self._refresh_license_panel()
            self.login_page.set_message('تعذر التحقق من خدمة الترخيص. حاول مجدداً.',True)
        self.safe_thread(lambda:self._licensing.verify(code),complete,failed,require_auth=False)

    def activate_license_code(self, number):
        self._check_license_online(number)

    def start_login(self,values,save_password=False):
        status = self.current_license_access()
        if not status.permitted:
            self.login_page.set_message(status.message, True)
            return
        use_trial=status.mode in ('ready','trial')
        if use_trial and not self.login_page.trial_opted_in:
            self.login_page.set_message('اختر «بدء التجربة المجانية — 20 يوماً» أولاً، أو فعّل رمز الترخيص.',True)
            return
        if self._closing or self.session.state!='locked' or self._pending_jobs:
            return
        try:row=login_settings(values)
        except (ValueError,TypeError) as exc:
            self.login_page.set_message(str(exc),True)
            return
        self.login_page.vars['host'].set(row['host'])
        self.login_page.vars['port'].set(str(row['port']))
        attempt=self.session.begin()
        self.login_page.set_busy(True)
        self.login_page.set_message('جارٍ تسجيل الدخول إلى '+endpoint_text(row['host'],row['port'])+(' عبر الرابط البعيد...' if row['connection_mode']=='remote' else '...'))
        def complete(result):
            api,identity=result
            try:row.update(remember_connection(self.db,row,identity,save_password))
            except Exception:
                api.close()
                self.session.lock()
                self.login_page.set_busy(False)
                self.login_page.set_message('نجح اتصال الراوتر، لكن تعذر حفظ بيانات الاتصال محليًا. لم تفتح لوحة العمل.',True)
                LOGGER.exception('Could not save connection metadata')
                return
            if not self.session.accept(attempt,row,api,identity):return
            # Record the first successful RouterOS login, including paid-license users.
            # This matches the original 20-day trial semantics: activating a paid
            # license must not reserve a fresh future trial after revocation.
            trial_result=begin_trial(clock=self._trial_clock)
            if use_trial and not trial_result.permitted:
                self.session.lock()
                self.login_page.set_busy(False)
                self.login_page.set_message(trial_result.message,True)
                self._refresh_license_panel()
                return
            self.login_page.set_busy(False)
            self.login_page.vars['password'].set('')
            self.login_page.pack_forget()
            self.title(f'{AR(APP_NAME)}  v{APP_VERSION} — {identity}')
            try:
                self._build_shell()
                self._set_sidebar_connection(True)
                self.status_var.set(AR('تم تسجيل الدخول بنجاح'))
                self.show_page('dashboard')
                self.refresh_sidebar_routers()
                self._schedule_heartbeat()
                self._schedule_dashboard_poll(2500)
                self.refresh_network()
            except Exception:
                LOGGER.exception('Could not open workspace')
                self._show_login('تعذر فتح لوحة العمل. راجع سجل الأعطال ثم حاول مجددًا.',True)
        def failed(exc):
            self.session.lock()
            self.login_page.set_busy(False)
            detail='فشل تسجيل الدخول: '+str(exc)
            if row['connection_mode']=='remote':detail+='\nتحقق من منفذ API الخارجي لدى مزود الرابط؛ نجاح WinBox وحده لا يثبت إتاحة API.'
            self.login_page.set_message(detail,True)
        self.safe_thread(lambda:authenticate_router(row),complete,failed,require_auth=False,
                         on_discard=lambda result:result[0].close())

    def cancel_login(self):
        if self._pending_lock_message or self.session.state!='connecting':return
        self.session.lock()
        self.login_page.set_busy(False)
        self.login_page.set_message('تم إلغاء محاولة الاتصال. يمكنك المحاولة مجددًا.')

    def logout(self):
        if self._pending_jobs:
            return messagebox.showinfo('العملية جارية','انتظر اكتمال العملية الحالية قبل تسجيل الخروج.')
        if messagebox.askyesno('تسجيل الخروج','غلق اتصال البرنامج والعودة لشاشة الدخول؟ لن يُقطع اتصال زبائن الشبكة.'):
            try:self._remember_draft()
            except (ValueError,tk.TclError,OSError) as exc:
                return messagebox.showerror('حفظ الإعدادات',str(exc))
            self._show_login('تم تسجيل الخروج. سجّل الدخول للمتابعة.')

    def run_network(self,operation,on_ok=None,on_err=None,read_only=False,affects_inventory=None):
        """Reuse one authenticated connection; serialize whole operations.

        There is no retry of a mutation after an uncertain reply. Reconnection
        here is only before an operation starts, or explicitly for safe reads
        inside NetworkGateway. UI work always returns through the main queue.

        ``affects_inventory`` controls whether the cached voucher lifecycle is
        discarded afterwards.  Re-reading the User Manager user table is the
        slowest request in the program, so only writes that can really change
        the voucher counters are allowed to trigger it.  Reads never do.
        """
        if not self.session.authenticated:return
        if not read_only and self._network_write_pending:
            error=ValueError('توجد عملية تعديل جارية على الراوتر؛ انتظر نتيجتها قبل تنفيذ تعديل آخر.')
            if on_err:on_err(error)
            else:messagebox.showinfo('عملية جارية',str(error))
            return
        if not read_only:self._network_write_pending=True
        api=self.session.api;lock=self._network_lock
        if self._network_gateway is None:
            self._network_gateway=NetworkGateway(api)
        gateway=self._network_gateway
        # Default: any non-read may have changed the inventory (conservative).
        # Callers that only touch profiles, settings, files or bindings pass
        # affects_inventory=False explicitly.
        if affects_inventory is None:affects_inventory=not read_only
        def work():
            with lock:
                try:
                    if api.sock is None:api.connect()
                    gateway.reset_read_budget()
                    return operation(gateway)
                finally:
                    # Invalidate while holding the same lock as reads. A main
                    # thread callback must not clear another worker's new scan.
                    if affects_inventory:gateway.invalidate_voucher_status_cache()
        def complete(result):
            if not read_only:self._network_write_pending=False
            if affects_inventory:self.invalidate_lifecycle()
            if on_ok:on_ok(result)
        def failed(exc):
            if not read_only:self._network_write_pending=False
            if affects_inventory:self.invalidate_lifecycle()
            if on_err:on_err(exc)
            else:messagebox.showerror('خطأ',str(exc))
        self.safe_thread(work,complete,failed)

    def invalidate_lifecycle(self):
        self._last_lifecycle_refresh=0.0
        self._dashboard_poll_last['total']=0.0
        if self.current_page is self.page_frames.get('dashboard'):
            self._schedule_dashboard_poll(2200)
        page=self.page_frames.get('manage')
        if page and hasattr(page,'invalidate_cache'):page.invalidate_cache()
        life=self.network_catalog.get('services',{}).get('userman',{}).get('lifecycle',{})
        if life.get('complete'):
            life['stale']=True
            life['reason']='قد تكون حالة الكروت تغيرت بعد عملية على الراوتر؛ حدّث حالة الكروت للتأكد.'

    def current_profile(self):
        state=self.network_catalog.get('services',{}).get(self.active_service,{})
        return next((p for p in state.get('profiles',[]) if p['id']==self.active_profile_id),None)

    def _remember_draft(self):
        self.flush_page_settings()
        saved=self.db.package(self.active_package_id)
        if saved:self.db.save_package(saved['name'],self.db.all_settings(),saved['id'])

    def select_service(self,service):
        if service not in SERVICES:return False
        if service==self.active_service:return True
        # Tkinter 3.13+ also exposes busy() as a widget method.
        if any(getattr(p,'busy',False) is True for p in self.page_frames.values()):
            messagebox.showinfo('عملية جارية','انتظر انتهاء إرسال الكروت قبل تبديل الخدمة.');return False
        try:self._remember_draft()
        except (ValueError,tk.TclError) as exc:
            messagebox.showerror('إعدادات الباقة',str(exc));return False
        self.active_service=service;self.active_profile_id=None;self.active_package_id=None
        row=self.session.router_row()
        if row:self.db.set_setting('network.service.%s' % row['id'],service)
        profiles=self.network_catalog.get('services',{}).get(service,{}).get('profiles',[])
        wanted=self.db.get_setting('network.profile.%s.%s' % (row['id'],service)) if row else None
        profile=next((p for p in profiles if p['id']==wanted),profiles[0] if profiles else None)
        if profile:self._activate_profile(profile)
        self.refresh_package_views();return True

    def _activate_profile(self,profile):
        row=self.session.router_row()
        saved=self.db.router_preset(row['id'],self.active_service,profile)
        self.active_profile_id=profile['id'];self.active_package_id=saved['id']
        settings=dict(saved['settings']);settings['create.profile']=profile['name']
        if self.active_service=='userman':
            defaults=profile_defaults('userman',profile)
            for key in ('create.price','create.limit_bytes','create.limit_unit','create.limit_uptime_h','create.limit_uptime_m','create.validity_days'):
                settings[key]=defaults[key]
        self.db.replace_preset_settings(settings)
        self.db.set_setting('network.profile.%s.%s' % (row['id'],self.active_service),profile['id'])
        self.db.set_setting('packages.active_id',saved['id'])

    def select_router_profile(self,ident):
        if ident==self.active_profile_id:return True
        if any(getattr(p,'busy',False) is True for p in self.page_frames.values()):
            messagebox.showinfo('عملية جارية','انتظر انتهاء الإرسال قبل تبديل الباقة.');return False
        profiles=self.network_catalog.get('services',{}).get(self.active_service,{}).get('profiles',[])
        profile=next((p for p in profiles if p['id']==ident),None)
        if not profile:return False
        try:self._remember_draft();self._activate_profile(profile)
        except (ValueError,tk.TclError) as exc:
            messagebox.showerror('إعدادات الباقة',str(exc));return False
        self.refresh_package_views();return True

    def _merge_dashboard_catalog(self, fresh):
        return merge_snapshot(self.network_catalog,fresh)

    def _apply_live_snapshot(self,catalog):
        self.network_catalog=self._merge_dashboard_catalog(catalog)
        self._last_dashboard_refresh=time.monotonic()
        page=self.page_frames.get('dashboard')
        if page is self.current_page and page:page.refresh()

    def refresh_dashboard(self,force=False):
        """Quick status only. Never scan User Manager from this button."""
        if not self.session.authenticated:return
        if self.catalog_busy or self.lifecycle_busy or self._pending_jobs:
            self.status_var.set(AR('توجد عملية جارية؛ تُعرض آخر قراءة دون إرسال طلبات متداخلة.'))
            return
        now=time.monotonic();elapsed=now-self._last_dashboard_refresh
        limit=3.0 if force else self._dashboard_refresh_min_interval
        if self.network_catalog and self._last_dashboard_refresh and elapsed < limit:
            self.status_var.set(AR('الإحصائيات محدثة حديثاً؛ تم تجنب طلب مكرر للراوتر.'))
            return
        self.catalog_busy=True
        self.status_var.set(AR('جارٍ تحديث الاتصال والمعالج وعدادات الواجهات...'))
        def done(catalog):
            self.catalog_busy=False
            self._apply_live_snapshot(catalog)
            self.status_var.set(AR('تم تحديث الاتصال والمعالج والواجهات دون فحص جلسات أو حالات مدير المستخدمين.'))
        def failed(exc):
            self.catalog_busy=False
            self.status_var.set(AR('تعذر تحديث المؤشرات؛ تُعرض آخر قراءة: '+str(exc)))
        self.run_network(lambda gateway:gateway.dashboard_catalog(),done,failed,read_only=True)

    def _stop_dashboard_polling(self):
        job=getattr(self,'_dashboard_poll_job',None)
        if job is not None:
            try:self.after_cancel(job)
            except (tk.TclError,ValueError):pass
        self._dashboard_poll_job=None

    def _schedule_dashboard_poll(self,delay_ms=25000):
        self._stop_dashboard_polling()
        if not self._closing and self.session.authenticated:
            self._dashboard_poll_job=self.after(max(1000,int(delay_ms)),self._poll_dashboard)

    def _poll_dashboard(self):
        self._dashboard_poll_job=None
        if not self.session.authenticated or self._closing:return
        # Only poll when the dashboard is visible and the shared API is idle.
        if (self.current_page is not self.page_frames.get('dashboard') or
                self._pending_jobs or self.catalog_busy or self.lifecycle_busy or
                self._dashboard_poll_busy):
            self._schedule_dashboard_poll(8000)
            return
        now=time.monotonic()
        due=[name for name,delay in self._dashboard_poll_interval.items()
             if now-self._dashboard_poll_last[name]>=delay]
        if not due:
            remaining=min(self._dashboard_poll_interval[k]-(now-self._dashboard_poll_last[k]) for k in self._dashboard_poll_interval)
            self._schedule_dashboard_poll(int(max(3.0,remaining)*1000))
            return
        self._dashboard_poll_busy=True
        def ok(snapshot):
            self._dashboard_poll_busy=False;self._dashboard_poll_failures=0
            self._apply_live_snapshot(snapshot)
            stamp=time.monotonic()
            for key in due:self._dashboard_poll_last[key]=stamp
            self._schedule_dashboard_poll(5000)
        def failed(exc):
            self._dashboard_poll_busy=False
            self._dashboard_poll_failures=min(6,self._dashboard_poll_failures+1)
            # Keep last successful counters and exponentially back off.
            self._schedule_dashboard_poll(min(300000,10000*2**(self._dashboard_poll_failures-1)))
            LOGGER.warning('Lightweight dashboard polling unavailable: %s',type(exc).__name__)
        self.run_network(lambda gateway:gateway.dashboard_counters(
            include_total='total' in due,include_clients='clients' in due),
            ok,failed,read_only=True)

    def _refresh_lifecycle_once_after_login(self):
        """Defer the one automatic heavy scan until the dashboard is idle."""
        if (self._initial_lifecycle_refresh_started or not self.session.authenticated or
                self._closing or getattr(self,'_initial_lifecycle_job',None) is not None):return
        generation=self.session.generation
        def start():
            if generation!=self.session.generation:return
            self._initial_lifecycle_job=None
            if not self.session.authenticated or self._closing:return
            if getattr(self,'_last_lifecycle_refresh',0.0):
                self._initial_lifecycle_refresh_started=True
                return
            if (self.current_page is not self.page_frames.get('dashboard') or
                    self._pending_jobs or self.catalog_busy or self.lifecycle_busy):
                self._refresh_lifecycle_once_after_login()
                return
            self.refresh_lifecycle(force=True)
            if self.lifecycle_busy:self._initial_lifecycle_refresh_started=True
            else:self._refresh_lifecycle_once_after_login()
        self._initial_lifecycle_job=self.after(5000,start)

    def _stop_initial_lifecycle(self):
        job=getattr(self,'_initial_lifecycle_job',None)
        if job is not None:
            try:self.after_cancel(job)
            except (tk.TclError,ValueError):pass
        self._initial_lifecycle_job=None

    def refresh_lifecycle(self,force=False):
        """Refresh voucher inventory; automatic callers may use the 5-minute cache."""
        if not self.session.authenticated:return
        if self.lifecycle_busy or self.catalog_busy or self._pending_jobs:
            self.status_var.set(AR('انتظر اكتمال العملية الحالية قبل تحديث حالة الكروت.'))
            return
        now=time.monotonic()
        if self._last_lifecycle_attempt and now-self._last_lifecycle_attempt < (3.0 if force else 30.0):
            return
        if not force and self._last_lifecycle_refresh and now-self._last_lifecycle_refresh<300.0:return
        self.lifecycle_busy=True;self._last_lifecycle_attempt=now
        self.status_var.set(AR('جارٍ فحص حالة الكروت بقراءة مجمعة؛ يمكنك متابعة العمل...'))
        page=self.page_frames.get('dashboard')
        if page and page is self.current_page:page.refresh()
        def done(catalog):
            self.lifecycle_busy=False
            self.network_catalog=merge_snapshot(self.network_catalog,catalog)
            life=self.network_catalog.get('services',{}).get('userman',{}).get('lifecycle',{})
            if life.get('complete') and not life.get('stale'):
                self._last_lifecycle_refresh=time.monotonic()
            text=('تم تحديث حالة الكروت؛ القراءة محفوظة 5 دقائق لتخفيف الحمل.'
                  if life.get('complete') and not life.get('stale') else 'تعذّر تأكيد حالة الكروت؛ راجع التنبيه في الرئيسية.')
            self.status_var.set(AR(text))
            page=self.page_frames.get('dashboard')
            if page and page is self.current_page:page.refresh()
        def failed(exc):
            # A failed inventory read is not a successful refresh.  Keep the
            # previous success timestamp so the normal 5-minute success cache
            # cannot suppress the next retry.  _last_lifecycle_attempt already
            # provides the short 30-second anti-spam guard.
            self.lifecycle_busy=False
            self.network_catalog=merge_snapshot(self.network_catalog,{'kind':'inventory','services':{
                'userman':{'lifecycle':{'complete':False},'lifecycle_error':str(exc),'warnings':[str(exc)]}}})
            self.status_var.set(AR('تعذر تحديث حالة الكروت؛ لم تُستبدل آخر قراءة ناجحة بأصفار.'))
            page=self.page_frames.get('dashboard')
            if page and page is self.current_page:page.refresh()
        generation=self.session.generation
        def publish(snapshot):
            def apply():
                self.network_catalog=merge_snapshot(self.network_catalog,snapshot)
                page=self.page_frames.get('dashboard')
                if page and page is self.current_page:page.refresh()
            self.post_ui(apply,generation=generation)
        self.run_network(lambda gateway:gateway.inventory_catalog(force=force,on_lifecycle=publish),done,failed,read_only=True)

    def _merge_profile_catalog(self, fresh):
        return merge_snapshot(self.network_catalog,dict(fresh,kind='profiles'))

    def refresh_network(self):
        if not self.session.authenticated or self.catalog_busy or self.lifecycle_busy:return
        now=time.monotonic()
        if self._last_profile_attempt and now-self._last_profile_attempt<5.0:
            self.status_var.set(AR('تم طلب الباقات حديثاً؛ تم تجنب التحديث المكرر.'));return
        self._last_profile_attempt=now
        first=not self._profiles_loaded
        self.catalog_busy=True
        self.status_var.set(AR('جارٍ تحميل مؤشرات الاتصال ثم باقات الراوتر...'))
        def done(catalog):
            initial=not self._profiles_loaded
            self.network_catalog=self._merge_profile_catalog(catalog);self.catalog_busy=False
            self._profiles_loaded=True
            row=self.session.router_row()
            if initial:
                wanted=self.db.get_setting('network.service.%s' % row['id'],'hotspot')
                self.active_service=wanted if wanted in SERVICES else 'hotspot'
            profiles=catalog.get('services',{}).get(self.active_service,{}).get('profiles',[])
            selected=self.current_profile()
            if not selected:
                wanted=self.db.get_setting('network.profile.%s.%s' % (row['id'],self.active_service))
                selected=next((p for p in profiles if p['id']==wanted),profiles[0] if profiles else None)
                self.active_profile_id=None;self.active_package_id=None
                if selected:self._activate_profile(selected)
                for page in self.page_frames.values():
                    if isinstance(page,CreateCardsPage):page._loaded=None
            elif self.active_service=='userman':
                defaults=profile_defaults('userman',selected)
                keys=('create.price','create.limit_bytes','create.limit_unit','create.limit_uptime_h','create.limit_uptime_m','create.validity_days')
                for key in keys:self.db.set_setting(key,defaults[key])
                page=self.page_frames.get('create')
                if page and not page.busy:
                    for key,setting in page.MAP.items():
                        if setting in keys:page.vars[key].set(defaults[setting])
            self.status_var.set(AR('تم تحميل الباقات وإبقاء وظائف الإدارة دون فحص جرد مدير المستخدمين.'))
            self.refresh_all()
            self._refresh_lifecycle_once_after_login()
        def failed(exc):
            self.catalog_busy=False
            # A failed refresh must not discard working profiles or templates.
            self.network_catalog.setdefault('errors',[]).append('فشلت قراءة الباقات: '+str(exc))
            self.status_var.set(AR('تعذرت قراءة الراوتر: '+str(exc)));self.refresh_all()
            self._refresh_lifecycle_once_after_login()
        generation=self.session.generation
        def read_catalog(gateway):
            if first:
                live=gateway.dashboard_catalog()
                self.post_ui(lambda:self._apply_live_snapshot(live),generation=generation)
            return gateway.catalog(include_profiles=True,include_interfaces=False,include_servers=True,
                include_hotspot_lifecycle=False,allow_state_fallback=False,include_stats=False,include_lifecycle=False,
                include_resource=False)
        self.run_network(read_catalog,done,failed,read_only=True)

    def offer_maintenance(self,reason):
        if self.session.authenticated and self.workspace:
            offer_optional_maintenance(self,reason)

    def open_maintenance(self,action=None):
        """Dashboard buttons only navigate; all router writes require review."""
        if not self.session.authenticated:return
        self.show_page('maintenance')
        page=self.page_frames.get('maintenance')
        if action and page and hasattr(page,'select_action'):
            page.select_action(action)

    def router_choices(self):
        row=self.session.router_row()
        return [row] if row else []

    def _schedule_heartbeat(self, delay=60000):
        if self._heartbeat_job:
            try:self.after_cancel(self._heartbeat_job)
            except Exception:pass
            self._heartbeat_job=None
        if not self._closing and self.session.authenticated:
            self._heartbeat_job=self.after(max(1000,int(delay)),self._check_connection)

    def _check_connection(self):
        self._heartbeat_job=None
        if not self.session.authenticated:return
        if self._pending_jobs:
            self._schedule_heartbeat(10000)
            return
        api=self.session.api
        def ok(identity):
            self._heartbeat_failures=0
            if identity:self.session.identity=str(identity)
            self.refresh_sidebar_routers()
            self.status_var.set(AR('الاتصال بالراوتر مستقر'))
            self._schedule_heartbeat()
        def failed(exc):
            # A single missed heartbeat is commonly a transient TCP/API timeout.
            # Keep the authenticated workspace alive, close the broken stream in
            # RouterOSAPI, and let the next heartbeat or user action reconnect it.
            self._heartbeat_failures+=1
            retry=5000 if self._heartbeat_failures<4 else 15000
            self._set_sidebar_connection(False)
            self.status_var.set(AR('تعذر فحص الراوتر مؤقتًا (%s/%s): %s — لم يتم تسجيل الخروج.' %
                                   (self._heartbeat_failures,4,str(exc))))
            self._schedule_heartbeat(retry)
        def heartbeat():
            with self._network_lock:
                if getattr(api,'sock',None) is None and hasattr(api,'connect'):
                    api.connect()
                return api.identity()
        self.safe_thread(heartbeat,ok,failed)

    def report_callback_exception(self,exc_type,exc_value,exc_traceback):
        LOGGER.error('Unhandled Tk callback error',exc_info=(exc_type,exc_value,exc_traceback))
        messagebox.showerror(
            'خطأ غير متوقع',
            'حدث خطأ وتم تسجيل تفاصيله في ملف السجل:\n%s\n\n%s' % (LOG_DIR/'hadramout.log',exc_value),
        )

    def _monitor_license(self):
        self._license_monitor_id = None
        if self._closing:
            return
        if not self._license_check_busy and self._licensing.needs_check():
            self._check_license_online()
        status = self.current_license_access()
        if self.workspace is not None and not status.permitted:
            # Refuse new work immediately. Let existing router/print operations
            # finish and persist their results before closing the session/socket.
            if not self._pending_lock_message:
                LOGGER.warning('Supabase license check denied application session')
                self._pending_lock_message=status.message
                self._stop_dashboard_polling()
                if self._heartbeat_job:
                    self.after_cancel(self._heartbeat_job)
                    self._heartbeat_job=None
            self.status_var.set(AR(status.message))
            if not self._pending_jobs:
                self._show_login(self._pending_lock_message, True)
        elif self.workspace is None:
            self.login_page.show_license_status(status)
        else:
            dashboard = self.page_frames.get('dashboard')
            if dashboard is not None:
                dashboard.show_license_status(status)
        self._license_monitor_id = self.after(5000, self._monitor_license)

    def _style(self):
        apply_style(self)

    def _build_shell(self):
        self.workspace=tk.Frame(self,bg=COLORS['bg'])
        self.workspace.pack(fill='both',expand=True)
        self.sidebar=tk.Frame(self.workspace,bg=COLORS['navy'],width=224,
                              highlightbackground=COLORS['line'],highlightthickness=1)
        self.sidebar.pack(side='right',fill='y',padx=(0,7),pady=7)
        self.sidebar.pack_propagate(False)
        # Distinct brand panel: visual styling only, separate from navigation.
        brand=tk.Frame(self.sidebar,bg='#163C60',highlightbackground='#4386A8',highlightthickness=1)
        brand.pack(fill='x',padx=7,pady=(8,8))
        tk.Frame(brand,bg='#27C4D8',height=3).pack(fill='x')
        tk.Label(brand,text='حضرموت',bg='#163C60',fg='#FFFFFF',
                 font=('Arial',20,'bold'),anchor='e').pack(fill='x',padx=10,pady=(7,0))
        brand_description=tk.Label(brand,text='لإدارة الشبكات وطباعة الكروت',
                                   bg='#163C60',fg='#BFE7FA',font=('Arial',9,'bold'),
                                   anchor='e',justify='right')
        brand_description.pack(fill='x',padx=10,pady=(1,5))
        brand.bind('<Configure>',lambda event:brand_description.configure(
            wraplength=max(170,event.width-26)),add='+')
        version_line=tk.Frame(brand,bg='#163C60')
        version_line.pack(fill='x',padx=9,pady=(0,8))
        tk.Label(version_line,text='v'+APP_VERSION,bg='#25577A',fg='#E2F8FF',
                 font=('Arial',10,'bold'),padx=9,pady=2).pack(side='right')
        tk.Frame(self.sidebar,bg='#28466B',height=1).pack(fill='x',padx=10,pady=(0,5))
        connection=tk.Frame(self.sidebar,bg='#103457')
        connection.pack(side='bottom',fill='x',padx=7,pady=7)
        status_row=tk.Frame(connection,bg='#103457')
        status_row.pack(fill='x',padx=8,pady=(8,5))
        # Right-to-left: the colored connection lamp is on the right of the text.
        self.sidebar_status_light=tk.Canvas(status_row,width=20,height=20,bg='#103457',
                                            bd=0,highlightthickness=0)
        self.sidebar_status_light.pack(side='right',padx=(4,2))
        self.sidebar_status_light.create_oval(2,2,18,18,outline='#F8A3AC',width=1,
                                              fill='#E84B5A',tags='status_lamp')
        self.sidebar_status=tk.Label(status_row,textvariable=self.router_status_var,
                                      bg='#103457',fg='#FFB6BD',font=('Arial',14,'bold'),anchor='e')
        self.sidebar_status.pack(side='right',padx=(0,3))
        ttk.Button(connection,text='تسجيل الخروج',style='Danger.TButton',command=self.logout).pack(fill='x',padx=5,pady=(4,6))
        nav=[('dashboard','router_info.png','الرئيسية'),('packages','packages.png','باقات الشبكة'),
             ('create','create_cards.png','إنشاء وطباعة الكروت'),('manage','manage_cards.png','إدارة المستخدمين'),
             ('active_cards','active_cards.png','الكروت النشطة'),('expired_cards','expired_cards.png','الكروت المنتهية'),
             ('activity','total_cards.png','النشاط'),
             ('maintenance','maintenance.png','التحكم بالشبكة والصيانة'),
             ('monitor','connected_users.png','المتصلون والهوتسبوت'),('network','router_info.png','الشبكة والتشخيص')]
        navigation=tk.Frame(self.sidebar,bg=COLORS['navy'])
        navigation.pack(fill='both',expand=True,padx=5,pady=(0,3))
        navigation.columnconfigure(0,weight=1)
        for row,(key,icon_file,title) in enumerate(nav):
            navigation.rowconfigure(row,weight=1)
            nav_icon=self.load_asset_icon(icon_file,(22,22))
            button=tk.Button(navigation,text=title,image=nav_icon,compound='right',relief='flat',bd=0,
                             bg='#103457',fg=COLORS['white'],activebackground=COLORS['accent2'],
                             activeforeground='white',font=('Arial',12,'bold'),padx=6,pady=4,
                             anchor='e',highlightthickness=0,wraplength=0,
                             command=lambda k=key:self.show_page(k))
            button.grid(row=row,column=0,sticky='nsew',pady=2)
            self.nav_buttons[key]=button
        # Fit the longest complete label and its icon using the active font.
        # The extra space includes the sidebar border and navigation padding.
        self.sidebar.configure(width=max(224,max(b.winfo_reqwidth() for b in self.nav_buttons.values())+12))
        bottom=tk.Frame(self.workspace,bg=COLORS['panel'],height=26,highlightbackground=COLORS['line'],highlightthickness=1)
        bottom.pack(side='bottom',fill='x');bottom.pack_propagate(False)
        tk.Label(bottom,textvariable=self.status_var,bg=COLORS['panel'],fg=COLORS['muted'],font=('Arial',10)).pack(side='right',padx=10)
        self.content=tk.Frame(self.workspace,bg=COLORS['bg'])
        self.content.pack(fill='both',expand=True)

    def _set_sidebar_connection(self,connected):
        """Update only the sidebar status text/lamp; never show router identity or IP."""
        self.router_status_var.set(AR('متصل' if connected else 'غير متصل'))
        if not self.workspace or not hasattr(self,'sidebar_status_light'):return
        color='#24D976' if connected else '#E84B5A'
        rim='#91F6BB' if connected else '#F8A3AC'
        self.sidebar_status_light.itemconfigure('status_lamp',fill=color,outline=rim)
        self.sidebar_status.configure(fg='#CFFFE3' if connected else '#FFB6BD')

    def refresh_sidebar_routers(self):
        if not self.workspace:return
        # Use the existing session and heartbeat result, never stored router metadata.
        live=(self.session.authenticated and self._heartbeat_failures==0 and
              getattr(self.session.api,'sock',None) is not None)
        self._set_sidebar_connection(live)

    def show_page(self,key):
        if key=='print':key='create'
        if not self.session.authenticated or self._pending_lock_message or not self.workspace:
            return
        voucher_status={
            'active_cards':('active','#2E5C8A'),
            'expired_cards':('expired','#B83C53'),
        }
        if key in voucher_status:
            status,color=voucher_status[key]
            VoucherStatusWindow(self,self,status,color)
            return
        if key!='create' and any(getattr(p,'busy',False) is True for p in self.page_frames.values()):
            return messagebox.showinfo('إرسال الكروت','انتظر اكتمال إرسال الدفعة قبل التنقل.')
        if self.current_page:
            try:self.flush_page_settings()
            except (ValueError,tk.TclError) as exc:
                messagebox.showerror('الإعدادات',str(exc));return
            for name in ('_save_job','_preview_job'):
                job=getattr(self.current_page,name,None)
                if job:
                    self.current_page.after_cancel(job)
                    setattr(self.current_page,name,None)
            if isinstance(self.current_page,DesignPage) and 'create' in self.page_frames:
                self.page_frames['create']._loaded=None
            self.current_page.pack_forget()
        if key not in self.page_frames:
            cls={
                'dashboard':DashboardPage,'packages':PackagesPage,'create':CreateCardsPage,'design':DesignPage,
                'manage':NetworkUsersPage,'activity':ActivityPage,'monitor':HotspotMonitorPage,'network':NetworkToolsPage,
                'archive':CardManagerPage,'archive_print':PrintPage,'mikrotik':MikroTikPage,'hotspot':HotspotPage,
                'reports':ReportsPage,'settings':SettingsPage,
                'maintenance':RouterMaintenancePage}[key]
            self.page_frames[key]=cls(self.content,self)
        self.current_page=self.page_frames[key]
        # Cached pages keep their controls and preview between navigations. Their
        # own refresh methods invalidate only when the active package changes.
        self.current_page.pack(fill='both',expand=True)
        for k,b in self.nav_buttons.items():
            b.configure(bg=COLORS['accent2'] if k==key else '#103457',fg='white')
        # Repaint cached data on entry, including updates received while this
        # page was hidden. This does not send any additional router request.
        if hasattr(self.current_page,'refresh'):
            try:self.current_page.refresh()
            except Exception:
                LOGGER.exception('Page refresh failed: %s',key);self.status_var.set('تعذر تحديث الصفحة؛ راجع سجل الأعطال')
        # Re-entering the dashboard uses the lightweight counter path.  Package
        # discovery remains cached until an explicit network/package refresh.
        if key=='dashboard' and self.network_catalog and not self.catalog_busy:
            self._schedule_dashboard_poll(1500)
            # Explicit actions may still refresh the full lightweight snapshot.
            # Timed polling uses only server-side count-only requests.
            # Runs only after an existing light read finishes on a future entry,
            # or immediately when the light snapshot is still fresh.

    def refresh_all(self):
        if not self.session.authenticated or self._pending_lock_message:return
        self.refresh_sidebar_routers()
        # Hidden pages refresh when opened, not during every statistics update.
        # This avoids rebuilding tables/previews and resetting hidden drafts.
        page=self.current_page
        if page and hasattr(page,'refresh'):
            try:page.refresh()
            except Exception:LOGGER.exception('Page refresh failed: %s',page.__class__.__name__)

    def post_ui(self,callback,generation=None):
        if not self._closing:
            token=self.session.generation if generation is None else generation
            self._results.put((token,False,lambda _result:callback(),None,None,None,None))

    def _drain_results(self):
        if self._closing:return
        deadline=time.monotonic()+0.008
        for _ in range(100):
            if time.monotonic()>=deadline:break
            try:token,guarded,ok,err,result,error,discard=self._results.get_nowait()
            except Empty:break
            if guarded:self._pending_jobs-=1
            if token is not None and token!=self.session.generation:
                if discard and error is None:discard(result)
                continue
            try:
                if error is not None:
                    if err:err(error)
                    else:messagebox.showerror('خطأ',str(error))
                elif ok:ok(result)
            except Exception:
                self.report_callback_exception(*sys.exc_info())
            if self._pending_lock_message and not self._pending_jobs:
                self._show_login(self._pending_lock_message,True)
        self._dispatch_job=self.after(1 if not self._results.empty() else 50,self._drain_results)

    def safe_thread(self,fn,on_ok=None,on_err=None,*,require_auth=True,on_discard=None):
        if self._closing or (require_auth and (not self.session.authenticated or self._pending_lock_message)):
            return
        token=self.session.generation
        if require_auth:self._pending_jobs+=1
        def run():
            result=None;error=None
            try:
                result=fn()
            except Exception as exc:
                error=exc
                LOGGER.warning('Background operation failed: %s',type(exc).__name__)
            self._results.put((token,require_auth,on_ok,on_err,result,error,on_discard))
            if self._closing and on_discard and error is None:on_discard(result)
        threading.Thread(target=run,daemon=True).start()

    def on_close(self):
        # Do not allow a queued dashboard timer to outlive the window.
        if self._pending_jobs:
            return messagebox.showinfo('العملية جارية','انتظر اكتمال العملية الحالية قبل إغلاق البرنامج لحفظ نتيجتها.')
        if self.session.authenticated:
            try:self._remember_draft()
            except Exception as exc:
                if not messagebox.askyesno('حفظ الإعدادات','تعذر حفظ آخر تعديل: %s\nالإغلاق دون حفظ هذا التعديل؟' % exc):return
        self._closing=True
        self._stop_dashboard_polling()
        self._stop_initial_lifecycle()
        self.session.lock()
        if self._heartbeat_job:self.after_cancel(self._heartbeat_job)
        if self._license_monitor_id:self.after_cancel(self._license_monitor_id)
        self.after_cancel(self._dispatch_job)
        while not self._results.empty():
            _token,_guarded,_ok,_err,result,error,discard=self._results.get_nowait()
            if discard and error is None:discard(result)
        try:self.db.close()
        except Exception:pass
        LOGGER.info('Application closed')
        self.destroy()

class Page(tk.Frame):
    def __init__(self,parent,app,title,subtitle=''):
        super().__init__(parent,bg=COLORS['bg'])
        self.app=app; self.db=app.db
        head=tk.Frame(self,bg=COLORS['title_bg'],height=48,highlightbackground=COLORS['accent'],highlightthickness=1)
        head.pack(fill='x',padx=8,pady=(3,4))
        head.pack_propagate(False)
        tk.Frame(head,bg=COLORS['accent'],width=6).pack(side='right',fill='y')
        text=tk.Frame(head,bg=COLORS['title_bg'])
        text.pack(side='right',fill='both',expand=True,padx=(10,8),pady=4)
        tk.Label(text,text=title,bg=COLORS['title_bg'],fg=COLORS['title_text'],font=('Arial',14,'bold'),anchor='e').pack(fill='x')
        if subtitle:tk.Label(text,text=subtitle,bg=COLORS['title_bg'],fg=COLORS['title_muted'],font=('Arial',9),anchor='e').pack(fill='x',pady=(1,0))
        self._viewport=PageFrame(self,COLORS['bg'])
        self._viewport.pack(fill='both',expand=True,padx=6,pady=(0,3))
        self.body=self._viewport.inner

    def destroy(self):
        for name in ('_save_job','_preview_job'):
            job=getattr(self,name,None)
            if job:
                try:self.after_cancel(job)
                except tk.TclError:pass
                setattr(self,name,None)
        super().destroy()

    def panel(self,parent=None,padx=14,pady=12,accent=None):
        # Compact white cards; selected pages can opt into a meaningful colored edge.
        f=tk.Frame(parent or self.body,bg=COLORS['panel'],highlightbackground=accent or COLORS['line'],highlightthickness=1)
        return f

    def label(self,parent,text,muted=False,**kw):
        return tk.Label(parent,text=text,bg=kw.pop('bg',parent.cget('bg')),
                        fg=kw.pop('fg',COLORS['muted'] if muted else COLORS['text']),
                        font=kw.pop('font',('Arial',10)),**kw)

    def heading(self,parent,text,color=None,font=('Arial',11,'bold'),**kw):
        """Dark sub-section title used consistently across internal pages."""
        return tk.Label(parent,text=text,bg=color or COLORS['navy'],fg=COLORS['white'],
                        font=font,anchor=kw.pop('anchor','e'),padx=kw.pop('padx',9),
                        pady=kw.pop('pady',5),**kw)

    def field(self,parent,label,var,width=20,row=0,col=0,kind='entry',values=None,show=None):
        self.label(parent,label,muted=True).grid(row=row,column=col,sticky='w',padx=4,pady=(3,1))
        if kind=='combo':
            choice_type=LocalizedChoice if isinstance(values,dict) else ttk.Combobox
            w=choice_type(parent,textvariable=var,values=values or [],state='readonly',width=width)
        elif kind=='spin':
            w=ttk.Spinbox(parent,textvariable=var,from_=0,to=999999,width=width)
        else:
            w=ttk.Entry(parent,textvariable=var,width=width,show=show)
        w.grid(row=row+1,column=col,sticky='ew',padx=4,pady=(0,4))
        return w

class CardManagerPage(Page):
    STATUS_MAP={'الكل':'ALL','جديد':'CREATED','مُرسل':'SENT','متعذر':'FAILED','لم يتأكد الإرسال':'UNKNOWN','إرسال لم يُغلق':'SENDING','معطّل':'DISABLED','محذوف من الراوتر':'REMOTE_DELETED'}
    def __init__(self,parent,app):
        super().__init__(parent,app,'إدارة الكروت','بحث وتصفية وتصدير وإعادة إرسال وتعطيل الكروت')
        self.search_var=tk.StringVar();self.status_filter=tk.StringVar(value='الكل');self.router_var=tk.StringVar()
        tools=self.panel(accent=COLORS['accent']);tools.pack(fill='x',pady=(0,6))
        self.label(tools,'بحث',muted=True,bg=COLORS['panel']).pack(side='left',padx=(12,4),pady=10)
        search=ttk.Entry(tools,textvariable=self.search_var,width=24);search.pack(side='left',padx=4);search.bind('<Return>',lambda _e:self.refresh())
        ttk.Combobox(tools,textvariable=self.status_filter,values=list(self.STATUS_MAP),state='readonly',width=18).pack(side='left',padx=5)
        ttk.Button(tools,text='تحديث',style='Dark.TButton',command=self.refresh).pack(side='left',padx=5)
        self.count_label=self.label(tools,'0 كرت',muted=True,bg=COLORS['panel']);self.count_label.pack(side='right',padx=14)

        remote=self.panel(accent=COLORS['accent2']);remote.pack(fill='x',pady=(0,6))
        self.label(remote,'الراوتر',muted=True,bg=COLORS['panel']).pack(side='left',padx=(12,4),pady=10)
        self.router_combo=ttk.Combobox(remote,textvariable=self.router_var,state='readonly',width=20);self.router_combo.pack(side='left',padx=4)
        remote_actions=tk.Frame(remote,bg=COLORS['panel']);remote_actions.pack(side='left',fill='both',expand=True)
        remote_buttons=[ttk.Button(remote_actions,text=title,style=style,command=lambda a=action:self.remote_action(a))
                        for title,style,action in [('إرسال/إعادة إرسال','Green.TButton','send'),
                                                  ('تعطيل','Dark.TButton','disable'),
                                                  ('تفعيل','Dark.TButton','enable'),
                                                  ('حذف من الراوتر','Danger.TButton','remove')]]
        fit_buttons(remote_actions,remote_buttons,(4,2,1),padx=4,pady=3)

        table=self.panel();table.pack(fill='both',expand=True)
        cols=('seq','user','pass','profile','limit','status','printed','date')
        self.tree=ttk.Treeview(table,columns=cols,show='headings',selectmode='extended')
        headers=[('seq','التسلسل',85),('user','اسم المستخدم',145),('pass','كلمة المرور',125),('profile','البروفايل',130),('limit','السعة',105),('status','الحالة',115),('printed','الطباعة',80),('date','الإنشاء',170)]
        for key,title,width in headers:self.tree.heading(key,text=title);self.tree.column(key,width=width,anchor='center')
        fit_table(self.tree)
        self.tree.pack(fill='both',expand=True,padx=6,pady=6)
        actions=self.panel();actions.pack(fill='x',pady=(6,0))
        action_buttons=[ttk.Button(actions,text=title,style=style,command=command)
                        for title,style,command in [('تصدير Excel','Dark.TButton',lambda:self.export_selected('excel')),
                                                    ('CSV','Dark.TButton',lambda:self.export_selected('csv')),
                                                    ('RouterOS Script','Dark.TButton',lambda:self.export_selected('script')),
                                                    ('طباعة دفعة محفوظة','TButton',lambda:self.app.show_page('archive_print')),
                                                    ('حذف محلي','Danger.TButton',self.delete_local)]]
        fit_buttons(actions,action_buttons,(5,3,2,1),padx=5,pady=7)
        self._rows=[]

    def refresh(self):
        routers=self.app.router_choices();values=[f"{row['name']} ({row['host']})" for row in routers];self.router_combo['values']=values
        if values and self.router_var.get() not in values:self.router_var.set(values[0])
        status=self.STATUS_MAP.get(self.status_filter.get(),'ALL')
        self._rows=[dict(row) for row in self.db.cards(2000,search=self.search_var.get().strip() or None,status=status)]
        for item in self.tree.get_children():self.tree.delete(item)
        for row in self._rows:
            self.tree.insert('', 'end',iid=str(row['id']),values=(row['sequence'],row['username'],row.get('password') or '—',row.get('profile') or '',readable_bytes(row.get('limit_bytes',0)),row.get('status',''),('نعم' if row.get('printed') else 'لا'),str(row.get('created_at','')).replace('T',' ')))
        self.count_label.config(text=f'{len(self._rows)} كرت')

    def selected_cards(self,allow_all=False):
        selected=[self.db.card(int(item)) for item in self.tree.selection()]
        selected=[dict(row) for row in selected if row]
        if not selected and allow_all:return list(self._rows)
        return selected

    def router_row(self):
        return self.app.session.router_row()

    def export_selected(self,kind):
        cards=self.selected_cards(allow_all=True)
        if not cards:return messagebox.showwarning('تنبيه','لا توجد كروت للتصدير')
        out=Path(self.db.get_setting('paths.output',str(ROOT_DIR/'exports')));out.mkdir(parents=True,exist_ok=True);stamp=datetime.now().strftime('%Y%m%d_%H%M%S')
        try:
            if kind=='excel':path=export_excel(cards,out/f'cards_{stamp}.xlsx')
            elif kind=='csv':path=export_csv(cards,out/f'cards_{stamp}.csv')
            else:
                for card in cards:
                    batch=self.db.batch(card['batch_id']);router=self.router_row()
                    if not router or not batch or batch['router_id']!=router['id']:
                        raise ValueError('صدّر سكربتًا لراوتر واحد فقط؛ اتصل براوتر الدفعة وحدد كروته.')
                    if card.get('service')=='userman':
                        state=self.app.network_catalog.get('services',{}).get('userman',{})
                        profile=next((p for p in state.get('profiles',[]) if p['name']==card['profile']),{})
                        card.update(um_version=state.get('um_version'),customer=profile.get('raw',{}).get('owner',''))
                path=export_script(cards,out/f'cards_{stamp}.rsc')
            self.db.log('CARDS','تصدير كروت','SUCCESS',f'{len(cards)} كرت - {path}')
            messagebox.showinfo('تم',f'تم حفظ {len(cards)} كرت:\n{path}')
        except Exception as exc:messagebox.showerror('فشل التصدير',str(exc))

    def delete_local(self):
        if getattr(self,'busy',False) is True:return
        cards=self.selected_cards()
        if not cards:return messagebox.showwarning('تنبيه','حدد كرتًا واحدًا على الأقل')
        if not messagebox.askyesno('تأكيد الحذف',f'سيتم حذف {len(cards)} كرت من قاعدة البرنامج فقط. هل تتابع؟'):return
        try:count=self.db.delete_cards([card['id'] for card in cards])
        except (ValueError,OSError) as exc:return messagebox.showerror('الحذف المحلي',str(exc))
        self.db.log('CARDS','حذف كروت محلية','SUCCESS',f'{count} كرت');self.refresh();self.app.refresh_all()

    def remote_action(self,action):
        if getattr(self,'busy',False) is True:return
        cards=self.selected_cards();router=self.router_row()
        if not cards:return messagebox.showinfo('الكروت','حدد كرتًا واحدًا على الأقل.')
        if not router:return
        for card in cards:
            batch=self.db.batch(card['batch_id'])
            if not batch or batch['router_id']!=router['id']:
                return messagebox.showerror('راوتر مختلف','توجد كروت تخص راوترًا آخر. سجّل الدخول براوتر الدفعة قبل تعديلها.')
        if action=='send':
            cards=[c for c in cards if c['status'] in ('CREATED','FAILED')]
            if not cards:return messagebox.showinfo('الإرسال','لا توجد كروت مؤهلة للإرسال. الكروت ذات النتيجة غير المؤكدة تحتاج مراجعة المستخدم وباقته من إدارة مستخدمي الراوتر أولًا.')
        if not messagebox.askyesno('تنفيذ على الراوتر','تنفيذ العملية على %s كرت في الراوتر المتصل؟' % len(cards)):return
        self.busy=True
        db_path=self.db.path
        def work(g):
            results=[];users={};profiles={}
            if action=='send':
                names=g.voucher_names()
                for service in {c.get('service','hotspot') for c in cards}:
                    profiles.update({(service,r['name']):r for r in g.profiles(service)})
                return send_cards(g,cards,db_path,router['id'],profiles,
                                  known_names={service:names for service in SERVICES})
            for service in {c.get('service','hotspot') for c in cards}:
                users[service]={r['_name']:r for r in g.users(service)}
            for index,card in enumerate(cards):
                try:
                    service=card.get('service','hotspot');user=users[service].get(card['username'])
                    if not user:raise ValueError('المستخدم غير موجود على الراوتر.')
                    g.user_action(service,action,user)
                    results.append((card['id'],True,''))
                except Exception as exc:
                    results.append((card['id'],False,str(exc)))
                    if getattr(g.api,'sock',True) is None:
                        results.extend((c['id'],False,'لم تُنفذ العملية بعد انقطاع الاتصال.') for c in cards[index+1:]);break
            return results
        def done(results):
            self.busy=False
            original={c['id']:c['status'] for c in cards};errors=[error for _,ok,error in results if not ok]
            if action!='send':
                for ident,ok,error in results:
                    status=original[ident]
                    if ok and (action=='remove' or status in ('SENT','DISABLED')):
                        status={'disable':'DISABLED','enable':'SENT','remove':'REMOTE_DELETED'}[action]
                    self.db.set_card_status([ident],status,error)
            self.db.log('NETWORK',action,'PARTIAL' if errors else 'SUCCESS','%s/%s' % (len(results)-len(errors),len(results)))
            self.refresh();self.app.refresh_network()
            if errors:messagebox.showwarning('نتيجة العملية','فشل %s من %s.\n%s' % (len(errors),len(results),errors[0]))
        def failed(exc):
            self.busy=False;self.refresh()
            messagebox.showerror('نتيجة العملية','توقفت العملية؛ راجع النتائج المحفوظة قبل إعادة المحاولة.\n'+str(exc))
        self.app.run_network(work,done,failed)


class DesignPage(Page):
    REF_W,REF_H=280,130
    def __init__(self,parent,app):
        super().__init__(parent,app,'تعديل متقدم للتصميم','رفع خلفية الكرت والتحكم بالأبعاد والعناصر ومواقعها')
        self.package_bar=PackageBar(self.body,app,COLORS);self.package_bar.pack(fill='x',pady=(0,3))
        self.vars={
            'width':tk.DoubleVar(value=50.0),'height':tk.DoubleVar(value=19.0),'dpi':tk.IntVar(value=300),
            'mode':tk.StringVar(value='Stretch'),'font_family':tk.StringVar(value='Arial'),'font_size':tk.IntVar(value=14),
            'bold':tk.BooleanVar(value=True),'border_width':tk.IntVar(value=1),'qr_size':tk.IntVar(value=29),
            'title':tk.StringVar(value='شبكة حضرموت'),'phone':tk.StringVar(value='رقم الهاتف'),
            'qr_mode':tk.StringVar(value='بيانات الدخول'),'qr_url':tk.StringVar(value='http://hotspot.local/login'),
        }
        self.element_vars={k:tk.BooleanVar(value=False) for k in ['title','sequence','price','capacity','duration','phone','qr']}
        self.element_vars['validity']=tk.BooleanVar(value=False);self.element_vars['date']=tk.BooleanVar(value=False);self.element_vars['other']=tk.BooleanVar(value=False);self.element_vars['other2']=tk.BooleanVar(value=False)
        self.selected_element=None;self._save_job=None;self._refreshing=False;self._position_overrides={}
        area=tk.Frame(self.body,bg=COLORS['bg']);area.pack(fill='both',expand=True)
        editor=ttk.Notebook(area,width=315);editor.pack(side='right',fill='y',padx=(6,0))
        left=tk.Frame(editor,bg=COLORS['bg']);right=tk.Frame(editor,bg=COLORS['bg'])
        editor.add(left,text='الصورة والعناصر');editor.add(right,text='الأبعاد والخط')
        center=tk.Frame(area,bg=COLORS['bg']);center.pack(side='left',fill='both',expand=True)
        p=self.panel(left,accent=COLORS['blue']);p.pack(fill='x')
        upload=tk.Frame(p,bg=COLORS['panel']);upload.pack(fill='x',padx=6,pady=4)
        self.bg_path_var=tk.StringVar(value='')
        self.heading(upload,'رفع التصميم',COLORS['blue'],font=('Arial',10,'bold')).pack(side='right',padx=3)
        ttk.Button(upload,text='اختيار صورة',style='Accent.TButton',command=self.choose_background).pack(side='left',padx=3)
        self.bg_label=self.label(p,'لا توجد صورة محددة',muted=True,bg=COLORS['panel'],wraplength=270,justify='right',font=('Arial',8));self.bg_label.pack(fill='x',padx=7,pady=(0,3))

        ep=self.panel(left,accent=COLORS['accent']);ep.pack(fill='x',pady=4)
        self.heading(ep,'العناصر',COLORS['accent']).pack(fill='x',pady=(0,4))
        names={'title':'العنوان','sequence':'التسلسل','price':'السعر','capacity':'السعة','duration':'المدة','validity':'الصلاحية','date':'التاريخ','phone':'رقم الهاتف','qr':'QR Code','other':'أخرى 1','other2':'أخرى 2'}
        grid=tk.Frame(ep,bg=COLORS['panel']);grid.pack(fill='x',padx=6,pady=(0,3))
        for i,(k,n) in enumerate(names.items()):
            grid.columnconfigure(i%3,weight=1)
            ttk.Checkbutton(grid,text=n,variable=self.element_vars[k],command=self.save_and_render).grid(row=i//3,column=i%3,sticky='w',padx=2,pady=1)

        lp=self.panel(left,accent=COLORS['accent2']);lp.pack(fill='x')
        self.heading(lp,'مواقع العناصر',COLORS['accent2']).pack(fill='x',pady=(0,4))
        self.label(lp,'اسحب الإطارات الزرقاء لتحريك العناصر، واستخدم عجلة الماوس لتكبير أو تصغير العنصر المحدد. رقم الكرت هو اسم المستخدم المطبوع.',muted=True,bg=COLORS['panel'],wraplength=285,justify='right',font=('Arial',8)).pack(anchor='e',padx=7)
        ttk.Button(lp,text='إعادة المواقع الافتراضية',style='Dark.TButton',command=self.reset_positions).pack(fill='x',padx=7,pady=5)

        toolbar=self.panel(center,accent=COLORS['line']);toolbar.pack(fill='x')
        toolbar_buttons=[ttk.Button(toolbar,text=title,style=style,command=command)
                         for title,style,command in [('جديد','Dark.TButton',self.reset_design),
                                                     ('فتح','Dark.TButton',self.choose_background),
                                                     ('تحديث المعاينة','Accent.TButton',self.save_and_render),
                                                     ('تصدير PNG','Dark.TButton',self.export_preview)]]
        fit_buttons(toolbar,toolbar_buttons,(4,2,1),padx=4,pady=4)
        self.canvas_wrap=self.panel(center,accent=COLORS['accent']);self.canvas_wrap.pack(fill='both',expand=True,pady=4)
        self.canvas=tk.Canvas(self.canvas_wrap,bg='#E9E6F7',highlightthickness=0,width=300,height=205)
        self.canvas.pack(fill='both',expand=True,padx=7,pady=7)
        self._preview_editor=CardPreview(self.canvas)
        self.canvas.bind('<Configure>',lambda e:self.render_canvas())
        self.canvas.bind('<ButtonPress-1>',self.on_drag_start);self.canvas.bind('<B1-Motion>',self.on_drag);self.canvas.bind('<ButtonRelease-1>',self.on_drag_end)
        self.canvas.bind('<MouseWheel>',self.on_element_wheel);self.canvas.bind('<Button-4>',self.on_element_wheel);self.canvas.bind('<Button-5>',self.on_element_wheel)

        sp=self.panel(center,accent=COLORS['blue']);sp.pack(fill='x')
        self.heading(sp,'القوالب المحفوظة',COLORS['blue']).pack(fill='x',padx=6,pady=(3,0))
        ttk.Button(sp,text='حفظ قالب',style='Dark.TButton',command=self.save_template).pack(side='right',padx=6,pady=6)
        ttk.Button(sp,text='استيراد قالب',style='Dark.TButton',command=self.load_template).pack(side='right',padx=6,pady=6)

        rp=self.panel(right,accent=COLORS['accent']);rp.pack(fill='both',expand=True)
        settings_head=tk.Frame(rp,bg=COLORS['panel']);settings_head.pack(fill='x',padx=6,pady=2)
        self.heading(settings_head,'إعدادات الكرت',COLORS['accent']).pack(side='right')
        ttk.Button(settings_head,text='حفظ التصميم',style='Accent.TButton',command=self.save_and_render).pack(side='left')
        form=tk.Frame(rp,bg=COLORS['panel']);form.pack(fill='x',padx=7)
        self.field(form,'العرض مم',self.vars['width'],12,0,0);self.field(form,'الارتفاع مم',self.vars['height'],12,0,1)
        self.field(form,'DPI',self.vars['dpi'],12,2,0,'spin');self.field(form,'عرض الصورة',self.vars['mode'],12,2,1,'combo',{'Fit':'احتواء','Fill':'ملء','Stretch':'تمديد','Crop':'اقتصاص','Tile':'تكرار'})
        self.field(form,'الخط',self.vars['font_family'],15,4,0,'combo',['Arial','Tahoma']);self.field(form,'حجم الخط',self.vars['font_size'],10,4,1,'spin')
        self.field(form,'العنوان',self.vars['title'],18,6,0);self.field(form,'رقم الهاتف',self.vars['phone'],18,6,1)
        self.field(form,'سمك الحدود',self.vars['border_width'],10,8,0,'spin');self.field(form,'حجم QR مم',self.vars['qr_size'],10,8,1,'spin')
        self.field(form,'محتوى QR',self.vars['qr_mode'],18,10,0,'combo',['اسم المستخدم فقط','بيانات الدخول','رابط تسجيل الدخول'])
        self.field(form,'رابط QR',self.vars['qr_url'],24,10,1)
        ttk.Checkbutton(form,text='خط عريض',variable=self.vars['bold'],command=self.schedule_render).grid(row=12,column=0,sticky='w',padx=5,pady=4)
        ttk.Button(form,text='لون الخط',style='Dark.TButton',command=self.choose_font_color).grid(row=12,column=1,sticky='ew',padx=5,pady=4)
        ttk.Button(form,text='لون رقم الكرت',style='Dark.TButton',command=self.choose_card_number_color).grid(row=13,column=0,sticky='ew',padx=5,pady=4)
        ttk.Button(form,text='لون الحدود',style='Dark.TButton',command=self.choose_border_color).grid(row=13,column=1,sticky='ew',padx=5,pady=4)
        for v in self.vars.values():
            try:v.trace_add('write',lambda *_:self.schedule_render())
            except Exception:pass

    def refresh(self):
        self.package_bar.refresh()
        self._refreshing=True
        try:
            s=self.db.all_settings();self._position_overrides={};self.bg_path_var.set(s.get('card.background_path',''));self.bg_label.config(text=Path(self.bg_path_var.get()).name if self.bg_path_var.get() else s.get('card.background_name','القالب الافتراضي'))
            self.vars['width'].set(s.get('card.width_mm',50.0));self.vars['height'].set(s.get('card.height_mm',19.0));self.vars['dpi'].set(s.get('card.dpi',300))
            self.vars['mode'].set(str(s.get('card.background_mode','stretch')).title());self.vars['font_family'].set(s.get('card.font_family','Arial'));self.vars['font_size'].set(s.get('card.font_size',14));self.vars['bold'].set(s.get('card.font_bold',True));self.vars['border_width'].set(s.get('card.border_width',1));self.vars['qr_size'].set(s.get('card.qr_size_mm',24));self.vars['title'].set(s.get('card.title_text','شبكة حضرموت'));self.vars['phone'].set(s.get('card.phone_text','رقم الهاتف'))
            self.vars['qr_mode'].set({'username':'اسم المستخدم فقط','credentials':'بيانات الدخول','login_url':'رابط تسجيل الدخول'}.get(s.get('card.qr_mode','credentials'),'بيانات الدخول'));self.vars['qr_url'].set(s.get('card.qr_login_url','http://hotspot.local/login'))
            for k in self.element_vars:self.element_vars[k].set(s.get('card.add_'+k, self.element_vars[k].get()))
        finally:self._refreshing=False
        self.render_canvas()

    def settings_from_ui(self):
        width=float(self.vars['width'].get());height=float(self.vars['height'].get());dpi=int(self.vars['dpi'].get())
        if not 1<=width<=300 or not 1<=height<=300:raise ValueError('أبعاد الكرت يجب أن تكون بين 1 و300 مم.')
        if not 72<=dpi<=600:raise ValueError('DPI يجب أن يكون بين 72 و600.')
        qr_mode={'اسم المستخدم فقط':'username','بيانات الدخول':'credentials','رابط تسجيل الدخول':'login_url'}.get(self.vars['qr_mode'].get(),'credentials')
        s=self.db.all_settings();s['card.positions']=dict(s.get('card.positions',{}),**self._position_overrides);s.update({'card.width_mm':width,'card.height_mm':height,'card.dpi':dpi,'card.background_path':self.bg_path_var.get(),'card.background_mode':self.vars['mode'].get().lower(),'card.font_family':self.vars['font_family'].get(),'card.font_size':self.vars['font_size'].get(),'card.font_bold':self.vars['bold'].get(),'card.border_width':self.vars['border_width'].get(),'card.qr_size_mm':self.vars['qr_size'].get(),'card.qr_mode':qr_mode,'card.qr_login_url':self.vars['qr_url'].get().strip(),'card.title_text':self.vars['title'].get(),'card.phone_text':self.vars['phone'].get()})
        for k,v in self.element_vars.items():s['card.add_'+k]=v.get()
        return s

    def draft_settings(self):
        return {key:value for key,value in self.settings_from_ui().items() if key.startswith('card.')}

    def schedule_render(self):
        if self._refreshing or self._preview_editor.drag is not None:return
        if self._save_job:
            try:self.after_cancel(self._save_job)
            except Exception:pass
        self._save_job=self.after(220,self.save_and_render)

    def save_and_render(self):
        self._save_job=None
        try:
            s=self.settings_from_ui()
            self.db.set_settings({k:v for k,v in s.items() if k.startswith('card.')})
            self.render_canvas()
        except (ValueError,tk.TclError) as exc:self.app.status_var.set(str(exc))

    def choose_background(self):
        p=filedialog.askopenfilename(title='اختيار تصميم الكرت',filetypes=[('Images','*.png *.jpg *.jpeg *.bmp'),('All files','*.*')])
        if p:
            try:
                source=Path(p)
                checked=self.db.all_settings();checked['card.background_path']=str(source)
                package_snapshot(checked)
                designs=DATA_DIR/'designs';designs.mkdir(parents=True,exist_ok=True)
                if source.parent.resolve()!=designs.resolve():
                    name=f"{datetime.now().strftime('%Y%m%d_%H%M%S')}_{source.name}";destination=designs/name;shutil.copy2(source,destination)
                else:destination=source
                with Image.open(destination, formats=CARD_IMAGE_FORMATS) as check:check.verify()
                self.bg_path_var.set(str(destination));self.bg_label.config(text=destination.name);self.save_and_render()
            except Exception as exc:messagebox.showerror('صورة غير صالحة',str(exc))

    def choose_font_color(self):
        c=colorchooser.askcolor(self.db.get_setting('card.font_color','#E7FFF7'))[1]
        if c:self.db.set_setting('card.font_color',c);self.render_canvas()
    def choose_card_number_color(self):
        c=colorchooser.askcolor(self.db.get_setting('card.color.username','#000000'))[1]
        if c:self.db.set_settings({'card.color.username':c,'card.color.password':c});self.render_canvas()
    def choose_border_color(self):
        c=colorchooser.askcolor(self.db.get_setting('card.border_color','#00C98D'))[1]
        if c:self.db.set_setting('card.border_color',c);self.render_canvas()

    def render_canvas(self):
        if not self.canvas.winfo_exists() or self._preview_editor.drag is not None:return
        try:
            s=self.settings_from_ui()
            geometry=self._current_geometry()
            self._preview_editor.draw(s,sample_card(s),geometry)
        except Exception as exc:
            self.app.status_var.set(AR(str(exc)))

    def _current_geometry(self):
        return preview_geometry(self.canvas,self.vars['width'].get(),self.vars['height'].get(),padding=20)

    def on_drag_start(self,e):
        if self._save_job:self.after_cancel(self._save_job);self._save_job=None
        if self._preview_editor.start(e):
            self.selected_element=self._preview_editor.selected
            self.app.status_var.set(AR('تحريك '+ELEMENT_NAMES[self.selected_element]+' — أفلت الماوس للحفظ'))
        return 'break'

    def on_drag(self,e):
        self._preview_editor.move(e)
        return 'break'

    def on_drag_end(self,e):
        result=self._preview_editor.finish(e)
        if result:
            name,value=result
            self._position_overrides[name]=value
            pos=dict(self.db.get_setting('card.positions',{}) or {});pos[name]=value
            self.db.set_setting('card.positions',pos)
            self.app.status_var.set(AR('تم حفظ موقع '+ELEMENT_NAMES[name]))
            self.save_and_render()
        else:self.render_canvas()
        return 'break'

    def on_element_wheel(self,e):
        if self._preview_editor.drag is not None:return 'break'
        element=self._preview_editor.hit(e.x,e.y) or self.selected_element
        if not element:return 'break'
        direction=1 if getattr(e,'delta',0)>0 or getattr(e,'num',0)==4 else -1
        self.selected_element=element;self._preview_editor.selected=element
        if element=='qr':
            try:current=float(self.vars['qr_size'].get())
            except (ValueError,tk.TclError):current=24.0
            maximum=max(1.0,min(float(self.vars['width'].get()),float(self.vars['height'].get())))
            self.vars['qr_size'].set(round(max(1.0,min(maximum,current+direction)),2))
        else:
            key='card.size.'+element
            current=float(self.db.get_setting(key,1.0) or 1.0)
            self.db.set_setting(key,round(max(.4,min(4.0,current+direction*.1)),2))
            self.render_canvas()
        return 'break'
    def reset_positions(self):
        from core.db import DEFAULT_SETTINGS
        self._position_overrides={}
        self.db.set_setting('card.positions',DEFAULT_SETTINGS['card.positions']);self.render_canvas()
    def reset_design(self):
        self.db.set_settings({'card.background_data':'','card.background_name':'','card.background_path':str(ROOT_DIR/'assets'/'default_card.png')})
        self.bg_path_var.set(str(ROOT_DIR/'assets'/'default_card.png'));self.vars['width'].set(50.0);self.vars['height'].set(19.0);self.vars['mode'].set('Stretch')
        for var in self.element_vars.values():var.set(False)
        self.save_and_render()
    def export_preview(self):
        p=filedialog.asksaveasfilename(defaultextension='.png',filetypes=[('PNG','*.png')],initialfile='Hadramout_card.png')
        if p:
            try:
                s=self.settings_from_ui();render_card(s,None).save(p,dpi=(int(s['card.dpi']),int(s['card.dpi'])));self.app.status_var.set('تم تصدير صورة الكرت بالدقة المحددة')
            except Exception as exc:messagebox.showerror('فشل التصدير',str(exc))
    def save_template(self):
        p=filedialog.asksaveasfilename(defaultextension='.hadramout-template.json',filetypes=[('قالب حضرموت','*.json')],initialfile='card_template.json')
        if p:
            try:
                data={k:v for k,v in package_snapshot(self.settings_from_ui()).items() if k.startswith('card.')}
                Path(p).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf-8')
            except Exception as exc:messagebox.showerror('حفظ القالب',str(exc))
    def load_template(self):
        p=filedialog.askopenfilename(filetypes=[('قالب حضرموت','*.json'),('JSON','*.json')])
        if p:
            try:
                from core.packages import load_card_template
                data=load_card_template(p,self.db.all_settings())
                self.db.replace_preset_settings(data)
                self.refresh()
            except Exception as exc:messagebox.showerror('قالب غير صالح',str(exc))

class PrintPage(Page):
    PRINT_MAP={'printer':'default_printer','paper':'paper','rows':'rows','cols':'columns',
               'gap_h':'gap_h_mm','gap_v':'gap_v_mm','page_margin':'page_margin_mm',
               'borders':'show_borders','cut_marks':'cut_marks'}
    ORDER={'يسار إلى يمين':'left_to_right','يمين إلى يسار':'right_to_left','متعرج':'snake',
           'أعمدة من اليسار':'column_left','أعمدة من اليمين':'column_right'}
    CARD_MAP={'width':'width_mm','height':'height_mm','dpi':'dpi','mode':'background_mode'}

    def __init__(self,parent,app):
        super().__init__(parent,app,'إدارة الطباعة','دفعات الراوتر المتصل وقوالبها وقت الإنشاء؛ لإنشاء كروت جديدة افتح إنشاء وطباعة الكروت')
        self._preview_job=None;self._loading=False;self._loaded_package=object();self._batches={};self.busy=False
        self.vars={key:tk.StringVar() for key in list(self.PRINT_MAP)+['orientation','order','batch']+list(self.CARD_MAP)}
        for key in ('borders','cut_marks'):self.vars[key]=tk.BooleanVar()
        self.label(self.body,'طباعة من إعدادات الدفعة المحفوظة وقت الإنشاء',muted=True).pack(fill='x',pady=4)
        selector=self.panel(accent=COLORS['accent']);selector.pack(fill='x',pady=(0,3))
        self.heading(selector,'دفعة الكروت',COLORS['accent']).pack(side='right',fill='y',padx=(0,8))
        self.batch_combo=ttk.Combobox(selector,textvariable=self.vars['batch'],state='readonly',width=45)
        self.batch_combo.pack(side='right',fill='x',expand=True,padx=6,pady=6)
        self.batch_combo.bind('<<ComboboxSelected>>',self.select_batch)
        ttk.Button(selector,text='تحديث',style='Dark.TButton',command=self.refresh).pack(side='left',padx=6)
        area=tk.Frame(self.body,bg=COLORS['bg']);area.pack(fill='both',expand=True)
        controls=ttk.Notebook(area,width=300);controls.pack(side='right',fill='y',padx=(6,0))
        paper=tk.Frame(controls,bg=COLORS['panel']);art=tk.Frame(controls,bg=COLORS['panel'])
        controls.add(paper,text='إعدادات الورقة');controls.add(art,text='قالب الباقة')
        for i in range(2):paper.columnconfigure(i,weight=1);art.columnconfigure(i,weight=1)
        self.printer_combo=self.field(paper,'الطابعة',self.vars['printer'],24,0,0,'combo',windows_printers())
        self.printer_combo.grid_configure(columnspan=2)
        self.field(paper,'الورق',self.vars['paper'],12,2,0,'combo',['A4','A3'])
        self.field(paper,'الاتجاه',self.vars['orientation'],12,2,1,'combo',['عمودي','أفقي'])
        self.field(paper,'الصفوف',self.vars['rows'],12,4,0,'spin')
        self.field(paper,'الأعمدة',self.vars['cols'],12,4,1,'spin')
        self.field(paper,'فاصل أفقي مم',self.vars['gap_h'],12,6,0)
        self.field(paper,'فاصل رأسي مم',self.vars['gap_v'],12,6,1)
        self.field(paper,'هامش الورقة مم',self.vars['page_margin'],12,8,0)
        self.field(paper,'ترتيب الكروت',self.vars['order'],12,8,1,'combo',list(self.ORDER))
        ttk.Checkbutton(paper,text='الحدود',variable=self.vars['borders']).grid(row=10,column=0,sticky='e',padx=5,pady=3)
        ttk.Checkbutton(paper,text='علامات القص',variable=self.vars['cut_marks']).grid(row=10,column=1,sticky='e',padx=5,pady=3)
        ttk.Button(paper,text='ضبط الصفوف والأعمدة تلقائيًا',style='Green.TButton',command=self.auto_fit).grid(row=11,column=0,columnspan=2,sticky='ew',padx=5,pady=4)
        self.field(art,'عرض الكرت مم',self.vars['width'],12,0,0)
        self.field(art,'ارتفاع الكرت مم',self.vars['height'],12,0,1)
        self.field(art,'دقة DPI',self.vars['dpi'],12,2,0,'spin')
        self.field(art,'ملاءمة الصورة',self.vars['mode'],12,2,1,'combo',{'stretch':'تمديد','fit':'احتواء','fill':'ملء','crop':'اقتصاص','tile':'تكرار'})
        self.template_label=self.label(art,'',muted=True,wraplength=295,justify='right')
        self.template_label.grid(row=4,column=0,columnspan=2,sticky='ew',padx=5,pady=4)
        self.card_preview=tk.Label(art,bg=COLORS['panel']);self.card_preview.grid(row=5,column=0,columnspan=2,padx=6,pady=8)
        ttk.Button(art,text='اختيار صورة القالب',style='Dark.TButton',command=self.choose_background).grid(row=6,column=0,columnspan=2,sticky='ew',padx=8,pady=5)
        ttk.Button(art,text='تعديل النصوص وQR ومواقع العناصر',style='Dark.TButton',command=lambda:app.show_page('design')).grid(row=7,column=0,columnspan=2,sticky='ew',padx=8,pady=5)
        preview=self.panel(area,accent=COLORS['accent2']);preview.pack(side='left',fill='both',expand=True)
        self.page_canvas=tk.Canvas(preview,bg='#E9E6F7',width=350,height=310,highlightthickness=0)
        self.page_canvas.pack(fill='both',expand=True,padx=6,pady=6)
        self.page_canvas.bind('<Configure>',lambda _e:self.schedule_preview())
        self.preview_note=self.label(preview,'',muted=True,wraplength=390,justify='right')
        self.preview_note.pack(fill='x',padx=8,pady=4)
        footer=tk.Frame(self,bg=COLORS['panel']);footer.pack(side='bottom',fill='x',before=self._viewport,padx=8,pady=4)
        self.total_var=tk.StringVar()
        tk.Label(footer,textvariable=self.total_var,bg=COLORS['panel'],fg=COLORS['text'],font=('Arial',10)).pack(side='right',padx=8)
        for title,command,style in [('طباعة',lambda:self.export(True),'Accent.TButton'),
                                   ('معاينة PDF',lambda:self.export(False,True),'Dark.TButton'),
                                   ('حفظ PDF',lambda:self.export(False),'Dark.TButton')]:
            ttk.Button(footer,text=title,command=command,style=style).pack(side='left',padx=3,pady=5)
        for key,var in self.vars.items():
            if key!='batch':var.trace_add('write',lambda *_:self.schedule_preview())

    def _load_controls(self,settings):
        self._loading=True
        try:
            for key,value in self.PRINT_MAP.items():self.vars[key].set(settings.get('print.'+value,''))
            for key,value in self.CARD_MAP.items():self.vars[key].set(settings.get('card.'+value,''))
            self.vars['orientation'].set('عمودي' if settings.get('print.orientation')=='portrait' else 'أفقي')
            order=settings.get('print.order','left_to_right')
            self.vars['order'].set(next((label for label,value in self.ORDER.items() if value==order),next(iter(self.ORDER))))
        finally:self._loading=False

    def refresh(self):
        changed=self._loaded_package!=self.app.active_package_id
        old_batch=self._batch_id()
        router=self.app.session.router_row()
        batches=self.db.batches(10000,router_id=router['id'] if router else None)
        self._batches={('%s | %s | %s %s' % (b['batch_no'],b['package_name'] or b['profile'] or '',b['quantity'],AR('كرت'))):dict(b) for b in batches}
        self.batch_combo['values']=list(self._batches)
        chosen=next((label for label,b in self._batches.items() if b['id']==old_batch),next(iter(self._batches),''))
        self.vars['batch'].set(chosen)
        if changed or old_batch!=self._batch_id():self._load_controls(self._base_settings())
        self._loaded_package=self.app.active_package_id
        self.printer_combo['values']=windows_printers()
        self.refresh_preview()

    def _batch_id(self):
        row=self._batches.get(self.vars['batch'].get())
        return row['id'] if row else None

    def _cards(self):
        ident=self._batch_id()
        return [dict(row) for row in reversed(self.db.cards(100000,ident))] if ident is not None else []

    def _base_settings(self):
        settings=self.db.all_settings()
        batch=self._batches.get(self.vars['batch'].get())
        if batch:
            if batch.get('print_settings_json'):settings.update(json.loads(batch['print_settings_json']))
            settings.update({'create.profile':batch['profile'],'create.server':batch['server'],'create.price':batch['price']})
        return settings

    def draft_settings(self):
        s=self._base_settings()
        for key,value in self.PRINT_MAP.items():s['print.'+value]=self.vars[key].get()
        for key in ('rows','columns'):s['print.'+key]=int(s['print.'+key])
        for key in ('gap_h_mm','gap_v_mm','page_margin_mm'):s['print.'+key]=float(s['print.'+key])
        s['print.orientation']='portrait' if self.vars['orientation'].get()=='عمودي' else 'landscape'
        s['print.order']=self.ORDER[self.vars['order'].get()]
        for key,value in self.CARD_MAP.items():
            s['card.'+value]=self.vars[key].get() if key=='mode' else int(self.vars[key].get()) if key=='dpi' else float(self.vars[key].get())
        return {key:value for key,value in s.items() if key.startswith(('card.','create.','print.'))}

    @staticmethod
    def layout(settings):
        return calculate_layout(settings,settings['print.rows'],settings['print.columns'],settings['print.paper'],
                                settings['print.orientation'],settings['print.gap_h_mm'],settings['print.gap_v_mm'],settings['print.page_margin_mm'])

    def select_batch(self,_event=None):
        self._load_controls(self._base_settings())
        self.refresh_preview()

    def schedule_preview(self):
        if self._loading:return
        if self._preview_job:self.after_cancel(self._preview_job)
        self._preview_job=self.after(250,self.refresh_preview)

    def auto_fit(self):
        try:
            s=self.draft_settings()
            rows,cols=auto_grid(s,s['print.paper'],s['print.orientation'],s['print.gap_h_mm'],s['print.gap_v_mm'],s['print.page_margin_mm'])
            self.vars['rows'].set(rows);self.vars['cols'].set(cols)
        except Exception as exc:messagebox.showerror('التخطيط',str(exc))

    def choose_background(self):
        path=filedialog.askopenfilename(title='صورة قالب الباقة',filetypes=[('Images','*.png *.jpg *.jpeg *.bmp')])
        if not path:return
        try:
            s=self.draft_settings();s['card.background_path']=path;s=package_snapshot(s)
            batch=self._batches.get(self.vars['batch'].get())
            if batch:
                s=self.db.update_batch_print_settings(batch['id'],s)
                batch['print_settings_json']=json.dumps(s,ensure_ascii=False)
            else:
                self.db.set_settings({key:value for key,value in s.items() if key.startswith('card.background_')})
            self.refresh_preview()
        except Exception as exc:messagebox.showerror('القالب',str(exc))

    def refresh_preview(self):
        if self._preview_job:self.after_cancel(self._preview_job);self._preview_job=None
        canvas=self.page_canvas;canvas.delete('all')
        try:
            s=self.draft_settings();layout=self.layout(s);cards=self._cards()
            count=len(cards);per_page=layout['per_page'];pages=(count+per_page-1)//per_page
            self.total_var.set(AR('%s كرت | %s كرت/ورقة | %s صفحة' % (count,per_page,pages)))
            self.preview_note.config(text=AR('معاينة الصفحة الأولى من الدفعة المختارة' if cards else 'لا توجد كروت لهذه الباقة. المعروض مثال للقالب فقط.'))
            self.template_label.config(text=AR('القالب: ')+(s.get('card.background_name') or Path(s.get('card.background_path') or 'افتراضي').name))
            self.card_photo=card_to_tk_image(s,cards[0] if cards else sample_card(s),max_size=(280,135))
            self.card_preview.configure(image=self.card_photo)
            w=max(160,canvas.winfo_width());h=max(160,canvas.winfo_height())
            page_w,page_h=layout['page_size'];scale=min((w-24)/page_w,(h-24)/page_h)
            pw,ph=page_w*scale,page_h*scale;x=(w-pw)/2;y=(h-ph)/2
            canvas.create_rectangle(x,y,x+pw,y+ph,fill='white',outline='#A49DB8')
            self._preview_imgs=[]
            slots=next(card_pages(cards or [sample_card(s)],layout,s['print.order'],'single',s['print.orientation']))
            for card,slot_x,slot_y in slots:
                cx=x+slot_x*scale
                cy=y+(page_h-slot_y-layout['card_height'])*scale
                width=layout['card_width']*scale;height=layout['card_height']*scale
                image=render_card(s,dict(card),target_size=(max(2,int(width)),max(2,int(height))))
                photo=ImageTk.PhotoImage(image);self._preview_imgs.append(photo)
                canvas.create_image(cx,cy,image=photo,anchor='nw')
                if s['print.show_borders']:canvas.create_rectangle(cx,cy,cx+width,cy+height,outline='#8C85A2')
        except Exception as exc:
            self.total_var.set(AR('راجع إعدادات التخطيط'))
            canvas.create_text(16,16,text=AR(str(exc)),anchor='nw',fill=COLORS['red'],width=max(150,canvas.winfo_width()-32))

    def export(self,do_print=False,open_after=False):
        if self.__dict__.get('busy',False):return
        cards=self._cards()
        if any(c.get('status')!='SENT' for c in cards):
            if not messagebox.askyesno('حالة الكروت','طباعة الكروت المرسلة والمفعلة فقط من الدفعة؟'):return
            cards=[c for c in cards if c.get('status')=='SENT']
        if not cards:return messagebox.showwarning('الطباعة','لا توجد كروت في الدفعة المختارة. أنشئ كروتًا لهذه الباقة أولًا.')
        try:
            s=package_snapshot(self.draft_settings());self.layout(s)
            out=Path(self.db.get_setting('paths.output',str(ROOT_DIR/'exports')));out.mkdir(parents=True,exist_ok=True)
            path=out/('print_'+datetime.now().strftime('%Y%m%d_%H%M%S_%f')+'.pdf')
            cards=[dict(card) for card in cards]
        except Exception as exc:return messagebox.showerror('الطباعة',str(exc))
        self.busy=True
        self.app.status_var.set(AR('جارٍ تجهيز ملف الطباعة في الخلفية...'))
        progress=printing_progress(self.app)
        def work():
            # Archive printing uses the batch snapshot; never overwrite a different live profile.
            if do_print:return print_cards(s,cards,s['print.default_printer'],pdf_path=path,progress=progress)
            export_settings_pdf(s,cards,path,progress=progress)
            return False
        def done(printed):
            self.busy=False
            if do_print:
                try:
                    if printed:self.db.mark_printed([card['id'] for card in cards])
                    self.db.log('PRINT','طباعة كروت' if printed else 'حفظ PDF','SUCCESS',str(path))
                except Exception as exc:
                    messagebox.showwarning('سجل الطباعة','اكتملت العملية لكن تعذر تحديث السجل. لا تكرر الطباعة بسبب هذا التنبيه.\n'+str(exc))
                self.app.status_var.set(AR('تم تسليم الدفعة للطابعة' if printed else 'تم تجهيز PDF'))
                try:
                    saved_copy=offer_print_copy(self,path)
                    if saved_copy:messagebox.showinfo('تم الحفظ','حُفظت نسخة ملف الطباعة في:\n'+str(saved_copy))
                except Exception as exc:
                    messagebox.showwarning('حفظ نسخة الطباعة',('أُرسلت الدفعة للطابعة.\n' if printed else '')+'تعذر حفظ النسخة المختارة:\n'+str(exc)+'\nالنسخة التلقائية:\n'+str(path))
            else:
                if open_after:_open_path(path)
                else:messagebox.showinfo('تم حفظ PDF',str(path))
        def failed(exc):
            self.busy=False
            detail=str(exc)
            if path.is_file():detail+='\n\nملف PDF محفوظ في:\n'+str(path)
            messagebox.showerror('الطباعة',detail)
        self.app.safe_thread(work,done,failed)

class MikroTikPage(Page):
    def __init__(self,parent,app):
        super().__init__(parent,app,'الاتصالات المحفوظة','حفظ بيانات الراوترات؛ إدارة الباقات والمستخدمين من الأقسام الرئيسية')
        self.vars={'name':tk.StringVar(),'host':tk.StringVar(value='192.168.88.1'),'port':tk.StringVar(value='8728'),'username':tk.StringVar(value='admin'),'password':tk.StringVar(),'ssl':tk.BooleanVar(value=False),'verify_ssl':tk.BooleanVar(value=False),'ftp_port':tk.IntVar(value=21),'default':tk.BooleanVar(value=True),'connection_mode':tk.StringVar(value='local')}
        self._testing=False
        left=self.panel();left.pack(side='left',fill='y');left.configure(width=235);left.pack_propagate(False)
        center=self.panel();center.pack(side='left',fill='both',expand=True,padx=10)
        self.heading(left,'الراوترات المحفوظة',COLORS['blue'],font=('Arial',12,'bold')).pack(fill='x',pady=(0,6))
        self.router_tree=ttk.Treeview(left,columns=('name','host'),show='headings',height=14);self.router_tree.heading('name',text='الاسم');self.router_tree.heading('host',text='العنوان');self.router_tree.column('name',width=135);self.router_tree.column('host',width=135);fit_table(self.router_tree);self.router_tree.pack(fill='both',expand=True,padx=10);self.router_tree.bind('<<TreeviewSelect>>',self.select_router)
        btns=tk.Frame(left,bg=COLORS['panel']);btns.pack(fill='x',padx=7,pady=6);ttk.Button(btns,text='جديد',style='Accent.TButton',command=self.new_router).pack(side='left',expand=True,fill='x',padx=3);ttk.Button(btns,text='حذف',style='Danger.TButton',command=self.delete_router).pack(side='left',expand=True,fill='x',padx=3)
        self.heading(center,'إعدادات الاتصال',COLORS['accent'],font=('Arial',12,'bold')).pack(fill='x',pady=(0,6))
        form=tk.Frame(center,bg=COLORS['panel']);form.pack(fill='x',padx=10)
        for c in range(2):form.grid_columnconfigure(c,weight=1)
        self.field(form,'اسم الراوتر',self.vars['name'],22,0,0);self.field(form,'عنوان IP أو رابط الاتصال',self.vars['host'],22,0,1);self.field(form,'منفذ API / الخارجي للرابط',self.vars['port'],12,2,0);self.field(form,'اسم المستخدم',self.vars['username'],18,2,1);self.field(form,'كلمة المرور',self.vars['password'],18,4,0,show='*');self.field(form,'منفذ FTP',self.vars['ftp_port'],12,4,1)
        ttk.Checkbutton(form,text='API-SSL',variable=self.vars['ssl']).grid(row=6,column=0,sticky='w',padx=5,pady=5);ttk.Checkbutton(form,text='التحقق من شهادة SSL',variable=self.vars['verify_ssl']).grid(row=6,column=1,sticky='w',padx=5,pady=5);ttk.Checkbutton(form,text='تعيين كافتراضي',variable=self.vars['default']).grid(row=7,column=0,sticky='w',padx=5,pady=5)
        mode_bar=tk.Frame(form,bg=COLORS['panel']);mode_bar.grid(row=8,column=0,columnspan=2,sticky='ew')
        for text,value in (('محلي','local'),('عن بُعد — رابط VPN','remote')):
            ttk.Radiobutton(mode_bar,text=text,value=value,variable=self.vars['connection_mode'],command=self.switch_connection_mode).pack(side='right',padx=8)
        self.remote_hint=self.label(form,'للرابط البعيد: أدخل منفذ API الخارجي من المزود. منفذ WinBox الموجود في الرابط لا يُستخدم تلقائياً.',muted=True,bg=COLORS['panel'],wraplength=550)
        self.remote_hint.grid(row=9,column=0,columnspan=2,sticky='ew',padx=5,pady=4);self.remote_hint.grid_remove()
        actions=tk.Frame(center,bg=COLORS['panel']);actions.pack(fill='x',padx=7,pady=6)
        ttk.Button(actions,text='حفظ',style='Dark.TButton',command=self.save_router).pack(side='left',expand=True,fill='x',padx=3)
        self.test_button=ttk.Button(actions,text='اختبار الاتصال',style='Accent.TButton',command=self.test_connection);self.test_button.pack(side='left',expand=True,fill='x',padx=3)
        ttk.Button(actions,text='باقات الشبكة',style='Green.TButton',command=lambda:self.app.show_page('packages')).pack(side='left',expand=True,fill='x',padx=3)
        self.conn_status=self.label(center,'الحالة: غير متصل',muted=True,bg=COLORS['panel']);self.conn_status.pack(anchor='w',padx=8,pady=5)
        self.selected_id=None

    def switch_connection_mode(self,reset_port=True):
        remote=self.vars['connection_mode'].get()=='remote'
        if remote:
            self.remote_hint.grid()
            if reset_port and self.vars['port'].get() in ('8728','8729'):self.vars['port'].set('')
        else:
            self.remote_hint.grid_remove()
            if not self.vars['port'].get():self.vars['port'].set('8729' if self.vars['ssl'].get() else '8728')

    def refresh(self):
        for i in self.router_tree.get_children():self.router_tree.delete(i)
        for r in self.db.routers():self.router_tree.insert('', 'end', iid=str(r['id']), values=(r['name']+(' ★' if r['is_default'] else ''),r['host']))
        active=self.app.session.router_row()
        selected=self.selected_id or (active['id'] if active else None)
        if selected and self.router_tree.exists(str(selected)):
            self.router_tree.selection_set(str(selected))
            self.select_router()
    def select_router(self,e=None):
        sel=self.router_tree.selection();
        if not sel:return
        self.selected_id=int(sel[0]);r=self.db.router(self.selected_id)
        if not r:return
        for k in ['name','host','port','username','password','ftp_port']:self.vars[k].set(r[k])
        self.vars['ssl'].set(bool(r['use_ssl']));self.vars['verify_ssl'].set(bool(r['verify_ssl']) if 'verify_ssl' in r.keys() else False);self.vars['default'].set(bool(r['is_default']))
        self.vars['connection_mode'].set(r.get('connection_mode','local'));self.switch_connection_mode(reset_port=False)
        active=self.app.session.router_row()
        text='الحالة: متصل — '+self.app.session.identity if active and active['id']==self.selected_id else 'اتصال محفوظ؛ سجّل الخروج ثم ادخل بهذا الراوتر لاستخدامه.'
        self.conn_status.config(text=AR(text))
    def new_router(self):
        self.selected_id=None;self.router_tree.selection_remove(self.router_tree.selection())
        defaults={'name':'','host':'192.168.88.1','port':'8728','username':'admin','password':'','ftp_port':21,'connection_mode':'local'}
        for key,value in defaults.items():self.vars[key].set(value)
        self.vars['ssl'].set(False);self.vars['verify_ssl'].set(False);self.vars['default'].set(not bool(self.db.routers()));self.conn_status.config(text='الحالة: أدخل بيانات الراوتر ثم اضغط حفظ')
        self.switch_connection_mode(reset_port=False)
    def save_router(self):
        try:
            values=dict(name=self.vars['name'].get() or self.vars['host'].get(),host=self.vars['host'].get(),port=self.vars['port'].get(),username=self.vars['username'].get(),password=self.vars['password'].get(),use_ssl=self.vars['ssl'].get(),verify_ssl=self.vars['verify_ssl'].get(),ftp_port=self.vars['ftp_port'].get(),is_default=self.vars['default'].get(),connection_mode=self.vars['connection_mode'].get())
            if self.selected_id:self.db.update_router(self.selected_id,**values)
            else:self.selected_id=self.db.add_router(**values)
            # A newly added router may reuse numbering already stored for
            # another device, so re-evaluate per-router username uniqueness now
            # instead of waiting for the next program start.
            self.db.refresh_router_uniqueness()
            self.db.log('MIKROTIK','حفظ راوتر','SUCCESS',self.vars['host'].get());self.refresh();self.app.refresh_sidebar_routers();self.app.status_var.set(AR('تم الحفظ؛ تطبّق بيانات الاتصال الجديدة عند تسجيل الدخول التالي'))
        except Exception as e:messagebox.showerror('خطأ',str(e))
    def delete_router(self):
        active=self.app.session.router_row()
        if active and self.selected_id==active['id']:
            return messagebox.showwarning('الراوتر متصل','لا يمكن حذف الراوتر أثناء الجلسة الحالية.')
        if self.selected_id and messagebox.askyesno('تأكيد','حذف الراوتر المحدد؟'):
            self.db.delete_router(self.selected_id);self.selected_id=None;self.refresh();self.app.refresh_sidebar_routers()
    def current_row(self):
        active=self.app.session.router_row()
        if active and self.selected_id==active['id']:return active
        messagebox.showwarning('تغيير الراوتر','سجّل الخروج ثم ادخل إلى الراوتر المطلوب. العمليات متاحة للراوتر المسجّل دخوله فقط.')
        return None
    def test_connection(self):
        if self._testing:return
        try:
            r=login_settings(dict(host=self.vars['host'].get(),port=self.vars['port'].get(),username=self.vars['username'].get(),password=self.vars['password'].get(),use_ssl=self.vars['ssl'].get(),verify_ssl=self.vars['verify_ssl'].get(),connection_mode=self.vars['connection_mode'].get()))
        except (ValueError,tk.TclError) as exc:
            return messagebox.showerror('بيانات الاتصال',str(exc),parent=self)
        target=endpoint_text(r['host'],r['port'])
        self._testing=True;self.test_button.configure(state='disabled')
        self.conn_status.config(text='الحالة: اختبار '+target+'...')
        def work():
            with connect_from_row(r) as api:return api.identity()
        def finished():
            self._testing=False;self.test_button.configure(state='normal')
        def ok(name):
            finished();self.conn_status.config(text='نجح اختبار '+target+' — '+name+'؛ الجلسة الحالية لم تتغير.')
            self.db.log('MIKROTIK','اختبار الاتصال','SUCCESS',target)
        def err(e):
            finished();self.conn_status.config(text='فشل اختبار '+target)
            detail=str(e)
            if r['connection_mode']=='remote':detail+='\nاطلب منفذ API الخارجي من مزود الرابط؛ قد يختلف عن منفذ WinBox.'
            self.db.log('MIKROTIK','اختبار الاتصال','FAILED',detail);messagebox.showerror('فشل الاتصال',detail,parent=self)
        self.app.safe_thread(work,ok,err)

class HotspotPage(Page):
    def __init__(self,parent,app):
        super().__init__(parent,app,'صفحات دخول السيرفر','قوالب محلية، معاينة في المتصفح ونسخ ملفات السيرفر')
        self.selected_folder=tk.StringVar();self.router_var=tk.StringVar();self.progress=tk.DoubleVar(value=0)
        self.remote_folder=tk.StringVar(value='hotspot');self._directories_loaded=False;self._local_files={}
        self.busy=False;self.transfer_buttons=[]
        bar=self.panel();bar.pack(fill='x',pady=4)
        bar.columnconfigure(1,weight=1);bar.columnconfigure(3,weight=1)
        self.router_combo=ttk.Combobox(bar,textvariable=self.router_var,state='readonly',width=23);self.router_combo.grid(row=0,column=3,sticky='ew',padx=3,pady=4)
        self.label(bar,'مجلد السيرفر').grid(row=0,column=2,padx=4)
        self.dir_combo=ttk.Combobox(bar,textvariable=self.remote_folder,width=21);self.dir_combo.grid(row=0,column=1,sticky='ew',padx=3)
        ttk.Button(bar,text='قراءة مجلدات السيرفر',command=self.fetch_directories).grid(row=0,column=0,padx=4)
        area=tk.Frame(self.body,bg=COLORS['bg']);area.pack(fill='both',expand=True)
        area.columnconfigure(0,weight=1);area.columnconfigure(1,weight=2);area.columnconfigure(2,weight=1);area.rowconfigure(0,weight=1)
        left=self.panel(area);left.grid(row=0,column=0,sticky='nsew',padx=(0,4))
        center=self.panel(area);center.grid(row=0,column=1,sticky='nsew',padx=4)
        right=self.panel(area);right.grid(row=0,column=2,sticky='nsew',padx=(4,0))
        self.heading(left,'قوالب صفحات الدخول',COLORS['blue']).pack(fill='x',pady=(0,5))
        self.listbox=tk.Listbox(left,width=20,height=11,font=('Arial',10),exportselection=False)
        self.listbox.pack(fill='both',expand=True);self.listbox.bind('<<ListboxSelect>>',self.select_template)
        ttk.Button(left,text='استيراد مجلد قالب',command=self.import_template).pack(fill='x',pady=4)
        ttk.Button(left,text='فتح مجلد القوالب',command=self.open_templates).pack(fill='x',pady=4)
        self.sel_label=self.label(center,'اختر قالبًا محليًا أو نزّل صفحات السيرفر',wraplength=330);self.sel_label.pack(fill='x',pady=4)
        self.preview=tk.Text(center,width=38,height=12,wrap='word',bg='#FAF9FF',font=('Courier New',10))
        self.preview.pack(fill='both',expand=True);self.preview.configure(state='disabled')
        ttk.Button(center,text='معاينة الصفحة في المتصفح',command=self.preview_selected).pack(fill='x',pady=4)
        self.heading(right,'ملفات القالب / السيرفر',COLORS['accent2']).pack(fill='x',pady=(0,5))
        self.file_tree=ttk.Treeview(right,show='tree',height=10)
        self.file_tree.column('#0',width=200,minwidth=130);self.file_tree.pack(fill='both',expand=True)
        self.file_tree.bind('<<TreeviewSelect>>',self.select_file)
        ttk.Button(right,text='قراءة ملفات السيرفر',command=self.fetch_files).pack(fill='x',pady=4)
        actions=self.panel();actions.pack(fill='x',pady=4)
        for title,command in [('رفع القالب مع نسخة احتياطية',self.upload),('تنزيل صفحات السيرفر',self.backup),('فحص القالب',self.validate_selected)]:
            button=ttk.Button(actions,text=title,command=command)
            button.pack(side='right',expand=True,fill='x',padx=4,pady=4)
            self.transfer_buttons.append(button)
        ttk.Progressbar(self.body,variable=self.progress,maximum=100).pack(fill='x',pady=3)
        self.log=tk.Text(self.body,height=3,wrap='word',font=('Arial',10));self.log.pack(fill='x')

    def fetch_directories(self):
        def done(rows):
            values=sorted({r.get('html-directory','hotspot') or 'hotspot' for r in rows})
            self.dir_combo.configure(values=values)
            if values and self.remote_folder.get() not in values:self.remote_folder.set(values[0])
            self._directories_loaded=True
        def failed(exc):self.log.insert('end','تعذرت قراءة مسارات الصفحات: '+str(exc)+'\n')
        self.app.run_network(lambda g:g.api.command('/ip/hotspot/profile/print',{'.proplist':'name,html-directory'}),done,failed,read_only=True,affects_inventory=False)

    def fetch_files(self):
        folder=self.remote_folder.get().strip().strip('/')
        if not folder:return messagebox.showerror('المجلد','حدد مجلد صفحات الدخول على الراوتر.')
        def done(rows):
            self._local_files={}
            for item in self.file_tree.get_children():self.file_tree.delete(item)
            for row in rows:
                name=row.get('name','')
                if name.startswith(folder+'/'):self.file_tree.insert('','end',text=name[len(folder)+1:])
            self.sel_label.configure(text=AR('قائمة ملفات السيرفر؛ نزّلها لعرض محتواها.'))
        self.app.run_network(lambda g:g.api.command('/file/print',{'.proplist':'name,type,size'}),done,read_only=True,affects_inventory=False)

    def select_file(self,_event=None):
        selected=self.file_tree.selection()
        if not selected:return
        path=self._local_files.get(selected[0])
        if not path:return
        self.preview.configure(state='normal');self.preview.delete('1.0','end')
        if path.suffix.lower() in ('.html','.css','.js','.txt','.json','.xml'):
            if path.stat().st_size<=512000:self.preview.insert('1.0',path.read_text(encoding='utf-8',errors='replace'))
            else:self.preview.insert('1.0',AR('الملف كبير؛ افتحه من مجلد القالب.'))
        else:self.preview.insert('1.0',AR('ملف من ملفات القالب: ')+path.name)
        self.preview.configure(state='disabled')

    def refresh(self):
        base=Path(self.db.get_setting('paths.hotspot_templates',str(ROOT_DIR/'hotspot_templates')));base.mkdir(parents=True,exist_ok=True);self.listbox.delete(0,'end');[self.listbox.insert('end',p.name) for p in sorted(base.iterdir()) if p.is_dir()]
        vals=[f"{r['name']} ({r['host']})" for r in self.app.router_choices()];self.router_combo['values']=vals
        if vals and self.router_var.get() not in vals:self.router_var.set(vals[0])
        if not self._directories_loaded:self.fetch_directories()
    def router_row(self):
        return self.app.session.router_row()
    def select_template(self,e=None):
        sel=self.listbox.curselection();
        if not sel:return
        base=Path(self.db.get_setting('paths.hotspot_templates',str(ROOT_DIR/'hotspot_templates')));p=base/self.listbox.get(sel[0]);self.selected_folder.set(str(p));self.sel_label.config(text=AR('محتوى القالب: ')+p.name)
        for item in self.file_tree.get_children():self.file_tree.delete(item)
        self._local_files={}
        for path in sorted(p.rglob('*')):
            if path.is_file():
                ident=self.file_tree.insert('','end',text=path.relative_to(p).as_posix());self._local_files[ident]=path
                if path.name=='login.html':self.file_tree.selection_set(ident)
        self.select_file()
    def open_templates(self):
        p=self.db.get_setting('paths.hotspot_templates',str(ROOT_DIR/'hotspot_templates'));Path(p).mkdir(parents=True,exist_ok=True)
        try:_open_path(p)
        except Exception:pass
    def import_template(self):
        p=filedialog.askdirectory(title='اختيار مجلد صفحة الهوتسبوت')
        if not p:return
        try:validate_hotspot_template(p)
        except Exception as exc:return messagebox.showerror('قالب غير صالح',str(exc))
        base=Path(self.db.get_setting('paths.hotspot_templates',str(ROOT_DIR/'hotspot_templates')));base.mkdir(parents=True,exist_ok=True);source=Path(p).resolve();dst=base/source.name
        if dst.exists():
            index=2
            while (base/f'{source.name}_{index}').exists():index+=1
            dst=base/f'{source.name}_{index}'
        try:shutil.copytree(source,dst);self.refresh();self.db.log('HOTSPOT','استيراد قالب','SUCCESS',dst.name);messagebox.showinfo('تم',f'تم استيراد القالب باسم {dst.name}')
        except Exception as exc:messagebox.showerror('فشل الاستيراد',str(exc))
    def validate_selected(self):
        folder=self.selected_folder.get()
        if not folder:return messagebox.showwarning('تنبيه','اختر قالبًا')
        try:
            result=validate_hotspot_template(folder);missing='، '.join(result['missing_recommended']) or 'لا يوجد'
            messagebox.showinfo('نتيجة الفحص',f"القالب صالح ✓\nعدد الملفات: {result['files']}\nملفات مستحسنة غير موجودة: {missing}")
        except Exception as exc:messagebox.showerror('قالب غير صالح',str(exc))
    def preview_selected(self):
        folder=Path(self.selected_folder.get())
        login=folder/'login.html'
        if not login.is_file():return messagebox.showwarning('تنبيه','ملف login.html غير موجود')
        try:_open_path(login)
        except Exception as exc:messagebox.showerror('المعاينة',str(exc))
    def _progress(self,i,total,name,generation=None):
        def update():
            self.progress.set(i/max(1,total)*100)
            self.log.insert('end',f'{i}/{total}  {name}\n')
        self.app.post_ui(update,generation=generation)
    def upload(self):
        if self.busy:return
        if self.app._network_write_pending:
            return messagebox.showinfo('عملية جارية','انتظر اكتمال عملية الراوتر الحالية قبل رفع القالب.')
        r=self.router_row();folder=self.selected_folder.get()
        if not r or not folder:return messagebox.showwarning('تنبيه','اختر راوتر وقالب')
        try:validate_hotspot_template(folder)
        except Exception as exc:return messagebox.showerror('قالب غير صالح',str(exc))
        remote=self.remote_folder.get().strip()
        if not remote:return messagebox.showerror('المجلد','حدد مجلد السيرفر.')
        if not messagebox.askyesno('رفع القالب','سيُحفظ محتوى المجلد «%s» احتياطيًا ثم تُرفع ملفات القالب إليه. متابعة؟' % remote):return
        backup=Path(self.db.get_setting('paths.backup'))/('before_upload_'+datetime.now().strftime('%Y%m%d_%H%M%S_%f'))
        r=dict(r);folder=str(folder)
        self.log.insert('end','بدء الرفع عبر FTP...\n');self.progress.set(0)
        operation_generation=self.app.session.generation
        def work():
            download_folder(r['host'],r['username'],r['password'],backup,remote,r['ftp_port'])
            progress=lambda i,total,name:self._progress(i,total,name,operation_generation)
            return upload_folder(r['host'],r['username'],r['password'],folder,remote,r['ftp_port'],progress)
        def ok(n):self.log.insert('end',f'تم رفع {n} ملف بنجاح.\n');self.db.log('HOTSPOT','رفع صفحات','SUCCESS',f'{n} ملف إلى {r["host"]}')
        self._transfer(work,ok)
    def backup(self):
        if self.busy:return
        if self.app._network_write_pending:
            return messagebox.showinfo('عملية جارية','انتظر اكتمال عملية الراوتر الحالية قبل تنزيل الصفحات.')
        r=self.router_row()
        if not r:return messagebox.showwarning('تنبيه','اختر راوتر')
        r=dict(r);safe_name=''.join(ch if ch not in '<>:"/\\|?*' else '_' for ch in r['name']).strip(' .') or 'router'
        base=Path(self.db.get_setting('paths.backup',str(ROOT_DIR/'backups')))/f"hotspot_{safe_name}_{datetime.now().strftime('%Y%m%d_%H%M%S')}";self.progress.set(0);self.log.insert('end','بدء تنزيل صفحات hotspot...\n')
        remote=self.remote_folder.get().strip()
        if not remote:return messagebox.showerror('المجلد','حدد مجلد السيرفر.')
        def work():return download_folder(r['host'],r['username'],r['password'],base,remote,r['ftp_port'],self._progress)
        def ok(n):self.log.insert('end',f'تم حفظ {n} ملف في {base}\n');self.db.log('HOTSPOT','نسخة احتياطية للصفحات','SUCCESS',str(base))
        self._transfer(work,ok)

    def _transfer(self,work,on_ok):
        if (self.busy or self.app._network_write_pending or not self.app.session.authenticated
                or self.app._pending_lock_message):return
        self.busy=True;self.app._network_write_pending=True
        for button in self.transfer_buttons:button.configure(state='disabled')
        def finish():
            self.busy=False;self.app._network_write_pending=False
            for button in self.transfer_buttons:button.configure(state='normal')
        def done(result):
            finish();on_ok(result)
        def failed(exc):
            finish();messagebox.showerror('نقل صفحات السيرفر',str(exc))
        self.app.safe_thread(work,done,failed)

class ReportsPage(Page):
    def __init__(self,parent,app):
        super().__init__(parent,app,'السجل والتقارير','سجل العمليات وتقارير الكروت والطباعة')
        metrics=tk.Frame(self.body,bg=COLORS['bg']);metrics.pack(fill='x')
        self.vars={k:tk.StringVar(value='0') for k in ['total','printed','unprinted','batches']}
        for i,(name,k) in enumerate([('إجمالي الكروت','total'),('الكروت المطبوعة','printed'),('غير المطبوعة','unprinted'),('الدفعات','batches')]):
            p=self.panel(metrics);p.pack(side='left',fill='x',expand=True,padx=(0 if i==0 else 7,0));self.label(p,name,muted=True,bg=COLORS['panel']).pack(anchor='w',padx=12,pady=(6,0));tk.Label(p,textvariable=self.vars[k],bg=COLORS['panel'],fg=COLORS['accent'],font=('Arial',18,'bold')).pack(anchor='w',padx=12,pady=(0,6))
        actions=self.panel();actions.pack(fill='x',pady=5);ttk.Button(actions,text='تصدير Excel',style='Green.TButton',command=self.export_excel).pack(side='left',padx=6,pady=6);ttk.Button(actions,text='تحديث',style='Dark.TButton',command=self.refresh).pack(side='left',padx=6)
        table=self.panel();table.pack(fill='both',expand=True);cols=('time','category','action','status','details');self.tree=ttk.Treeview(table,columns=cols,show='headings');
        for c,t,w in [('time','الوقت',145),('category','القسم',90),('action','العملية',125),('status','الحالة',80),('details','التفاصيل',470)]:self.tree.heading(c,text=t);self.tree.column(c,width=w,anchor='center')
        fit_table(self.tree)
        self.tree.pack(fill='both',expand=True,padx=12,pady=12)
    def refresh(self):
        st=self.db.stats();[self.vars[k].set(f'{v:,}') for k,v in st.items() if k in self.vars]
        for i in self.tree.get_children():self.tree.delete(i)
        for r in self.db.activities(1000):self.tree.insert('', 'end', values=(r['ts'].replace('T',' '),r['category'],r['action'],r['status'],r['details']))
    def export_excel(self):
        try:
            from openpyxl import Workbook
            out=Path(self.db.get_setting('paths.output',str(ROOT_DIR/'exports')))
            out.mkdir(parents=True,exist_ok=True)
            p=out/f"report_{datetime.now().strftime('%Y%m%d_%H%M%S_%f')}.xlsx"
            wb=Workbook();ws=wb.active;ws.title='Activity'
            ws.append(['time','category','action','status','details'])
            for row in self.db.activities(5000):
                ws.append([row[key] for key in ('ts','category','action','status','details')])
                # Router messages and imported log fields are data, never Excel formulas.
                for cell in ws[ws.max_row]:
                    if isinstance(cell.value,str):cell.data_type='s'
            wb.save(p)
            messagebox.showinfo('تم',f'تم حفظ التقرير:\n{p}')
        except Exception as e:messagebox.showerror('Excel',str(e))

class SettingsPage(Page):
    def __init__(self,parent,app):
        super().__init__(parent,app,'الإعدادات','إعدادات البرنامج والطباعة وقاعدة البيانات والنسخ الاحتياطي')
        nb=ttk.Notebook(self.body);nb.pack(fill='both',expand=True)
        self.tabs={}
        for key,title in [('general','عام'),('print','الطباعة'),('database','قاعدة البيانات'),('backup','النسخ الاحتياطي'),('appearance','المظهر')]:
            f=tk.Frame(nb,bg=COLORS['panel']);nb.add(f,text=title);self.tabs[key]=f
        g=self.tabs['general'];self.output_var=tk.StringVar();self.backup_var=tk.StringVar();self.templates_var=tk.StringVar()
        self._path_row(g,'مسار حفظ الملفات',self.output_var,0);self._path_row(g,'مسار النسخ الاحتياطي',self.backup_var,1);self._path_row(g,'مجلد قوالب الهوتسبوت',self.templates_var,2)
        ttk.Button(g,text='حفظ الإعدادات',style='Accent.TButton',command=self.save).grid(row=4,column=0,columnspan=3,sticky='ew',padx=10,pady=8)
        p=self.tabs['print'];self.label(p,'الطابعة الافتراضية',bg=COLORS['panel']).pack(anchor='w',padx=12,pady=(10,3));self.printer_var=tk.StringVar();ttk.Combobox(p,textvariable=self.printer_var,values=windows_printers(),state='readonly',width=45).pack(anchor='w',padx=18);ttk.Button(p,text='حفظ الطابعة الافتراضية',style='Accent.TButton',command=self.save_printer).pack(anchor='w',padx=12,pady=6);self.label(p,'حجم الورق وترتيب الكروت يتم التحكم بهما من صفحة الطباعة.',muted=True,bg=COLORS['panel']).pack(anchor='w',padx=12,pady=7)
        d=self.tabs['database'];self.label(d,f'قاعدة البيانات الحالية:\n{self.db.path}',bg=COLORS['panel'],justify='left').pack(anchor='w',padx=12,pady=10);ttk.Button(d,text='فتح مجلد قاعدة البيانات',style='Dark.TButton',command=lambda:self.open_path(self.db.path.parent)).pack(anchor='w',padx=18);ttk.Button(d,text='فحص سلامة قاعدة البيانات',style='Accent.TButton',command=self.check_database).pack(anchor='w',padx=12,pady=6);ttk.Button(d,text='فتح سجل الأعطال',style='Dark.TButton',command=lambda:self.open_path(LOG_DIR)).pack(anchor='w',padx=18)
        b=self.tabs['backup'];self.label(b,'إنشاء نسخة احتياطية من قاعدة البيانات والإعدادات.',bg=COLORS['panel']).pack(anchor='w',padx=12,pady=10);ttk.Button(b,text='إنشاء نسخة الآن',style='Accent.TButton',command=self.backup_now).pack(anchor='w',padx=18);ttk.Button(b,text='استعادة نسخة',style='Dark.TButton',command=self.restore_backup).pack(anchor='w',padx=12,pady=6)
        a=self.tabs['appearance'];self.label(a,'السمة الحالية: ألوان متناسقة — واجهة مدمجة',bg=COLORS['panel']).pack(anchor='w',padx=12,pady=10);self.label(a,f'حضرموت v{APP_VERSION} — نسخة محمولة لـWindows',muted=True,bg=COLORS['panel'],justify='left').pack(anchor='w',padx=18)
    def _path_row(self,parent,text,var,row):
        self.label(parent,text,bg=COLORS['panel']).grid(row=row,column=0,sticky='w',padx=10,pady=6);ttk.Entry(parent,textvariable=var,width=70).grid(row=row,column=1,sticky='ew',padx=8);ttk.Button(parent,text='اختيار',style='Dark.TButton',command=lambda v=var:self.choose_dir(v)).grid(row=row,column=2,padx=8);parent.grid_columnconfigure(1,weight=1)
    def refresh(self):self.output_var.set(self.db.get_setting('paths.output',str(ROOT_DIR/'exports')));self.backup_var.set(self.db.get_setting('paths.backup',str(ROOT_DIR/'backups')));self.templates_var.set(self.db.get_setting('paths.hotspot_templates',str(ROOT_DIR/'hotspot_templates')));self.printer_var.set(self.db.get_setting('print.default_printer',''))
    def choose_dir(self,var):
        p=filedialog.askdirectory(initialdir=var.get() or str(ROOT_DIR));
        if p:var.set(p)
    def save(self):
        try:
            for value in (self.output_var.get(),self.backup_var.get(),self.templates_var.get()):Path(value).mkdir(parents=True,exist_ok=True)
            self.db.set_settings({'paths.output':self.output_var.get(),'paths.backup':self.backup_var.get(),'paths.hotspot_templates':self.templates_var.get()});messagebox.showinfo('تم','تم حفظ الإعدادات')
        except Exception as exc:messagebox.showerror('فشل الحفظ',str(exc))
    def save_printer(self):
        self.db.set_setting('print.default_printer',self.printer_var.get());messagebox.showinfo('تم','تم حفظ الطابعة الافتراضية')
    def check_database(self):
        result=self.db.integrity_check();messagebox.showinfo('فحص قاعدة البيانات','النتيجة: سليمة ✓' if result=='ok' else f'نتيجة الفحص: {result}')
    def backup_now(self):
        try:
            b=Path(self.backup_var.get() or ROOT_DIR/'backups');b.mkdir(parents=True,exist_ok=True);dst=b/f"hadramout_backup_{datetime.now().strftime('%Y%m%d_%H%M%S')}.db";self.db.backup_to(dst);self.db.log('BACKUP','نسخة احتياطية','SUCCESS',str(dst));messagebox.showinfo('تم',f'تم إنشاء نسخة سليمة وفحصها:\n{dst}')
        except Exception as exc:messagebox.showerror('فشل النسخ الاحتياطي',str(exc))
    def restore_backup(self):
        if self.app._pending_jobs:
            return messagebox.showinfo('العملية جارية','انتظر اكتمال العملية الحالية قبل استعادة قاعدة البيانات.')
        p=filedialog.askopenfilename(filetypes=[('SQLite DB','*.db'),('All','*.*')]);
        if not p:return
        if messagebox.askyesno('تأكيد','سيتم استبدال قاعدة البيانات الحالية. هل تريد المتابعة؟'):
            try:
                b=Path(self.backup_var.get() or ROOT_DIR/'backups');b.mkdir(parents=True,exist_ok=True);safety=b/f"before_restore_{datetime.now().strftime('%Y%m%d_%H%M%S')}.db";self.db.backup_to(safety)
                self.db.restore_from(p);self.db.log('BACKUP','استعادة نسخة','SUCCESS',str(p));messagebox.showinfo('تم',f'تمت الاستعادة بنجاح.\nتم حفظ نسخة أمان قبل الاستعادة في:\n{safety}')
                self.app._show_login('تمت استعادة البيانات. سجّل الدخول مجددًا للمتابعة.')
            except Exception as exc:LOGGER.exception('Database restore failed');messagebox.showerror('فشل الاستعادة',str(exc))
    def open_path(self,p):
        try:_open_path(p)
        except Exception:pass

if __name__=='__main__':
    # Startup displays the GitHub activation screen even if the network is unavailable;
    # start_login and the recurring session monitor strictly enforce access.
    app=HadramoutApp()
    app.mainloop()
