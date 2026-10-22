import os
import tkinter as tk
from tkinter import filedialog, messagebox
import customtkinter as ctk
from typing import Optional, Callable

from src.config import SCAN_MODE_MODERATE, SCAN_MODE_DESCRIPTIONS
from src.scanner import InstalledApp
from src.ui.wizard_dialog import UninstallWizard

class ForcedUninstallDialog(ctk.CTkToplevel):
    def __init__(self, parent, on_complete: Optional[Callable[[], None]] = None):
        super().__init__(parent)
        self.parent = parent
        self.on_complete = on_complete

        self.title("Desinstalação Forçada - RevoClean")
        self.geometry("640x500")
        self.minsize(580, 440)
        self.transient(parent)
        self.grab_set()

        self.app_name_var = ctk.StringVar()
        self.target_path_var = ctk.StringVar()
        self.scan_mode_var = ctk.IntVar(value=SCAN_MODE_MODERATE)

        self._build_ui()

    def _build_ui(self):
        # Header
        header = ctk.CTkFrame(self, height=65, fg_color=("gray85", "#1e222d"), corner_radius=0)
        header.pack(fill="x", side="top")
        ctk.CTkLabel(
            header,
            text="⚡ Desinstalação Forçada",
            font=ctk.CTkFont(size=16, weight="bold")
        ).pack(side="left", padx=20, pady=15)

        content = ctk.CTkScrollableFrame(self)
        content.pack(fill="both", expand=True, padx=20, pady=15)

        info_card = ctk.CTkFrame(content, fg_color=("gray90", "#2b2f3d"), corner_radius=8)
        info_card.pack(fill="x", pady=(0, 15))
        ctk.CTkLabel(
            info_card,
            text="A Desinstalação Forçada permite remover sobras de programas cujo desinstalador original foi corrompido, excluído ou não aparece no Painel de Controle.",
            font=ctk.CTkFont(size=12),
            text_color="gray",
            wraplength=540,
            justify="left"
        ).pack(fill="x", padx=15, pady=12)

        # Nome do programa
        ctk.CTkLabel(content, text="Nome do Programa ou Termo de Busca:", font=ctk.CTkFont(weight="bold")).pack(anchor="w", pady=(5, 3))
        ctk.CTkEntry(
            content,
            placeholder_text="Ex: Antivírus X, Navegador Y, Programa Antigo...",
            textvariable=self.app_name_var,
            height=35
        ).pack(fill="x", pady=(0, 15))

        # Pasta ou executável
        ctk.CTkLabel(content, text="Pasta ou Executável do Programa (Opcional):", font=ctk.CTkFont(weight="bold")).pack(anchor="w", pady=(5, 3))
        path_frame = ctk.CTkFrame(content, fg_color="transparent")
        path_frame.pack(fill="x", pady=(0, 15))

        ctk.CTkEntry(
            path_frame,
            placeholder_text="C:\\Program Files\\Exemplo ou arquivo .exe...",
            textvariable=self.target_path_var,
            height=35
        ).pack(side="left", fill="x", expand=True, padx=(0, 8))

        ctk.CTkButton(
            path_frame,
            text="Procurar...",
            width=90,
            height=35,
            command=self._browse_target
        ).pack(side="right")

        # Modo de varredura
        ctk.CTkLabel(content, text="Modo de Varredura:", font=ctk.CTkFont(weight="bold")).pack(anchor="w", pady=(5, 5))
        for mode, data in SCAN_MODE_DESCRIPTIONS.items():
            card = ctk.CTkFrame(content, fg_color=("gray92", "#242834"), corner_radius=6)
            card.pack(fill="x", pady=3)
            ctk.CTkRadioButton(
                card,
                text=data["title"],
                value=mode,
                variable=self.scan_mode_var,
                font=ctk.CTkFont(size=13, weight="bold")
            ).pack(anchor="w", padx=12, pady=6)

        # Footer
        footer = ctk.CTkFrame(self, height=60, fg_color="transparent")
        footer.pack(fill="x", side="bottom", padx=20, pady=10)

        ctk.CTkButton(
            footer,
            text="Cancelar",
            fg_color="transparent",
            border_width=1,
            text_color=("black", "white"),
            command=self.destroy,
            width=100
        ).pack(side="left")

        ctk.CTkButton(
            footer,
            text="Iniciar Varredura Forçada ➔",
            font=ctk.CTkFont(weight="bold"),
            command=self._start_forced_flow,
            width=210
        ).pack(side="right")

    def _browse_target(self):
        choice = messagebox.askquestion(
            "Selecionar Alvo",
            "Deseja selecionar uma PASTA de programa?\n(Clique em 'Não' para selecionar um arquivo executável .EXE)",
            icon="question"
        )
        if choice == "yes":
            d = filedialog.askdirectory(title="Selecione a Pasta do Programa")
            if d:
                self.target_path_var.set(d)
                if not self.app_name_var.get():
                    self.app_name_var.set(os.path.basename(d))
        else:
            f = filedialog.askopenfilename(
                title="Selecione o Executável",
                filetypes=[("Executáveis (*.exe)", "*.exe"), ("Todos os Arquivos", "*.*")]
            )
            if f:
                self.target_path_var.set(f)
                if not self.app_name_var.get():
                    self.app_name_var.set(os.path.splitext(os.path.basename(f))[0])

    def _start_forced_flow(self):
        name = self.app_name_var.get().strip()
        path = self.target_path_var.get().strip()

        if not name and not path:
            messagebox.showwarning("Aviso", "Por favor, digite o nome do programa ou selecione um diretório/executável.")
            return

        if not name and path:
            name = os.path.basename(path.rstrip("\\/"))

        # Cria objeto de app sintético
        app_target = InstalledApp(
            name=name,
            publisher="",
            version="",
            install_date="",
            install_location=path if os.path.isdir(path) else os.path.dirname(path),
            uninstall_string="",
            quiet_uninstall_string="",
            display_icon="",
            estimated_size_bytes=0,
            is_system_component=False,
            is_windows_installer=False,
            registry_root="HKCU",
            registry_path="",
            key_name=name
        )

        self.destroy()

        # Abre o assistente direto na etapa de varredura profunda
        wizard = UninstallWizard(self.parent, app_target, on_complete=self.on_complete)
        wizard.scan_mode_var.set(self.scan_mode_var.get())
        wizard.create_restore_var.set(False)
        wizard.start_deep_scan()
