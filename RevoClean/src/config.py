import os
import sys

# Application Metadata
APP_NAME = "RevoClean Portable"
APP_TITLE = "RevoClean - Desinstalador Avançado Portable"
APP_VERSION = "1.0.0"

# Scan Modes
SCAN_MODE_SAFE = 1
SCAN_MODE_MODERATE = 2
SCAN_MODE_ADVANCED = 3

SCAN_MODE_DESCRIPTIONS = {
    SCAN_MODE_SAFE: {
        "title": "Seguro",
        "desc": "Verifica apenas a pasta de instalação original e as chaves de registro oficiais do aplicativo. O mais rápido e seguro."
    },
    SCAN_MODE_MODERATE: {
        "title": "Moderado (Recomendado)",
        "desc": "Inclui o modo Seguro mais varredura nas pastas de dados do usuário (AppData, LocalAppData, ProgramData) e busca de chaves associadas no Registro."
    },
    SCAN_MODE_ADVANCED: {
        "title": "Avançado",
        "desc": "Varredura profunda completa no Registro, atalhos de inicialização, cache, tarefas e resíduos do sistema. Ideal para desinstalações teimosas."
    }
}

# Whitelist of directories that must NEVER be deleted or suggested for deletion
SYSTEM_PROTECTED_DIRS = {
    os.path.normpath(os.environ.get("SystemRoot", r"C:\Windows")).lower(),
    os.path.normpath(os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "System32")).lower(),
    os.path.normpath(os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "SysWOW64")).lower(),
    os.path.normpath(os.path.join(os.environ.get("SystemRoot", r"C:\Windows"), "WinSxS")).lower(),
    os.path.normpath(os.environ.get("ProgramFiles", r"C:\Program Files")).lower(),
    os.path.normpath(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")).lower(),
    os.path.normpath(os.environ.get("ProgramData", r"C:\ProgramData")).lower(),
    os.path.normpath(os.environ.get("APPDATA", r"")).lower(),
    os.path.normpath(os.environ.get("LOCALAPPDATA", r"")).lower(),
    os.path.normpath(os.environ.get("USERPROFILE", r"")).lower(),
    os.path.normpath("C:\\").lower(),
    os.path.normpath("D:\\").lower(),
}

# Protected Registry paths
PROTECTED_REGISTRY_KEYS = {
    r"software\microsoft\windows",
    r"software\microsoft\windows nt",
    r"software\microsoft\windows defender",
    r"software\microsoft\directx",
    r"software\microsoft\ole",
    r"software\microsoft\rpc",
    r"software\classes",
    r"software\policies",
    r"system",
    r"sam",
    r"security",
    r"hardware",
}

# Common words to exclude when searching for app leftovers
GENERIC_SEARCH_EXCLUSIONS = {
    "software", "app", "application", "setup", "installer", "install", 
    "uninstall", "windows", "microsoft", "system", "program", "files",
    "x86", "x64", "64-bit", "32-bit", "free", "version", "update",
    "updater", "service", "client", "player", "tools", "util", "utility",
    "edition", "release", "patch", "portable", "desktop", "suite", "the",
    "and", "for", "pro", "plus", "standard", "community",
    # Corporate suffixes
    "llc", "inc", "corp", "corporation", "ltd", "limited", "gmbh",
    "sarl", "co", "holding", "holdings", "group", "technologies"
}

# Local storage for backup files
BACKUP_DIR = os.path.join(
    os.environ.get("LOCALAPPDATA", os.path.expanduser("~")),
    "RevoClean_Portable",
    "Backups"
)
os.makedirs(BACKUP_DIR, exist_ok=True)
