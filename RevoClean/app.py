import os
import sys
import ctypes

# Habilita DPI awareness para interface ultra nítida em telas modernas
try:
    ctypes.windll.shcore.SetProcessDpiAwareness(1)
except Exception:
    try:
        ctypes.windll.user32.SetProcessDPIAware()
    except Exception:
        pass

# Adiciona o diretório raiz ao PYTHONPATH
CURRENT_DIR = os.path.dirname(os.path.abspath(__file__))
if CURRENT_DIR not in sys.path:
    sys.path.insert(0, CURRENT_DIR)

from src.ui.main_window import MainWindow

def main():
    try:
        app = MainWindow()
        app.mainloop()
    except KeyboardInterrupt:
        sys.exit(0)
    except Exception as e:
        import traceback
        traceback.print_exc()
        try:
            import tkinter.messagebox as mb
            mb.showerror("Erro Fatal", f"Ocorreu um erro inesperado:\n{str(e)}")
        except Exception:
            pass

if __name__ == "__main__":
    main()
