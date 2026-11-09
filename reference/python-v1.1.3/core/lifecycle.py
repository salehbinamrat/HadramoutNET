"""Pure, linear-time voucher classification. Never count profile rows as users.

RouterOS 7 source: https://help.mikrotik.com/docs/spaces/ROS/pages/2555940/User+Manager
A user may have many profiles; only one is active at a time. Disabled and
unclassified accounts are separate from the three usable/expired categories.
"""
from collections import defaultdict
from datetime import datetime


def empty_lifecycle(source=''):
    return dict(total=0, valid=0, active=0, expired=0, disabled=0,
                unclassified=0, complete=False, source=source,
                updated_at='', stale=False, reason='')


def normalized_state(value):
    return ' '.join(str(value or '').strip().lower().replace('-', ' ').replace(',', ' ').split())


def classify_userman7(users, assignments):
    """Return mutually exclusive account counts, not assignment counts.

    Precedence: disabled > running active > running > all-used > unknown.
    Unknown states must never make a user with an expired *old* profile expire.
    No-assignment users are unclassified, not valid. Missing read-only states
    are reported as unavailable rather than a misleading all-zero snapshot.
    """
    out = empty_lifecycle('user-manager-7-unique-users')
    accounts = {}
    aliases = {}
    for index, row in enumerate(users):
        name = str(row.get('name', row.get('username', ''))).strip()
        ident = str(row.get('.id', '')).strip()
        key = name or ident or ('missing-reference-%s' % index)
        accounts[key] = row
        if name:
            aliases[name] = key
    # Documented references are usernames; use internal IDs only as a fallback.
    for key, row in accounts.items():
        ident = str(row.get('.id', '')).strip()
        if ident:
            aliases.setdefault(ident, key)
    states = defaultdict(list)
    orphans = 0
    for assignment in assignments:
        ref = str(assignment.get('user', '')).strip()
        key = aliases.get(ref)
        if key is None:
            orphans += 1
            continue
        states[key].append(normalized_state(assignment.get('state')))
    out['total'] = len(accounts)
    observed = 0
    unreadable = 0
    for key, row in accounts.items():
        if str(row.get('disabled', '')).lower() in ('yes', 'true', '1'):
            out['disabled'] += 1
            continue
        values = states.get(key, [])
        if 'running active' in values:
            out['active'] += 1
            observed += 1
        elif 'running' in values:
            out['valid'] += 1
            observed += 1
        elif values and all(value == 'used' for value in values):
            out['expired'] += 1
            observed += 1
        else:
            out['unclassified'] += 1
            if values:
                unreadable += 1
    out['complete'] = not (unreadable and observed == 0)
    out['orphan_profiles'] = orphans
    out['updated_at'] = datetime.now().isoformat(timespec='seconds')
    if not out['complete']:
        out['reason'] = 'لم يرسل الراوتر حالات البروفايلات المعروفة؛ لا يمكن تأكيد أعداد الكروت.'
    elif out['unclassified']:
        out['reason'] = 'توجد حسابات بلا باقة أو بحالة غير معروفة؛ تظهر منفصلة ولا تُعد صالحة أو منتهية.'
    if orphans:
        out['reason'] += ' توجد روابط لا تطابق الحسابات المقروءة؛ قد تكون البيانات تغيرت أثناء القراءة.'
    return out
