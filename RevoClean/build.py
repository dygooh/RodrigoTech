import os
import sys
import shutil
import subprocess

def build():
    print("==================================================")
    print("Iniciando build do RevoClean Portable (.exe único)")
    print("==================================================")

    base_dir = os.path.dirname(os.path.abspath(__file__))
    app_py = os.path.join(base_dir, "app.py")
    dist_dir = os.path.join(base_dir, "dist")
    build_dir = os.path.join(base_dir, "build")

    # Comando PyInstaller
    cmd = [
        sys.executable, "-m", "PyInstaller",
        "--noconfirm",
        "--onefile",
        "--windowed",
        "--uac-admin",
        "--name", "RevoClean_Portable",
        "--collect-all", "customtkinter",
        "--add-data", f"{os.path.join(base_dir, 'src')}{os.pathsep}src",
        app_py
    ]

    print(f"Executando comando:\n{' '.join(cmd)}\n")
    res = subprocess.run(cmd)

    if res.returncode == 0:
        exe_src = os.path.join(dist_dir, "RevoClean_Portable.exe")
        exe_dest = os.path.join(base_dir, "RevoClean_Portable.exe")
        if os.path.exists(exe_src):
            shutil.copy2(exe_src, exe_dest)
            size_mb = os.path.getsize(exe_dest) / (1024 * 1024)
            print("\n==================================================")
            print(f"BUILD CONCLUÍDO COM SUCESSO!")
            print(f"Arquivo executável portable gerado:")
            print(f"-> {exe_dest} ({size_mb:.2f} MB)")
            print("Pode ser executado diretamente em qualquer computador sem instalar!")
            print("==================================================")
            return True
    else:
        print("\nFalha ao gerar o executável.")
        return False

if __name__ == "__main__":
    success = build()
    sys.exit(0 if success else 1)
