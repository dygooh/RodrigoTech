import os
import sys
import ctypes
import subprocess
import winreg
from ctypes import wintypes
import datetime
from src.config import SYSTEM_PROTECTED_DIRS, PROTECTED_REGISTRY_KEYS, BACKUP_DIR

class SHFILEOPSTRUCTW(ctypes.Structure):
    _fields_ = [
        ('hwnd', wintypes.HWND),
        ('wFunc', wintypes.UINT),
        ('pFrom', wintypes.LPCWSTR),
        ('pTo', wintypes.LPCWSTR),
        ('fFlags', wintypes.WORD),
        ('fAnyOperationsAborted', wintypes.BOOL),
        ('hNameMappings', wintypes.LPVOID),
        ('lpszProgressTitle', wintypes.LPCWSTR),
    ]

FO_DELETE = 3
FOF_ALLOWUNDO = 0x0040
FOF_NOCONFIRMATION = 0x0010
FOF_SILENT = 0x0004

def is_admin() -> bool:
    """Verifica se o processo possui privilégios de Administrador."""
    try:
        return ctypes.windll.shell32.IsUserAnAdmin() != 0
    except Exception:
        return False

def request_admin_elevation():
    """Relança o aplicativo solicitando elevação de privilégios via UAC."""
    if is_admin():
        return True
    try:
        params = " ".join([f'"{arg}"' for arg in sys.argv[1:]])
        ret = ctypes.windll.shell32.ShellExecuteW(
            None,
            "runas",
            sys.executable,
            params if getattr(sys, 'frozen', False) else f'"{sys.argv[0]}" {params}',
            None,
            1  # SW_SHOWNORMAL
        )
        if ret > 32:
            sys.exit(0)
    except Exception as e:
        print(f"Erro ao solicitar privilégios de administrador: {e}")
    return False

