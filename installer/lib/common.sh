# 打包脚本共享助手 —— 被 installer/build-{linux,macos,tui}.sh source。
# 用法约定：调用方先 source installer/packaging.conf，再 source 本文件（APP_NAME 由 conf 提供）。
# 约束：变量后面紧跟全角中文标点（（）：，。）时必须写成 ${var}。
# macOS 自带 bash 3.2 按字节解析变量名，会把多字节字符的首字节吞进名字里，
# 在 set -u 下报 "dir…: unbound variable"（Linux 的 bash 5 + UTF-8 语言环境不会）。

set -euo pipefail

_COMMON_SH_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$_COMMON_SH_DIR/../.." && pwd)"
DIST_DIR="$REPO_ROOT/dist"

log() { printf '==> %s\n' "$*"; }
die() { printf '错误: %s\n' "$*" >&2; exit 1; }

# 版本推导优先级：显式参数（--version）> MYTHOLOGY_VERSION > 最近一个 v* tag。
# 允许 SemVer 预发布后缀（0.0.2-alpha.1）；各平台消费点自行处理：
# NuGet/nupkg 直接用全版本号，deb 转 "~"，macOS/Windows 安装器元数据用去掉后缀的数字段。
resolve_version() {
  local v="${1:-${MYTHOLOGY_VERSION:-}}"
  if [ -z "$v" ]; then
    v="$(git -C "$REPO_ROOT" describe --tags --match 'v*' --abbrev=0 2>/dev/null || true)"
    v="${v#v}"
  fi
  [ -n "$v" ] || die "无法确定版本号：传 --version X.Y.Z[-pre] 或设置 MYTHOLOGY_VERSION"
  # 允许 SemVer 预发布后缀（0.0.2-alpha.1）；PRERELEASE 非空时为预发布版。
  printf '%s' "$v" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$' \
    || die "版本号必须是 X.Y.Z 或 X.Y.Z-预发布（不带 v、不带构建号 +meta），实际：$v"
  printf '%s' "$v"
}

# 通用参数解析：调用方先声明 VERSION="" RID="" PUBLISH_DIR="" SKIP_PUBLISH=0 再调用。
parse_common_args() {
  while [ $# -gt 0 ]; do
    case "$1" in
      --version) [ $# -ge 2 ] || die "--version 缺少参数"; VERSION="$2"; shift 2 ;;
      --rid) [ $# -ge 2 ] || die "--rid 缺少参数"; RID="$2"; shift 2 ;;
      --publish-dir) [ $# -ge 2 ] || die "--publish-dir 缺少参数"; PUBLISH_DIR="$2"; shift 2 ;;
      --skip-publish) SKIP_PUBLISH=1; shift ;;
      *) die "未知参数：$1（支持 --version/--rid/--publish-dir/--skip-publish）" ;;
    esac
  done
}

# 校验文件（macOS 无 sha256sum → shasum -a 256）。输出统一为 "HASH␣␣<basename>"：
# MSYS 的 sha256sum 默认写 "HASH *name"（二进制模式标记），自己拼格式才能保证三平台字节一致，
# 且 release job 的 sha256sum -c 认这个格式。
write_sha256() {
  local f="$1" dir base hash
  [ -f "$f" ] || die "缺少产物：$f"
  dir="$(cd "$(dirname "$f")" && pwd)"
  base="$(basename "$f")"
  if command -v sha256sum >/dev/null 2>&1; then
    hash="$( cd "$dir" && sha256sum "$base" | cut -d' ' -f1 )"
  else
    hash="$( cd "$dir" && shasum -a 256 "$base" | cut -d' ' -f1 )"
  fi
  [ -n "$hash" ] || die "计算 sha256 失败：$f"
  printf '%s  %s\n' "$hash" "$base" > "$dir/$base.sha256"
  log "sha256 $(cat "$dir/$base.sha256")"
}

# 断言发布载荷完整：内置插件与 jieba 词典是 Build 阶段 Target 复制出来的运行时必需文件，
# dotnet publish 本身不带（见 csproj 的 CopyRuntimeAssetsToPublish），缺失即装出残废客户端。
assert_payload() {
  local dir="$1" exe="${2:-${APP_NAME:-MythDesktop}}"
  [ -d "$dir" ] || die "发布目录不存在：$dir"
  [ -f "$dir/$exe" ] || die "发布载荷缺少可执行文件：$dir/$exe"
  chmod +x "$dir/$exe"
  [ -f "$dir/plugins/novel-writing-tools/Plugins.NovelTools.dll" ] \
    || die "发布载荷缺少内置插件：$dir/plugins/novel-writing-tools/Plugins.NovelTools.dll"
  [ -f "$dir/Resources/dict.txt" ] || die "发布载荷缺少 jieba 词典：$dir/Resources/dict.txt"
  [ -f "$dir/appsettings.json" ] || die "发布载荷缺少 appsettings.json：$dir/appsettings.json"
  find "$dir" -name '*.pdb' -delete
  log "载荷就绪：${dir}（插件 $(find "$dir/plugins" -mindepth 1 -maxdepth 1 -type d | wc -l | tr -d ' ') 个）"
}

publish_desktop() {
  local rid="$1" out="$2" version="$3"
  log "dotnet publish src/Desktop ($rid) → $out"
  dotnet publish "$REPO_ROOT/src/Desktop/Desktop.csproj" -c Release -r "$rid" --self-contained true \
    -p:Version="$version" -o "$out" || die "dotnet publish 失败（${rid}）"
  assert_payload "$out"
}
