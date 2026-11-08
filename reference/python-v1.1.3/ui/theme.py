"""Shared visual language for the portable Tkinter desktop application.

The module intentionally contains only Tkinter primitives and standard-library
values so the bundled Windows runtime does not need an additional dependency.
"""
from __future__ import annotations

import tkinter as tk
from tkinter import ttk


COLORS = {
    'bg': '#F2F7FF',
    'panel': '#FFFFFF',
    'panel2': '#E8F0FB',
    'line': '#C9D8EA',
    'text': '#102644',
    'muted': '#5B7090',
    'accent': '#18B8F5',
    'accent2': '#0B67CF',
    'green': '#177E68',
    'red': '#C14867',
    'yellow': '#B47A19',
    'blue': '#1A6DD6',
    'navy': '#09244A',
    'sidebar': '#E9F1FD',
    'white': '#FFFFFF',
    'pale': '#F8FBFF',
    'accent_soft': '#DDF6FF',
    'navy_soft': '#E7EEFA',
    'title_bg': '#0D2F62',
    'title_text': '#FFFFFF',
    'title_muted': '#B8D0F1',
    'violet': '#5563C2',
    'orange': '#C77A16',
    'rose': '#BF4D76',
    'cyan': '#159BD9',
}


FONT = 'Arial'


def apply_style(app):
    """Apply the shared ttk style to *app* and return the style object.

    This is the single source of ttk styles for the application.
    """
    style = ttk.Style(app)
    try:
        style.theme_use('clam')
    except tk.TclError:
        pass
    style.configure('.', font=(FONT, 11), background=COLORS['bg'], foreground=COLORS['text'])
    for name, color in (('TFrame','bg'),('Panel.TFrame','panel'),('Panel2.TFrame','panel2')):
        style.configure(name, background=COLORS[color])
    style.configure('TLabel', background=COLORS['bg'], foreground=COLORS['text'])
    style.configure('Panel.TLabel', background=COLORS['panel'], foreground=COLORS['text'])
    style.configure('Muted.TLabel', background=COLORS['panel'], foreground=COLORS['muted'])
    style.configure('Title.TLabel', background=COLORS['bg'], foreground=COLORS['text'], font=(FONT,18,'bold'))
    style.configure('AccentTitle.TLabel', background=COLORS['bg'], foreground=COLORS['accent'], font=(FONT,18,'bold'))
    style.configure('Metric.TLabel', background=COLORS['panel'], foreground=COLORS['text'], font=(FONT,23,'bold'))
    style.configure('TSpinbox', fieldbackground=COLORS['white'], foreground=COLORS['text'], arrowcolor=COLORS['accent'], bordercolor=COLORS['line'], padding=4)
    # Shared button geometry; semantic colors remain consistent on every page.
    button_specs = (
        ('TButton', '#214978', '#17375A', '#2B6096'),
        ('ColorPicker.TButton', '#0C376A', '#08294D', '#1B5E9B'),
        ('Accent.TButton', COLORS['accent2'], '#084F9E', '#1878DC'),
        ('Green.TButton', COLORS['green'], '#105D51', '#218E77'),
        ('SavePackage.TButton', COLORS['navy'], '#071C37', '#294D73'),
        ('Blue.TButton', '#0F63B6', '#084F91', '#1879CE'),
        ('Template.TButton', '#345CA8', '#244A82', '#416BBB'),
        ('Danger.TButton', '#B83952', '#942C41', '#C74963'),
        ('Dark.TButton', '#173B66', '#0F2C4A', '#2A5A8A'),
        ('Slate.TButton', '#30506E', '#213A53', '#3F688E'),
        ('Indigo.TButton', '#1C5FA6', '#174A82', '#2E78C2'),
        ('Amber.TButton', '#956314', '#754D10', '#AB741B'),
        ('Teal.TButton', '#087F8C', '#06626D', '#11939F'),
        ('Purple.TButton', '#6654B8', '#504294', '#7967CC'),
    )
    for name, normal, pressed, active in button_specs:
        style.configure(name, background=normal, foreground=COLORS['white'],
                        padding=(7, 4), font=(FONT, 10, 'bold'), width=0, borderwidth=0)
        style.map(name, background=[('disabled', '#E1E8F0'), ('pressed', pressed), ('active', active)],
                  foreground=[('disabled', COLORS['muted']), ('active', COLORS['white'])])
    style.configure('SavePackage.TButton', padding=(10, 4), font=(FONT, 11, 'bold'),
                    borderwidth=2, bordercolor=COLORS['yellow'])
    style.configure('Small.Dark.TButton', font=(FONT,9,'bold'), padding=(4,3))
    style.configure('TEntry', fieldbackground=COLORS['white'], foreground=COLORS['text'],
                    insertcolor=COLORS['text'], bordercolor=COLORS['line'],
                    lightcolor=COLORS['line'], darkcolor=COLORS['line'], padding=4)
    style.configure('TCombobox', fieldbackground=COLORS['white'], background=COLORS['white'],
                    foreground=COLORS['text'], arrowcolor=COLORS['accent'],
                    bordercolor=COLORS['line'], lightcolor=COLORS['line'],
                    darkcolor=COLORS['line'], padding=4)
    style.map('TCombobox', fieldbackground=[('readonly', COLORS['white'])],
              foreground=[('readonly', COLORS['text'])],
              selectbackground=[('readonly', COLORS['white'])],
              selectforeground=[('readonly', COLORS['text'])])
    style.configure('Treeview', background=COLORS['white'], fieldbackground=COLORS['white'],
                    foreground=COLORS['text'], rowheight=28, bordercolor=COLORS['line'],
                    lightcolor=COLORS['line'], darkcolor=COLORS['line'], font=(FONT, 10))
    style.configure('Treeview.Heading', background='#0E315F', foreground=COLORS['white'],
                    font=(FONT, 10, 'bold'), relief='flat', padding=(6, 7))
    style.map('Treeview', background=[('selected', COLORS['accent2'])], foreground=[('selected', COLORS['white'])])
    style.map('Treeview.Heading', background=[('active', '#18467F')], foreground=[('active', COLORS['white'])])
    style.configure('TCheckbutton', background=COLORS['panel'], foreground=COLORS['text'], font=(FONT, 10))
    style.configure('Create.TCheckbutton',font=(FONT,10,'bold'))
    style.configure('TRadiobutton', background=COLORS['panel'], foreground=COLORS['text'], font=(FONT, 10))
    style.configure('Horizontal.TProgressbar', background=COLORS['accent2'],
                    troughcolor=COLORS['panel2'], bordercolor=COLORS['panel2'],
                    lightcolor=COLORS['accent2'], darkcolor=COLORS['accent2'])
    style.configure('TNotebook', background=COLORS['bg'], borderwidth=0, tabmargins=(2,2,2,0))
    style.configure('TNotebook.Tab', background='#15457B', foreground=COLORS['white'],
                    padding=(11, 6), font=(FONT, 11, 'bold'))
    style.map('TNotebook.Tab', background=[('selected', COLORS['accent2']),('active','#2A629B')],
              foreground=[('selected', COLORS['white']),('active',COLORS['white'])])
    return style
