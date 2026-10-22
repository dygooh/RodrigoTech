import os
import re
import winreg
from dataclasses import dataclass
from typing import List, Set, Dict, Optional, Tuple, Callable
from src.config import (
    SCAN_MODE_SAFE, SCAN_MODE_MODERATE, SCAN_MODE_ADVANCED,
    GENERIC_SEARCH_EXCLUSIONS, SYSTEM_PROTECTED_DIRS, PROTECTED_REGISTRY_KEYS
)
from src.system_utils import (
    is_path_safe_to_delete, is_registry_key_safe_to_delete,
    get_directory_size, format_size
)
from src.scanner import InstalledApp

@dataclass
class RegistryLeftover:
    root_name: str         # "HKCU" ou "HKLM"
    key_path: str          # ex: "Software\\MyApp"
    value_name: Optional[str] = None  # None se for a chave inteira, ou nome do valor
    value_data: Optional[str] = None
    selected: bool = True

    @property
    def display_path(self) -> str:
        if self.value_name:
            return f"{self.root_name}\\{self.key_path} -> [{self.value_name}]"
        return f"{self.root_name}\\{self.key_path}"


@dataclass
class FileLeftover:
    path: str
    item_type: str         # "Pasta", "Arquivo", "Atalho"
    size_bytes: int
    selected: bool = True

    @property
    def formatted_size(self) -> str:
        return format_size(self.size_bytes)


