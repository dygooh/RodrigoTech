import os
import threading
import tkinter as tk
from tkinter import ttk, messagebox
import customtkinter as ctk
from typing import List, Optional, Callable

from src.config import (
    SCAN_MODE_SAFE, SCAN_MODE_MODERATE, SCAN_MODE_ADVANCED,
    SCAN_MODE_DESCRIPTIONS, BACKUP_DIR
)
from src.scanner import InstalledApp
from src.deep_cleaner import DeepCleaner, RegistryLeftover, FileLeftover
from src.uninstaller_runner import UninstallerRunner
from src.system_utils import (
    create_system_restore_point, send_to_recycle_bin,
    export_registry_key_backup, delete_registry_key_recursive,
    delete_registry_value, format_size
)

class UninstallWizard(ctk.CTkToplevel):
    def __init__(self, parent, app: InstalledApp, on_complete: Optional[Callable[[], None]] = None):
        super().__init__(parent)
        self.app_target = app
        self.on_complete = on_complete

        self.title(f"Assistente de Desinstalação - {self.app_target.name}")
        self.geometry("820x620")
        self.minsize(760, 560)
        self.transient(parent)
        self.grab_set()

        # Configurações do processo
        self.scan_mode_var = ctk.IntVar(value=SCAN_MODE_MODERATE)
        self.create_restore_var = ctk.BooleanVar(value=True)

        # Dados encontrados
        self.reg_leftovers: List[RegistryLeftover] = []
        self.file_leftovers: List[FileLeftover] = []

        # Estatísticas finais
        self.deleted_reg_count = 0
        self.deleted_files_count = 0
        self.freed_bytes = 0

        # Container principal
        self.main_container = ctk.CTkFrame(self, corner_radius=0)
        self.main_container.pack(fill="both", expand=True)

        # Inicia no Passo 1
        self.show_step_1_options()

    def clear_container(self):
        for widget in self.main_container.winfo_children():
            widget.destroy()

    # =========================================================================
    # PASSO 1: Configuração e Modo de Varredura
    # =========================================================================
    def show_step_1_options(self):
        self.clear_container()

        # Cabeçalho
        header = ctk.CTkFrame(self.main_container, height=65, fg_color=("gray85", "#1e222d"), corner_radius=0)
        header.pack(fill="x", side="top")
        ctk.CTkLabel(
            header,
            text="Etapa 1 de 4: Análise e Opções de Desinstalação",
            font=ctk.CTkFont(size=16, weight="bold")
        ).pack(side="left", padx=20, pady=15)

        content = ctk.CTkScrollableFrame(self.main_container)
        content.pack(fill="both", expand=True, padx=20, pady=15)

        # Card de informações do aplicativo
        info_card = ctk.CTkFrame(content, fg_color=("gray90", "#2b2f3d"), corner_radius=8)
        info_card.pack(fill="x", pady=(0, 15), padx=5)

        ctk.CTkLabel(
            info_card,
            text=f"📦 {self.app_target.name}",
            font=ctk.CTkFont(size=18, weight="bold"),
            anchor="w"
        ).pack(fill="x", padx=15, pady=(12, 4))

        details_text = (
            f"• Publicador: {self.app_target.publisher or 'Desconhecido'}\n"
            f"• Versão: {self.app_target.version or 'N/A'}\n"
            f"• Tamanho Estimado: {self.app_target.formatted_size}\n"
            f"• Local: {self.app_target.install_location or 'Registro do Windows'}\n"
            f"• Comando de Desinstalação: {self.app_target.uninstall_string or 'MSI Installer'}"
        )
        ctk.CTkLabel(info_card, text=details_text, font=ctk.CTkFont(size=12), justify="left", anchor="w").pack(
            fill="x", padx=15, pady=(0, 12)
        )

        # Opção de Ponto de Restauração
        restore_frame = ctk.CTkFrame(content, fg_color=("gray92", "#242834"), corner_radius=8)
        restore_frame.pack(fill="x", pady=5, padx=5)
        ctk.CTkCheckBox(
            restore_frame,
            text="Criar Ponto de Restauração do Windows antes de desinstalar (Recomendado)",
            variable=self.create_restore_var,
            font=ctk.CTkFont(size=13, weight="bold")
        ).pack(anchor="w", padx=15, pady=12)

        # Seleção do Modo de Varredura
        mode_label = ctk.CTkLabel(
            content,
            text="Selecione o Modo de Varredura de Sobras:",
            font=ctk.CTkFont(size=15, weight="bold"),
            anchor="w"
        )
        mode_label.pack(fill="x", pady=(15, 8), padx=5)

        for mode, data in SCAN_MODE_DESCRIPTIONS.items():
            card = ctk.CTkFrame(content, fg_color=("gray92", "#242834"), corner_radius=8)
            card.pack(fill="x", pady=4, padx=5)

            rb = ctk.CTkRadioButton(
                card,
                text=data["title"],
                value=mode,
                variable=self.scan_mode_var,
                font=ctk.CTkFont(size=14, weight="bold")
            )
            rb.pack(anchor="w", padx=15, pady=(10, 2))

            desc = ctk.CTkLabel(
                card,
                text=data["desc"],
                font=ctk.CTkFont(size=12),
                text_color="gray",
                anchor="w",
                justify="left",
                wraplength=700
            )
            desc.pack(fill="x", padx=35, pady=(0, 10))

        # Barra de rodapé com botões
        footer = ctk.CTkFrame(self.main_container, height=60, fg_color="transparent")
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
            text="Iniciar Desinstalação ➔",
            font=ctk.CTkFont(weight="bold"),
            command=self.start_uninstallation_flow,
            width=180
        ).pack(side="right")

    # =========================================================================
    # INÍCIO DO FLUXO: Ponto de restauração & Desinstalador Oficial
    # =========================================================================
    def start_uninstallation_flow(self):
        self.clear_container()

        # Cabeçalho
        header = ctk.CTkFrame(self.main_container, height=65, fg_color=("gray85", "#1e222d"), corner_radius=0)
        header.pack(fill="x", side="top")
        self.step2_title = ctk.CTkLabel(
            header,
            text="Etapa 2 de 4: Executando Desinstalador Padrão",
            font=ctk.CTkFont(size=16, weight="bold")
        )
        self.step2_title.pack(side="left", padx=20, pady=15)

        body = ctk.CTkFrame(self.main_container, fg_color="transparent")
        body.pack(fill="both", expand=True, padx=30, pady=40)

        self.step2_status_label = ctk.CTkLabel(
            body,
            text="Preparando desinstalação...",
            font=ctk.CTkFont(size=16, weight="bold")
        )
        self.step2_status_label.pack(pady=(20, 10))

        self.step2_sub_label = ctk.CTkLabel(
            body,
            text="Aguarde enquanto o processo é iniciado.",
            font=ctk.CTkFont(size=13),
            text_color="gray"
        )
        self.step2_sub_label.pack(pady=(0, 20))

        self.step2_progress = ctk.CTkProgressBar(body, mode="indeterminate", width=500)
        self.step2_progress.pack(pady=20)
        self.step2_progress.start()

        # Botão para avançar se o desinstalador terminar ou o usuário preferir ir direto
        footer = ctk.CTkFrame(self.main_container, height=60, fg_color="transparent")
        footer.pack(fill="x", side="bottom", padx=20, pady=15)

        self.btn_next_to_scan = ctk.CTkButton(
            footer,
            text="Avançar para Varredura de Sobras ➔",
            font=ctk.CTkFont(weight="bold"),
            command=self.start_deep_scan,
            width=260
        )
        self.btn_next_to_scan.pack(side="right")

        threading.Thread(target=self._run_uninstaller_worker, daemon=True).start()

    def _run_uninstaller_worker(self):
        # 1. Ponto de restauração
        if self.create_restore_var.get():
            self.step2_status_label.configure(text="Criando Ponto de Restauração do Windows...")
            self.step2_sub_label.configure(text="Isso garante que você possa reverter as alterações se necessário.")
            create_system_restore_point(f"Desinstalação RevoClean: {self.app_target.name}")

        # 2. Executa o desinstalador oficial
        self.step2_status_label.configure(text="Executando o desinstalador integrado...")
        self.step2_sub_label.configure(
            text="Conclua a desinstalação na janela do programa. Quando terminar, clique no botão abaixo para varrer as sobras residuais."
        )

        runner = UninstallerRunner(self.app_target)
        if runner.build_command():
            runner.launch()
            # Fica aguardando se o processo fechar
            runner.wait_completion(timeout=120)

        self.step2_status_label.configure(text="Desinstalador concluído!")
        self.step2_sub_label.configure(
            text="O desinstalador oficial terminou. Agora clique abaixo para buscar arquivos e registros que foram deixados para trás."
        )
        self.step2_progress.stop()
        self.step2_progress.set(1.0)

    # =========================================================================
    # PASSO 3: Varredura Profunda de Sobras
    # =========================================================================
    def start_deep_scan(self):
        self.clear_container()

        header = ctk.CTkFrame(self.main_container, height=65, fg_color=("gray85", "#1e222d"), corner_radius=0)
        header.pack(fill="x", side="top")
        ctk.CTkLabel(
            header,
            text="Etapa 3 de 4: Varredura Profunda de Resíduos",
            font=ctk.CTkFont(size=16, weight="bold")
        ).pack(side="left", padx=20, pady=15)

        body = ctk.CTkFrame(self.main_container, fg_color="transparent")
        body.pack(fill="both", expand=True, padx=40, pady=60)

        self.scan_status_title = ctk.CTkLabel(
            body,
            text="Analisando o Registro e o Sistema de Arquivos...",
            font=ctk.CTkFont(size=16, weight="bold")
        )
        self.scan_status_title.pack(pady=(20, 10))

        self.scan_sub_label = ctk.CTkLabel(
            body,
            text="Localizando chaves órfãs, dados de aplicativos e diretórios residuais...",
            font=ctk.CTkFont(size=13),
            text_color="gray"
        )
        self.scan_sub_label.pack(pady=(0, 25))

        self.scan_bar = ctk.CTkProgressBar(body, mode="determinate", width=520)
        self.scan_bar.pack(pady=10)
        self.scan_bar.set(0)

        threading.Thread(target=self._run_scan_worker, daemon=True).start()

    def _run_scan_worker(self):
        cleaner = DeepCleaner(self.app_target, scan_mode=self.scan_mode_var.get())

        def on_progress(msg: str, progress: float):
            self.after(0, lambda: self._update_scan_progress(msg, progress))

        self.reg_leftovers, self.file_leftovers = cleaner.scan_all(on_progress)
        self.after(500, self.show_step_4_results)

    def _update_scan_progress(self, msg: str, progress: float):
        self.scan_sub_label.configure(text=msg)
        self.scan_bar.set(progress)

    # =========================================================================
    # PASSO 4: Revisão e Exclusão de Sobras (Revo Style)
    # =========================================================================
    def show_step_4_results(self):
        self.clear_container()

        header = ctk.CTkFrame(self.main_container, height=65, fg_color=("gray85", "#1e222d"), corner_radius=0)
        header.pack(fill="x", side="top")
        ctk.CTkLabel(
            header,
            text="Etapa 4 de 4: Revisão e Limpeza de Sobras",
            font=ctk.CTkFont(size=16, weight="bold")
        ).pack(side="left", padx=20, pady=15)

        # Tabview para Registro e Arquivos
        self.tabview = ctk.CTkTabview(self.main_container)
        self.tabview.pack(fill="both", expand=True, padx=15, pady=(5, 5))

        reg_tab_title = f"Registro ({len(self.reg_leftovers)})"
        files_tab_title = f"Arquivos e Pastas ({len(self.file_leftovers)})"

        self.tabview.add(reg_tab_title)
        self.tabview.add(files_tab_title)

        # Popula Aba de Registro
        self._build_registry_tab(self.tabview.tab(reg_tab_title))

        # Popula Aba de Arquivos
        self._build_files_tab(self.tabview.tab(files_tab_title))

        # Rodapé
        footer = ctk.CTkFrame(self.main_container, height=60, fg_color="transparent")
        footer.pack(fill="x", side="bottom", padx=20, pady=10)

        ctk.CTkButton(
            footer,
            text="Pular Limpeza",
            fg_color="transparent",
            border_width=1,
            text_color=("black", "white"),
            command=self.show_summary,
            width=120
        ).pack(side="left")

        ctk.CTkButton(
            footer,
            text="🗑️ Excluir Selecionados",
            fg_color="#e03131",
            hover_color="#c92a2a",
            font=ctk.CTkFont(weight="bold"),
            command=self.execute_deletion,
            width=200
        ).pack(side="right")

    def _build_registry_tab(self, parent):
        info_frame = ctk.CTkFrame(parent, fg_color=("gray90", "#242834"), corner_radius=6)
        info_frame.pack(fill="x", padx=5, pady=5)
        ctk.CTkLabel(
            info_frame,
            text="🛡️ Segurança: Um backup em arquivo .reg será salvo automaticamente antes de qualquer exclusão.",
            font=ctk.CTkFont(size=12),
            text_color="gray"
        ).pack(side="left", padx=10, pady=8)

        # Ações em lote
        action_bar = ctk.CTkFrame(parent, fg_color="transparent")
        action_bar.pack(fill="x", padx=5, pady=2)

        ctk.CTkButton(action_bar, text="Selecionar Todos", width=110, height=26, font=ctk.CTkFont(size=11), command=self._select_all_reg).pack(side="left", padx=2)
        ctk.CTkButton(action_bar, text="Desmarcar Todos", width=110, height=26, font=ctk.CTkFont(size=11), fg_color="gray40", command=self._deselect_all_reg).pack(side="left", padx=2)

        # Treeview de Registros
        tree_frame = ctk.CTkFrame(parent)
        tree_frame.pack(fill="both", expand=True, padx=5, pady=5)

        columns = ("selected", "type", "path")
        self.reg_tree = ttk.Treeview(tree_frame, columns=columns, show="headings", selectmode="extended")
        self.reg_tree.heading("selected", text="[X]")
        self.reg_tree.heading("type", text="Tipo")
        self.reg_tree.heading("path", text="Caminho no Registro")

        self.reg_tree.column("selected", width=45, anchor="center")
        self.reg_tree.column("type", width=90, anchor="center")
        self.reg_tree.column("path", width=580, anchor="w")

        scrollbar = ttk.Scrollbar(tree_frame, orient="vertical", command=self.reg_tree.yview)
        self.reg_tree.configure(yscrollcommand=scrollbar.set)
        self.reg_tree.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")

        self.reg_tree.bind("<ButtonRelease-1>", self._on_reg_tree_click)

        for i, item in enumerate(self.reg_leftovers):
            sel_char = "☑" if item.selected else "☐"
            itype = "Valor" if item.value_name else "Chave"
            self.reg_tree.insert("", "end", iid=str(i), values=(sel_char, itype, item.display_path))

    def _build_files_tab(self, parent):
        info_frame = ctk.CTkFrame(parent, fg_color=("gray90", "#242834"), corner_radius=6)
        info_frame.pack(fill="x", padx=5, pady=5)
        ctk.CTkLabel(
            info_frame,
            text="♻️ Segurança: Todos os arquivos e pastas são movidos para a Lixeira do Windows.",
            font=ctk.CTkFont(size=12),
            text_color="gray"
        ).pack(side="left", padx=10, pady=8)

        # Ações em lote
        action_bar = ctk.CTkFrame(parent, fg_color="transparent")
        action_bar.pack(fill="x", padx=5, pady=2)

        ctk.CTkButton(action_bar, text="Selecionar Todos", width=110, height=26, font=ctk.CTkFont(size=11), command=self._select_all_files).pack(side="left", padx=2)
        ctk.CTkButton(action_bar, text="Desmarcar Todos", width=110, height=26, font=ctk.CTkFont(size=11), fg_color="gray40", command=self._deselect_all_files).pack(side="left", padx=2)

        # Treeview de Arquivos
        tree_frame = ctk.CTkFrame(parent)
        tree_frame.pack(fill="both", expand=True, padx=5, pady=5)

        columns = ("selected", "type", "size", "path")
        self.file_tree = ttk.Treeview(tree_frame, columns=columns, show="headings", selectmode="extended")
        self.file_tree.heading("selected", text="[X]")
        self.file_tree.heading("type", text="Tipo")
        self.file_tree.heading("size", text="Tamanho")
        self.file_tree.heading("path", text="Caminho Completo")

        self.file_tree.column("selected", width=45, anchor="center")
        self.file_tree.column("type", width=110, anchor="center")
        self.file_tree.column("size", width=90, anchor="e")
        self.file_tree.column("path", width=480, anchor="w")

        scrollbar = ttk.Scrollbar(tree_frame, orient="vertical", command=self.file_tree.yview)
        self.file_tree.configure(yscrollcommand=scrollbar.set)
        self.file_tree.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")

        self.file_tree.bind("<ButtonRelease-1>", self._on_file_tree_click)

        for i, item in enumerate(self.file_leftovers):
            sel_char = "☑" if item.selected else "☐"
            self.file_tree.insert("", "end", iid=str(i), values=(sel_char, item.item_type, item.formatted_size, item.path))

    def _on_reg_tree_click(self, event):
        item_id = self.reg_tree.identify_row(event.y)
        if item_id:
            idx = int(item_id)
            self.reg_leftovers[idx].selected = not self.reg_leftovers[idx].selected
            sel_char = "☑" if self.reg_leftovers[idx].selected else "☐"
            vals = list(self.reg_tree.item(item_id, "values"))
            vals[0] = sel_char
            self.reg_tree.item(item_id, values=vals)

    def _on_file_tree_click(self, event):
        item_id = self.file_tree.identify_row(event.y)
        if item_id:
            idx = int(item_id)
            self.file_leftovers[idx].selected = not self.file_leftovers[idx].selected
            sel_char = "☑" if self.file_leftovers[idx].selected else "☐"
            vals = list(self.file_tree.item(item_id, "values"))
            vals[0] = sel_char
            self.file_tree.item(item_id, values=vals)

    def _select_all_reg(self):
        for i, item in enumerate(self.reg_leftovers):
            item.selected = True
            vals = list(self.reg_tree.item(str(i), "values"))
            vals[0] = "☑"
            self.reg_tree.item(str(i), values=vals)

    def _deselect_all_reg(self):
        for i, item in enumerate(self.reg_leftovers):
            item.selected = False
            vals = list(self.reg_tree.item(str(i), "values"))
            vals[0] = "☐"
            self.reg_tree.item(str(i), values=vals)

    def _select_all_files(self):
        for i, item in enumerate(self.file_leftovers):
            item.selected = True
            vals = list(self.file_tree.item(str(i), "values"))
            vals[0] = "☑"
            self.file_tree.item(str(i), values=vals)

    def _deselect_all_files(self):
        for i, item in enumerate(self.file_leftovers):
            item.selected = False
            vals = list(self.file_tree.item(str(i), "values"))
            vals[0] = "☐"
            self.file_tree.item(str(i), values=vals)

    # =========================================================================
    # EXECUÇÃO DA LIMPEZA
    # =========================================================================
    def execute_deletion(self):
        sel_reg = [r for r in self.reg_leftovers if r.selected]
        sel_files = [f for f in self.file_leftovers if f.selected]

        if not sel_reg and not sel_files:
            messagebox.showinfo("Aviso", "Nenhum item selecionado para exclusão.")
            return

        confirm = messagebox.askyesno(
            "Confirmar Limpeza",
            f"Deseja excluir com segurança:\n\n"
            f"• {len(sel_reg)} item(ns) do Registro (com backup automático)\n"
            f"• {len(sel_files)} arquivo(s)/pasta(s) (enviados para a Lixeira)?",
            icon="warning"
        )
        if not confirm:
            return

        # 1. Deleta registros selecionados
        import winreg
        for item in sel_reg:
            if item.root_name not in ("HKCU", "HKLM"):
                continue
            root_hkey = winreg.HKEY_CURRENT_USER if item.root_name == "HKCU" else winreg.HKEY_LOCAL_MACHINE
            # Exporta backup antes
            export_registry_key_backup(item.root_name, item.key_path, self.app_target.name)

            if item.value_name:
                if delete_registry_value(root_hkey, item.key_path, item.value_name):
                    self.deleted_reg_count += 1
            else:
                if delete_registry_key_recursive(root_hkey, item.key_path):
                    self.deleted_reg_count += 1

        # 2. Deleta arquivos selecionados para lixeira
        for item in sel_files:
            if send_to_recycle_bin(item.path):
                self.deleted_files_count += 1
                self.freed_bytes += item.size_bytes

        self.show_summary()

    # =========================================================================
    # RESUMO FINAL
    # =========================================================================
    def show_summary(self):
        self.clear_container()

        header = ctk.CTkFrame(self.main_container, height=65, fg_color=("gray85", "#1e222d"), corner_radius=0)
        header.pack(fill="x", side="top")
        ctk.CTkLabel(
            header,
            text="Limpeza Concluída",
            font=ctk.CTkFont(size=16, weight="bold")
        ).pack(side="left", padx=20, pady=15)

        body = ctk.CTkFrame(self.main_container, fg_color="transparent")
        body.pack(fill="both", expand=True, padx=40, pady=40)

        ctk.CTkLabel(
            body,
            text="🎉 Desinstalação e Limpeza Finalizadas!",
            font=ctk.CTkFont(size=20, weight="bold"),
            text_color="#40c057"
        ).pack(pady=(20, 15))

        stats_card = ctk.CTkFrame(body, fg_color=("gray90", "#242834"), corner_radius=8)
        stats_card.pack(fill="x", pady=10, padx=20)

        stats_text = (
            f"• Chaves e valores de registro removidos: {self.deleted_reg_count}\n"
            f"• Arquivos e pastas residuais limpos: {self.deleted_files_count}\n"
            f"• Espaço em disco liberado: {format_size(self.freed_bytes)}\n\n"
            f"📁 Backups das chaves de registro salvos em:\n{BACKUP_DIR}"
        )
        ctk.CTkLabel(
            stats_card,
            text=stats_text,
            font=ctk.CTkFont(size=13),
            justify="left",
            anchor="w"
        ).pack(fill="x", padx=20, pady=20)

        footer = ctk.CTkFrame(self.main_container, height=60, fg_color="transparent")
        footer.pack(fill="x", side="bottom", padx=20, pady=15)

        ctk.CTkButton(
            footer,
            text="Concluir e Fechar",
            font=ctk.CTkFont(weight="bold"),
            command=self._on_finish_all,
            width=180
        ).pack(side="right")

    def _on_finish_all(self):
        if self.on_complete:
            self.on_complete()
        self.destroy()
