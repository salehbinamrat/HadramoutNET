"""Merge independent profile/live/inventory snapshots without false freshness."""
from copy import deepcopy


def merge_snapshot(previous, fresh):
    result = deepcopy(previous or {})
    fresh = deepcopy(fresh or {})
    kind = fresh.get('kind', 'profiles')
    services = result.setdefault('services', {})
    for service, incoming in fresh.pop('services', {}).items():
        current = services.setdefault(service, {})
        # Warnings belong to their tier and cannot disappear on a light read.
        warnings_key = {'live': 'live_warnings', 'inventory': 'inventory_warnings'}.get(kind, 'profile_warnings')
        if 'warnings' in incoming:
            current[warnings_key] = incoming.pop('warnings')
        if kind == 'profiles':
            for key in ('users', 'active', 'active_names', 'disabled', 'sessions', 'lifecycle'):
                value = incoming.get(key)
                if value is None or (key == 'lifecycle' and not (value or {}).get('complete')):
                    incoming.pop(key, None)
        if kind == 'live':
            # Transient failed count-only reads cannot erase a previously verified count.
            for key in ('users','active'):
                if key in incoming and incoming[key] is None:
                    incoming.pop(key)
        if 'lifecycle' in incoming:
            life = incoming['lifecycle']
            old_life = current.get('lifecycle', {})
            if not life.get('complete') and old_life.get('complete'):
                retained = dict(old_life, stale=True)
                retained['reason'] = incoming.get('lifecycle_error') or life.get('reason') or 'تعذر تحديث حالة الكروت؛ تُعرض آخر قراءة ناجحة.'
                incoming['lifecycle'] = retained
            elif life.get('complete'):
                incoming['lifecycle_error'] = ''
        current.update(incoming)
        current['warnings'] = list(dict.fromkeys(
            current.get('profile_warnings', []) + current.get('live_warnings', []) + current.get('inventory_warnings', [])))
    if kind == 'profiles':
        # Profile discovery has no new live readings.
        for key in ('resource', 'interfaces', 'active_clients', 'active_clients_sources', 'active_clients_partial', 'updated_at'):
            fresh.pop(key, None)
    elif kind == 'inventory':
        fresh.pop('updated_at', None)
    else:
        # If every connected-client count fails, retain last verified reading
        # and explicitly mark it partial instead of turning it into zero/None.
        if 'active_clients' in fresh and fresh['active_clients'] is None:
            fresh.pop('active_clients')
            fresh.pop('active_clients_sources',None)
            fresh['active_clients_partial']=True
        # Retaining resource data after failure is useful, but label it stale.
        if 'resource' not in fresh:
            # Counter-only polling did not read resource data at all.
            fresh['resource_stale']=result.get('resource_stale',False)
        elif not fresh.get('resource') and result.get('resource'):
            fresh['resource'] = result['resource']
            fresh['resource_stale'] = True
        else:
            fresh['resource_stale'] = False
        if 'interfaces' not in fresh:
            fresh['interfaces_stale'] = result.get('interfaces_stale',False)
        else:
            fresh['interfaces_stale'] = False
    error_key = {'live': 'live_errors', 'inventory': 'inventory_errors'}.get(kind, 'profile_errors')
    # A partial tier refresh that does not carry an ``errors`` field has no
    # authority to erase the previous error list.  An explicit empty list still
    # clears it after a successful read.
    if 'errors' in fresh:
        result[error_key] = fresh.pop('errors')
    result.update(fresh)
    result['errors'] = list(dict.fromkeys(result.get('live_errors', []) + result.get('inventory_errors', []) + result.get('profile_errors', [])))
    return result
