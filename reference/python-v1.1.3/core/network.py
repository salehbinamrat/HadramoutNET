"""Router-backed profiles and users for Hotspot, PPPoE and User Manager 6/7.

Reads distinguish an empty service from an unavailable or forbidden service.
All mutations use the selected service's API menu and an explicit record ID.
"""
from __future__ import annotations

import re
import time
import math
from contextlib import contextmanager
from datetime import datetime

from .mikrotik import MikroTikError, RouterOSTrapError, DeliveryUncertainError
from .lifecycle import classify_userman7, empty_lifecycle, normalized_state


SERVICES = {'hotspot': 'هوتسبوت', 'userman': 'مدير المستخدمين', 'ppp': 'برودباند PPPoE'}
COLORS = {'hotspot': '#4D96FF', 'userman': '#8C6FF7', 'ppp': '#22B8A7'}


def unsupported(error):
    text=str(error).lower()
    return any(part in text for part in ('no such command', 'bad command name', 'unknown command'))


def truth(value):
    return str(value).lower() in ('true','yes','1')


def duration_seconds(value):
    text=str(value or '').strip().lower()
    if text in ('','0','0s','00:00:00','unlimited','none'):return 0
    if len(text)>128 or '-' in text:return 0
    # RouterOS exports either 1w2d03:04:05 or 1w2d3h4m5s.
    total=0.0
    for number,unit in re.findall(r'(\d+(?:\.\d+)?)([wdhms])',text):
        total+=float(number)*{'w':604800,'d':86400,'h':3600,'m':60,'s':1}[unit]
    clock=re.search(r'(\d+):(\d+):(\d+(?:\.\d+)?)',text)
    if clock:total+=float(clock[1])*3600+float(clock[2])*60+float(clock[3])
    elif text.isdigit():total=float(text)
    return int(total) if math.isfinite(total) else 0


def byte_count(value):
    """Parse RouterOS byte counters/limits without assuming plain integers.

    API replies are normally raw byte integers, but legacy User Manager builds
    and test/export data may contain suffixes such as KiB/MiB/GiB or k/M/G.
    Unknown values are treated as zero so a malformed cosmetic field can never
    mark a voucher expired by accident.
    """
    text=str(value or '').strip().lower().replace(' ','')
    if text in ('','0','0b','none','unlimited'):return 0
    if len(text)>128:return 0
    try:return max(0,int(text))
    except ValueError:pass
    try:
        number=float(text)
        return max(0,int(number)) if math.isfinite(number) else 0
    except (TypeError,ValueError,OverflowError):pass
    match=re.fullmatch(r'([0-9]+(?:\.[0-9]+)?)([kmgt]?i?b?)',text)
    if not match:return 0
    number=float(match.group(1));unit=match.group(2)
    powers={'k':1,'kb':1,'ki':1,'kib':1,'m':2,'mb':2,'mi':2,'mib':2,
            'g':3,'gb':3,'gi':3,'gib':3,'t':4,'tb':4,'ti':4,'tib':4,'b':0,'':0}
    power=powers.get(unit)
    if power is None:return 0
    result=number*(1024**power)
    return int(result) if math.isfinite(result) else 0


def numeric_value(value):
    """Parse legacy User Manager numeric/accounting fields such as money-used.

    The web UI can format money with decimal places or a trailing currency label.
    Only the first numeric token is relevant to lifecycle classification.
    """
    text=str(value or '').strip().replace(',','')
    if not text or len(text)>128:return 0.0
    match=re.search(r'-?\d+(?:\.\d+)?',text)
    if not match:return 0.0
    try:
        number=float(match.group(0))
        return number if math.isfinite(number) else 0.0
    except ValueError:return 0.0


def remote_password_available(record):
    """An omitted/null field and possible masks are not verified credentials.

    All-star values are ambiguous, not proof of masking. They can still be
    printed from the original local batch, whose stored value is authoritative.
    An explicitly returned empty string is distinct from an absent field.
    """
    value=record.get('password')
    return (isinstance(value,str) and value.casefold()!='<hidden>' and
            not (len(value)>=3 and set(value)=={'*'}))


def profile_defaults(service, profile):
    uptime=int(profile.get('uptime_seconds',duration_seconds(profile.get('uptime')))) if service=='userman' else 0
    seconds=duration_seconds(profile.get('validity')) if service=='userman' else 0
    capacity=int(profile.get('capacity') or 0) if service=='userman' else 0
    return {'create.profile':profile['name'], 'create.server':'all',
            'create.price':float(profile.get('price') or 0),
            'create.limit_bytes':capacity/(1024**2), 'create.limit_unit':'MB',
            'create.limit_uptime_h':uptime//3600, 'create.limit_uptime_m':uptime%3600//60,
            'create.validity_days':seconds//86400,
            'create.card_format':'code_password' if service=='ppp' else 'code_only'}


def _minimum_positive(values):
    positive=[int(v) for v in values if str(v).isdigit() and int(v)>0]
    return min(positive) if positive else 0


