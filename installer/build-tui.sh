#!/usr/bin/env bash
# 构建 TUI 发布包（src/Host 已是 PackAsTool 工具，ToolCommandName=mythology）。
# 用法：installer/build-tui.sh --version X.Y.Z
# 产物：dist/Mythology.<V>.nupkg (+ .sha256)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=packaging.conf
. "$SCRIPT_DIR/packaging.conf"
# shellcheck source=lib/common.sh
. "$SCRIPT_DIR/lib/common.sh"

VERSION="" PUBLISH_DIR="" SKIP_PUBLISH=0
parse_common_args "$@"
VERSION="$(resolve_version "$VERSION")"
command -v unzip >/dev/null 2>&1 || die "缺少 unzip：需要它核验 nupkg 内容"

NUPKG_DIR="$DIST_DIR/nupkg"
HOST_CSPROJ="$REPO_ROOT/src/Host/Host.csproj"
NUPKG="$NUPKG_DIR/Mythology.$VERSION.nupkg"

pack_tool() {
  rm -rf "$NUPKG_DIR"
  mkdir -p "$NUPKG_DIR"
  log "dotnet pack src/Host → $NUPKG_DIR"
  dotnet pack "$HOST_CSPROJ" -c Release -p:MythologyVersion="$VERSION" -o "$NUPKG_DIR" \
    || die "dotnet pack 失败"
}

# PackAsTool 的包内容取自 $(PublishDir)，即 csproj 里 CopyRuntimeAssetsToPublish 的落点。
# 内置插件与 jieba 词典缺任何一样，装出来的 CLI 就是残废的（无工具、无中文分词）。
verify_nupkg() {
  local listing
  listing="$(unzip -l "$NUPKG")"
  for want in "tools/net10.0/any/DotnetToolSettings.xml" \
              "tools/net10.0/any/Host.dll" \
              "tools/net10.0/any/plugins/novel-writing-tools/Plugins.NovelTools.dll" \
              "tools/net10.0/any/Resources/dict.txt"; do
    printf '%s\n' "$listing" | grep -Fq -- "$want" || return 1
  done
  log "nupkg 内容核验通过（4/4 关键条目）"
}

pack_tool
if ! verify_nupkg; then
  # SDK 若改变了 PackTool 对 $(PublishDir) 的取值时机（glob 早于 AfterTargets=Publish 的复制），
  # 就退回显式两步：先 publish 出完整载荷，再让 pack 用该目录作为包内容源。
  log "pack 未带上 plugins/Resources，回退两步式（publish → pack -p:PublishDir=）"
  BUILD_PUBLISH="$DIST_DIR/build/tui-publish"
  rm -rf "$BUILD_PUBLISH"
  dotnet publish "$HOST_CSPROJ" -c Release -o "$BUILD_PUBLISH" || die "dotnet publish（TUI 载荷）失败"
  assert_payload "$BUILD_PUBLISH" "Host.dll"
  rm -rf "$NUPKG_DIR"
  mkdir -p "$NUPKG_DIR"
  dotnet pack "$HOST_CSPROJ" -c Release -p:MythologyVersion="$VERSION" \
    -p:PublishDir="$BUILD_PUBLISH/" -p:NoBuild=true -o "$NUPKG_DIR" || die "dotnet pack（回退）失败"
  verify_nupkg || die "nupkg 仍缺少插件/词典载荷：$NUPKG"
fi

[ -f "$NUPKG" ] || die "未产出 nupkg：$NUPKG"

# 真装一次：tool manifest + shim 生成 + 插件扫描全链路都在这一步被证明。
TOOLDIR="$(mktemp -d)"
trap 'rm -rf "$TOOLDIR"' EXIT INT TERM
log "dotnet tool install --tool-path <tmp> Mythology --version $VERSION"
dotnet tool install --tool-path "$TOOLDIR" Mythology --version "$VERSION" \
  --add-source "$NUPKG_DIR" || die "dotnet tool install 失败"

TOOL_BIN="$TOOLDIR/mythology"
[ -f "$TOOL_BIN" ] || TOOL_BIN="$TOOLDIR/mythology.exe"   # Git Bash on Windows 下是 .exe
[ -f "$TOOL_BIN" ] || die "工具入口未生成：$TOOLDIR/mythology(.exe)"

PLUGIN_LIST="$("$TOOL_BIN" plugin list 2>&1)" || die "mythology plugin list 退出码非 0：
$PLUGIN_LIST"
printf '%s\n' "$PLUGIN_LIST"
for want in "FileSystem Tools" "Novel Writing Tools"; do
  printf '%s\n' "$PLUGIN_LIST" | grep -Fq -- "$want" \
    || die "plugin list 输出没有 '$want'，内置插件未从工具安装目录加载：
$PLUGIN_LIST"
done
log "工具冒烟通过：mythology plugin list 列出内置插件"

cp -f "$NUPKG" "$DIST_DIR/"
write_sha256 "$DIST_DIR/Mythology.$VERSION.nupkg"
log "完成。安装：dotnet tool install -g Mythology --version $VERSION --add-source <release 下载目录>"
