# ⚡ RodrigoTech

<p align="center">
  <img src="https://img.shields.io/badge/Platform-Windows%2010%20%7C%2011-blue?style=for-the-badge&logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/.NET-8.0-purple?style=for-the-badge&logo=dotnet" alt=".NET 8" />
  <img src="https://img.shields.io/badge/UI-WPF%20(XAML)-blueviolet?style=for-the-badge" alt="WPF" />
  <img src="https://img.shields.io/badge/Type-Standalone%20Single--File%20EXE-orange?style=for-the-badge" alt="Portable" />
  <img src="https://img.shields.io/badge/License-MIT-green?style=for-the-badge" alt="License" />
</p>

O **RodrigoTech** é uma suíte completa de diagnóstico de hardware, telemetria em tempo real, benchmarks de alta fidelidade (CPU e GPU Ray Tracing), otimização de sistema operacional, firewall por aplicativo (WFP) e desinstalador avançado com varredura profunda de sobras residuais (**RevoClean** integrado).

Tudo unificado em um **único aplicativo desktop nativo** para Windows (WPF .NET 8), veloz, responsivo e sem instalador.

---

## ✨ Recursos Principais

### 🔍 Diagnóstico e Telemetria
- **Especificações de Hardware (GPU-Z & CPU-Z style):** Leitura detalhada de CPU, memória RAM por módulo/slot, placa-mãe/BIOS, armazenamento e placa de vídeo (clocks, limites de energia, barramento PCIe, arquitetura, sensores via NVML e LibreHardwareMonitorLib).
- **Saúde dos Discos (S.M.A.R.T.):** Diagnóstico preditivo de SSDs e HDs (temperatura, horas de uso, setores realocados, desgaste e alerta de falhas).
- **Overlay para Jogos (Ctrl+Shift+O):** Indicador de FPS, frametime, 1% low, uso e temperaturas de CPU/GPU em jogos sem borda ou janela (via ETW / DXGI).
- **Detector de Gargalo:** Análise em tempo real do equilíbrio térmico e de desempenho entre CPU e GPU.

### ⚡ Benchmarks e Stress Test
- **Ray Tracing Benchmark (D3D12 / DXR 1.1):** Benchmark gráfico com compute shaders e hardware ray tracing em tempo real.
- **CPU Benchmark:** Renderizador path tracer determinístico multi-core e single-core.
- **Stress Test de CPU e GPU:** Teste de carga extrema com monitoramento térmico e corte de segurança por temperatura.

### 🛡️ Firewall por Aplicativo (WFP)
- Gerenciamento granular de regras de rede do Windows Filtering Platform: bloqueie o acesso à internet de softwares específicos com um clique.

### 🧹 Desinstalador Avançado (RevoClean Integrado)
- **Suporte Híbrido Completo:** Remove tanto programas clássicos Win32 quanto aplicativos modernos da Microsoft Store (AppX/UWP/MSIX).
- **Varredura Profunda de Sobras (Deep Cleaner):** Localiza arquivos órfãos, pastas temporárias em `AppData`/`ProgramData` e chaves esquecidas no Registro do Windows.
- **3 Níveis de Varredura:** Seguro, Moderado (Recomendado) e Avançado.
- **Desinstalação Forçada:** Permite eliminar programas cujo desinstalador oficial foi danificado ou excluído.
- **Segurança Máxima:** Backup automático do Registro em arquivos `.reg`, envio de sobras para a Lixeira nativa do Windows (`SHFileOperationW`) e criação de Ponto de Restauração em 1 clique.

### 🛠️ Otimização e Ferramentas Maker
- **Otimização de RAM:** Enxuga a memória em espera (ISLC) e processos em segundo plano com um clique.
- **Gerenciador de Inicialização:** Ativa/desativa ou remove programas que abrem junto com o Windows.
- **Instalador Winget:** Instala navegadores e ferramentas pós-formatação de forma silenciosa.
- **Portas COM e Drivers Seriais (Arduino / ESP32):** Limpa registros fantasmas de portas COM presas e inclui instaladores para drivers CH340, CP2102, FTDI, Prolific e Arduino.

---

## 🚀 Como Executar

1. Baixe o executável pronto na aba [Releases](../../releases): **`RodrigoTech.exe`**.
2. Execute como Administrador (necessário para ler sensores de hardware, gerenciar o firewall WFP e pastas protegidas).
3. Pronto! Não é necessário instalar o .NET ou outras dependências no computador de destino.

---

## 🛠️ Como Compilar a partir do Código-Fonte

### Requisitos
- Windows 10 ou 11 (x64)
- [.NET SDK 8.0](https://dotnet.microsoft.com/download/dotnet/8.0)

### Compilação e Publicação
Para gerar o executável único standalone em `dist/RodrigoTech.exe`:

```batch
src\publicar.bat
```

Ou via CLI:

```bash
dotnet publish src/RodrigoTech.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist
```

---

## 📄 Licença

Distribuído sob a licença [MIT](LICENSE).
