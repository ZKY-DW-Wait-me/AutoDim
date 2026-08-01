#!/usr/bin/env bash
# AutoDim 无界面回归测试：编译 -> SECURELOAD=0 + NETLOAD -> 开启自动清洗 -> ADIMALL -> 断言
# 用法: bash test/run-tests.sh
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="/c/Program Files/dotnet/dotnet.exe"
ACAD="/d/Program Files/AutoCAD 2025"
CORECONSOLE="$ACAD/accoreconsole.exe"
# 注意：模板 acadiso.dwt 首次初始化会在核心控制台卡住，改用仓库内的 test.dwg 作为输入。
INPUT="$ROOT/test.dwg"
SCRIPT="$ROOT/test/run-headless.scr"
DLL="$ROOT/src/AutoDim/bin/x64/Debug/net8.0-windows/AutoDim.dll"
LOG="$ROOT/test/last-run.log"
PLUGINS="$HOME/AppData/Roaming/Autodesk/ApplicationPlugins"
BUNDLE="$PLUGINS/AutoDim.bundle"

# accoreconsole 需要 Windows 路径（Git Bash 的 /d/... 经 timeout 包装后不转换）
CORECONSOLE_WIN="$(cygpath -w "$CORECONSOLE")"
INPUT_WIN="$(cygpath -w "$INPUT")"
DLL_WIN="$(cygpath -w "$DLL")"

echo "== 1) build =="
"$DOTNET" build "$ROOT/AutoDim.sln" -c Debug -v minimal || { echo "BUILD FAILED"; exit 1; }
[ -f "$DLL" ] || { echo "BUILD FAILED: 找不到 $DLL"; exit 1; }

echo "== 2) build headless script (SECURELOAD=0 + NETLOAD) =="
SCR="$ROOT/test/run-headless.generated.scr"
# 用 heredoc 生成：变量原样展开，反斜杠不会被吃掉
cat > "$SCR" <<EOF
SECURELOAD
0
NETLOAD
$DLL_WIN
ADIMSAMPLE
ADIMALL
ADIMCLEAN
ADIMCLEAN
EOF
SCR_WIN="$(cygpath -w "$SCR")"
echo "  -> $SCR"

echo "== 3) headless run (accoreconsole, 最多 90s) =="
# 注意：accoreconsole 从 Git Bash(MSYS) 直接启动会卡住，必须经 PowerShell 启动。
LOG_WIN="$(cygpath -w "$LOG")"
POWERSHELL="/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
PS_RUNNER_WIN="$(cygpath -w "$ROOT/test/run-headless.ps1")"
"$POWERSHELL" -NoProfile -ExecutionPolicy Bypass -File "$PS_RUNNER_WIN" -InputDwg "$INPUT_WIN" -Script "$SCR_WIN" -Log "$LOG_WIN" -TimeoutSec 90
rc=$?

echo "----- accoreconsole 输出 -----"
# 日志多为 UTF-16LE；先尝试解码为 UTF-8 便于读中文，失败则退回去 null 字节
DEC="$(iconv -f UTF-16LE -t UTF-8 "$LOG" 2>/dev/null | tr -d '\r')"
if [ -n "$DEC" ]; then printf '%s\n' "$DEC"; else tr -d '\000' < "$LOG"; fi
echo "------------------------------"
[ $rc -eq 124 ] && echo "(!! accoreconsole 超时 90s)"
rm -f "$SCR"

echo "== 4) assert =="
fail=0
# ASCII 前缀断言，规避中文控制台编码问题
CLEAN="$(tr -d '\000' < "$LOG")"
# 用 grep -c 而非 grep -q：set -o pipefail 下 grep -q 提前退出会让 echo 收 SIGPIPE，
# 管道被判失败(日志变大后必现)；-c 读完整个输入。
[ "$(echo "$CLEAN" | grep -c 'ADIMSAMPLE:')" -gt 0 ] || { echo "FAIL: 插件未加载或示例未生成"; fail=1; }
[ "$(echo "$CLEAN" | grep -c 'AUTODIM:')" -gt 0 ]    || { echo "FAIL: 未执行整图标注"; fail=1; }
[ "$(echo "$CLEAN" | grep -c 'ADIMCLEAN:')" -gt 0 ]  || { echo "FAIL: 未执行图纸清洗"; fail=1; }
# 断言至少生成了标注（具体数量随 Phase 2~5 的类别组合变化，不做硬编码）
tot="$(echo "$CLEAN" | sed -n 's/.*total=\([0-9][0-9]*\).*/\1/p' | head -n1)"
{ [ -n "$tot" ] && [ "$tot" -gt 0 ]; } || { echo "FAIL: total 标注数量为 0"; fail=1; }
# 幂等断言：连续两次 ADIMCLEAN 的统计行必须完全一致（组级清场修复的回归保护）
CL1="$(echo "$CLEAN" | grep "ADIMCLEAN:" | tail -2 | head -1)"
CL2="$(echo "$CLEAN" | grep "ADIMCLEAN:" | tail -1)"
[ -n "$CL1" ] && [ "$CL1" = "$CL2" ] || { echo "FAIL: 重复运行 ADIMCLEAN 结果不一致(非幂等)"; fail=1; }