class DeepCleaner:
    def __init__(self, app: InstalledApp, scan_mode: int = SCAN_MODE_MODERATE):
        self.app = app
        self.scan_mode = scan_mode
        self.app_tokens, self.pub_tokens = self._extract_keywords()
        self.search_tokens = list(dict.fromkeys(self.app_tokens + self.pub_tokens))

    def _extract_keywords(self) -> Tuple[List[str], List[str]]:
        """Extrai palavras-chave separando termos do aplicativo e do publicador."""
        # Termos do aplicativo
        raw_app_words = re.findall(r'[A-Za-z0-9_\-\.]+', self.app.name)
        app_tokens = []
        for w in raw_app_words:
            w_clean = w.strip().lower()
            if (
                len(w_clean) >= 3
                and w_clean not in GENERIC_SEARCH_EXCLUSIONS
                and not re.match(r'^\d+(\.\d+)*$', w_clean)
            ):
                app_tokens.append(w_clean)

        # Nome completo simplificado do app
        simplified_name = re.sub(r'\(.*?\)|\[.*?\]', '', self.app.name).strip().lower()
        if len(simplified_name) >= 3 and simplified_name not in GENERIC_SEARCH_EXCLUSIONS:
            if simplified_name not in app_tokens:
                app_tokens.insert(0, simplified_name)

        # Termos do publicador
        pub_tokens = []
        if self.app.publisher:
            for w in re.findall(r'[A-Za-z0-9_\-\.]+', self.app.publisher):
                w_clean = w.strip().lower()
                if (
                    len(w_clean) >= 3
                    and w_clean not in GENERIC_SEARCH_EXCLUSIONS
                    and not re.match(r'^\d+(\.\d+)*$', w_clean)
                ):
                    pub_tokens.append(w_clean)

        return app_tokens, pub_tokens

    def scan_all(self, progress_callback: Optional[Callable[[str, float], None]] = None) -> Tuple[List[RegistryLeftover], List[FileLeftover]]:
        """Executa a varredura completa de sobras de registro e arquivos."""
        if progress_callback:
            progress_callback("Iniciando varredura profunda no Registro...", 0.1)

        reg_leftovers = self.scan_registry(progress_callback)

        if progress_callback:
            progress_callback("Varrendo arquivos e diretórios residuais...", 0.6)

        file_leftovers = self.scan_files(progress_callback)

        if progress_callback:
            progress_callback("Varredura concluída!", 1.0)

        return reg_leftovers, file_leftovers

    def scan_registry(self, progress_callback: Optional[Callable[[str, float], None]] = None) -> List[RegistryLeftover]:
        """Varre o Registro procurando por chaves abandonadas."""
        leftovers: List[RegistryLeftover] = []
        seen_keys: Set[str] = set()

        def add_reg(root_name: str, key_path: str, value_name: Optional[str] = None, val_data: Optional[str] = None):
            full_id = f"{root_name}\\{key_path}\\{value_name or ''}".lower()
            if full_id in seen_keys:
                return
            if not is_registry_key_safe_to_delete(key_path):
                return

            # Safeguard: se for apenas o nome da empresa/publicador (ex: Software\Google) e contiver outros produtos, não adiciona a chave mãe
            key_parts = key_path.strip('\\').split('\\')
            last_part = key_parts[-1].lower()
            if self.app.publisher and (last_part in [p.lower() for p in self.pub_tokens] or last_part == self.app.publisher.lower()):
                root_hkey = winreg.HKEY_CURRENT_USER if root_name == "HKCU" else winreg.HKEY_LOCAL_MACHINE
                for access_flag in [0, winreg.KEY_WOW64_32KEY, winreg.KEY_WOW64_64KEY]:
                    try:
                        with winreg.OpenKey(root_hkey, key_path, 0, winreg.KEY_READ | access_flag) as test_k:
                            subkeys_count = winreg.QueryInfoKey(test_k)[0]
                            if subkeys_count > 0:
                                has_other_products = False
                                for idx in range(subkeys_count):
                                    sk_name = winreg.EnumKey(test_k, idx).lower()
                                    if not any(t in sk_name for t in self.app_tokens if t != last_part):
                                        has_other_products = True
                                        break
                                if has_other_products:
                                    return
                    except OSError:
                        pass

            seen_keys.add(full_id)
            leftovers.append(RegistryLeftover(
                root_name=root_name,
                key_path=key_path,
                value_name=value_name,
                value_data=val_data
            ))

        roots = [
            ("HKCU", winreg.HKEY_CURRENT_USER),
            ("HKLM", winreg.HKEY_LOCAL_MACHINE),
        ]

        # 1. Verifica se a chave de desinstalação original ainda existe
        if self.app.registry_path and self.app.registry_root in ("HKCU", "HKLM"):
            add_reg(self.app.registry_root, self.app.registry_path)

        # 2. Verifica caminhos diretos padrão: Software\<Publisher>\<App> e Software\<App>
        for root_name, root_hkey in roots:
            flags_list = [0]
            if root_name == "HKLM":
                flags_list.append(winreg.KEY_WOW64_32KEY)

            for flag in flags_list:
                for token in self.app_tokens:
                    candidates = [
                        f"Software\\{token}",
                        f"Software\\{self.app.publisher}\\{token}" if self.app.publisher else None,
                        f"Software\\WOW6432Node\\{token}" if root_name == "HKLM" else None,
                        f"Software\\WOW6432Node\\{self.app.publisher}\\{token}" if (root_name == "HKLM" and self.app.publisher) else None
                    ]
                    for pub in self.pub_tokens:
                        candidates.append(f"Software\\{pub}\\{token}")
                        if root_name == "HKLM":
                            candidates.append(f"Software\\WOW6432Node\\{pub}\\{token}")

                    for cand in candidates:
                        if not cand:
                            continue
                        try:
                            with winreg.OpenKey(root_hkey, cand, 0, winreg.KEY_READ | flag):
                                add_reg(root_name, cand)
                        except OSError:
                            pass

        # 3. Modo Moderado ou Avançado: busca em chaves de Run / Startup
        if self.scan_mode >= SCAN_MODE_MODERATE:
            run_paths = [
                r"Software\Microsoft\Windows\CurrentVersion\Run",
                r"Software\Microsoft\Windows\CurrentVersion\RunOnce"
            ]
            for root_name, root_hkey in roots:
                for rp in run_paths:
                    try:
                        with winreg.OpenKey(root_hkey, rp, 0, winreg.KEY_READ) as rkey:
                            vals_count = winreg.QueryInfoKey(rkey)[1]
                            for i in range(vals_count):
                                val_name, val_val, _ = winreg.EnumValue(rkey, i)
                                val_str = str(val_val).lower()
                                for token in self.search_tokens:
                                    if token in val_name.lower() or token in val_str:
                                        add_reg(root_name, rp, value_name=val_name, val_data=str(val_val))
                                        break
                    except OSError:
                        pass

        # 4. Modo Avançado: Varredura de subchaves sob HKCU\Software e HKLM\Software
        if self.scan_mode >= SCAN_MODE_ADVANCED:
            for root_name, root_hkey in roots:
                try:
                    with winreg.OpenKey(root_hkey, "Software", 0, winreg.KEY_READ) as s_key:
                        subs_count = winreg.QueryInfoKey(s_key)[0]
                        for i in range(subs_count):
                            sub = winreg.EnumKey(s_key, i)
                            sub_lower = sub.lower()
                            for token in self.search_tokens:
                                if token == sub_lower or (len(token) > 4 and token in sub_lower):
                                    add_reg(root_name, f"Software\\{sub}")
                                    break
                except OSError:
                    pass

        return leftovers

    def scan_files(self, progress_callback: Optional[Callable[[str, float], None]] = None) -> List[FileLeftover]:
        """Varre o sistema de arquivos procurando resíduos e pastas deixadas para trás."""
        leftovers: List[FileLeftover] = []
        seen_paths: Set[str] = set()

        def add_file_or_dir(item_path: str, item_type: Optional[str] = None):
            if not item_path or not os.path.exists(item_path):
                return
            norm = os.path.normpath(os.path.abspath(item_path))
            norm_lower = norm.lower()

            if norm_lower in seen_paths:
                return
            if not is_path_safe_to_delete(norm):
                return

            is_dir = os.path.isdir(norm)

            # Safeguard: se for um diretório de fornecedor/publicador que contém outros produtos de terceiros/irmãos, não deve ser adicionado por inteiro
            if is_dir:
                dir_name = os.path.basename(norm).lower()
                # Se o nome do diretório é apenas o publicador e o app tem nome diferente, verifica se tem outros filhos
                if self.app.publisher and (dir_name in [p.lower() for p in self.pub_tokens] or dir_name == self.app.publisher.lower()):
                    try:
                        subdirs = [e.name.lower() for e in os.scandir(norm) if e.is_dir()]
                        # Se há outros subdiretórios além dos que batem com o app, não adiciona a pasta mãe
                        has_unrelated_subdirs = any(
                            not any(tok in s for tok in self.app_tokens if tok != dir_name)
                            for s in subdirs
                        )
                        if has_unrelated_subdirs:
                            return
                    except (PermissionError, OSError):
                        pass

            seen_paths.add(norm_lower)
            detected_type = item_type or ("Pasta" if is_dir else ("Atalho" if norm.endswith(".lnk") else "Arquivo"))
            size = get_directory_size(norm) if is_dir else os.path.getsize(norm)

            leftovers.append(FileLeftover(
                path=norm,
                item_type=detected_type,
                size_bytes=size
            ))

        # 1. Pasta de instalação original informada pelo registro
        if self.app.install_location and os.path.exists(self.app.install_location):
            add_file_or_dir(self.app.install_location, "Pasta de Instalação")

        # 1.1 Pasta de dados do pacote AppX (Microsoft Store)
        localappdata = os.environ.get("LOCALAPPDATA", "")
        if getattr(self.app, "package_family_name", "") and localappdata:
            appx_data_dir = os.path.join(localappdata, "Packages", self.app.package_family_name)
            if os.path.exists(appx_data_dir):
                add_file_or_dir(appx_data_dir, "Dados do Aplicativo (Loja)")

            # Atalho de execução em WindowsApps
            for token in self.app_tokens:
                alias = os.path.join(localappdata, "Microsoft", "WindowsApps", f"{token}.exe")
                if os.path.exists(alias):
                    add_file_or_dir(alias, "Atalho de Inicialização")

        # 2. Pastas comuns de dados e programas
        appdata = os.environ.get("APPDATA", "")
        localappdata = os.environ.get("LOCALAPPDATA", "")
        progdata = os.environ.get("ProgramData", r"C:\ProgramData")
        prog_files = os.environ.get("ProgramFiles", r"C:\Program Files")
        prog_files_x86 = os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")
        user_profile = os.environ.get("USERPROFILE", "")

        base_search_dirs = [
            appdata,
            localappdata,
            os.path.join(localappdata, "Programs"),
            progdata,
            prog_files,
            prog_files_x86,
        ]

        if self.scan_mode >= SCAN_MODE_MODERATE:
            for base in base_search_dirs:
                if not base or not os.path.isdir(base):
                    continue

                for token in self.app_tokens:
                    candidates = [
                        os.path.join(base, token),
                        os.path.join(base, self.app.publisher, token) if self.app.publisher else None
                    ]
                    for pub in self.pub_tokens:
                        candidates.append(os.path.join(base, pub, token))

                    for cand in candidates:
                        if cand and os.path.exists(cand):
                            add_file_or_dir(cand, "Pasta Residual")

                    # Varre subpastas de 1º nível caso o nome seja similar
                    try:
                        with os.scandir(base) as it:
                            for entry in it:
                                if entry.is_dir():
                                    entry_lower = entry.name.lower()
                                    if token == entry_lower or (len(token) > 4 and token in entry_lower):
                                        add_file_or_dir(entry.path, "Pasta Residual")
                    except (PermissionError, OSError):
                        pass

        # 3. Atalhos na Área de Trabalho e Menu Iniciar
        shortcut_dirs = [
            os.path.join(user_profile, "Desktop"),
            r"C:\Users\Public\Desktop",
            os.path.join(appdata, r"Microsoft\Windows\Start Menu\Programs"),
            os.path.join(progdata, r"Microsoft\Windows\Start Menu\Programs")
        ]

        for sdir in shortcut_dirs:
            if not os.path.isdir(sdir):
                continue
            try:
                for root, dirs, files in os.walk(sdir):
                    for f in files:
                        if f.lower().endswith(".lnk"):
                            f_lower = f.lower()
                            for token in self.search_tokens:
                                if token in f_lower:
                                    add_file_or_dir(os.path.join(root, f), "Atalho")
                                    break
                    # Também verifica pastas no menu iniciar
                    for d in dirs:
                        d_lower = d.lower()
                        for token in self.search_tokens:
                            if token in d_lower:
                                add_file_or_dir(os.path.join(root, d), "Pasta do Menu Iniciar")
                                break
            except (PermissionError, OSError):
                pass

        # Ordena: pastas primeiro, depois arquivos maiores
        leftovers.sort(key=lambda item: (0 if item.item_type.startswith("Pasta") else 1, -item.size_bytes))
        return leftovers
