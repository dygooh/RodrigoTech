@echo off
rem Gera o RodrigoTech pronto para levar a outro PC (nao precisa instalar o .NET la).
rem Saida: ..\dist\RodrigoTech.exe
cd /d "%~dp0"
echo Fechando o RodrigoTech se estiver aberto ...
taskkill /f /im RodrigoTech.exe >nul 2>&1

echo Limpando cache e preparando ambiente de publicacao ...
rmdir /s /q obj >nul 2>&1
rmdir /s /q bin >nul 2>&1
set "BUILD_TMP=%TEMP%\RodrigoTechBuild"
if exist "%BUILD_TMP%" rmdir /s /q "%BUILD_TMP%" >nul 2>&1
mkdir "%BUILD_TMP%" >nul 2>&1

echo Gerando RodrigoTech.exe a partir do codigo atual ...
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "%BUILD_TMP%" -nologo -v q
if errorlevel 1 (
    echo.
    echo FALHOU. Veja os erros acima. O .exe antigo NAO foi atualizado.
    pause
    exit /b 1
)

if not exist "..\dist" mkdir "..\dist" >nul 2>&1
copy /y "%BUILD_TMP%\RodrigoTech.exe" "..\dist\RodrigoTech.exe" >nul
if exist "%BUILD_TMP%" rmdir /s /q "%BUILD_TMP%" >nul 2>&1
copy /y "LEIA-ME.txt" "..\dist\LEIA-ME.txt" >nul
echo.
echo Pronto: %~dp0..\dist
for %%F in ("..\dist\RodrigoTech.exe") do echo Arquivo gerado em %%~tF (%%~zF bytes)
pause
