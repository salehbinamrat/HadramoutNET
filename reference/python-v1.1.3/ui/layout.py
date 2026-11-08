"""Window-sized pages and tables, without page scrollbars."""
import tkinter as tk


class PageFrame(tk.Frame):
    def __init__(self, parent, bg):
        super().__init__(parent, bg=bg)
        self.inner = tk.Frame(self, bg=bg)
        self.inner.pack(fill='both', expand=True)


def fit_buttons(frame,buttons,columns,padx=3,pady=1,ipady=0,sticky='ew'):
    """Choose a row layout from real control widths, including translated text."""
    def resize(event):
        if event.widget is not frame or event.width<2:return
        count=1
        for candidate in columns:
            widths=[max((button.winfo_reqwidth()+2*padx for index,button in enumerate(buttons)
                         if index%candidate==column),default=0) for column in range(candidate)]
            if sum(widths)<=event.width-4:
                count=candidate;break
        for column in range(max(columns)):frame.columnconfigure(column,weight=1 if column<count else 0)
        for index,button in enumerate(buttons):
            button.grid(row=index//count,column=index%count,sticky=sticky,padx=padx,pady=pady,ipady=ipady)
    frame.bind('<Configure>',resize,add='+')


def fit_table(tree):
    """Fit every column to the table; keep native row/keyboard navigation."""
    columns = tuple(tree['columns'])
    weights = {name: max(1, tree.column(name, 'width')) for name in columns}
    tree.configure(height=1)

    def resize(event):
        if event.widget is not tree or event.width < 2:
            return
        available = max(len(columns), event.width - 4)
        total = sum(weights.values())
        remaining = available
        for index, name in enumerate(columns):
            width = remaining if index == len(columns) - 1 else max(1, int(available * weights[name] / total))
            tree.column(name, width=width, minwidth=1, stretch=False)
            remaining -= width

    tree.bind('<Configure>', resize, add='+')
