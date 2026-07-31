#!/usr/bin/env bash
# 编译并把插件部署到 ApplicationPlugins(AutoCAD 启动时自动加载)。
# 改代码后的验证流程：关闭 AutoCAD -> 运行本脚本 -> 重开 AutoCAD -> 跑命令。
# (.NET 程序集无法在运行中的 AutoCAD 里热替换，所以必须先关闭 AutoCAD。)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
DOTNET="/c/Program Files/dotnet/dotnet.exe"
DLL="$ROOT/src/AutoDim/bin/x64/Debug/net8.0-windows/AutoDim.dll"
BUNDLE="$HOME/AppData/Roaming/Autodesk/ApplicationPlugins/AutoDim.bundle"

echo "== build =="
"$DOTNET" build "$ROOT/AutoDim.sln" -c Debug -v minimal
[ -f "$DLL" ] || { echo "构建失败: 找不到 $DLL"; exit 1; }

echo "== deploy =="
mkdir -p "$BUNDLE/Contents"
cp "$ROOT/deploy/AutoDim.bundle/PackageContents.xml" "$BUNDLE/PackageContents.xml"
cp "$DLL" "$BUNDLE/Contents/AutoDim.dll"

echo "已部署到: $BUNDLE"
echo "重启 AutoCAD 即加载最新版 (AutoDim 会自动加载)。"
