import os
import csv
import io
import winreg
import subprocess
import xml.etree.ElementTree as ET
from dataclasses import dataclass, asdict
from typing import List, Optional
from src.system_utils import get_directory_size, format_size

@dataclass
class InstalledApp:
    name: str
    publisher: str
    version: str
    install_date: str
    install_location: str
    uninstall_string: str
    quiet_uninstall_string: str
    display_icon: str
    estimated_size_bytes: int
    is_system_component: bool
    is_windows_installer: bool
    registry_root: str  # "HKLM", "HKCU" ou "AppX"
    registry_path: str
    key_name: str
    app_type: str = "win32"  # "win32" ou "store"
    package_family_name: str = ""

    @property
    def formatted_size(self) -> str:
        return format_size(self.estimated_size_bytes)

    @property
    def type_badge(self) -> str:
        return "Loja Windows" if self.app_type == "store" else "Desktop"

    def to_dict(self):
        d = asdict(self)
        d['formatted_size'] = self.formatted_size
        d['type_badge'] = self.type_badge
        return d


def format_install_date(raw_date: str) -> str:
    """Formata datas brutas como '20251104' para '04/11/2025'."""
    if not raw_date:
        return ""
    clean = str(raw_date).strip()
    if len(clean) == 8 and clean.isdigit():
        return f"{clean[6:8]}/{clean[4:6]}/{clean[0:4]}"
    return clean


def clean_icon_path(raw_icon: str) -> str:
    """Extrai caminho limpo de ícone (remove vírgula e índice, aspas, etc)."""
    if not raw_icon:
        return ""
    clean = raw_icon.strip(' "\'')
    if "," in clean:
        parts = clean.split(",")
        clean = parts[0].strip(' "\'')
    return clean


def extract_appx_friendly_name(install_loc: str, package_name: str) -> str:
    """Extrai o nome amigável do manifesto do aplicativo da Microsoft Store."""
    if install_loc and os.path.isdir(install_loc):
        manifest_path = os.path.join(install_loc, "AppxManifest.xml")
        if os.path.exists(manifest_path):
            try:
                tree = ET.parse(manifest_path)
                root = tree.getroot()
                for elem in root.iter():
                    if elem.tag.endswith("VisualElements") and "DisplayName" in elem.attrib:
                        val = elem.attrib["DisplayName"].strip()
                        if val and not val.startswith("ms-resource:"):
                            return val
                for elem in root.iter():
                    if elem.tag.endswith("DisplayName") and elem.text:
                        val = elem.text.strip()
                        if val and not val.startswith("ms-resource:"):
                            return val
            except Exception:
                pass

    # Heurística para limpar nomes como SpotifyAB.SpotifyMusic -> Spotify
    clean = package_name
    if "." in clean:
        parts = clean.split(".")
        last = parts[-1]
        for suffix in ["Music", "App", "Desktop", "Client"]:
            if last.endswith(suffix) and len(last) > len(suffix):
                last = last[:-len(suffix)]
        clean = last
    return clean


