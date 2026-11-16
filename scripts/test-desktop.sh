#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

# dotnet test 不读取 Directory.Build.rsp；用环境变量关闭 MSBuild node reuse。
# 退出时兜底清理构建服务器，确保测试结束后不残留 VBCSCompiler / MSBuild node。
export MSBUILDDISABLENODEREUSE=1
trap 'dotnet build-server shutdown >/dev/null 2>&1 || true' EXIT

dotnet test src/Desktop/Tests/Desktop.Tests.csproj \
  -p:UseSharedCompilation=false "$@"
