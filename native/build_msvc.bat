@echo off
rem 在 "x64 Native Tools Command Prompt for VS" 里运行。/MT = 静态链接 CRT，lst-ocr.exe 本身不依赖 VC++ 运行库
cd /d "%~dp0"
cl /nologo /O2 /EHsc /std:c++17 /MT /utf-8 /Isrc\include src\lst_ocr.cpp /Fe:lst-ocr.exe psapi.lib
if errorlevel 1 exit /b 1
del lst_ocr.obj 2>nul
echo 完成：native\lst-ocr.exe
