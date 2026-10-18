@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

rem 用法：双击或在命令行运行 build_installer.bat
rem 产物：dist\LightShotTranslate-Setup-版本.exe（安装包）和 dist\LightShotTranslate-portable-版本.zip（免安装便携包）
rem 两者都自带 .NET 运行时和 VC++ 运行库，目标电脑无需安装任何环境（Windows 10 1607 及以上 x64）。

echo [1/3] dotnet publish（自带运行时） ...
powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 -SelfContained
if errorlevel 1 goto :fail

set "VER=0.4.0"
for /f "delims=" %%v in ('powershell -NoProfile -Command "([xml](Get-Content 'LightShotTranslate.csproj')).Project.PropertyGroup.Version"') do set "VER=%%v"

echo [2/3] Inno Setup 编译安装包（版本 %VER%） ...
set "PF86=%ProgramFiles(x86)%"
set "ISCC="
if exist "%PF86%\Inno Setup 5\ISCC.exe" set "ISCC=%PF86%\Inno Setup 5\ISCC.exe"
if exist "%PF86%\Inno Setup 6\ISCC.exe" set "ISCC=%PF86%\Inno Setup 6\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 6\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if exist "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%PF86%\Inno Setup 7\ISCC.exe" set "ISCC=%PF86%\Inno Setup 7\ISCC.exe"
if exist "%ProgramFiles%\Inno Setup 7\ISCC.exe" set "ISCC=%ProgramFiles%\Inno Setup 7\ISCC.exe"
if "%ISCC%"=="" goto :noiscc

"%ISCC%" /DAppVersion=%VER% installer.iss
if errorlevel 1 goto :fail

echo [3/3] 生成便携 zip ...
powershell -NoProfile -Command "Compress-Archive -Path 'publish\*' -DestinationPath 'dist\LightShotTranslate-portable-%VER%.zip' -Force"
if errorlevel 1 goto :fail

echo.
echo 完成：
echo   dist\LightShotTranslate-Setup-%VER%.exe
echo   dist\LightShotTranslate-portable-%VER%.zip
exit /b 0

:noiscc
echo 找不到 ISCC.exe：请安装 Inno Setup 6，或把本文件里的路径改成你的安装位置。
echo 也可以先用 publish 目录打 zip 发给测试人员（解压后直接运行 LightShotTranslate.exe）。
exit /b 1

:fail
echo 失败，请查看上面的错误信息。
exit /b 1