class NetworkGateway:
    def __init__(self, api):
        self.api=api
        self.um_path=None
        self.um_version=None
        self._read_reconnects=0
        self._count_support={}
        # Heavy User Manager voucher scans are performed only on demand.  Keep
        # their already-classified rows in this gateway so clicking a dashboard
        # status card does not download 10k+ users a second time.
        self._voucher_status_cache={}
        self._voucher_status_clock=None
        self._voucher_lifecycle=None
        # True when the cached rows came from the same authoritative bulk scan
        # that produced the counters. Summary-only fallbacks set this False.
        self._voucher_status_cache_authoritative=False
        # The .proplist that this RouterOS 6 build accepted, once discovered.
        # ``None`` means "not tried yet"; ``''`` means "no projection works".
        self._userman6_fields=None

    def user_manager(self):
        if self.um_path:return self.um_path
        try:
            rows=self.api.command('/user-manager/profile/print')
            self.um_path='/user-manager';self.um_version=7
        except MikroTikError as exc:
            if not unsupported(exc):raise
            rows=self.api.command('/tool/user-manager/profile/print')
            self.um_path='/tool/user-manager';self.um_version=6
        self._um_profiles=rows
        return self.um_path

    def profile_path(self, service):
        if service=='hotspot':return '/ip/hotspot/user/profile'
        if service=='ppp':return '/ppp/profile'
        if service=='userman':return self.user_manager()+'/profile'
        raise ValueError('نوع الخدمة غير صالح.')

    def user_path(self, service):
        if service=='hotspot':return '/ip/hotspot/user'
        if service=='ppp':return '/ppp/secret'
        if service=='userman':return self.user_manager()+'/user'
        raise ValueError('نوع الخدمة غير صالح.')

    def profiles(self, service):
        path=self.profile_path(service)
        if service=='userman' and hasattr(self,'_um_profiles'):
            rows=self._um_profiles;del self._um_profiles
        else:rows=self.api.command(path+'/print')
        limits=[];links=[];limit_error=''
        if service=='userman':
            limitation_path=self.um_path+('/limitation' if self.um_version==7 else '/profile/limitation')
            link_path=self.um_path+('/profile-limitation' if self.um_version==7 else '/profile/profile-limitation')
            try:
                limits=self.api.command(limitation_path+'/print')
                links=self.api.command(link_path+'/print')
            except MikroTikError as exc:limit_error=str(exc)
        # Index links and limit aliases once; avoid rescanning both definition
        # tables for every profile on routers with many packages.
        links_by_profile={};limits_by_alias={}
        for index,link in enumerate(links):
            links_by_profile.setdefault(link.get('profile'),[]).append((index,link))
        for index,limit in enumerate(limits):
            for alias in set((limit.get('name'),limit.get('.id'))):
                limits_by_alias.setdefault(alias,[]).append((index,limit))
        result=[]
        for row in rows:
            if not row.get('name'):continue
            matched={i:link for alias in set((row['name'],row.get('.id')))
                     for i,link in links_by_profile.get(alias,[])}
            matches=[matched[i] for i in sorted(matched)]
            chosen={i:limit for link in matches
                    for i,limit in limits_by_alias.get(link.get('limitation'),[])}
            selected=[chosen[i] for i in sorted(chosen)]
            rate=row.get('rate-limit','')
            if selected:
                rate=' | '.join('/'.join((limit.get('rate-limit-rx','0'),limit.get('rate-limit-tx','0'))) for limit in selected)
            result.append({'id':row.get('.id',row['name']), 'name':row['name'], 'raw':dict(row),
                'rate':rate, 'shared':row.get('shared-users',row.get('override-shared-users','')),
                'uptime':' | '.join(limit.get('uptime-limit','0s') for limit in selected) if selected else row.get('session-timeout',''),
                'capacity':_minimum_positive(limit.get('transfer-limit',0) for limit in selected),
                'uptime_seconds':_minimum_positive(duration_seconds(limit.get('uptime-limit')) for limit in selected),
                'download_limit':_minimum_positive(limit.get('download-limit',0) for limit in selected),
                'upload_limit':_minimum_positive(limit.get('upload-limit',0) for limit in selected),
                'validity':row.get('validity',''), 'price':row.get('price',''),
                'limits':selected,'links':matches,'limit_error':limit_error,
                'um_version':self.um_version if service=='userman' else None})
        return result

    def reset_read_budget(self):
        """At most one reconnect for an entire UI operation, never for writes."""
        self._read_reconnects=0

    @contextmanager
    def _slow_api_read(self, idle_timeout=75.0, command_timeout=300.0):
        """Temporarily extend both RouterOS command and socket-idle deadlines.

        ``RouterConfig.timeout`` is also the socket idle timeout used by the
        buffered API reader.  The previous hotfix only raised
        ``command_timeout`` to 120 seconds, but the socket still aborted after
        about 8 seconds while a 20k+ User Manager query was being prepared.
        """
        cfg=getattr(self.api,'cfg',None)
        if cfg is None:
            yield
            return
        old_idle=float(getattr(cfg,'timeout',8.0))
        old_command=float(getattr(cfg,'command_timeout',45.0))
        reader=getattr(self.api,'_reader',None)
        old_reader_idle=getattr(reader,'idle_timeout',None) if reader is not None else None
        cfg.timeout=max(old_idle,float(idle_timeout))
        cfg.command_timeout=max(old_command,float(command_timeout))
        if reader is not None and hasattr(reader,'idle_timeout'):
            reader.idle_timeout=max(float(old_reader_idle or 0.0),float(idle_timeout))
        try:
            yield
        finally:
            cfg.timeout=old_idle
            cfg.command_timeout=old_command
            reader=getattr(self.api,'_reader',None)
            if reader is not None and hasattr(reader,'idle_timeout'):
                reader.idle_timeout=old_idle

    def _read_retry(self, operation):
        try:
            return operation()
        except MikroTikError:
            if (getattr(self.api,'sock',None) is None and hasattr(self.api,'connect')
                    and self._read_reconnects < 1):
                self._read_reconnects+=1
                self.api.connect()
                return operation()
            raise

    def count(self, path, queries=None):
        reply=self._read_retry(lambda:self.api.command(path+'/print', {'count-only':''}, queries=queries, return_done=True))
        if isinstance(reply,tuple) and len(reply)==2:rows,done=reply
        else:rows,done=reply or [],{}
        value=done.get('ret',done.get('count')) if isinstance(done,dict) else None
        if value is None and rows and isinstance(rows[0],dict):value=rows[0].get('ret',rows[0].get('count'))
        try:
            count=int(value)
            return count if count>=0 else None
        except (TypeError,ValueError,OverflowError):return None

    def _count_flexible(self, path, queries=None):
        """Fallback for count syntax only, never retry denied/absent menus.

        Remember count-only support for this connection's menu. A failed count
        must not cause repeated full ID-table reads on every dashboard click.
        """
        if self._count_support.get(path,True):
            try:
                value=self.count(path,queries)
                if value is not None:return value
            except MikroTikError as exc:
                text=str(exc).lower()
                if not any(part in text for part in (
                        'expected end of command','unknown parameter','unknown argument',
                        'no such argument','invalid argument','unsupported count')):
                    raise
            self._count_support[path]=False
        rows=self._read_retry(lambda:self.api.command(path+'/print',{'.proplist':'.id'},queries))
        return len(rows)

    def _safe_metric(self, operation, label, warnings):
        try:return operation()
        except MikroTikError as exc:
            warnings.append('%s: %s' % (label,str(exc)))
            return None

    def _basic_service_stats(self, service):
        """Read only server-side counters for dashboard statistics.

        Older builds downloaded every active Hotspot/PPP row just to collect
        usernames even though the dashboard never uses those names. On busy
        routers that creates needless API traffic and CPU work. Keep the
        dashboard path count-only; detailed user lists are fetched on demand.
        """
        warnings=[];path=self.user_path(service)
        users=self._safe_metric(lambda:self._count_flexible(path),'المستخدمون',warnings)
        disabled=self._safe_metric(lambda:self._count_flexible(path,['?disabled=yes']),'المعطلون',warnings)
        if disabled is None:
            disabled=self._safe_metric(lambda:self._count_flexible(path,['?disabled=true']),'المعطلون',warnings)
        active_path='/ip/hotspot/active' if service=='hotspot' else '/ppp/active'
        active=self._safe_metric(lambda:self._count_flexible(active_path),'المتصلون',warnings)
        return users,disabled,active,None,warnings

    @staticmethod
    def _int_value(value):
        try:return int(float(str(value or '0').strip()))
        except (TypeError,ValueError):return 0

    @staticmethod
    def _empty_lifecycle(source=''):
        return empty_lifecycle(source)

    def _hotspot_lifecycle(self):
        """Classify HotSpot vouchers from RouterOS usage counters and limits.

        valid   = never used and still enabled
        active  = used at least once and has remaining time/data
        expired = configured uptime/data limit has been exhausted

        Disabled users are reported separately instead of being mislabeled as
        expired, because disabling is an administrative state rather than
        consumption of the voucher allowance.
        """
        fields='.id,name,disabled,uptime,bytes-in,bytes-out,limit-uptime,limit-bytes-in,limit-bytes-out,limit-bytes-total'
        rows=self._read_retry(lambda:self.api.command('/ip/hotspot/user/print',{'.proplist':fields}))
        out=self._empty_lifecycle('hotspot-user-counters');out['complete']=True;out['total']=len(rows)
        for row in rows:
            if truth(row.get('disabled','no')):
                out['disabled']+=1;continue
            uptime=duration_seconds(row.get('uptime'))
            bin_=self._int_value(row.get('bytes-in'));bout=self._int_value(row.get('bytes-out'))
            total=bin_+bout
            lim_uptime=duration_seconds(row.get('limit-uptime'))
            lim_in=self._int_value(row.get('limit-bytes-in'));lim_out=self._int_value(row.get('limit-bytes-out'))
            lim_total=self._int_value(row.get('limit-bytes-total'))
            expired=((lim_uptime>0 and uptime>=lim_uptime) or
                     (lim_in>0 and bin_>=lim_in) or
                     (lim_out>0 and bout>=lim_out) or
                     (lim_total>0 and total>=lim_total))
            if expired:out['expired']+=1
            elif uptime>0 or total>0:out['active']+=1
            else:out['valid']+=1
        return out

    def _cache_voucher_status_rows(self, rows_by_status, authoritative=True):
        rows_by_status=rows_by_status or {}
        self._voucher_status_cache={key:list(rows_by_status.get(key,[]) or []) for key in ('active','expired')}
        self._voucher_status_clock=time.monotonic()
        self._voucher_status_cache_authoritative=bool(authoritative)

    # A classification is reused for this long before the detail window will
    # consider downloading the user table again.
    VOUCHER_CACHE_TTL_SECONDS=300.0
    def _voucher_status_cache_fresh(self):
        """True when the cached classification is recent enough to reuse."""
        stamp=self._voucher_status_clock
        return stamp is not None and 0 <= time.monotonic()-stamp < self.VOUCHER_CACHE_TTL_SECONDS

    def invalidate_voucher_status_cache(self):
        self._voucher_status_cache={}
        self._voucher_status_clock=None
        self._voucher_lifecycle=None
        self._voucher_status_cache_authoritative=False

    @staticmethod
    def _voucher_row(row, status, reason='', profile='', expiry_source=''):
        """Compact, password-free row used by the status detail window.

        RouterOS 6's User Manager web UI names the cumulative counters
        ``Total Download Used`` / ``Total Upload Used``. Some API builds expose
        the shorter historical aliases. Normalize both without caching passwords.
        """
        name=str(row.get('username',row.get('name',row.get('user',''))) or '').strip()
        download=row.get('total-download-used',row.get('download-used',row.get('total-download','0')))
        upload=row.get('total-upload-used',row.get('upload-used',row.get('total-upload','0')))
        last_seen=(row.get('last-seen') or row.get('last-used-ip') or row.get('last-used-mac') or '')
        result={
            '_id':row.get('.id',''), '_name':name, '_profile':profile or row.get('actual-profile',''),
            '_status':status, '_reason':reason, 'last-seen':last_seen,
            'uptime-used':row.get('uptime-used',row.get('total-uptime','')),
            'download-used':download, 'upload-used':upload,
            'money-used':row.get('money-used',''), 'time-left':row.get('time-left',row.get('total-time-left','')),
            'transfer-limit':row.get('transfer-limit',''),
            'active-sessions':row.get('active-sessions','0'),
            '_started':status=='active' or NetworkGateway._userman6_started(row),
        }
        if expiry_source:
            result['_expiry_source']=expiry_source
        return result

    @classmethod
    def _userman6_started(cls, row):
        """Return True only when a legacy User Manager row shows real usage.

        RouterOS 6 can omit ``actual-profile`` after a voucher exhausts its
        allowance.  Usage evidence is therefore kept independent from the
        profile field so a missing profile is not mistaken for an unused row.
        """
        uptime=duration_seconds(row.get('uptime-used'))
        download=byte_count(row.get('total-download-used',row.get('download-used')))
        upload=byte_count(row.get('total-upload-used',row.get('upload-used')))
        money=numeric_value(row.get('money-used'))
        active_now=(truth(row.get('active','no')) or cls._int_value(row.get('active-sessions'))>0)
        last_used=bool(str(row.get('last-used-ip','') or '').strip() or str(row.get('last-used-mac','') or '').strip())
        last_seen=str(row.get('last-seen','') or '').strip().lower()
        if last_seen not in ('','never','unknown'):
            last_used=True
        return bool(money>0 or uptime>0 or download>0 or upload>0 or active_now or last_used)

    def _userman_lifecycle_v7(self, users_total=None, disabled_total=None, allow_fallback=True):
        """Read two minimal tables once, then deduplicate by user in O(U+P).

        RouterOS 7 exposes the authoritative user-profile state: running active,
        running, or used.  Counts are therefore user counts, never assignment
        counts, and the same classification is cached for the detail window.
        """
        users=self._read_retry(lambda:self.api.command(
            self.um_path+'/user/print',{'.proplist':'.id,name,disabled,transfer-limit,total-download-used,total-upload-used,download-used,upload-used'}))
        assignments=self._read_retry(lambda:self.api.command(
            self.um_path+'/user-profile/print',{'.proplist':'user,state'}))
        compatibility=False
        # Only an *unprojected* re-read can reveal a state the projection
        # omitted, so it is worth its cost only when the projected read came
        # back with rows whose states are all unrecognized.  An empty
        # assignment table is a legitimate result (every voucher unused) and
        # must not trigger a second full download of the whole table.
        if (allow_fallback and assignments and not any(
                normalized_state(row.get('state')) in ('running','running active','used') for row in assignments)):
            fallback=self._read_retry(lambda:self.api.command(self.um_path+'/user-profile/print'))
            if fallback:assignments=fallback
            compatibility=True
        result=classify_userman7(users,assignments)
        if compatibility:result['source']+='-compat'

        aliases={};accounts={}
        for index,row in enumerate(users):
            name=str(row.get('name',row.get('username','')) or '').strip()
            ident=str(row.get('.id','') or '').strip();key=name or ident or ('missing-%s' % index)
            accounts[key]=row
            if name:aliases[name]=key
            if ident:aliases.setdefault(ident,key)
        states={key:[] for key in accounts}
        for item in assignments:
            key=aliases.get(str(item.get('user','') or '').strip())
            if key is not None:states.setdefault(key,[]).append(normalized_state(item.get('state')))
        detail={key:[] for key in ('active','expired')}
        for key,row in accounts.items():
            if truth(row.get('disabled','no')):
                status,reason='disabled','معطل إداريًا'
            else:
                values=states.get(key,[])
                if 'running active' in values:status,reason='active','الباقة الحالية مفعّلة'
                elif 'running' in values:status,reason='valid','الباقة جاهزة للاستخدام'
                elif values and all(value=='used' for value in values):
                    limit=byte_count(row.get('transfer-limit'))
                    used=(byte_count(row.get('total-download-used',row.get('download-used'))) +
                          byte_count(row.get('total-upload-used',row.get('upload-used'))))
                    if limit>0 and used>=limit:
                        status,reason='expired','استُنفد حد إجمالي نقل البيانات'
                    else:
                        # A used profile can expire by time, administrative
                        # replacement or another limit: never infer transfer use.
                        status,reason='unclassified','لم يثبت استنفاد Transfer Limit'
                        result['expired']-=1
                        result['unclassified']+=1
                else:status,reason='unclassified','لا توجد حالة باقة مؤكدة'
            if status in detail:detail[status].append(self._voucher_row(row,status,reason,expiry_source='transfer-limit' if status=='expired' else ''))
        if result['unclassified']:
            result['reason']=((result.get('reason') or '')+' بعض سجلات User Manager 7 لا تثبت استنفاد Transfer Limit؛ لم تُعتبر منتهية.').strip()
        self._cache_voucher_status_rows(detail)
        return result

    def _userman6_profile_limit_map(self):
        """Return effective legacy User Manager limits keyed by profile name/id.

        Only the positive Transfer Limit matters for expired vouchers. Read
        small profile/link definition tables once instead of session histories.
        No time, upload-only or download-only allowance can mark expiration.
        """
        profile_rows=self._read_retry(lambda:self.api.command(
            self.um_path+'/profile/print',{'.proplist':'.id,name'}))
        limitation_rows=self._read_retry(lambda:self.api.command(
            self.um_path+'/profile/limitation/print',{'.proplist':'.id,name,transfer-limit'}))
        link_rows=self._read_retry(lambda:self.api.command(
            self.um_path+'/profile/profile-limitation/print',{'.proplist':'profile,limitation'}))
        limits_by_alias={}
        for row in limitation_rows:
            for alias in (row.get('.id'),row.get('name')):
                if alias:limits_by_alias[str(alias)]=row
        profile_by_alias={}
        definitions={}
        for row in profile_rows:
            name=str(row.get('name','') or '').strip();ident=str(row.get('.id','') or '').strip()
            key=name or ident
            if not key:continue
            info={'name':name or ident,'transfer_limit':0}
            definitions[key]=info
            if name:profile_by_alias[name]=key
            if ident:profile_by_alias[ident]=key
        buckets={key:[] for key in definitions}
        for link in link_rows:
            key=profile_by_alias.get(str(link.get('profile','') or ''))
            limitation=limits_by_alias.get(str(link.get('limitation','') or ''))
            if key and limitation:buckets[key].append(limitation)
        for key,items in buckets.items():
            definitions[key]['transfer_limit']=_minimum_positive(byte_count(item.get('transfer-limit')) for item in items)
        expanded={}
        for alias,key in profile_by_alias.items():expanded[alias]=definitions[key]
        return expanded

    def _userman6_user_inventory(self):
        """Read the legacy User Manager user table with real lifecycle fields.

        Only the usage counters and Transfer Limit are needed for expiration;
        deliberately omit Total Time Left and other non-transfer limits from
        heavy projected user-table reads. Some v6 versions silently omit dynamic
        fields from ``.proplist``, so validate the projected result and fall back
        to one complete read if needed.
        """
        path=self.um_path+'/user/print'
        property_sets=(
            # Matches the fields visible in the legacy User Manager Advanced Search.
            '.id,username,name,disabled,incomplete,actual-profile,money-used,uptime-used,total-download-used,total-upload-used,transfer-limit,last-used-ip,last-used-mac,active,active-sessions',
            # Older API aliases used by some RouterOS 6 builds.
            '.id,username,name,disabled,incomplete,actual-profile,money-used,uptime-used,download-used,upload-used,transfer-limit,last-seen,active',
            # Minimal state set if optional accounting fields are rejected.
            '.id,username,name,disabled,actual-profile,money-used,uptime-used,transfer-limit',
        )
        state_fields={
            'actual-profile','money-used','uptime-used','total-download-used','total-upload-used',
            'download-used','upload-used','time-left','total-time-left','transfer-limit','uptime-limit'
        }
        last=None
        # A 24k+ User Manager table can legitimately spend a long time before the
        # first reply. This read is background-only and never runs on normal page
        # navigation, so correctness is preferred over an aggressive timeout.
        with self._slow_api_read(idle_timeout=120.0,command_timeout=600.0):
            # A build that rejected a projection will reject it again.  Remember
            # the one that worked so later refreshes do not pay for the failed
            # attempts, each of which downloads the whole table.
            if self._userman6_fields is not None:
                property_sets=((self._userman6_fields,) if self._userman6_fields == '' else
                    (self._userman6_fields,)+tuple(
                    fields for fields in property_sets if fields!=self._userman6_fields)
                )
            for fields in property_sets:
                try:
                    rows=self._read_retry(lambda f=fields:self.api.command(path,{'.proplist':f} if f else None))
                except MikroTikError as exc:
                    last=exc;low=str(exc).lower()
                    if not any(part in low for part in (
                            'unknown parameter','unknown argument','no such argument',
                            'expected end of command','invalid argument','invalid value')):
                        raise
                    continue
                # Do not accept the ROS6 projection bug where all users arrive but
                # every dynamic lifecycle property is silently absent.
                if not rows or any(any(field in row for field in state_fields) for row in rows):
                    self._userman6_fields=fields
                    return rows
                # A successful projected response with no state fields exposes
                # the silent projection bug. More projections just download the
                # same table again; go directly to the full compatibility read.
                if not fields:
                    return rows
                break

            # Final compatibility path: the legacy web UI clearly has these fields
            # even on builds that omit them from .proplist. Read the full rows once,
            # classify immediately, then keep only sanitized compact rows in cache.
            try:
                rows=self._read_retry(lambda:self.api.command(path))
                if rows:
                    self._userman6_fields=''
                    return rows
            except MikroTikError as exc:
                last=exc
        if last:raise last
        return []

    def _userman_lifecycle_v6(self):
        """Classify RouterOS 6 User Manager vouchers by *usage lifecycle*.

        The dashboard already has a separate "connected now" counter, therefore
        "active voucher" here means *started/used and still has allowance*, not
        merely a currently connected session.  This mirrors the legacy User
        Manager Advanced Search fields visible on RouterOS 6.49.x:

        valid   = has Actual Profile and has not started usage yet
        active  = usage/Money Used exists and the card still has time/data left
        expired = started card demonstrably exhausted positive Transfer Limit

        Disabled/incomplete/unassigned accounts remain outside the three counts.
        """
        rows=self._userman6_user_inventory()
        out=self._empty_lifecycle('user-manager-6-usage-lifecycle');out['complete']=True;out['total']=len(rows)
        out['updated_at']=datetime.now().isoformat(timespec='seconds')
        out['estimated']=False;out['legacy']=True;out['limits_checked']=False
        limit_map={};limit_warning=''
        try:
            limit_map=self._userman6_profile_limit_map();out['limits_checked']=True
        except MikroTikError as exc:
            # Per-user fields from Advanced Search still let us classify most cards.
            limit_warning=str(exc)

        detail={key:[] for key in ('active','expired')}
        lifecycle_keys={
            'actual-profile','money-used','uptime-used','total-download-used','total-upload-used',
            'download-used','upload-used','time-left','total-time-left','transfer-limit','uptime-limit'
        }
        observed_state=False
        for row in rows:
            profile=str(row.get('actual-profile','') or '').strip()
            if any(key in row for key in lifecycle_keys):observed_state=True
            if truth(row.get('disabled','no')):
                status,reason='disabled','معطل إداريًا'
            elif truth(row.get('incomplete','no')):
                status,reason='unclassified','حساب غير مكتمل في مدير المستخدمين'
            else:
                download=byte_count(row.get('total-download-used',row.get('download-used')))
                upload=byte_count(row.get('total-upload-used',row.get('upload-used')))
                started=self._userman6_started(row)
                profile_limits=limit_map.get(profile,{}) if profile else {}
                transfer_limit=(byte_count(row.get('transfer-limit')) or
                                int(profile_limits.get('transfer_limit',0) or 0))
                expire_reason='';expiry_source=''
                # The only admissible expiry proof is a positive transfer
                # allowance actually exhausted by the two usage counters.
                # Time Left, uptime, separate download/upload limits and missing
                # actual-profile must NEVER mark a voucher expired or deletable.
                if started and transfer_limit>0 and download+upload>=transfer_limit:
                    expire_reason='استُنفد حد إجمالي نقل البيانات'
                    expiry_source='transfer-limit'

                if expire_reason:
                    status,reason='expired',expire_reason
                elif started and profile:
                    status,reason='active','بدأ استخدام الكرت وما زال لديه رصيد/وقت'
                elif (not started) and profile:
                    status,reason='valid','صالح وجاهز للاستخدام ولم يبدأ بعد'
                elif started and not profile:
                    status,reason='unclassified','حساب مستخدم بلا باقة فعلية؛ لا يوجد إثبات باستنفاد Transfer Limit'
                else:
                    status,reason='unclassified','لا توجد باقة فعلية ولم يبدأ استخدام مؤكد'
            out[status]+=1
            if status in detail:
                detail[status].append(self._voucher_row(
                    row,status,reason,profile,expiry_source if status=='expired' else ''))

        classified=out['valid']+out['active']+out['expired']
        non_admin=max(0,out['total']-out['disabled']-out.get('incomplete',0))
        if out['total']>0 and (not observed_state or (classified==0 and out['unclassified']>=non_admin)):
            # Do not invent active/valid counts from the old ``active`` flag: the
            # user's requested semantics are usage lifecycle, not online sessions.
            out['complete']=False
            out['reason']=(
                'أعاد RouterOS 6 المستخدمين دون بيانات المبلغ المستهلك ووقت الاستخدام والباقة الفعلية وحدودها. '
                'تعذر تصنيف الحالات بدقة؛ لم يستبدل البرنامج البيانات بأصفار مضللة.')
            self._cache_voucher_status_rows(detail)
            return out

        self._cache_voucher_status_rows(detail)
        note=('في RouterOS 6: الكرت المنتهي هو الذي استنفد Transfer Limit موجباً فقط؛ '
              'لا يؤدي انتهاء الوقت أو حذف الباقة وحده إلى اعتباره منتهياً.')
        if limit_warning:
            note+=' تعذر قراءة تعريفات حدود الباقات (%s)، فاستُخدمت الحدود الموجودة على المستخدم مباشرة.' % limit_warning
        out['reason']=note
        return out

    def _scan_voucher_lifecycle(self, force=False):
        if (not force and self._voucher_status_cache_authoritative and
                self._voucher_lifecycle is not None and self._voucher_status_cache_fresh()):
            return dict(self._voucher_lifecycle)
        self.invalidate_voucher_status_cache()
        result = self._userman_lifecycle_v7() if self.um_version == 7 else self._userman_lifecycle_v6()
        if result.get('complete'):
            self._voucher_lifecycle = dict(result)
        else:
            self.invalidate_voucher_status_cache()
        return result

    def voucher_status_rows(self, status):
        """Return rows behind a lifecycle card without heavy filtered scans.

        An empty authoritative cache means the count is genuinely zero. Older
        builds treated that as a cache miss and launched ``where actual-profile``
        against all 24k users, which is why the detail window could end in a
        RouterOS response timeout.

        A recent authoritative classification is reused as-is: opening the
        active and expired windows in a row must not download the whole user
        table twice.
        """
        if status not in ('active','expired'):
            raise ValueError('حالة كروت غير صالحة.')
        self._read_retry(self.user_manager)
        result = self._scan_voucher_lifecycle()
        if not result.get('complete'):
            raise MikroTikError(result.get('reason') or 'لم تكتمل قراءة موثوقة لحالة الكروت.')
        return list(self._voucher_status_cache.get(status,[]))

    def purge_expired_vouchers(self, rows=None):
        """Delete ONLY vouchers with positive exhausted Transfer Limit.

        A fresh authoritative scan is mandatory. Every write is one confirmed
        record ID and is never retried after an uncertain reply. UI confirmation
        is required before invoking the operation.
        """
        self._read_retry(self.user_manager)
        if rows is None:
            rows=self.voucher_status_rows('expired')
        rows=list(rows or [])
        # The window may have stayed open while a user was renewed. Reclassify
        # before deletion and intersect by BOTH id and name; never trust UI rows
        # or a stale list as authority to delete an account.
        current_life=self._scan_voucher_lifecycle(force=True)
        if not current_life.get('complete') or not self._voucher_status_cache_authoritative:
            raise MikroTikError('لا يمكن تنظيف الكروت المنتهية قبل اكتمال قراءة موثوقة لحالات مدير المستخدمين.')
        confirmed_rows={(str(r.get('_id','')),str(r.get('_name',''))):r
                        for r in self._voucher_status_cache.get('expired',[])}
        path=self.um_path+'/user'
        seen=set();targets=[];skipped=0;verification_skipped=0;warnings=[]
        for row in rows:
            ident=str(row.get('_id',row.get('.id','')) or '').strip()
            name=str(row.get('_name','') or '')
            confirmed=confirmed_rows.get((ident,name))
            if (not ident or ident in seen or confirmed is None or
                    row.get('_status')!='expired' or confirmed.get('_expiry_source')!='transfer-limit'):
                skipped+=1
                continue
            seen.add(ident);targets.append((ident,name,confirmed))
        removed=0;errors=[];removed_ids=[]
        for index,(ident,name,row) in enumerate(targets):
            # One authoritative complete re-scan above already confirmed both
            # identity and exhausted positive Transfer Limit. Never perform
            # per-card missing-profile lookups (which caused severe latency).
            try:
                self.api.command(path+'/remove',{'.id':ident})
                removed+=1;removed_ids.append(ident)
            except Exception as exc:
                errors.append({'id':ident,'name':name,'error':str(exc)[:240]})
                if getattr(self.api,'sock',True) is None:
                    # A broken stream may conceal the outcome of this write;
                    # do not reconnect/replay it or attempt the remaining IDs.
                    skipped+=len(targets)-index-1
                    break
        # Any successful or failed write can make the old classification stale.
        self.invalidate_voucher_status_cache()
        return {'requested':len(targets),'removed':removed,'failed':len(errors),
                'skipped':skipped+verification_skipped,'verification_skipped':verification_skipped,
                'warnings':warnings,'errors':errors,'removed_ids':removed_ids}

    def _service_lifecycle(self,service,users_total=None,disabled_total=None,allow_fallback=True):
        if service=='hotspot':return self._hotspot_lifecycle()
        if service=='userman':
            self._read_retry(self.user_manager)
            return self._scan_voucher_lifecycle()
        return self._empty_lifecycle('not-applicable')

    def _detect_userman_count(self):
        """Detect UM generation with a cheap user count and reuse the result."""
        if self.um_path:
            return self._count_flexible(self.um_path+'/user')
        attempts=(('/user-manager',7),('/tool/user-manager',6))
        last=None
        for path,version in attempts:
            try:
                total=self._count_flexible(path+'/user')
                self.um_path=path;self.um_version=version
                return total
            except MikroTikError as exc:
                last=exc
                if not unsupported(exc):raise
        if last:raise last
        raise MikroTikError('مدير المستخدمين غير متوفر')

    def _userman_stats(self):
        """Read User Manager using count-only requests wherever possible."""
        warnings=[]
        try:users=self._read_retry(self._detect_userman_count)
        except MikroTikError as exc:
            return None,None,None,None,None,['مدير المستخدمين: '+str(exc)]
        path=self.um_path+'/user'
        disabled=self._safe_metric(lambda:self._count_flexible(path,['?disabled=yes']),'المعطلون',warnings)
        if disabled is None:
            disabled=self._safe_metric(lambda:self._count_flexible(path,['?disabled=true']),'المعطلون',warnings)
        session_path=self.um_path+'/session'
        sessions=self._safe_metric(lambda:self._count_flexible(session_path),'إجمالي الجلسات',warnings)
        active=self._safe_metric(lambda:self._count_flexible(session_path,['?active=yes']),'الجلسات النشطة',warnings)
        if active is None:
            active=self._safe_metric(lambda:self._count_flexible(session_path,['?active=true']),'الجلسات النشطة',warnings)
        return users,disabled,active,None,sessions,warnings

    def catalog(self, include_profiles=True, include_interfaces=True, include_servers=True,
                include_hotspot_lifecycle=False, allow_state_fallback=True,
                include_stats=True, include_lifecycle=True, include_resource=True):
        """Build router data with optional heavy statistics/lifecycle reads.

        Package/profile discovery is latency-sensitive in the desktop UI.  The
        caller can now fetch profiles first, show them immediately, then refresh
        dashboard counters independently without making the user wait for every
        RouterOS statistic.  Defaults preserve the full historical behaviour.
        """
        result={'services':{},'resource':{},'errors':[],'servers':[],'interfaces':[],
                'updated_at':datetime.now().isoformat(timespec='seconds'),'lightweight':not include_profiles}
        if include_resource:
            try:result['resource']=self._read_retry(self.api.resource)
            except MikroTikError as exc:result['errors'].append('بيانات الجهاز: '+str(exc))
        for service in SERVICES:
            state={'available':False,'profiles':[],'error':'','users':None,'active':None,'active_names':None,
                   'disabled':None,'sessions':None,'lifecycle':self._empty_lifecycle(),'warnings':[]}
            result['services'][service]=state
            # Read counters independently from profiles. A profile permission or
            # limitation error must not blank the dashboard statistics.  During
            # the fast profile pass these calls are intentionally skipped.
            if include_stats:
                try:
                    if service=='userman':
                        users,disabled,active,names,sessions,stat_warnings=self._userman_stats()
                        state['sessions']=sessions
                    else:users,disabled,active,names,stat_warnings=self._basic_service_stats(service)
                    state.update(users=users,disabled=disabled,active=active,active_names=names)
                    state['warnings'].extend(stat_warnings)
                    # Any successfully read metric means the section is reachable.
                    if any(value is not None for value in (users,disabled,active,names)):state['available']=True
                    if service=='userman' and self.um_version:state['um_version']=self.um_version
                except MikroTikError as exc:
                    state['warnings'].append('تعذرت قراءة الإحصاءات: '+str(exc))
            if include_lifecycle and (service=='userman' or (service=='hotspot' and include_hotspot_lifecycle)):
                try:
                    state['lifecycle']=self._service_lifecycle(
                        service,state.get('users'),state.get('disabled'),allow_state_fallback)
                    if state['lifecycle'].get('complete'):state['available']=True
                    if state['lifecycle'].get('unclassified'):
                        state['warnings'].append('كروت غير مصنفة: %s' % state['lifecycle']['unclassified'])
                except MikroTikError as exc:
                    state['warnings'].append('حالة الكروت: '+str(exc))
            if include_profiles:
                try:
                    # Bind the loop variable now: the lambda is handed to a
                    # retry helper, so a future change that defers the call
                    # must not silently read the last service in SERVICES.
                    state['profiles']=self._read_retry(
                        lambda service=service:self.profiles(service));state['available']=True
                    if service=='userman':state['um_version']=self.um_version
                except MikroTikError as exc:
                    message=('القسم غير متوفر على هذا الراوتر.' if unsupported(exc) else 'تعذرت قراءة الباقات: '+str(exc))
                    state['error']=message
                    if state['users'] is None and unsupported(exc):state['available']=False
            if state['users'] is None and not state['error'] and not state['warnings']:
                state['warnings'].append('تعذرت قراءة إحصاءات هذا القسم.')
        # "Connected now" on the dashboard means every client session on this
        # router, not User Manager accounting sessions. User Manager can back
        # Hotspot/PPPoE through RADIUS, so adding UM sessions would double-count
        # those same clients. Count the NAS live tables instead.
        live_sources={}
        for service in ('hotspot','ppp'):
            value=result['services'].get(service,{}).get('active')
            live_sources[service]=value
        available_live=[int(value) for value in live_sources.values() if value is not None]
        result['active_clients']=sum(available_live) if available_live else None
        result['active_clients_sources']=live_sources
        result['active_clients_partial']=any(value is None for value in live_sources.values())

        if include_servers and result['services']['hotspot']['available']:
            try:result['servers']=self._read_retry(self.api.hotspot_servers)
            except MikroTikError as exc:result['errors'].append('سيرفرات Hotspot: '+str(exc))
        # Interface counters feed the compact traffic table on the dashboard.
        # Failure here is intentionally non-fatal because some restricted API
        # groups can read users but not interface statistics.
        if include_interfaces:
            try:
                rows=self._read_retry(lambda:self.api.command('/interface/print',{
                    '.proplist':'name,type,running,disabled,rx-byte,tx-byte'}))
                cleaned=[]
                for row in rows:
                    name=str(row.get('name','')).strip()
                    if not name:continue
                    cleaned.append({'name':name,'type':row.get('type',''),
                        'running':truth(row.get('running','no')),'disabled':truth(row.get('disabled','no')),
                        'rx':int(row.get('rx-byte',0) or 0),'tx':int(row.get('tx-byte',0) or 0)})
                result['interfaces']=cleaned
            except (MikroTikError,ValueError,TypeError) as exc:
                result['errors'].append('حركة الواجهات: '+str(exc))
        return result




    def hotspot_servers(self):
        """Actual Hotspot Servers (not user profiles or IP bindings)."""
        rows = self._read_retry(lambda:self.api.command('/ip/hotspot/print',
             {'.proplist':'.id,name,interface,profile,disabled,invalid,running'}))
        result = []
        for row in rows:
            ident = str(row.get('.id',''))
            if not re.fullmatch(r'\*[0-9A-Fa-f]+', ident):
                continue
            result.append({'id':ident, 'name':str(row.get('name') or ''),
                           'interface':str(row.get('interface') or ''),
                           'profile':str(row.get('profile') or ''),
                           'disabled':truth(row.get('disabled','no')),
                           'invalid':truth(row.get('invalid','no')),
                           'running':truth(row.get('running','no'))})
        return sorted(result,key=lambda r:(r['name'].lower(),r['interface']))

    def neighbor_devices(self):
        """Router-discovered neighbors only; never scan the connected network."""
        count=self.count('/ip/neighbor')
        if count is None or count>5000:
            raise ValueError('عدد الأجهزة المتصلة غير معروف أو تجاوز 5000؛ أُلغيت القراءة لحماية الأداء.')
        rows=self._read_retry(lambda:self.api.command('/ip/neighbor/print',
                    {'.proplist':'interface,address,address4,mac-address,identity,platform,version,board'}))
        return [{key:str(row.get(key,'') or '')[:120] for key in
                 ('interface','address','address4','mac-address','identity','platform','version','board')}
                for row in rows[:5000]]

    def database_maintenance_status(self):
        self.user_manager()
        path=self.um_path+'/database/print'
        try:
            rows=self._read_retry(lambda:self.api.command(path))
            if not rows:return {'version':self.um_version,'status':'لم يُرجع الراوتر بيانات عن قاعدة البيانات.'}
            # Avoid returning any unforeseen fields such as secrets or paths.
            return {'version':self.um_version,'status':'متاحة',
                    'db_size':str(rows[0].get('db-size','')),
                    'free_space':str(rows[0].get('free-disk-space',''))}
        except MikroTikError as exc:
            return {'version':self.um_version,'status':'تعذرت قراءة الحالة: '+str(exc)[:130]}

    def _server_by_exact_identity(self,initial,current):
        hits=[r for r in current if r['name']==initial['name'] and
              r['interface']==initial['interface']]
        if len(hits)!=1:
            raise MikroTikError('سيرفر مفقود أو غير فريد: '+initial['name']+'؛ تُرك دون تعديل.')
        return hits[0]

    @staticmethod
    def _check_selected_servers(targets,expected):
        if expected is None:return
        original={row['id']:row for row in expected}
        if set(original)!={row['id'] for row in targets} or any(
                any(row[key]!=original[row['id']][key] for key in ('name','interface','disabled'))
                for row in targets):
            raise MikroTikError('تغيرت هوية السيرفر أو حالته بعد الاختيار؛ حدّث القائمة وأكدها مجدداً.')

    def change_hotspot_servers(self,ids,disabled,router,data_dir,expected=None):
        from .maintenance_state import file_for,operation_lock
        with operation_lock(file_for(data_dir,router)):
            return self._change_hotspot_servers(ids,disabled,router,data_dir,expected)

    def _change_hotspot_servers(self,ids,disabled,router,data_dir,expected):
        """Exact selected IDs, single-record mutations, verified read-back."""
        if not ids or len(ids)>500 or len(ids)!=len(set(ids)):
            raise ValueError('حدد سيرفرات صالحة وغير مكررة (حتى 500).')
        if not isinstance(disabled,bool):raise ValueError('حالة السيرفر غير صحيحة.')
        current=self.hotspot_servers()
        known={row['id']:row for row in current}
        if any(ident not in known for ident in ids):
            raise MikroTikError('تغيرت قائمة السيرفرات. حدّثها ثم أكد الإجراء مجددًا.')
        targets=[known[ident] for ident in ids]
        self._check_selected_servers(targets,expected)
        from .maintenance_state import file_for,read,write,make_record,set_status
        from uuid import uuid4
        path=file_for(data_dir,router)
        active=read(path)
        if active and active['status']!='restored':
            raise MikroTikError('استعد وضع الصيانة السابق قبل تعديل حالات السيرفرات.')
        # Keep every direct change's pre-state, including uncertain partial
        # operations. These audit files do not start temporary maintenance.
        journal=path.parent/'changes'/(path.stem+'-'+uuid4().hex+'.json')
        record=make_record(router,targets);record['requested_disabled']=disabled
        write(journal,record)
        changed=[]
        for row in targets:
            if row['disabled']==disabled:continue
            self.api.command('/ip/hotspot/set',{'.id':row['id'],
                                                'disabled':'yes' if disabled else 'no'})
            changed.append(row['name'])
        # Read back every targeted row, never infer success from API request.
        updated=self.hotspot_servers()
        for row in targets:
            actual=self._server_by_exact_identity(row,updated)
            if actual['disabled']!=disabled:
                raise MikroTikError('لم تتأكد حالة السيرفر '+row['name']+' بعد الأمر.')
        set_status(journal,record,'completed')
        return {'requested':len(targets),'changed':len(changed),'names':changed}

    def start_temporary_hotspot_mode(self,ids,router,data_dir,expected=None):
        from .maintenance_state import file_for,operation_lock
        with operation_lock(file_for(data_dir,router)):
            return self._start_temporary_hotspot_mode(ids,router,data_dir,expected)

    def _start_temporary_hotspot_mode(self,ids,router,data_dir,expected):
        """Persist original server states before changing any router settings."""
        from core.maintenance_state import file_for,read,write,make_record,set_status
        path=file_for(data_dir,router)
        existing=read(path)
        if existing and existing['status']!='restored':
            raise MikroTikError('هناك وضع صيانة سابق لم يستعد حالاته؛ نفّذ الاستعادة أولًا.')
        current=self.hotspot_servers()
        known={row['id']:row for row in current}
        if not ids or len(ids)!=len(set(ids)) or len(ids)>500 or any(i not in known for i in ids):
            raise ValueError('قائمة السيرفرات تغيرت؛ حدّث الاختيار.')
        chosen=[known[i] for i in ids]
        self._check_selected_servers(chosen,expected)
        record=make_record(router,chosen)
        write(path,record)
        try:
            for row in chosen:
                if not row['disabled']:
                    self.api.command('/ip/hotspot/set',{'.id':row['id'],'disabled':'yes'})
            verified=self.hotspot_servers()
            for row in chosen:
                if not self._server_by_exact_identity(row,verified)['disabled']:
                    raise MikroTikError('لم يتأكد تعطيل السيرفر: '+row['name'])
            set_status(path,record,'active')
        except Exception:
            # Pending journal survives an uncertain write; safe manual recovery.
            raise
        return {'selected':len(chosen),'modified':sum(not r['disabled'] for r in chosen)}

    def restore_temporary_hotspot_mode(self,router,data_dir):
        from .maintenance_state import file_for,operation_lock
        with operation_lock(file_for(data_dir,router)):
            return self._restore_temporary_hotspot_mode(router,data_dir)

    def _restore_temporary_hotspot_mode(self,router,data_dir):
        from core.maintenance_state import file_for,read,set_status
        path=file_for(data_dir,router)
        record=read(path)
        if not record or record['status']=='restored':
            raise MikroTikError('لا يوجد وضع صيانة نشط محفوظ لهذا الراوتر.')
        if str(record['router_host'])!=str(router['host']) or str(record['router_port'])!=str(router['port']):
            raise MikroTikError('سجل الاستعادة لا يطابق الراوتر المتصل.')
        set_status(path,record,'restoring')
        current=self.hotspot_servers()
        # Validate the whole target set before issuing the first restoration.
        targets=[self._server_by_exact_identity(before,current) for before in record['servers']]
        for before,row in zip(record['servers'],targets):
            if row['disabled']!=before['disabled_before']:
                self.api.command('/ip/hotspot/set',
                  {'.id':row['id'],'disabled':'yes' if before['disabled_before'] else 'no'})
        verified=self.hotspot_servers()
        for before in record['servers']:
            row=self._server_by_exact_identity(before,verified)
            if row['disabled']!=before['disabled_before']:
                raise MikroTikError('لم تتأكد الحالة الأصلية للسيرفر '+before['name']+'؛ بقي سجل الاستعادة محفوظًا.')
        set_status(path,record,'restored')
        return {'restored':len(record['servers'])}

    def maintenance_mode_status(self,router,data_dir):
        from core.maintenance_state import file_for,read
        record=read(file_for(data_dir,router))
        if not record or record['status']=='restored':return {'active':False,'count':0}
        return {'active':True,'count':len(record['servers']),'status':record['status'],
                'created':record.get('created','')}

    def maintenance_sessions(self):
        """Read-only listing with strict limits for targeted disconnection."""
        output=[]
        for label,path in (('Hotspot','/ip/hotspot/active'),('PPPoE','/ppp/active')):
            count=self.count(path)
            if count is None:
                raise ValueError('الراوتر لا يدعم عدّ الجلسات الآمن لهذا الإجراء.')
            if count>2500:
                raise ValueError('يوجد أكثر من 2500 جلسة في %s؛ لا يمكن عرض إجراء شامل بأمان.' % label)
            rows=self._read_retry(lambda p=path:self.api.command(
                p+'/print',{'.proplist':'.id,user,address,name,caller-id,session-id,mac-address,server'}))
            for row in rows:
                ident=str(row.get('.id',''))
                if re.fullmatch(r'\*[0-9A-Fa-f]+',ident):
                    output.append({'id':ident,'service':label,'record':dict(row),
                                   'user':str(row.get('user') or row.get('name') or '—')[:90],
                                   'address':str(row.get('address') or '')[:80]})
        return output

    def perform_maintenance(self,actions):
        """Sequentially perform ONLY confirmed whitelisted operations.

        A reboot reply can be lost as the socket shuts down: report dispatched,
        not 'verified complete'. No mutation is retried on an uncertain result.
        """
        result=[]
        for action,data in actions:
            try:
                self._perform_maintenance_step(action,data,result)
            except (MikroTikError,ValueError) as exc:
                raise MikroTikError('توقفت الصيانة بعد نجاح %s خطوة. فشلت خطوة %s: %s' %
                                   (len(result),action,str(exc))) from exc
        return result

    def _perform_maintenance_step(self,action,data,result):
        if action=='disconnect':
            if not isinstance(data,dict) or data.get('service') not in ('Hotspot','PPPoE'):
                raise ValueError('اختيار جلسة غير صالح.')
            service=data['service'];record=data.get('record',{})
            ident=record.get('.id','')
            self.user_action('hotspot' if service=='Hotspot' else 'ppp','disconnect',record)
            result.append('فصل جلسة %s %s' % (service,ident))
        elif action=='optimize':
            self.user_manager()
            if self.um_version!=7:raise ValueError('تحسين قاعدة البيانات متاح فقط بعد تحقق RouterOS 7.')
            self.api.command('/user-manager/database/optimize-db')
            result.append('استجاب الراوتر لأمر تحسين قاعدة بيانات مدير المستخدمين (الإصدار ٧)؛ اكتمال المعالجة يحتاج التحقق من حالة القاعدة.')
        elif action=='reboot':
            try:self.api.command('/system/reboot')
            except MikroTikError as exc:
                # Never claim router reboot succeeded if the ACK was lost.
                if 'أغلق الراوتر الاتصال' not in str(exc) and 'انقطع الاتصال' not in str(exc):raise
                result.append('أُرسل طلب إعادة التشغيل وانقطع الاتصال؛ التحقق من إقلاع الراوتر مطلوب.')
            else:
                result.append('قَبِل الراوتر طلب إعادة التشغيل؛ جارٍ انتظار عودة الاتصال.')
        else:raise ValueError('إجراء صيانة غير مصرح به.')

    def dashboard_counters(self,include_total=True,include_clients=True):
        """Tiny, independent count-only refresh; no profile/session/history scans."""
        result={'kind':'live','services':{},'lightweight':True,
                'updated_at':datetime.now().isoformat(timespec='seconds')}
        if include_total:
            warnings=[]
            try:
                if self.um_path and self._count_support.get(self.um_path+'/user') is False:
                    total=None
                    warnings.append('الراوتر لا يدعم عدّ كروت مدير المستخدمين دون تنزيل القائمة.')
                else:
                    total=self._read_retry(self._detect_userman_count)
            except MikroTikError as exc:
                total=None;warnings.append('إجمالي الكروت: '+str(exc))
            result['services']['userman']={'users':total,'warnings':warnings}
        if include_clients:
            sources={}
            for name,path in (('hotspot','/ip/hotspot/active'),('ppp','/ppp/active')):
                warnings=[]
                count=self._safe_metric(lambda p=path:self.count(p),'المتصلون',warnings)
                if count is None:warnings.append('لا يتوفر عدّ سريع لهذه الخدمة.')
                sources[name]=count
                result['services'][name]={'active':count,'warnings':warnings}
            values=[n for n in sources.values() if n is not None]
            result['active_clients']=sum(values) if values else None
            result['active_clients_sources']=sources
            result['active_clients_partial']=len(values)<2
        return result

    def dashboard_catalog(self):
        """Lightweight reads including one UM count; no history or profile scans."""
        result={'kind':'live','services':{},'resource':{},'errors':[],
                'lightweight':True,'updated_at':datetime.now().isoformat(timespec='seconds')}
        try:result['resource']=self._read_retry(self.api.resource)
        except MikroTikError as exc:result['errors'].append('بيانات الجهاز: '+str(exc))
        # The home card needs the real User Manager card total. This is one
        # count-only request: it never downloads users, profiles, or sessions.
        um_warnings=[]
        try:
            total=self._read_retry(self._detect_userman_count)
            result['services']['userman']={'users':total,'available':True,'warnings':um_warnings,
                                           'um_version':self.um_version}
        except MikroTikError as exc:
            um_warnings.append('مجموع كروت مدير المستخدمين: '+str(exc))
            result['services']['userman']={'users':None,'available':False,'warnings':um_warnings}
        sources={}
        for service,path in (('hotspot','/ip/hotspot/active'),('ppp','/ppp/active')):
            warnings=[]
            active=self._safe_metric(lambda p=path:self._count_flexible(p),'المتصلون',warnings)
            result['services'][service]={'active':active,'warnings':warnings}
            sources[service]=active
        known=[value for value in sources.values() if value is not None]
        result['active_clients']=sum(known) if known else None
        result['active_clients_sources']=sources
        result['active_clients_partial']=len(known)<2
        try:
            rows=self._read_retry(lambda:self.api.command('/interface/print',{
                '.proplist':'name,type,running,disabled,rx-byte,tx-byte'},['?dynamic=false']))
            result['interfaces']=[dict(name=row['name'],type=row.get('type',''),
                running=truth(row.get('running')),disabled=truth(row.get('disabled')),
                rx=self._int_value(row.get('rx-byte')),tx=self._int_value(row.get('tx-byte')))
                for row in rows if row.get('name')]
        except MikroTikError as exc:result['errors'].append('عدادات الواجهات: '+str(exc))
        result['updated_at']=datetime.now().isoformat(timespec='seconds')
        return result

    def inventory_catalog(self, force=False, on_lifecycle=None):
        """Slow-tier snapshot, cached by the UI for five minutes.

        Session history is COUNT ONLY, without any row-download fallback.
        An unsupported count is unavailable, not an empty successful count.
        """
        result={'kind':'inventory','services':{},'errors':[],
                'inventory_updated_at':datetime.now().isoformat(timespec='seconds')}
        # User Manager first: this is the user's primary voucher inventory.
        warnings=[]
        state={'warnings':warnings}
        result['services']['userman']=state
        try:
            if force:self.invalidate_voucher_status_cache()
            if not self.um_path:self._detect_userman_count()
            state['um_version']=self.um_version
            life=self._service_lifecycle('userman')
            state.update(lifecycle=life,users=life['total'],disabled=life['disabled'],available=True)
            if life.get('reason'):warnings.append(life['reason'])
            if not life.get('complete'):state['lifecycle_error']=life.get('reason','تعذرت قراءة حالة الكروت.')
            if on_lifecycle:
                from copy import deepcopy
                on_lifecycle({'kind':'inventory','services':{'userman':deepcopy(state)},
                              'inventory_updated_at':datetime.now().isoformat(timespec='seconds')})
            session_path=self.um_path+'/session'
            state['sessions']=self._safe_metric(lambda:self.count(session_path),'إجمالي الجلسات',warnings)
            state['active']=self._safe_metric(lambda:self.count(session_path,['?active=yes']),'جلسات RADIUS النشطة',warnings)
            if state['sessions'] is None:warnings.append('عدّ الجلسات غير متاح؛ لم يُنزّل سجل الجلسات لتجنب الحمل.')
        except MikroTikError as exc:
            state['lifecycle']=self._empty_lifecycle('unavailable')
            state['lifecycle_error']=('مدير المستخدمين غير متوفر على هذا الراوتر.' if unsupported(exc) else str(exc))
            warnings.append('حالة الكروت: '+state['lifecycle_error'])
        for service,path in (('hotspot','/ip/hotspot/user'),('ppp','/ppp/secret')):
            warnings=[]
            users=self._safe_metric(lambda p=path:self._count_flexible(p),'المستخدمون',warnings)
            disabled=self._safe_metric(lambda p=path:self._count_flexible(p,['?disabled=yes']),'المعطلون',warnings)
            result['services'][service]={'users':users,'disabled':disabled,'warnings':warnings}
        result['inventory_updated_at']=datetime.now().isoformat(timespec='seconds')
        return result

    def users(self, service, active=False, names_only=False):
        path=self.user_path(service)
        query=None;attrs=None
        if active:
            path={'hotspot':'/ip/hotspot/active','ppp':'/ppp/active'}.get(service,self.um_path+'/session' if self.um_path else '')
            if service=='userman':query=['?active=yes']
        if names_only:attrs={'.proplist':'username' if service=='userman' and self.um_version==6 else 'name'}
        try:rows=self.api.command(path+'/print',attrs,query)
        except MikroTikError:
            if not (active and service=='userman'):raise
            rows=self.api.command(path+'/print',attrs,['?active=true'])
        if names_only:return {r.get('name',r.get('username','')) for r in rows}
        assignments={}
        if rows and service=='userman' and not active and self.um_version==7:
            for item in self.api.command(self.um_path+'/user-profile/print',{'.proplist':'user,profile'}):
                assignments.setdefault(item.get('user',''),[]).append(item.get('profile',''))
        result=[]
        for row in rows:
            name=(row.get('user',row.get('name',row.get('username',''))) if active
                  else row.get('name',row.get('username',row.get('user',''))))
            profile=row.get('profile',row.get('actual-profile',''))
            assigned=list(dict.fromkeys(assignments.get(name,[])+assignments.get(row.get('.id'),[])))
            if assigned:profile=', '.join(assigned)
            result.append(dict(row,_id=row.get('.id',''),_name=name,_profile=profile,_profiles=assigned or [profile],
                               _address=row.get('address',row.get('user-address','')),
                               _uptime=row.get('uptime',row.get('uptime-used',''))))
        return result

    def voucher_names(self):
        """Reserve names in all installed authentication services on this router.

        A local Hotspot/PPP account can shadow a RADIUS/User Manager voucher.
        Only an absent menu can be skipped; a denied read must stop creation.
        """
        names=set()
        for service in SERVICES:
            try:names.update(self._read_retry(lambda s=service:self.users(s,names_only=True)))
            except MikroTikError as exc:
                if not unsupported(exc):raise
        names.discard('')
        return names

    def validate_selected_record(self,path,record,fields):
        """Recheck a displayed non-user record before a targeted write."""
        ident=str(record.get('.id',''))
        if not re.fullmatch(r'\*[0-9A-Fa-f]+',ident) or not any(record.get(k) for k in fields):
            raise ValueError('هوية السجل غير مكتملة؛ حدّث القائمة قبل تنفيذ الإجراء.')
        rows=self.api.command(path+'/print',{'.proplist':','.join(('.id',)+tuple(fields))},['?.id='+ident])
        if (len(rows)!=1 or rows[0].get('.id')!=ident or
                any(rows[0].get(k,'')!=record.get(k,'') for k in fields)):
            raise MikroTikError('تغيّر السجل المحدد؛ حدّث القائمة. لم يُنفذ الإجراء.')
        return ident

    def _validate_user_target(self, path, record, active=False):
        """Fail closed if the selected id no longer identifies the shown user."""
        ident=str(record.get('_id',record.get('.id','')))
        name=record.get('_name',record.get('user' if active else 'name',
                        record.get('name',record.get('username',''))))
        if not re.fullmatch(r'\*[0-9A-Fa-f]+',ident) or not isinstance(name,str) or not name:
            raise ValueError('هوية المستخدم غير مكتملة؛ حدّث القائمة قبل تنفيذ الإجراء.')
        rows=self.api.command(path+'/print',
            {'.proplist':'.id,name,username,user,address,user-address,caller-id,session-id,active,mac-address,server'},
            ['?.id='+ident])
        if len(rows)!=1 or str(rows[0].get('.id',''))!=ident:
            raise MikroTikError('تغيّر المستخدم المحدد أو تعذرت قراءته؛ حدّث القائمة. لم يُنفذ الإجراء.')
        current=rows[0]
        current_name=(current.get('user',current.get('name',current.get('username',''))) if active
                      else current.get('name',current.get('username',current.get('user',''))))
        if current_name!=name:
            raise MikroTikError('تغيّرت هوية المستخدم المحدد؛ حدّث القائمة. لم يُنفذ الإجراء.')
        if active:
            if 'active' in current and not truth(current['active']):
                raise MikroTikError('انتهت الجلسة المحددة؛ حدّث القائمة. لم يُنفذ الإجراء.')
            for field in ('address','user-address','caller-id','session-id','mac-address','server'):
                if record.get(field) not in (None,'') and current.get(field)!=record[field]:
                    raise MikroTikError('تغيّرت الجلسة المحددة؛ حدّث القائمة. لم يُنفذ الإجراء.')
        return ident

    def user_action(self, service, action, record):
        if action not in ('disconnect','remove','enable','disable'):
            raise ValueError('عملية غير صالحة.')
        path=self.user_path(service)
        if action=='disconnect':
            if service=='userman':
                # A UM session is accounting data. Remove the live NAS session instead.
                self._validate_user_target(self.um_path+'/session',record,active=True)
                name=record.get('_name',record.get('user',''))
                if not str(name or '').strip():raise ValueError('اسم المستخدم غير متوفر؛ لم يُفصل أي اتصال.')
                total=0
                for menu,field in (('/ip/hotspot/active','user'),('/ppp/active','name')):
                    for active in self.api.command(menu+'/print',queries=['?%s=%s' % (field,name)]):
                        if active.get(field)!=name:
                            raise MikroTikError('تعذرت مطابقة الجلسة بالمستخدم؛ لم تُفصل الجلسة.')
                        target=self._validate_user_target(menu,active,active=True)
                        self.api.command(menu+'/remove',{'.id':target});total+=1
                if not total:raise MikroTikError('الجلسة على NAS آخر؛ افصلها من الراوتر الذي يخدم العميل.')
                return total
            path='/ip/hotspot/active' if service=='hotspot' else '/ppp/active'
            ident=self._validate_user_target(path,record,active=True)
            return self.api.command(path+'/remove',{'.id':ident})
        ident=self._validate_user_target(path,record)
        if action=='remove':return self.api.command(path+'/remove',{'.id':ident})
        if action in ('enable','disable'):
            return self.api.command(path+'/set',{'.id':ident,'disabled':'yes' if action=='disable' else 'no'})

    def add_user(self, service, profile, card, customer=''):
        path=self.user_path(service)
        name=card['username'];password=card.get('password','')
        if service=='hotspot':
            return self.api.add_hotspot_user(name,password,profile['name'],card.get('server','all'),
                        card.get('limit_uptime',''),card.get('limit_bytes',0),card.get('comment',''))
        attrs={'name':name,'password':password,'profile':profile['name'],'service':'pppoe','comment':card.get('comment','')}
        if service=='ppp':return self.api.command(path+'/add',attrs)
        attrs={'password':password}
        if self.um_version==7:attrs.update(name=name)
        else:
            owner=customer or profile['raw'].get('owner',profile['raw'].get('customer',''))
            if not owner:raise ValueError('اختر حساب مدير المستخدمين قبل إنشاء الكروت.')
            attrs.update(username=name,customer=owner)
        _,done=self.api.command(path+'/add',attrs,return_done=True)
        created_id=done.get('ret')
        if self.um_version==6 and not re.fullmatch(r'\*[0-9A-Fa-f]+',str(created_id or '')):
            raise DeliveryUncertainError('أُنشئ الحساب لكن لم يرجع معرف صالح واحد؛ لم يُطلب ربط الباقة. راجع المستخدم قبل الطباعة أو إعادة الإرسال.')
        try:
            if self.um_version==7:
                self.api.command(self.um_path+'/user-profile/add',{'user':name,'profile':profile['name']})
            else:
                self.api.command(path+'/create-and-activate-profile',{'numbers':created_id,'customer':owner,'profile':profile['name']})
        except Exception as exc:
            # A lost reply may mean the profile was applied successfully. Do
            # not delete a potentially live voucher or replay the write.
            if isinstance(exc, RouterOSTrapError) and created_id:
                try:
                    self.api.command(path+'/remove',{'.id':created_id})
                except Exception:
                    pass
                else:
                    raise RouterOSTrapError('رفض ربط الباقة؛ أُلغي المستخدم الجديد: '+str(exc)) from exc
            raise DeliveryUncertainError('لم يتأكد ربط الباقة أو التراجع؛ راجع المستخدم قبل الطباعة: '+str(exc)) from exc

    def customers(self):
        self.user_manager()
        if self.um_version==7:return []
        return [r.get('login',r.get('name','')) for r in self.api.command(self.um_path+'/customer/print')]

    def _inventory_command(self, path, attrs, **kwargs):
        """Quota/profile writes invalidate classifications, even on a lost reply."""
        try:
            return self.api.command(path, attrs, **kwargs)
        finally:
            self.invalidate_voucher_status_cache()

    def save_profile(self, service, values, existing=None):
        """Change profile fields, preserving scripts and unrelated router settings."""
        path=self.profile_path(service)
        name=str(values['name']).strip()
        if not name:raise ValueError('اسم الباقة مطلوب.')
        attrs={'name':name}
        if service in ('hotspot','ppp'):
            attrs.update({'rate-limit':values.get('rate',''),'session-timeout':values.get('uptime','0s') or '0s'})
            if service=='hotspot':
                shared=int(values.get('shared') or 1)
                if shared<1:raise ValueError('عدد المشتركين يجب أن يكون موجبًا.')
                attrs['shared-users']=shared
            else:
                attrs.update({'local-address':values.get('local-address',''),'remote-address':values.get('remote-address',''),
                              'only-one':values.get('only-one','default')})
        else:
            attrs.update({'validity':values.get('validity') or '0s', 'price':str(float(values.get('price') or 0)),
                          'override-shared-users':values.get('shared') or 'off'})
            if self.um_version==7:attrs['starts-when']=values.get('starts','first-auth')
            else:
                attrs['starts-at']='logon' if values.get('starts','first-auth')=='first-auth' else 'now'
                if not existing:
                    owner=values.get('owner','').strip()
                    if not owner:raise ValueError('حساب مدير المستخدمين مطلوب.')
                    attrs['owner']=owner
            if str(attrs['validity']).lower() in ('','0','0s','unlimited'):attrs['validity']='0s' if self.um_version==6 else 'unlimited'
        if existing:
            attrs['.id']=self._validate_user_target(path,{'_id':existing['id'],'_name':existing['name']})
        command=self._inventory_command if service=='userman' else self.api.command
        _,done=command(path+('/set' if existing else '/add'),attrs,return_done=True)
        return existing['id'] if existing else done.get('ret')

    def remove_profile(self, service, profile):
        path=self.profile_path(service)
        ident=self._validate_user_target(path,{'_id':profile['id'],'_name':profile['name']})
        command=self._inventory_command if service=='userman' else self.api.command
        return command(path+'/remove',{'.id':ident})

    def save_limitation(self, values, existing=None):
        """A dedicated editor exposes shared limitations instead of overwriting them silently."""
        base=self.user_manager();path=base+('/limitation' if self.um_version==7 else '/profile/limitation')
        attrs={key:values[key] for key in ('name','transfer-limit','uptime-limit','rate-limit-rx','rate-limit-tx') if key in values}
        if not str(attrs.get('name','')).strip():raise ValueError('اسم المحدد مطلوب.')
        capacity=int(attrs.get('transfer-limit') or 0)
        if capacity<0:raise ValueError('السعة يجب أن تكون صفرًا أو أكبر.')
        attrs['transfer-limit']=str(capacity)
        if self.um_version==6 and not existing:
            if not values.get('owner'):raise ValueError('حساب مدير المستخدمين مطلوب.')
            attrs['owner']=values['owner']
        if existing:attrs['.id']=self._validate_user_target(path,existing)
        return self._inventory_command(path+('/set' if existing else '/add'),attrs)

    def limitations(self):
        base=self.user_manager()
        return self.api.command(base+('/limitation/print' if self.um_version==7 else '/profile/limitation/print'))

    def link_limitation(self, profile, limitation, remove_id=None):
        base=self.user_manager();path=base+('/profile-limitation' if self.um_version==7 else '/profile/profile-limitation')
        self._validate_user_target(base+'/profile',{'_id':profile['id'],'_name':profile['name']})
        limit_path=base+('/limitation' if self.um_version==7 else '/profile/limitation')
        self._validate_user_target(limit_path,limitation)
        if remove_id:
            if not re.fullmatch(r'\*[0-9A-Fa-f]+',str(remove_id)):
                raise ValueError('معرف الربط غير صالح؛ حدّث الباقات.')
            rows=self.api.command(path+'/print',{'.proplist':'.id,profile,limitation'},['?.id='+remove_id])
            if (len(rows)!=1 or rows[0].get('.id')!=remove_id or
                    rows[0].get('profile') not in (profile['id'],profile['name']) or
                    rows[0].get('limitation') not in (limitation['.id'],limitation['name'])):
                raise MikroTikError('تغيّر ربط المحدد؛ حدّث الباقات. لم يُنفذ الإجراء.')
            return self._inventory_command(path+'/remove',{'.id':remove_id})
        return self._inventory_command(path+'/add',{'profile':profile['name'],'limitation':limitation['name']})

    def diagnostics(self):
        result=[]
        for title,path in (('هوية الراوتر','/system/identity'),('بيانات الجهاز','/system/resource'),
                           ('بروفايلات Hotspot','/ip/hotspot/user/profile'),('سيرفرات Hotspot','/ip/hotspot'),
                           ('بروفايلات PPPoE','/ppp/profile'),('إعدادات RADIUS','/radius'),
                           ('واجهات الشبكة','/interface'),('عناوين الشبكة','/ip/address')):
            try:
                rows=self.api.command(path+'/print')
                # Do not expose secrets, scripts or stored credentials in diagnostics.
                public=[{k:v for k,v in row.items() if k in ('.id','name','version','board-name','cpu-load','uptime','address','interface','disabled','running','service')} for row in rows]
                result.append((title,'تمت القراءة',public))
            except MikroTikError as exc:result.append((title,'تعذرت القراءة: '+str(exc),[]))
        try:result.append(('مدير المستخدمين',self.user_manager(),[{'name':r['name']} for r in self.profiles('userman')]))
        except MikroTikError as exc:result.append(('مدير المستخدمين',str(exc),[]))
        return result