echo "== 5) 第二张图泛化(合成 extra.dxf) =="
EXTRA="$ROOT/test/extra.dxf"
python "$ROOT/test/make_extra_dxf.py" "$(cygpath -w "$EXTRA")" || { echo "FAIL: 生成 extra.dxf 失败"; exit 1; }
SCR2="$ROOT/test/run-headless.extra.scr"
cat > "$SCR2" <<EOF
SECURELOAD
0
NETLOAD
$DLL_WIN
ADIMCLEAN
EOF
LOG2="$ROOT/test/extra-run.log"
"$POWERSHELL" -NoProfile -ExecutionPolicy Bypass -File "$PS_RUNNER_WIN" \
    -InputDwg "$(cygpath -w "$EXTRA")" -Script "$(cygpath -w "$SCR2")" \
    -Log "$(cygpath -w "$LOG2")" -TimeoutSec 90
rm -f "$SCR2"
CLEAN2="$(tr -d '\000' < "$LOG2")"
echo "$CLEAN2" | grep -q "ADIMCLEAN:" || { echo "FAIL: 第二张图未执行清洗"; fail=1; }
f2="$(echo "$CLEAN2" | sed -n 's/.*faces=\([0-9][0-9]*\).*/\1/p' | head -n1)"
{ [ -n "$f2" ] && [ "$f2" -ge 3 ]; } || { echo "FAIL: 第二张图闭合面过少($f2)"; fail=1; }
tot2="$(echo "$CLEAN2" | sed -n 's/.*落地 \([0-9][0-9]*\) 个尺寸.*/\1/p' | head -n1)"
tot2="$(echo "$CLEAN2" | sed -n 's/.*landed=\([0-9][0-9]*\).*/\1/p' | head -n1)"
{ [ -n "$tot2" ] && [ "$tot2" -gt 0 ]; } || { echo "FAIL: 第二张图标注数为 0"; fail=1; }

echo "== 6) 干净 CAD 图泛化(test2.dwg，SW 导出) =="
if [ -f "$ROOT/test2.DWG" ]; then
    SCR3="$ROOT/test/run-headless.test2.scr"
    cat > "$SCR3" <<EOF
SECURELOAD
0
NETLOAD
$DLL_WIN
ADIMCLEAN
EOF
    LOG3="$ROOT/test/test2-run.log"
    "$POWERSHELL" -NoProfile -ExecutionPolicy Bypass -File "$PS_RUNNER_WIN" \
        -InputDwg "$(cygpath -w "$ROOT/test2.DWG")" -Script "$(cygpath -w "$SCR3")" \
        -Log "$(cygpath -w "$LOG3")" -TimeoutSec 90
    rm -f "$SCR3"
    CLEAN3="$(tr -d '\000' < "$LOG3")"
    echo "$CLEAN3" | grep -q "ADIMCLEAN:" || { echo "FAIL: test2 未执行清洗"; fail=1; }
    t3="$(echo "$CLEAN3" | sed -n 's/.*landed=\([0-9][0-9]*\).*/\1/p' | head -n1)"
    { [ -n "$t3" ] && [ "$t3" -gt 0 ]; } || { echo "FAIL: test2 标注数为 0"; fail=1; }
    th3="$(echo "$CLEAN3" | sed -n 's/.*textHits=\([0-9][0-9]*\).*/\1/p' | head -n1)"
    { [ -n "$th3" ] && [ "$th3" -le 2 ]; } || { echo "FAIL: test2 文字撞车过多($th3)"; fail=1; }
else
    echo "SKIP: 未找到 test2.DWG"
fi

if [ "$fail" = "0" ]; then echo "== PASS =="; exit 0; else echo "== TESTS FAILED =="; exit 1; fi
