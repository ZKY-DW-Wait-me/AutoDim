#!/usr/bin/env bash
# 圆弧/圆角标注通用性回归：7 张不同尺寸/结构的合成图逐个跑 ADIMCLEAN，
# 断言 arc 标注数量符合预期（防"只按单张图调参"）。
set -uo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="/c/Program Files/dotnet/dotnet.exe"
ACAD="/d/Program Files/AutoCAD 2025"
CORECONSOLE_WIN="D:\\Program Files\\AutoCAD 2025\\accoreconsole.exe"
DLL_WIN="$(cygpath -w "$ROOT/src/AutoDim/bin/x64/Debug/net8.0-windows/AutoDim.dll")"
POWERSHELL="/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"
PS_RUNNER_WIN="$(cygpath -w "$ROOT/test/run-headless.ps1")"
OUT="$ROOT/test/arc_cases"

echo "== 1) build =="
"$DOTNET" build "$ROOT/AutoDim.sln" -c Debug -v minimal || { echo "BUILD FAILED"; exit 1; }

echo "== 2) generate arc cases =="
python "$ROOT/test/make_arc_cases.py" "$(cygpath -w "$OUT")" || { echo "GEN FAILED"; exit 1; }

SCR="$ROOT/test/run-arc-cases.generated.scr"
cat > "$SCR" <<EOF
SECURELOAD
0
FILEDIA
0
NETLOAD
$DLL_WIN
ADIMCLEAN
EOF
SCR_WIN="$(cygpath -w "$SCR")"

fail=0
for dxf in "$OUT"/*.dxf; do
    name="$(basename "$dxf" .dxf)"
    log="$ROOT/test/arc-cases.$name.log"
    "$POWERSHELL" -NoProfile -ExecutionPolicy Bypass -File "$PS_RUNNER_WIN" \
        -InputDwg "$(cygpath -w "$dxf")" -Script "$SCR_WIN" \
        -Log "$(cygpath -w "$log")" -TimeoutSec 90
    clean="$(tr -d '\000' < "$log")"
    if echo "$clean" | grep -q "出错"; then
        echo "FAIL: $name 运行出错"
        fail=1
        continue
    fi
    # 累加所有 AUTODIM 行的 overall/segment/arc/circle
    ovr=0; seg=0; arc=0; cir=0
    while IFS= read -r line; do
        case "$line" in
            *AUTODIM:*)
                v="$(echo "$line" | sed -n 's/.*overall=\([0-9][0-9]*\).*/\1/p')"
                [ -n "$v" ] && ovr=$((ovr + v))
                v="$(echo "$line" | sed -n 's/.*segment=\([0-9][0-9]*\).*/\1/p')"
                [ -n "$v" ] && seg=$((seg + v))
                v="$(echo "$line" | sed -n 's/.*arc=\([0-9][0-9]*\).*/\1/p')"
                [ -n "$v" ] && arc=$((arc + v))
                v="$(echo "$line" | sed -n 's/.*circle=\([0-9][0-9]*\).*/\1/p')"
                [ -n "$v" ] && cir=$((cir + v))
                ;;
        esac
    done <<< "$clean"
    echo "  $name: overall=$ovr segment=$seg arc=$arc circle=$cir"

    # 预期 arc（同半径圆角 >=3 合并为 N×R，故不等于圆角个数）
    case "$name" in
        a_small_50x30_r5)      [ "$arc" -eq 1 ] || { echo "FAIL: $name arc=$arc 期望1"; fail=1; };;
        b_large_500x300_r25)   [ "$arc" -eq 1 ] || { echo "FAIL: $name arc=$arc 期望1"; fail=1; };;
        c_Uslot)               [ "$arc" -eq 3 ] || { echo "FAIL: $name arc=$arc 期望3"; fail=1; };;
        d_mixed_bulge_arc)     [ "$arc" -eq 2 ] || { echo "FAIL: $name arc=$arc 期望2(bulge+独立Arc各合并)"; fail=1; };;
        e_12fillet)            [ "$arc" -eq 2 ] || { echo "FAIL: $name arc=$arc 期望2(超量合并)"; fail=1; };;
        f_micro_10x8_r2)       [ "$arc" -eq 1 ] || { echo "FAIL: $name arc=$arc 期望1"; fail=1; };;
        g_huge_2000x1200_r100) [ "$arc" -eq 1 ] || { echo "FAIL: $name arc=$arc 期望1"; fail=1; };;
        h_rect_closed)         { [ "$ovr" -eq 2 ] && [ "$seg" -eq 0 ]; } || { echo "FAIL: $name 应只出总体长宽(ovr=2,seg=0)"; fail=1; };;
        i_rect_lines)          { [ "$ovr" -eq 2 ] && [ "$seg" -eq 0 ]; } || { echo "FAIL: $name 应只出总体长宽(ovr=2,seg=0)"; fail=1; };;
        j_fillet_lines)        { [ "$ovr" -eq 2 ] && [ "$seg" -eq 4 ] && [ "$arc" -eq 1 ]; } || { echo "FAIL: $name 应总体100×80+4段直边+4×R10"; fail=1; };;
    esac
    # 通用健康：除纯矩形 h/i 外，每张图必须有总体+分段+圆弧
    case "$name" in
        h_rect_closed|i_rect_lines) ;;
        *) { [ "$ovr" -ge 2 ] && [ "$seg" -ge 4 ] && [ "$arc" -ge 1 ]; } || { echo "FAIL: $name 总体/分段/圆弧缺失"; fail=1; };;
    esac
done

rm -f "$SCR"
for f in "$OUT"/*.dxf; do rm -f "$f"; done
rm -f "$ROOT"/test/arc-cases.*.log "$ROOT"/test/arc-cases.*.log.err

if [ "$fail" = "0" ]; then echo "== ARC CASES PASS =="; exit 0; else echo "== ARC CASES FAILED =="; exit 1; fi
