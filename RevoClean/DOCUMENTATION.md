# 📘 Documentação Técnica de Implementação - RevoClean Portable

Este documento apresenta a especificação técnica detalhada de todas as implementações, decisões de engenharia e módulos que compõem o **RevoClean Portable**. Ele foi estruturado para servir como documentação de referência para desenvolvedores, mantenedores e colaboradores do projeto no GitHub.

---

## 📑 Sumário

1. [Visão Geral e Princípios de Design](#1-visão-geral-e-princípios-de-design)
2. [Estrutura Modular do Projeto](#2-estrutura-modular-do-projeto)
3. [Módulo de Enumeração Híbrido (`scanner.py`)](#3-módulo-de-enumeração-híbrido-scannerpy)
   - [3.1 Descoberta de Programas Clássicos (Win32)](#31-descoberta-de-programas-clássicos-win32)
   - [3.2 Descoberta de Aplicativos da Microsoft Store (AppX/UWP/MSIX)](#32-descoberta-de-aplicativos-da-microsoft-store-appxuwpmsix)
   - [3.3 Normalização de Metadados e Modelagem de Dados](#33-normalização-de-metadados-e-modelagem-de-dados)
4. [Módulo de Execução de Desinstaladores (`uninstaller_runner.py`)](#4-módulo-de-execução-de-desinstaladores-uninstallerrunnerpy)
   - [4.1 Tratamento de Desinstaladores Executáveis (.exe)](#41-tratamento-de-desinstaladores-executáveis-exe)
   - [4.2 Tratamento de Pacotes Windows Installer (.msi)](#42-tratamento-de-pacotes-windows-installer-msi)
   - [4.3 Tratamento de Pacotes Modernos da Loja (AppX)](#43-tratamento-de-pacotes-modernos-da-loja-appx)
   - [4.4 Monitoramento Assíncrono de Processos](#44-monitoramento-assíncrono-de-processos)
5. [Motor de Varredura Profunda de Sobras (`deep_cleaner.py`)](#5-motor-de-varredura-profunda-de-sobras-deepcleanerpy)
   - [5.1 Extração e Higienização de Tokens](#51-extração-e-higienização-de-tokens)
   - [5.2 Modos de Varredura (Seguro, Moderado, Avançado)](#52-modos-de-varredura-seguro-moderado-avançado)
   - [5.3 Varredura no Registro do Windows](#53-varredura-no-registro-do-windows)
   - [5.4 Varredura no Sistema de Arquivos](#54-varredura-no-sistema-de-arquivos)
   - [5.5 Salvaguarda Contra Exclusão de Fornecedores Compartilhados](#55-salvaguarda-contra-exclusão-de-fornecedores-compartilhados)
6. [Camada de Segurança e Integração Win32 (`system_utils.py`)](#6-camada-de-segurança-e-integração-win32-systemutilspy)
   - [6.1 Exclusão com Recuperação via Lixeira (`SHFileOperationW`)](#61-exclusão-com-recuperação-via-lixeira-shfileoperationw)
   - [6.2 Backup Preventivo de Chaves de Registro (.reg)](#62-backup-preventivo-de-chaves-de-registro-reg)
   - [6.3 Whitelist Estrita de Diretórios e Chaves Críticas](#63-whitelist-estrita-de-diretórios-e-chaves-críticas)
   - [6.4 Criação Automatizada de Pontos de Restauração](#64-criação-automatizada-de-pontos-de-restauração)
   - [6.5 Gerenciamento de Privilégios Administrativos (UAC)](#65-gerenciamento-de-privilégios-administrativos-uac)
7. [Interface Gráfica e Experiência do Usuário (`src/ui/`)](#7-interface-gráfica-e-experiência-do-usuário-srcui)
   - [7.1 Janela Principal (`main_window.py`)](#71-janela-principal-mainwindowpy)
   - [7.2 Assistente de Desinstalação (`wizard_dialog.py`)](#72-assistente-de-desinstalação-wizarddialogpy)
   - [7.3 Assistente de Desinstalação Forçada (`forced_dialog.py`)](#73-assistente-de-desinstalação-forçada-forceddialogpy)
8. [Pipeline de Empacotamento Portátil (`build.py`)](#8-pipeline-de-empacotamento-portátil-buildpy)
9. [Instruções para Extensão e Desenvolvimento](#9-instruções-para-extensão-e-desenvolvimento)

---

## 1. Visão Geral e Princípios de Design

O **RevoClean Portable** foi concebido com os seguintes princípios arquiteturais:

- **Autonomia Portátil:** Zero instalação e zero dependências de runtime externo na máquina do usuário final. Todo o interpretador, bibliotecas de UI e dependências Win32 são empacotados em um único arquivo `.exe`.
- **Arquitetura em Duas Fases:** A primeira fase respeita o desinstalador oficial fornecido pelo fabricante; a segunda fase analisa de forma independente os resíduos que o desinstalador oficial deixou órfãos no sistema.
- **Transparência e Controle:** O usuário sempre tem visualização explícita e controle por checkbox de cada chave de registro e arquivo antes de qualquer ação de limpeza.
- **Fail-Safe (Segurança Primeiro):** Nenhuma exclusão é definitiva. Arquivos vão para a Lixeira do Windows e chaves de registro são exportadas para arquivos `.reg` antes de qualquer deleção.

---

## 2. Estrutura Modular do Projeto

```text
Uninstaller/
├── app.py                      # Ponto de entrada, configuração de DPI awareness e inicialização
├── build.py                    # Script automatizado de compilação com PyInstaller
├── requirements.txt            # Dependências Python para desenvolvimento
├── README.md                   # Documentação geral de apresentação do projeto
├── DOCUMENTATION.md            # Esta documentação técnica de implementação
├── LICENSE                     # Licença MIT
├── .gitignore                  # Padrões de arquivos ignorados pelo Git
└── src/
    ├── config.py               # Constantes de configuração, temas e whitelists de segurança
    ├── system_utils.py         # Chamadas de baixo nível às APIs Win32 e PowerShell
    ├── scanner.py              # Mecanismo de enumeração de softwares Win32 e Store AppX
    ├── uninstaller_runner.py   # Gerenciador de execução e monitoramento de desinstaladores
    ├── deep_cleaner.py         # Algoritmo de varredura profunda de arquivos e registros órfãos
    └── ui/
        ├── main_window.py      # Tela principal, barra de ferramentas e listagem interativa
        ├── wizard_dialog.py    # Assistente de desinstalação guiado passo a passo
        └── forced_dialog.py    # Interface de desinstalação forçada para softwares corrompidos
```

---

## 3. Módulo de Enumeração Híbrido (`scanner.py`)

O módulo `scanner.py` implementa um mecanismo de descoberta de softwares que unifica dois subsistemas distintos do Windows em um modelo de dados coerente.

### 3.1 Descoberta de Programas Clássicos (Win32)
Varre as três ramificações do Registro onde softwares convencionais registram seus desinstaladores:
1. `HKLM\Software\Microsoft\Windows\CurrentVersion\Uninstall` (softwares 64-bit no escopo da máquina)
2. `HKLM\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall` (softwares 32-bit em SO de 64-bit)
3. `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall` (softwares instalados no perfil do usuário atual)

O scanner utiliza as flags nativas `KEY_WOW64_64KEY` e `KEY_WOW64_32KEY` da API Win32 para garantir acesso semântico correto a cada árvore, evitando colisões ou omissões por virtualização de chaves.

### 3.2 Descoberta de Aplicativos da Microsoft Store (AppX/UWP/MSIX)
Aplicativos modernos distribuídos pela Microsoft Store não utilizam as chaves clássicas do Painel de Controle, residindo no repositório do **AppX Deployment Service**.
- O `ProgramScanner.get_store_apps()` consulta os pacotes via PowerShell:
  ```powershell
  Get-AppxPackage | Where-Object { -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.NonRemovable }
  ```
- **Filtro de Ruído:** Descarta automaticamente frameworks de runtime (DirectX, VCLibs, pacotes de linguagem e recursos dependentes), isolando apenas aplicativos de usuário instalados e desinstaláveis.
- **Extração de Nome Amigável:** Lê o manifesto `AppxManifest.xml` dentro do diretório de instalação do pacote, inspecionando o elemento `VisualElements -> DisplayName` para converter identificadores técnicos em nomes de exibição claros.
- **Cálculo de Espaço:** Soma o diretório de instalação em `C:\Program Files\WindowsApps\` com a pasta de dados e cache do pacote em `%LOCALAPPDATA%\Packages\<PackageFamilyName>`.

### 3.3 Normalização de Metadados e Modelagem de Dados
Todos os programas encontrados são encapsulados na dataclass `InstalledApp`:
- `name`: Nome legível da aplicação.
- `publisher`: Fabricante ou editor.
- `version`: Número de versão.
- `install_date`: Data de instalação formatada (`DD/MM/AAAA`).
- `install_location`: Caminho base dos arquivos no disco.
- `uninstall_string` / `quiet_uninstall_string`: Linha de comando para disparar a remoção.
- `estimated_size_bytes`: Tamanho em bytes (calculado ou extraído).
- `app_type`: `"win32"` (Desktop tradicional) ou `"store"` (Microsoft Store).
- `package_family_name`: Identificador de família para apps da Loja.

---

## 4. Módulo de Execução de Desinstaladores (`uninstaller_runner.py`)

A classe `UninstallerRunner` orquestra a execução da ferramenta de desinstalação original antes de liberar o sistema para a varredura profunda de resíduos.

### 4.1 Tratamento de Desinstaladores Executáveis (.exe)
Para desinstaladores clássicos baseados em executáveis (Inno Setup, NSIS, InstallShield, executáveis customizados):
- A string de desinstalação é analisada para preservar aspas e parâmetros de linha de comando.
- É executada como um novo grupo de processos (`CREATE_NEW_PROCESS_GROUP`).

### 4.2 Tratamento de Pacotes Windows Installer (.msi)
- Quando o instalador é identificado como MSI (ou quando o comando contém `msiexec.exe /I{GUID}`), o runner substitui dinamicamente o parâmetro `/I` por `/X`, assegurando que o Windows Installer seja aberto no modo de desinstalação e não de reinstalação/reparo.

### 4.3 Tratamento de Pacotes Modernos da Loja (AppX)
- Para aplicações com `app_type == "store"`, o comando é despachado via:
  ```cmd
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Remove-AppxPackage -Package <PackageFullName>"
  ```
  Isso executa a desinstalação nativa gerenciada pelo próprio sistema operacional sem abrir janelas extras.

### 4.4 Monitoramento Assíncrono de Processos
- O processo é monitorado em uma thread em segundo plano (`threading.Thread`).
- Ao finalizar, o código de retorno (`exit_code`) é registrado e a interface gráfica é notificada automaticamente para permitir que o usuário avance para a etapa de varredura.

---

## 5. Motor de Varredura Profunda de Sobras (`deep_cleaner.py`)

O `DeepCleaner` é o motor analítico responsável por rastrear tudo o que o desinstalador oficial não removeu.

### 5.1 Extração e Higienização de Tokens
O algoritmo decompõe o nome do aplicativo e do fabricante em tokens de busca:
1. Divide as palavras descartando pontuações e símbolos.
2. Descarta números puros e termos com menos de 3 caracteres.
3. Aplica a lista `GENERIC_SEARCH_EXCLUSIONS` em [`src/config.py`](file:///c:/Users/Desenvolvimento/Downloads/Unistaller/src/config.py), descartando termos comuns (`"software"`, `"app"`, `"installer"`, `"setup"`, `"free"`, `"desktop"`) e sufixos corporativos (`"llc"`, `"inc"`, `"corp"`, `"ltd"`, `"group"`, `"technologies"`).
4. Separa os termos em `app_tokens` (específicos do produto) e `pub_tokens` (específicos da empresa).

### 5.2 Modos de Varredura
- **Modo Seguro (`SCAN_MODE_SAFE`):** Limita a varredura ao caminho de instalação original cadastrado e às chaves primárias oficiais no Registro.
- **Modo Moderado (`SCAN_MODE_MODERATE` - Padrão):** Inclui o modo seguro, mais varredura nas pastas de perfis de usuário (`AppData\Roaming`, `AppData\Local`, `ProgramData`), atalhos no Menu Iniciar e Desktop, e chaves de autoinicialização (`Run` e `RunOnce`).
- **Modo Avançado (`SCAN_MODE_ADVANCED`):** Realiza uma varredura profunda e recursiva sob as ramificações `Software` de `HKCU` e `HKLM`.

### 5.3 Varredura no Registro do Windows
- Inspeciona chaves diretas estruturadas como `Software\<App>`, `Software\<Publisher>\<App>`, e versões sob `WOW6432Node`.
- Analisa valores em `Software\Microsoft\Windows\CurrentVersion\Run` e `RunOnce` para identificar entradas de inicialização vinculadas aos executáveis do app.
- No modo avançado, percorre as subchaves de `Software` identificando padrões coincidentes.

### 5.4 Varredura no Sistema de Arquivos
- **Pasta de Instalação:** Se a pasta original ainda existir após o desinstalador oficial, é adicionada para remoção.
- **Dados do AppX:** Para aplicativos da Loja, localiza a pasta em `%LOCALAPPDATA%\Packages\<PackageFamilyName>` (onde ficam caches, cookies e dados de perfil) e atalhos de execução em `%LOCALAPPDATA%\Microsoft\WindowsApps`.
- **Pastas de Dados de Usuário:** Vasculha `APPDATA`, `LOCALAPPDATA`, `ProgramData` e `Program Files` para combinações de `<Publisher>\<App>` e `<App>`.
- **Atalhos:** Varre as árvores de atalhos (`.lnk`) na Área de Trabalho e no Menu Iniciar do usuário e público.

### 5.5 Salvaguarda Contra Exclusão de Fornecedores Compartilhados
Para evitar que a remoção de um software específico apague a pasta de um fabricante que possui outros produtos instalados (ex: apagar `AppData\Local\Google` ao desinstalar o Chrome, afetando o Google Drive):
- O motor analisa os subdiretórios de pastas nomeadas pelo fabricante.
- Se existirem outros subdiretórios pertencentes a outros produtos que não coincidam com os tokens do software selecionado, **a pasta raiz do fabricante é protegida**, marcando para exclusão unicamente o subdiretório do aplicativo específico. O mesmo algoritmo é aplicado recursivamente para chaves do Registro.

---

## 6. Camada de Segurança e Integração Win32 (`system_utils.py`)

A integridade do sistema operacional é a prioridade número um do projeto.

### 6.1 Exclusão com Recuperação via Lixeira (`SHFileOperationW`)
Em vez de utilizar exclusão irrecuperável de arquivos (`os.remove`), o RevoClean se comunica com a Shell do Windows via `ctypes`:
```python
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

# Operação com flag FOF_ALLOWUNDO enviando para a Lixeira do Windows
sh_op = SHFILEOPSTRUCTW()
sh_op.wFunc = 3  # FO_DELETE
sh_op.pFrom = path + '\0\0'  # Requisito de terminação dupla nula Win32
sh_op.fFlags = 0x0040 | 0x0010 | 0x0004  # FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT
ctypes.windll.shell32.SHFileOperationW(ctypes.byref(sh_op))
```
Isso garante que qualquer pasta ou arquivo apagado pelo usuário possa ser restaurado a qualquer momento diretamente pela Lixeira do Windows.

### 6.2 Backup Preventivo de Chaves de Registro (.reg)
Antes de excluir qualquer chave do Registro:
- O utilitário executa o comando nativo `reg export "CHAVE" "BACKUP.reg" /y`.
- Os backups são organizados com timestamp em `%LOCALAPPDATA%\RevoClean_Portable\Backups\`.
- Para reverter qualquer alteração, o usuário precisa apenas dar um duplo clique no arquivo `.reg` gerado.

### 6.3 Whitelist Estrita de Diretórios e Chaves Críticas
O método `is_path_safe_to_delete` bloqueia terminantemente a exclusão de:
- Raízes de unidade (`C:\`, `D:\`, etc.).
- Pastas do sistema operacional (`C:\Windows`, `System32`, `SysWOW64`, `WinSxS`).
- Raízes de diretórios compartilhados (`C:\Program Files`, `C:\ProgramData`, `%USERPROFILE%`).
- Chaves vitais do Registro do Windows (`Software\Microsoft\Windows`, `System`, `SAM`, `Security`, `Policies`).

### 6.4 Criação Automatizada de Pontos de Restauração
Integração com o mecanismo de Restauração do Sistema do Windows via PowerShell:
```powershell
Checkpoint-Computer -Description 'Desinstalação RevoClean: <App>' -RestorePointType 'APPLICATION_UNINSTALL'
```
Pode ser disparado automaticamente antes de cada desinstalação ou sob demanda pelo botão da barra de ferramentas.

### 6.5 Gerenciamento de Privilégios Administrativos (UAC)
- Função `is_admin()` que verifica tokens de processo através de `IsUserAnAdmin()`.
- Botão interativo na barra de status que permite solicitar elevação UAC (`runas`) sem necessidade de fechar e reabrir manualmente o software.

---

## 7. Interface Gráfica e Experiência do Usuário (`src/ui/`)

Desenvolvida com a biblioteca moderna **CustomTkinter** e temas nativos.

### 7.1 Janela Principal (`main_window.py`)
- **Barra Superior:** Identificador de privilégios de Administrador, alternador de tema Claro/Escuro e barra de pesquisa com busca incremental em tempo real.
- **Barra de Ferramentas:**
  - Botão de desinstalação principal.
  - Botão de desinstalação forçada.
  - Seletor por abas/categorias: **Todos**, **Desktop** e **Windows Store**.
  - Ações rápidas: Atualizar lista, Criar Ponto de Restauração, Abrir Pasta no Explorer e Exportar lista para `.csv` ou `.txt`.
- **Tabela de Dados (`ttk.Treeview`):**
  - Colunas formatadas: Nome do Programa, Tipo (`Desktop` ou `Loja Windows`), Editor, Versão, Data, Tamanho e Local de Instalação.
  - Ordenação dinâmica em ambas as direções ao clicar no cabeçalho de qualquer coluna.
  - Menu de contexto (botão direito) com opções para desinstalar, abrir pasta, abrir no Regedit e pesquisar o aplicativo no Google.

### 7.2 Assistente de Desinstalação (`wizard_dialog.py`)
Janela modal em quatro passos guiados:
1. **Opções Iniciais:** Detalhes do software, checkbox de Ponto de Restauração e seleção do modo de varredura.
2. **Execução Supervisionada:** Execução do desinstalador padrão com barra de progresso e botão para avançar para a varredura assim que o desinstalador terminar.
3. **Varredura Ativa:** Análise multithread com feedback em tempo real das etapas de verificação.
4. **Revisão e Exclusão:** Exibição dos resultados em abas separadas para Registro e Arquivos, com caixas de seleção, contadores de itens e tamanhos, e botão de exclusão protegida.
5. **Resumo Final:** Relatório com total de chaves removidas, arquivos apagados e volume total de espaço em disco liberado.

### 7.3 Assistente de Desinstalação Forçada (`forced_dialog.py`)
- Permite que o usuário informe o nome de um software e/ou aponte para um diretório ou arquivo executável que não possua um desinstalador válido.
- Encaminha os dados diretamente para o motor do `DeepCleaner`, possibilitando a eliminação de resíduos órfãos deixados por programas incompletos.

---

## 8. Pipeline de Empacotamento Portátil (`build.py`)

Para viabilizar a distribuição em um executável autônomo:
- Script automatizado que invoca o **PyInstaller 6.x**:
  - `--onefile`: Consolida todo o ecossistema em um único `.exe`.
  - `--windowed`: Suprime a abertura da janela de terminal preta do Windows.
  - `--uac-admin`: Insere o manifesto de solicitação de privilégios administrativos diretamente no binário PE do Windows.
  - `--collect-all customtkinter`: Empacota automaticamente todos os temas JSON, fontes e recursos gráficos da UI.
  - `--add-data "src;src"`: Inclui todos os módulos internos da aplicação no pacote.
- O executável compilado tem tamanho em torno de **19.8 MB** e pode ser executado em qualquer computador com Windows 10 ou 11 sem qualquer pré-requisito de instalação.

---

## 9. Instruções para Extensão e Desenvolvimento

Para colaboradores que desejarem evoluir o código no GitHub:
1. **Adicionar novos filtros de desinstalação:** Podem ser implementados em `src/scanner.py`.
2. **Novas regras de detecção de resíduos:** Podem ser configuradas em `src/deep_cleaner.py`.
3. **Configuração de palavras protegidas:** Edite o arquivo `src/config.py`.
4. **Para compilar uma nova versão do `.exe`:** Basta executar `python build.py` na raiz do repositório.
