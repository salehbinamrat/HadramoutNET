"""Durable, single-attempt voucher delivery. Never replay an uncertain write."""
import sqlite3
from datetime import datetime

from .mikrotik import RouterOSTrapError


def send_cards(gateway, cards, db_path, router_id, profiles, customer='', known_names=None, progress=None):
    cards = [dict(card) for card in cards]
    ids = [card['id'] for card in cards]
    if len(set(ids)) != len(ids):
        raise ValueError('تكرر كرت داخل طلب الإرسال؛ لم تبدأ العملية.')
    results = []
    conn = sqlite3.connect(str(db_path), timeout=10)
    conn.row_factory = sqlite3.Row
    try:
        conn.execute('PRAGMA busy_timeout=5000')
        # Persist the pre-send marker and result even across a power loss.
        conn.execute('PRAGMA synchronous=FULL')
        for index, card in enumerate(cards):
            service = card.get('service', 'hotspot')
            profile = profiles.get((service, card.get('profile')))
            error = ''
            if not profile:
                error = 'باقة الكرت غير موجودة على الراوتر؛ لم يُرسل.'
            elif card['username'] in (known_names or {}).get(service, set()):
                error = 'الاسم موجود على الراوتر؛ لم يُستبدل ولم يُعتبر جاهزًا للطباعة.'
            if error:
                results.append((card['id'], False, error))
                continue
            with conn:
                current = conn.execute(
                    'SELECT c.*,b.router_id AS target_router FROM cards c '
                    'JOIN batches b ON b.id=c.batch_id WHERE c.id=?', (card['id'],)).fetchone()
                if (current is None or current['target_router'] != router_id or
                        any(current[key] != card.get(key) for key in (
                            'username', 'password', 'profile', 'service', 'server',
                            'limit_uptime', 'limit_bytes', 'comment'))):
                    raise ValueError('تغيرت بيانات الكرت أو راوتر الدفعة؛ أُوقف الإرسال.')
                leased = conn.execute(
                    "UPDATE cards SET status='SENDING',last_error=? WHERE id=? AND status IN ('CREATED','FAILED')",
                    ('بدأ طلب الإرسال؛ إذا توقف البرنامج فتحقق من الراوتر قبل الطباعة أو إعادة الإرسال.', card['id']))
                if leased.rowcount != 1:
                    results.append((card['id'], False, 'الكرت مرسل أو قيد التحقق؛ مُنع طلب إرسال مكرر.'))
                    continue
            success = False
            state = 'UNKNOWN'
            try:
                gateway.add_user(service, profile, card, customer)
                success, state = True, 'SENT'
            except (RouterOSTrapError, ValueError) as exc:
                state, error = 'FAILED', str(exc)
            except Exception as exc:
                error = 'لم يتأكد اكتمال الإرسال؛ راجع المستخدم وباقته على الراوتر. ' + str(exc)
            # A local persistence failure stops the entire batch. SENDING stays
            # non-printable; the UI must never rewrite it as a confirmed result.
            try:
                with conn:
                    saved = conn.execute("UPDATE cards SET status=?,sent_at=?,last_error=? WHERE id=? AND status='SENDING'",
                        (state, datetime.now().isoformat(timespec='seconds') if success else None,
                         error[:1000], card['id']))
                    if saved.rowcount != 1:
                        raise sqlite3.DatabaseError('سجل الإرسال مفقود أو تغير أثناء الطلب.')
            except sqlite3.Error as exc:
                raise RuntimeError('توقف الإرسال لتعذر حفظ نتيجته محليًا. لا تُعد إنشاء الدفعة؛ راجع آخر كرت على الراوتر.') from exc
            results.append((card['id'], success, error))
            if progress:
                progress(index + 1, len(cards))
            if state == 'UNKNOWN' or getattr(gateway.api, 'sock', True) is None:
                results.extend((c['id'], False, 'لم يُرسل: توقفت الدفعة بعد نتيجة غير مؤكدة أو انقطاع الاتصال.')
                               for c in cards[index + 1:])
                break
        return results
    finally:
        conn.close()