def is_path_safe_to_delete(path: str) -> bool:
    """Garante que caminhos críticos do sistema operacional nunca sejam excluídos."""
    if not path or not os.path.exists(path):
        return False
    
    norm = os.path.normpath(os.path.abspath(path)).lower()
    
    # Verifica contra lista protegida exata
    if norm in SYSTEM_PROTECTED_DIRS:
        return False
        
    # Raízes de unidades (C:\, D:\)
    if len(norm) <= 3 and norm.endswith(":\\"):
        return False
        
    # Windows e System32
    win_dir = os.path.normpath(os.environ.get("SystemRoot", r"C:\Windows")).lower()
    if norm.startswith(win_dir):
        return False

    # Evita deletar a própria pasta raiz de Program Files
    pf1 = os.path.normpath(os.environ.get("ProgramFiles", r"C:\Program Files")).lower()
    pf2 = os.path.normpath(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")).lower()
    if norm == pf1 or norm == pf2:
        return False

    return True

def send_to_recycle_bin(path: str) -> bool:
    """Move arquivo ou pasta com segurança para a Lixeira do Windows."""
    if not is_path_safe_to_delete(path):
        return False
        
    try:
        abs_path = os.path.abspath(path)
        # Requer terminação dupla nula conforme especificação Win32
        p_from = abs_path + '\0\0'
        
        sh_op = SHFILEOPSTRUCTW()
        sh_op.wFunc = FO_DELETE
        sh_op.pFrom = p_from
        sh_op.fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
        
        ret = ctypes.windll.shell32.SHFileOperationW(ctypes.byref(sh_op))
        return ret == 0 and not os.path.exists(abs_path)
    except Exception as e:
        print(f"Erro ao mover para a lixeira ({path}): {e}")
        return False

def format_size(size_bytes: int) -> str:
    """Formata bytes para exibição legível (KB, MB, GB)."""
    if not size_bytes or size_bytes <= 0:
        return "Desconhecido"
    
    units = ["B", "KB", "MB", "GB", "TB"]
    size = float(size_bytes)
    idx = 0
    while size >= 1024.0 and idx < len(units) - 1:
        size /= 1024.0
        idx += 1
    return f"{size:.2f} {units[idx]}"

def get_directory_size(path: str) -> int:
    """Calcula tamanho total de um diretório em bytes."""
    total = 0
    try:
        if os.path.isfile(path):
            return os.path.getsize(path)
        for root, dirs, files in os.walk(path):
            for f in files:
                fp = os.path.join(root, f)
                try:
                    total += os.path.getsize(fp)
                except (OSError, PermissionError):
                    pass
    except Exception:
        pass
    return total

def is_registry_key_safe_to_delete(key_path: str) -> bool:
    """Garante que chaves vitais do Windows não sejam apagadas."""
    clean_path = key_path.lower().strip("\\")
    for protected in PROTECTED_REGISTRY_KEYS:
        if clean_path == protected or clean_path.startswith(protected + "\\"):
            return False
    return True

def export_registry_key_backup(root_name: str, subkey_path: str, app_name: str) -> str:
    """Cria backup em arquivo .reg antes de remover qualquer chave do registro."""
    try:
        sanitized_app = "".join(c for c in app_name if c.isalnum() or c in (' ', '_', '-')).strip()
        timestamp = datetime.datetime.now().strftime("%Y%m%d_%H%M%S")
        backup_file = os.path.join(BACKUP_DIR, f"{sanitized_app}_{timestamp}.reg")
        
        full_key = f"{root_name}\\{subkey_path}"
        cmd = f'reg export "{full_key}" "{backup_file}" /y'
        
        result = subprocess.run(cmd, shell=True, capture_output=True, text=True)
        if result.returncode == 0 and os.path.exists(backup_file):
            return backup_file
    except Exception as e:
        print(f"Erro ao exportar backup do registro: {e}")
    return ""

def delete_registry_key_recursive(root_hkey, subkey_path: str) -> bool:
    """Deleta recursivamente uma chave do Registro do Windows e todas as suas subchaves."""
    if not is_registry_key_safe_to_delete(subkey_path):
        print(f"Tentativa de deletar chave protegida bloqueada: {subkey_path}")
        return False

    try:
        # Abre a chave com direitos de enumeração e deleção
        with winreg.OpenKey(root_hkey, subkey_path, 0, winreg.KEY_READ | winreg.KEY_WRITE) as key:
            while True:
                try:
                    sub_subkey = winreg.EnumKey(key, 0)
                    delete_registry_key_recursive(root_hkey, f"{subkey_path}\\{sub_subkey}")
                except OSError:
                    # Sem mais subchaves
                    break
        
        # Deleta a própria chave
        winreg.DeleteKey(root_hkey, subkey_path)
        return True
    except FileNotFoundError:
        return True
    except Exception as e:
        print(f"Erro ao deletar chave {subkey_path}: {e}")
        return False

def delete_registry_value(root_hkey, subkey_path: str, value_name: str) -> bool:
    """Remove um valor específico dentro de uma chave do Registro."""
    if not is_registry_key_safe_to_delete(subkey_path):
        return False
    try:
        with winreg.OpenKey(root_hkey, subkey_path, 0, winreg.KEY_SET_VALUE) as key:
            winreg.DeleteValue(key, value_name)
            return True
    except Exception as e:
        print(f"Erro ao deletar valor {value_name} em {subkey_path}: {e}")
        return False

def create_system_restore_point(description: str = "Ponto de Restauração - RevoClean") -> tuple[bool, str]:
    """Cria um Ponto de Restauração do Windows via PowerShell."""
    try:
        ps_cmd = (
            f"powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "
            f"\"Checkpoint-Computer -Description '{description}' -RestorePointType 'APPLICATION_UNINSTALL'\""
        )
        res = subprocess.run(ps_cmd, shell=True, capture_output=True, text=True, timeout=60)
        if res.returncode == 0:
            return True, "Ponto de restauração criado com sucesso!"
        else:
            err = res.stderr.strip() or res.stdout.strip()
            return False, f"Não foi possível criar o ponto: {err}"
    except subprocess.TimeoutExpired:
        return False, "Tempo limite excedido ao criar ponto de restauração."
    except Exception as e:
        return False, f"Erro: {str(e)}"

def open_in_explorer(path: str):
    """Abre o Windows Explorer na pasta indicada ou seleciona o arquivo."""
    if not path or not os.path.exists(path):
        return
    try:
        norm = os.path.normpath(path)
        if os.path.isdir(norm):
            os.startfile(norm)
        else:
            subprocess.run(f'explorer.exe /select,"{norm}"', shell=True)
    except Exception as e:
        print(f"Erro ao abrir no Explorer: {e}")

def open_in_regedit(root_name: str, subkey_path: str):
    """Abre o editor de registro do Windows na chave informada."""
    try:
        full_key = f"Computer\\{root_name}\\{subkey_path}"
        # O Windows guarda a última chave aberta em HKCU\Software\Microsoft\Windows\CurrentVersion\Applets\Regedit\LastKey
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit", 0, winreg.KEY_SET_VALUE) as k:
            winreg.SetValueEx(k, "LastKey", 0, winreg.REG_SZ, full_key)
        subprocess.Popen("regedit.exe")
    except Exception as e:
        print(f"Erro ao abrir Regedit: {e}")
