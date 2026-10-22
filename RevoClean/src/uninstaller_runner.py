import os
import shlex
import subprocess
import threading
import time
from typing import Callable, Optional
from src.scanner import InstalledApp

class UninstallerRunner:
    def __init__(self, app: InstalledApp, quiet: bool = False):
        self.app = app
        self.quiet = quiet
        self.process: Optional[subprocess.Popen] = None
        self.is_running = False
        self.exit_code: Optional[int] = None

    def build_command(self) -> str:
        """Monta o comando adequado para executar o desinstalador."""
        if getattr(self.app, "app_type", "win32") == "store":
            return f'powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Remove-AppxPackage -Package {self.app.key_name}"'

        cmd = self.app.quiet_uninstall_string if (self.quiet and self.app.quiet_uninstall_string) else self.app.uninstall_string

        if not cmd and self.app.is_windows_installer:
            cmd = f"msiexec.exe /X {self.app.key_name}"

        # Se for um comando MSI comum com /I, substitui por /X para desinstalar
        if "msiexec" in cmd.lower() and "/i" in cmd.lower():
            cmd = cmd.replace("/i", "/x").replace("/I", "/X")

        return cmd.strip()

    def launch(self, on_finish: Optional[Callable[[int], None]] = None) -> bool:
        """Inicia o processo de desinstalação em background e monitora seu término."""
        cmd = self.build_command()
        if not cmd:
            return False

        try:
            self.process = subprocess.Popen(
                cmd,
                shell=True,
                creationflags=subprocess.CREATE_NEW_PROCESS_GROUP
            )
            self.is_running = True

            def monitor():
                self.process.wait()
                self.exit_code = self.process.returncode
                self.is_running = False
                if on_finish:
                    on_finish(self.exit_code)

            thread = threading.Thread(target=monitor, daemon=True)
            thread.start()
            return True
        except Exception as e:
            print(f"Erro ao disparar desinstalador: {e}")
            self.is_running = False
            return False

    def wait_completion(self, timeout: Optional[int] = None) -> int:
        """Espera a conclusão do desinstalador de forma síncrona."""
        if self.process:
            try:
                self.process.wait(timeout=timeout)
                self.exit_code = self.process.returncode
            except subprocess.TimeoutExpired:
                pass
        return self.exit_code if self.exit_code is not None else -1
