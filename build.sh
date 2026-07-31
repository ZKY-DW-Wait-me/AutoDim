#!/usr/bin/env bash
# 编译封装：bash build.sh [Debug|Release]
set -euo pipefail
DOTNET="/c/Program Files/dotnet/dotnet.exe"
CFG="${1:-Debug}"
"$DOTNET" build "$(cd "$(dirname "$0")" && pwd)/AutoDim.sln" -c "$CFG" -v minimal
