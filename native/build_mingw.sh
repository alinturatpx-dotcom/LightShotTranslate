#!/bin/sh
# 在 Linux / WSL 上用 MinGW-w64 交叉编译 lst-ocr.exe（静态链接，不依赖 VC++ 运行库）
#   sudo apt install g++-mingw-w64-x86-64-posix
set -e
cd "$(dirname "$0")"
x86_64-w64-mingw32-g++-posix -std=gnu++17 -O2 -s -static -static-libgcc -static-libstdc++ -Wl,--gc-sections \
    -Isrc/include src/lst_ocr.cpp -o lst-ocr.exe -lpsapi
ls -la lst-ocr.exe
