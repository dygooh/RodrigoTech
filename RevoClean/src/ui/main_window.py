import os
import sys
import csv
import webbrowser
import threading
import tkinter as tk
from tkinter import ttk, filedialog, messagebox
import customtkinter as ctk
from typing import List, Optional

from src.config import APP_TITLE, APP_VERSION, BACKUP_DIR
from src.scanner import ProgramScanner, InstalledApp
from src.system_utils import (
    is_admin, request_admin_elevation, open_in_explorer,
    open_in_regedit, create_system_restore_point, format_size
)
from src.ui.wizard_dialog import UninstallWizard
from src.ui.forced_dialog import ForcedUninstallDialog

class MainWindow(ctk.CTk):
    def __init__(self):
        super().__init__()

        self.title(f"{APP_TITLE} v{APP_VERSION}")
        self.geometry("1150x720")
        self.minsize(920, 560)

        # Estado
        self.is_dark_theme = True
        ctk.set_appearance_mode("dark")
        ctk.set_default_color_theme("blue")

        self.scanner = ProgramScanner()
        self.all_apps: List[InstalledApp] = []
        self.displayed_apps: List[InstalledApp] = []
        self.type_filter = "Todos"
        self.sort_column = "name"
        self.sort_reverse = False

        self._configure_tree_styles()
        self._build_ui()
        self.refresh_programs()

    def _configure_tree_styles(self):
        style = ttk.Style()
        style.theme_use("clam")

        # Configura tema escuro para o Treeview
        style.configure(
            "Treeview",
            background="#1e222d",
            foreground="#e0e0e0",
            fieldbackground="#1e222d",
            rowheight=28,
            font=("Segoe UI", 10),
            borderwidth=0
        )
        style.map(
            "Treeview",
            background=[("selected", "#1971c2")],
            foreground=[("selected", "#ffffff")]
        )
        style.configure(
            "Treeview.Heading",
            background="#2b2f3d",
            foreground="#ffffff",
            relief="flat",
            font=("Segoe UI", 10, "bold"),
            padding=5
        )
        style.map(
            "Treeview.Heading",
            background=[("active", "#373e4f")]
        )

    def _build_ui(self):
        # 1. Top Bar: Título, Pesquisa e Controles de Tema / Admin
        top_bar = ctk.CTkFrame(self, height=65, corner_radius=0, fg_color=("gray90", "#181b24"))
        top_bar.pack(fill="x", side="top")

        # Título / Logo
        logo_frame = ctk.CTkFrame(top_bar, fg_color="transparent")
        logo_frame.pack(side="left", padx=20, pady=12)

        ctk.CTkLabel(
            logo_frame,
            text="⚡ RevoClean",
            font=ctk.CTkFont(size=20, weight="bold"),
            text_color=("#1971c2", "#339af0")
        ).pack(side="left")

        ctk.CTkLabel(
            logo_frame,
            text=" PORTABLE",
            font=ctk.CTkFont(size=11, weight="bold"),
            text_color="gray"
        ).pack(side="left", padx=(4, 0), pady=(4, 0))

        # Admin Badge
        admin_text = "🛡️ Administrador" if is_admin() else "⚠️ Elevar para Admin"
        admin_color = ("#2b8a3e", "#2f9e44") if is_admin() else ("#d9480f", "#f76707")
        btn_admin = ctk.CTkButton(
            top_bar,
            text=admin_text,
            width=130,
            height=30,
            fg_color=admin_color,
            font=ctk.CTkFont(size=11, weight="bold"),
            command=self._handle_elevation
        )
        btn_admin.pack(side="right", padx=15, pady=15)

        # Tema Toggle
        self.btn_theme = ctk.CTkButton(
            top_bar,
            text="🌙 Tema",
            width=70,
            height=30,
            fg_color="transparent",
            border_width=1,
            text_color=("black", "white"),
            command=self._toggle_theme
        )
        self.btn_theme.pack(side="right", padx=(0, 10), pady=15)

        # Campo de Pesquisa em tempo real
        search_frame = ctk.CTkFrame(top_bar, fg_color="transparent")
        search_frame.pack(side="right", padx=15, pady=15)

        self.search_var = ctk.StringVar()
        self.search_var.trace_add("write", lambda *args: self._on_search_changed())

        search_entry = ctk.CTkEntry(
            search_frame,
            placeholder_text="🔍 Pesquisar programas ou apps...",
            textvariable=self.search_var,
            width=260,
            height=32
        )
        search_entry.pack(side="left")

        # 2. Barra de Ferramentas / Ações
        toolbar = ctk.CTkFrame(self, height=50, corner_radius=0, fg_color=("gray85", "#242834"))
        toolbar.pack(fill="x", side="top", padx=0, pady=0)

        # Botão Desinstalar Principal
        ctk.CTkButton(
            toolbar,
            text="🗑️ Desinstalar",
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#e03131",
            hover_color="#c92a2a",
            width=120,
            height=32,
            command=self.start_uninstall_selected
        ).pack(side="left", padx=(15, 6), pady=8)

        # Desinstalação Forçada
        ctk.CTkButton(
            toolbar,
            text="⚡ Desinstalação Forçada",
            font=ctk.CTkFont(size=12, weight="bold"),
            fg_color="#f08c00",
            hover_color="#e67700",
            width=170,
            height=32,
            command=self.open_forced_uninstall
        ).pack(side="left", padx=6, pady=8)

        # Atualizar
        ctk.CTkButton(
            toolbar,
            text="🔄 Atualizar",
            width=100,
            height=32,
            fg_color=("gray75", "#373e4f"),
            hover_color=("gray65", "#4c556b"),
            command=self.refresh_programs
        ).pack(side="left", padx=6, pady=8)

        # Ponto de Restauração
        ctk.CTkButton(
            toolbar,
            text="🛡️ Ponto de Restauração",
            width=165,
            height=32,
            fg_color=("gray75", "#373e4f"),
            hover_color=("gray65", "#4c556b"),
            command=self.create_restore_point_dialog
        ).pack(side="left", padx=6, pady=8)

        # Filtro de Categoria (Todos / Desktop / Windows Store)
        self.seg_filter = ctk.CTkSegmentedButton(
            toolbar,
            values=["Todos", "Desktop", "Windows Store"],
            command=self._on_filter_type_changed,
            height=30
        )
        self.seg_filter.set("Todos")
        self.seg_filter.pack(side="left", padx=15, pady=8)

        # Exportar Lista
        ctk.CTkButton(
            toolbar,
            text="💾 Exportar",
            width=90,
            height=32,
            fg_color=("gray75", "#373e4f"),
            hover_color=("gray65", "#4c556b"),
            command=self.export_apps_list
        ).pack(side="right", padx=(6, 15), pady=8)

        # Abrir Pasta
        ctk.CTkButton(
            toolbar,
            text="📂 Abrir Pasta",
            width=110,
            height=32,
            fg_color=("gray75", "#373e4f"),
            hover_color=("gray65", "#4c556b"),
            command=self.open_selected_folder
        ).pack(side="right", padx=6, pady=8)

        # 3. Tabela de Programas
        table_frame = ctk.CTkFrame(self, corner_radius=0)
        table_frame.pack(fill="both", expand=True, padx=15, pady=10)

        columns = ("name", "type", "publisher", "version", "install_date", "size", "location")
        self.tree = ttk.Treeview(table_frame, columns=columns, show="headings", selectmode="browse")

        self.tree.heading("name", text="Nome do Programa ▾", command=lambda: self._sort_by("name"))
        self.tree.heading("type", text="Tipo", command=lambda: self._sort_by("type"))
        self.tree.heading("publisher", text="Editor / Publicador", command=lambda: self._sort_by("publisher"))
        self.tree.heading("version", text="Versão", command=lambda: self._sort_by("version"))
        self.tree.heading("install_date", text="Data", command=lambda: self._sort_by("install_date"))
        self.tree.heading("size", text="Tamanho", command=lambda: self._sort_by("size"))
        self.tree.heading("location", text="Local de Instalação", command=lambda: self._sort_by("location"))

        self.tree.column("name", width=260, anchor="w")
        self.tree.column("type", width=95, anchor="center")
        self.tree.column("publisher", width=180, anchor="w")
        self.tree.column("version", width=100, anchor="w")
        self.tree.column("install_date", width=95, anchor="center")
        self.tree.column("size", width=100, anchor="e")
        self.tree.column("location", width=280, anchor="w")

        v_scroll = ttk.Scrollbar(table_frame, orient="vertical", command=self.tree.yview)
        h_scroll = ttk.Scrollbar(table_frame, orient="horizontal", command=self.tree.xview)
        self.tree.configure(yscrollcommand=v_scroll.set, xscrollcommand=h_scroll.set)

        self.tree.grid(row=0, column=0, sticky="nsew")
        v_scroll.grid(row=0, column=1, sticky="ns")
        h_scroll.grid(row=1, column=0, sticky="ew")

        table_frame.grid_rowconfigure(0, weight=1)
        table_frame.grid_columnconfigure(0, weight=1)

        # Eventos
        self.tree.bind("<Double-1>", lambda e: self.start_uninstall_selected())
        self.tree.bind("<Button-3>", self._show_context_menu)
        self.tree.bind("<<TreeviewSelect>>", self._on_item_selected)

        # Menu de contexto (botão direito)
        self.context_menu = tk.Menu(self, tearoff=0)
        self.context_menu.add_command(label="🗑️ Desinstalar", command=self.start_uninstall_selected)
        self.context_menu.add_command(label="⚡ Desinstalação Forçada", command=self.open_forced_uninstall)
        self.context_menu.add_separator()
        self.context_menu.add_command(label="📂 Abrir Local da Instalação", command=self.open_selected_folder)
        self.context_menu.add_command(label="📑 Abrir Chave no Regedit", command=self.open_selected_regedit)
        self.context_menu.add_command(label="🌐 Pesquisar no Google", command=self.search_selected_web)
        self.context_menu.add_separator()
        self.context_menu.add_command(label="📋 Copiar Nome do Programa", command=self._copy_app_name)

        # 4. Barra de Status Inferior
        self.status_bar = ctk.CTkFrame(self, height=32, corner_radius=0, fg_color=("gray90", "#181b24"))
        self.status_bar.pack(fill="x", side="bottom")

        self.lbl_status_total = ctk.CTkLabel(
            self.status_bar,
            text="Carregando programas e aplicativos da loja...",
            font=ctk.CTkFont(size=11),
            text_color="gray"
        )
        self.lbl_status_total.pack(side="left", padx=15, pady=4)

        self.lbl_status_selected = ctk.CTkLabel(
            self.status_bar,
            text="",
            font=ctk.CTkFont(size=11, weight="bold")
        )
        self.lbl_status_selected.pack(side="right", padx=15, pady=4)

    # =========================================================================
    # CARREGAMENTO E ATUALIZAÇÃO
    # =========================================================================
    def refresh_programs(self):
        self.lbl_status_total.configure(text="Varrendo programas do Desktop e aplicativos da Microsoft Store...")
        threading.Thread(target=self._scan_thread, daemon=True).start()

    def _scan_thread(self):
        apps = self.scanner.get_installed_programs(force_refresh=True)
        self.after(0, lambda: self._update_apps_list(apps))

    def _update_apps_list(self, apps: List[InstalledApp]):
        self.all_apps = apps
        self._apply_filter()

    def _on_search_changed(self):
        self._apply_filter()

    def _on_filter_type_changed(self, choice: str):
        self.type_filter = choice
        self._apply_filter()

    def _apply_filter(self):
        query = self.search_var.get().strip().lower()
        
        filtered = self.all_apps
        if self.type_filter == "Desktop":
            filtered = [a for a in filtered if a.app_type == "win32"]
        elif self.type_filter == "Windows Store":
            filtered = [a for a in filtered if a.app_type == "store"]

        if query:
            filtered = [
                a for a in filtered
                if query in a.name.lower() or query in a.publisher.lower() or query in a.version.lower()
            ]

        self.displayed_apps = filtered
        self._sort_and_populate()

    def _sort_by(self, col: str):
        if self.sort_column == col:
            self.sort_reverse = not self.sort_reverse
        else:
            self.sort_column = col
            self.sort_reverse = False
        self._sort_and_populate()

    def _sort_and_populate(self):
        key_map = {
            "name": lambda a: a.name.lower(),
            "type": lambda a: a.type_badge,
            "publisher": lambda a: a.publisher.lower(),
            "version": lambda a: a.version.lower(),
            "install_date": lambda a: a.install_date,
            "size": lambda a: a.estimated_size_bytes,
            "location": lambda a: a.install_location.lower(),
        }
        sorter = key_map.get(self.sort_column, lambda a: a.name.lower())
        self.displayed_apps.sort(key=sorter, reverse=self.sort_reverse)

        # Limpa e repopula treeview
        for item in self.tree.get_children():
            self.tree.delete(item)

        total_bytes = 0
        win32_count = 0
        store_count = 0

        for i, app in enumerate(self.displayed_apps):
            total_bytes += app.estimated_size_bytes
            if app.app_type == "store":
                store_count += 1
            else:
                win32_count += 1

            self.tree.insert(
                "",
                "end",
                iid=str(i),
                values=(
                    app.name,
                    app.type_badge,
                    app.publisher or "-",
                    app.version or "-",
                    app.install_date or "-",
                    app.formatted_size,
                    app.install_location or "-"
                )
            )

        self.lbl_status_total.configure(
            text=f"Total: {len(self.displayed_apps)} apps ({win32_count} Desktop, {store_count} Loja) | Espaço total ocupado: {format_size(total_bytes)}"
        )

    def _on_item_selected(self, event):
        app = self.get_selected_app()
        if app:
            self.lbl_status_selected.configure(
                text=f"Selecionado: {app.name} [{app.type_badge}] ({app.formatted_size})"
            )

    def get_selected_app(self) -> Optional[InstalledApp]:
        sel = self.tree.selection()
        if not sel:
            return None
        idx = int(sel[0])
        if 0 <= idx < len(self.displayed_apps):
            return self.displayed_apps[idx]
        return None

    # =========================================================================
    # AÇÕES
    # =========================================================================
    def start_uninstall_selected(self):
        app = self.get_selected_app()
        if not app:
            messagebox.showinfo("Aviso", "Selecione um programa na lista para desinstalar.")
            return

        UninstallWizard(self, app, on_complete=self.refresh_programs)

    def open_forced_uninstall(self):
        ForcedUninstallDialog(self, on_complete=self.refresh_programs)

    def open_selected_folder(self):
        app = self.get_selected_app()
        if not app or not app.install_location or not os.path.exists(app.install_location):
            messagebox.showwarning("Aviso", "Pasta de instalação não informada ou não acessível.")
            return
        open_in_explorer(app.install_location)

    def open_selected_regedit(self):
        app = self.get_selected_app()
        if not app or app.app_type == "store":
            messagebox.showinfo("Aviso", "Aplicativos da Loja Windows são gerenciados pelo repositório AppX e não pelo Painel de Controle clássico.")
            return
        if not app.registry_path:
            messagebox.showwarning("Aviso", "Chave de registro não disponível.")
            return
        open_in_regedit(app.registry_root, app.registry_path)

    def search_selected_web(self):
        app = self.get_selected_app()
        if app:
            query = f"{app.name} {app.publisher}".strip()
            webbrowser.open(f"https://www.google.com/search?q={query}")

    def _copy_app_name(self):
        app = self.get_selected_app()
        if app:
            self.clipboard_clear()
            self.clipboard_append(app.name)

    def _show_context_menu(self, event):
        item = self.tree.identify_row(event.y)
        if item:
            self.tree.selection_set(item)
            self._on_item_selected(None)
            self.context_menu.tk_popup(event.x_root, event.y_root)

    def create_restore_point_dialog(self):
        confirm = messagebox.askyesno(
            "Ponto de Restauração",
            "Deseja criar um Ponto de Restauração do Windows agora?",
            icon="question"
        )
        if not confirm:
            return

        self.lbl_status_total.configure(text="Criando ponto de restauração do sistema...")
        def worker():
            ok, msg = create_system_restore_point("RevoClean Ponto Manual")
            self.after(0, lambda: messagebox.showinfo("Ponto de Restauração", msg) if ok else messagebox.showwarning("Aviso", msg))
            self.after(0, lambda: self.lbl_status_total.configure(text=f"Total: {len(self.displayed_apps)} programas"))

        threading.Thread(target=worker, daemon=True).start()

    def export_apps_list(self):
        path = filedialog.asksaveasfilename(
            title="Exportar Lista de Programas",
            defaultextension=".csv",
            filetypes=[("Arquivo CSV (*.csv)", "*.csv"), ("Arquivo de Texto (*.txt)", "*.txt")]
        )
        if not path:
            return

        try:
            if path.endswith(".csv"):
                with open(path, "w", newline="", encoding="utf-8-sig") as f:
                    writer = csv.writer(f, delimiter=";")
                    writer.writerow(["Nome", "Tipo", "Editor", "Versao", "Data de Instalacao", "Tamanho", "Local"])
                    for a in self.displayed_apps:
                        writer.writerow([a.name, a.type_badge, a.publisher, a.version, a.install_date, a.formatted_size, a.install_location])
            else:
                with open(path, "w", encoding="utf-8") as f:
                    for a in self.displayed_apps:
                        f.write(f"{a.name} [{a.type_badge}] | {a.publisher} | {a.version} | {a.formatted_size} | {a.install_location}\n")
            messagebox.showinfo("Sucesso", f"Lista exportada com sucesso para:\n{path}")
        except Exception as e:
            messagebox.showerror("Erro ao Exportar", str(e))

    def _handle_elevation(self):
        if is_admin():
            messagebox.showinfo("Status", "O RevoClean já está em execução com privilégios de Administrador.")
        else:
            request_admin_elevation()

    def _toggle_theme(self):
        self.is_dark_theme = not self.is_dark_theme
        if self.is_dark_theme:
            ctk.set_appearance_mode("dark")
            self.btn_theme.configure(text="🌙 Tema")
            self._configure_tree_styles()
        else:
            ctk.set_appearance_mode("light")
            self.btn_theme.configure(text="☀️ Tema")
            style = ttk.Style()
            style.configure(
                "Treeview",
                background="#ffffff",
                foreground="#000000",
                fieldbackground="#ffffff",
                rowheight=28
            )
            style.configure(
                "Treeview.Heading",
                background="#e9ecef",
                foreground="#000000"
            )
