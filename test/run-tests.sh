#!/usr/bin/env bash
# AutoDim 无界面回归测试：编译 -> 部署 .bundle 自动加载 -> accoreconsole 运行 -> 断言
# 用法: bash test/run-tests.sh
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="/c/Program Files/dotnet/dotnet.exe"
ACAD="/d/Program Files/AutoCAD 2025"
CORECONSOLE="$ACAD/accoreconsole.exe"
SEED="$ACAD/UserDataCache/zh-cn/Template/acadiso.dwt"
SCRIPT="$ROOT/test/run-headless.scr"
DLL="$ROOT/src/AutoDim/bin/x64/Debug/net8.0-windows/AutoDim.dll"
LOG="$ROOT/test/last-run.log"
PLUGINS="$HOME/AppData/Roaming/Autodesk/ApplicationPlugins"
BUNDLE="$PLUGINS/AutoDim.bundle"

echo "== 1) build =="
"$DOTNET" build "$ROOT/AutoDim.sln" -c Debug -v minimal || { echo "BUILD FAILED"; exit 1; }
[ -f "$DLL" ] || { echo "BUILD FAILED: 找不到 $DLL"; exit 1; }

echo "== 2) deploy .bundle (auto-load, 绕开 NETLOAD/安全确认) =="
mkdir -p "$BUNDLE/Contents"
cp "$ROOT/deploy/AutoDim.bundle/PackageContents.xml" "$BUNDLE/PackageContents.xml"
cp "$DLL" "$BUNDLE/Contents/AutoDim.dll"
echo "  -> $BUNDLE"

echo "== 3) headless run (accoreconsole, 最多 90s) =="
timeout 90 "$CORECONSOLE" /i "$SEED" /s "$SCRIPT" /l en-US > "$LOG" 2>&1
rc=$?

echo "----- accoreconsole 输出 -----"
# 日志多为 UTF-16LE；先尝试解码为 UTF-8 便于读中文，失败则退回去 null 字节
DEC="$(iconv -f UTF-16LE -t UTF-8 "$LOG" 2>/dev/null | tr -d '\r')"
if [ -n "$DEC" ]; then printf '%s\n' "$DEC"; else tr -d '\000' < "$LOG"; fi
echo "------------------------------"
[ $rc -eq 124 ] && echo "(!! accoreconsole 超时 90s)"

echo "== 4) assert =="
fail=0
# ASCII 前缀断言，规避中文控制台编码问题
CLEAN="$(tr -d '\000' < "$LOG")"
echo "$CLEAN" | grep -q "ADIMSAMPLE:" || { echo "FAIL: 插件未加载或示例未生成"; fail=1; }
echo "$CLEAN" | grep -q "overall=2"   || { echo "FAIL: overall!=2（总体尺寸未生成 2 个）"; fail=1; }
echo "$CLEAN" | grep -q "total=2"     || { echo "FAIL: total!=2"; fail=1; }

if [ "$fail" = "0" ]; then echo "== PASS =="; exit 0; else echo "== TESTS FAILED =="; exit 1; fi
