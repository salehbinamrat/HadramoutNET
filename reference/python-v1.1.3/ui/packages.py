"""Shared preset selector used by creation, design and printing."""
import tkinter as tk
from tkinter import ttk


class PackageBar(tk.Frame):
    def __init__(self,parent,app,colors):
        super().__init__(parent,bg=colors['panel'],padx=6,pady=4,highlightbackground=colors['accent'],highlightthickness=1)
        self.app=app
        self.ids={}
        self.value=tk.StringVar()
        ttk.Button(self,text='إدارة الباقات',style='Dark.TButton',command=lambda:app.show_page('packages')).pack(side='left',padx=3)
        ttk.Button(self,text='حفظ إعدادات الباقة',style='Accent.TButton',command=app.save_active_package).pack(side='left',padx=3)
        tk.Label(self,text='باقة الشبكة وقالبها',bg=colors['navy'],fg=colors['white'],font=('Arial',9,'bold'),padx=9,pady=5).pack(side='right',padx=5)
        self.combo=ttk.Combobox(self,textvariable=self.value,state='readonly',width=25)
        self.combo.pack(side='right',fill='x',expand=True,padx=3)
        self.combo.bind('<<ComboboxSelected>>',self.select)
        self.refresh()

    def refresh(self):
        state=self.app.network_catalog.get('services',{}).get(self.app.active_service,{})
        self.ids={row['name']:row['id'] for row in state.get('profiles',[])}
        self.combo['values']=list(self.ids)
        self.value.set(next((label for label,ident in self.ids.items() if ident==self.app.active_profile_id),''))

    def select(self,_event=None):
        self.app.select_router_profile(self.ids.get(self.value.get()))
        self.refresh()
