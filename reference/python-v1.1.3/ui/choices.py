"""Arabic display labels without changing saved or RouterOS option values."""
import tkinter as tk
from tkinter import ttk


class LocalizedChoice(ttk.Combobox):
    def __init__(self,parent,*,textvariable,values,**kwargs):
        self._raw=textvariable
        self._labels=dict(values)
        self._values={label:value for value,label in self._labels.items()}
        self._display=tk.StringVar(parent)
        super().__init__(parent,textvariable=self._display,values=list(self._values),**kwargs)
        self._trace=self._raw.trace_add('write',self._refresh)
        self.bind('<<ComboboxSelected>>',self._selected)
        self.bind('<Destroy>',self._cleanup,add='+')
        self._refresh()

    def _refresh(self,*_args):
        value=self._raw.get()
        self._display.set(self._labels.get(value,value))

    def _selected(self,_event=None):
        label=self._display.get()
        if label in self._values:
            self._raw.set(self._values[label])

    def _cleanup(self,event):
        if event.widget is self and self._trace:
            self._raw.trace_remove('write',self._trace)
            self._trace=None