class ProgramScanner:
    def __init__(self, include_system_components: bool = False):
        self.include_system_components = include_system_components
        self._cached_win32: List[InstalledApp] = []
        self._cached_store: List[InstalledApp] = []

    def get_installed_programs(self, force_refresh: bool = False) -> List[InstalledApp]:
        """Retorna todos os programas (Win32 clássicos e Aplicativos da Microsoft Store)."""
        if force_refresh or not self._cached_win32:
            self._cached_win32 = self.get_win32_programs()
        if force_refresh or not self._cached_store:
            self._cached_store = self.get_store_apps()

        combined = self._cached_win32 + self._cached_store
        combined.sort(key=lambda a: a.name.lower())
        return combined

    def get_win32_programs(self) -> List[InstalledApp]:
        """Varre todas as chaves de registro do Windows para listar programas clássicos Win32."""
        targets = [
            (winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM", winreg.KEY_WOW64_64KEY),
            (winreg.HKEY_LOCAL_MACHINE, r"Software\Microsoft\Windows\CurrentVersion\Uninstall", "HKLM", winreg.KEY_WOW64_32KEY),
            (winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Uninstall", "HKCU", 0),
        ]

        apps: List[InstalledApp] = []
        seen_keys = set()

        for root_hkey, base_path, root_name, flags in targets:
            try:
                access = winreg.KEY_READ | flags
                with winreg.OpenKey(root_hkey, base_path, 0, access) as base_key:
                    subkeys_count, _, _ = winreg.QueryInfoKey(base_key)
                    for i in range(subkeys_count):
                        try:
                            subkey_name = winreg.EnumKey(base_key, i)
                            full_subpath = f"{base_path}\\{subkey_name}"
                            unique_id = f"{root_name}\\{subkey_name}".lower()

                            if unique_id in seen_keys:
                                continue

                            with winreg.OpenKey(root_hkey, full_subpath, 0, access) as subkey:
                                app = self._parse_app_key(
                                    subkey, root_name, full_subpath, subkey_name
                                )
                                if app:
                                    seen_keys.add(unique_id)
                                    apps.append(app)
                        except (PermissionError, OSError):
                            continue
            except (PermissionError, OSError):
                continue

        apps.sort(key=lambda a: a.name.lower())
        self._cached_win32 = apps
        return apps

    def get_store_apps(self) -> List[InstalledApp]:
        """Varre e lista aplicativos instalados pela Microsoft Store (AppX/UWP)."""
        apps: List[InstalledApp] = []
        try:
            ps_cmd = (
                "Get-AppxPackage | "
                "Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable } | "
                "Select-Object Name, PackageFullName, Version, InstallLocation, PackageFamilyName | "
                "ConvertTo-Csv -NoTypeInformation -Delimiter ';'"
            )
            res = subprocess.run(
                ["powershell", "-NoProfile", "-Command", ps_cmd],
                capture_output=True,
                text=True,
                timeout=20
            )
            if res.returncode == 0 and res.stdout.strip():
                reader = csv.DictReader(io.StringIO(res.stdout), delimiter=';')
                for item in reader:
                    pkg_name = item.get("Name", "").strip()
                    pkg_full_name = item.get("PackageFullName", "").strip()
                    version = item.get("Version", "").strip()
                    install_loc = item.get("InstallLocation", "").strip()
                    family_name = item.get("PackageFamilyName", "").strip()

                    if not pkg_name or not pkg_full_name:
                        continue

                    friendly_name = extract_appx_friendly_name(install_loc, pkg_name)

                    # Calcula tamanho aproximado da pasta do app se acessível
                    size_bytes = 0
                    if install_loc and os.path.exists(install_loc):
                        size_bytes = get_directory_size(install_loc)

                    # Adiciona dados da pasta de dados do usuário se existir
                    pkg_user_data = os.path.join(
                        os.environ.get("LOCALAPPDATA", ""),
                        "Packages",
                        family_name
                    )
                    if os.path.exists(pkg_user_data):
                        size_bytes += get_directory_size(pkg_user_data)

                    uninst_cmd = f'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Remove-AppxPackage -Package {pkg_full_name}"'

                    publisher = "Microsoft Store"
                    if "spotify" in pkg_name.lower():
                        publisher = "Spotify AB"
                    elif "whatsapp" in pkg_name.lower():
                        publisher = "Meta Platforms / WhatsApp"
                    elif "netflix" in pkg_name.lower():
                        publisher = "Netflix, Inc."

                    apps.append(InstalledApp(
                        name=friendly_name,
                        publisher=publisher,
                        version=version,
                        install_date="",
                        install_location=install_loc,
                        uninstall_string=uninst_cmd,
                        quiet_uninstall_string=uninst_cmd,
                        display_icon="",
                        estimated_size_bytes=size_bytes,
                        is_system_component=False,
                        is_windows_installer=False,
                        registry_root="AppX",
                        registry_path=family_name,
                        key_name=pkg_full_name,
                        app_type="store",
                        package_family_name=family_name
                    ))
        except Exception as e:
            print(f"Erro ao obter apps da Windows Store: {e}")

        apps.sort(key=lambda a: a.name.lower())
        self._cached_store = apps
        return apps

    def _parse_app_key(self, key, root_name: str, full_path: str, key_name: str) -> Optional[InstalledApp]:
        def get_val(val_name, default=""):
            try:
                v, _ = winreg.QueryValueEx(key, val_name)
                return v
            except OSError:
                return default

        name = get_val("DisplayName", "").strip()
        if not name:
            return None

        # Filtra componentes de sistema quando não solicitado
        system_component = bool(get_val("SystemComponent", 0))
        if system_component and not self.include_system_components:
            return None

        # Filtra atualizações do Windows (KBxxxxxx) por padrão
        parent_key = get_val("ParentKeyName", "")
        release_type = get_val("ReleaseType", "")
        if release_type.lower() in ("security update", "update", "hotfix"):
            return None

        publisher = get_val("Publisher", "").strip()
        version = get_val("DisplayVersion", "").strip()
        install_date = format_install_date(get_val("InstallDate", ""))
        install_location = get_val("InstallLocation", "").strip(' "\'')
        uninstall_string = get_val("UninstallString", "").strip()
        quiet_uninstall_string = get_val("QuietUninstallString", "").strip()
        display_icon = clean_icon_path(get_val("DisplayIcon", ""))
        is_msi = bool(get_val("WindowsInstaller", 0))

        # Tamanho estimado
        size_raw = get_val("EstimatedSize", 0)
        try:
            size_bytes = int(size_raw) * 1024  # O Windows armazena em KB
        except (ValueError, TypeError):
            size_bytes = 0

        # Se não há tamanho estimado no registro mas temos a pasta de instalação, calcula
        if size_bytes == 0 and install_location and os.path.isdir(install_location):
            size_bytes = get_directory_size(install_location)

        if not uninstall_string and not is_msi and not install_location:
            return None

        return InstalledApp(
            name=name,
            publisher=publisher,
            version=version,
            install_date=install_date,
            install_location=install_location,
            uninstall_string=uninstall_string,
            quiet_uninstall_string=quiet_uninstall_string,
            display_icon=display_icon,
            estimated_size_bytes=size_bytes,
            is_system_component=system_component,
            is_windows_installer=is_msi,
            registry_root=root_name,
            registry_path=full_path,
            key_name=key_name,
            app_type="win32",
            package_family_name=""
        )
