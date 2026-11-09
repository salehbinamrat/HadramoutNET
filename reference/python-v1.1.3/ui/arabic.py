"""Arabic display helper for Tk builds without reliable RTL/Arabic shaping.

The helper keeps Latin text and numbers in their natural order while shaping
Arabic glyphs for widgets that otherwise render Arabic disconnected/reversed.
"""
import re

# isolated, final, initial, medial; None means form is not available.
FORMS = {
'ء':('ﺀ',None,None,None),'آ':('ﺁ','ﺂ',None,None),'أ':('ﺃ','ﺄ',None,None),'ؤ':('ﺅ','ﺆ',None,None),
'إ':('ﺇ','ﺈ',None,None),'ئ':('ﺉ','ﺊ','ﺋ','ﺌ'),'ا':('ﺍ','ﺎ',None,None),'ب':('ﺏ','ﺐ','ﺑ','ﺒ'),
'ة':('ﺓ','ﺔ',None,None),'ت':('ﺕ','ﺖ','ﺗ','ﺘ'),'ث':('ﺙ','ﺚ','ﺛ','ﺜ'),'ج':('ﺝ','ﺞ','ﺟ','ﺠ'),
'ح':('ﺡ','ﺢ','ﺣ','ﺤ'),'خ':('ﺥ','ﺦ','ﺧ','ﺨ'),'د':('ﺩ','ﺪ',None,None),'ذ':('ﺫ','ﺬ',None,None),
'ر':('ﺭ','ﺮ',None,None),'ز':('ﺯ','ﺰ',None,None),'س':('ﺱ','ﺲ','ﺳ','ﺴ'),'ش':('ﺵ','ﺶ','ﺷ','ﺸ'),
'ص':('ﺹ','ﺺ','ﺻ','ﺼ'),'ض':('ﺽ','ﺾ','ﺿ','ﻀ'),'ط':('ﻁ','ﻂ','ﻃ','ﻄ'),'ظ':('ﻅ','ﻆ','ﻇ','ﻈ'),
'ع':('ﻉ','ﻊ','ﻋ','ﻌ'),'غ':('ﻍ','ﻎ','ﻏ','ﻐ'),'ف':('ﻑ','ﻒ','ﻓ','ﻔ'),'ق':('ﻕ','ﻖ','ﻗ','ﻘ'),
'ك':('ﻙ','ﻚ','ﻛ','ﻜ'),'ل':('ﻝ','ﻞ','ﻟ','ﻠ'),'م':('ﻡ','ﻢ','ﻣ','ﻤ'),'ن':('ﻥ','ﻦ','ﻧ','ﻨ'),
'ه':('ﻩ','ﻪ','ﻫ','ﻬ'),'و':('ﻭ','ﻮ',None,None),'ى':('ﻯ','ﻰ',None,None),'ي':('ﻱ','ﻲ','ﻳ','ﻴ'),
'ـ':('ـ','ـ','ـ','ـ'),
}
RIGHT_ONLY=set('آأؤإاةدذرزوى')
DUAL=set(FORMS)-RIGHT_ONLY-set('ء')
AR_RE=re.compile(r'[\u0600-\u06ff]')
DISPLAY_AR_RE=re.compile(r'[\u0600-\u06ff\ufe70-\ufeff]')


def _can_join_left(ch):
    return ch in DUAL or ch in RIGHT_ONLY

def _can_join_right(ch):
    return ch in DUAL

def _has_arabic_letters(text):
    return any(ch in FORMS for ch in str(text))

def shape_word(word):
    chars=list(str(word))
    out=[]
    for i,ch in enumerate(chars):
        if ch not in FORMS:
            out.append(ch);continue
        prev=chars[i-1] if i>0 else ''
        nxt=chars[i+1] if i+1<len(chars) else ''
        join_prev = prev in FORMS and _can_join_right(prev) and _can_join_left(ch)
        join_next = nxt in FORMS and _can_join_right(ch) and _can_join_left(nxt)
        iso,fin,ini,med=FORMS[ch]
        if join_prev and join_next and med: form=med
        elif join_prev and fin: form=fin
        elif join_next and ini: form=ini
        else: form=iso
        out.append(form)
    return ''.join(reversed(out))

def _shape_mixed_token(token):
    """Shape one token without reversing embedded numbers/Latin text."""
    token=str(token)
    if not _has_arabic_letters(token):
        return token
    parts=[];buf=[];arabic=None
    for ch in token:
        current=ch in FORMS
        if arabic is None or current==arabic:
            buf.append(ch);arabic=current
        else:
            parts.append((arabic,''.join(buf)));buf=[ch];arabic=current
    if buf:parts.append((arabic,''.join(buf)))
    visual=[]
    for is_arabic,part in reversed(parts):
        visual.append(shape_word(part) if is_arabic else part)
    return ''.join(visual)

def rtl(text):
    if text is None:return text
    s=str(text)
    # Already-shaped presentation forms must never be processed a second time.
    if not AR_RE.search(s) or re.search(r'[\ufe70-\ufeff]', s):
        return s
    tokens=re.findall(r'\S+',s)
    if not tokens:return s
    # Group consecutive LTR tokens so phrases such as "MikroTik RouterOS" keep
    # their own order when the surrounding Arabic paragraph is reversed.
    runs=[];current=[];direction=None
    for token in tokens:
        token_dir='rtl' if _has_arabic_letters(token) else 'ltr'
        if direction is None or token_dir==direction:
            current.append(token);direction=token_dir
        else:
            runs.append((direction,current));current=[token];direction=token_dir
    if current:runs.append((direction,current))
    visual=[]
    for run_dir,items in reversed(runs):
        if run_dir=='rtl':
            visual.extend(_shape_mixed_token(item) for item in reversed(items))
        else:
            visual.extend(items)
    return ' '.join(visual)


def ui_text(text):
    """Return logical-order Unicode for Tk widgets on Windows.

    Modern Tk on Windows already performs the Unicode bidi/shaping step. Feeding it
    pre-reversed presentation forms causes words such as ``اتصال`` to appear as
    ``لاصتا``. Keep widget text in logical order and reserve :func:`rtl` for
    raster/image fallbacks that explicitly need visual-order shaping.
    """
    if text is None:
        return text
    return str(text)
