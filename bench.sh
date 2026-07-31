#!/usr/bin/env bash
# AutoDim 基准：test.dwg 上跑 ADIMCLEAN，输出 标注总量/真实重叠对/耗时。
# 用于跟踪布局优化等后续改动的量化对比。
set -uo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="/c/Program Files/dotnet/dotnet.exe"

echo "== build =="
"$DOTNET" build "$ROOT/AutoDim.sln" -c Debug -v minimal >/dev/null || { echo "BUILD FAILED"; exit 1; }

SCR="$ROOT/test/run-headless.generated.scr"
DLL_WIN="$(cygpath -w "$ROOT/src/AutoDim/bin/x64/Debug/net8.0-windows/AutoDim.dll")"
cat > "$SCR" <<EOF
SECURELOAD
0
NETLOAD
$DLL_WIN
ADIMCLEAN
EOF

LOG_WIN="$(cygpath -w "$ROOT/test/bench.log")"
POWERSHELL="/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
PS_WIN="$(cygpath -w "$ROOT/test/run-headless.ps1")"
INPUT_WIN="$(cygpath -w "$ROOT/test.dwg")"

start=$(date +%s)
"$POWERSHELL" -NoProfile -ExecutionPolicy Bypass -File "$PS_WIN" \
    -InputDwg "$INPUT_WIN" -Script "$(cygpath -w "$SCR")" -Log "$LOG_WIN" -TimeoutSec 120
rc=$?
end=$(date +%s)
rm -f "$SCR"

DEC="$(iconv -f UTF-16LE -t UTF-8 "$ROOT/test/bench.log" 2>/dev/null | tr -d '\r')"
echo "$DEC" | grep -E "合计:|ADIMCLEAN:"
echo "耗时: $((end - start))s"
exit $rc
