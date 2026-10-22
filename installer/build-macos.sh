#!/usr/bin/env bash
# 构建 macOS .app + .dmg（Desktop 客户端，自包含 osx-arm64 载荷，ad-hoc 签名、不公证）。
# 用法：installer/build-macos.sh --version X.Y.Z [--rid osx-arm64] [--publish-dir DIR] [--skip-publish]
# 产物：dist/Mythology-Desktop-<V>-osx-arm64.dmg (+ .sha256)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=packaging.conf
. "$SCRIPT_DIR/packaging.conf"
# shellcheck source=lib/common.sh
. "$SCRIPT_DIR/lib/common.sh"

VERSION="" RID="osx-arm64" PUBLISH_DIR="" SKIP_PUBLISH=0
parse_common_args "$@"
VERSION="$(resolve_version "$VERSION")"
[ "$RID" = "osx-arm64" ] || die "本脚本只构建 osx-arm64（Apple Silicon），实际：$RID"
for tool in sips iconutil hdiutil codesign plutil; do
  command -v "$tool" >/dev/null 2>&1 || die "缺少 macOS 工具 ${tool}：本脚本必须在 macOS runner 上运行"
done

PUBLISH_DIR="${PUBLISH_DIR:-$DIST_DIR/publish/desktop-$RID}"
if [ "$SKIP_PUBLISH" = "1" ]; then
  assert_payload "$PUBLISH_DIR"
else
  publish_desktop "$RID" "$PUBLISH_DIR" "$VERSION"
fi

APP="$DIST_DIR/$MACOS_APP_NAME"
DMG="$DIST_DIR/Mythology-Desktop-$VERSION-$RID.dmg"
# CFBundleShortVersionString/CFBundleVersion 要求三段纯数字（Apple 校验），
# 预发布后缀（0.0.2-alpha.1）放进 CFBundleVersion 会被拒 → 用去掉后缀的 BASE。
BASE_VERSION="$(printf '%s' "$VERSION" | sed -E 's/-[0-9A-Za-z.-]+$//')"
mkdir -p "$DIST_DIR"

WORK="$(mktemp -d)"
MNT="$WORK/mnt"
cleanup() {
  hdiutil detach "$MNT" -quiet >/dev/null 2>&1 || true
  rm -rf "$WORK"
}
trap cleanup EXIT INT TERM

log "组装 $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

# 载荷整体进 Contents/MacOS：AppContext.BaseDirectory 即该目录，plugins/ 与 Resources/ 的相对布局保持不变。
cp -R "$PUBLISH_DIR/." "$APP/Contents/MacOS/"
chmod +x "$APP/Contents/MacOS/$APP_NAME"

# Info.plist：模板里的 {{TOKEN}} 逐行替换（不用 sed -i，BSD sed 的 -i 需要空串参数）。
PLIST_TMPL="$SCRIPT_DIR/macos/Info.plist.in"
[ -f "$PLIST_TMPL" ] || die "缺少模板：$PLIST_TMPL"
sed -e "s|{{BUNDLE_ID}}|$MACOS_BUNDLE_ID|" \
    -e "s|{{BUNDLE_NAME}}|$MACOS_BUNDLE_NAME|" \
    -e "s|{{DISPLAY_NAME}}|$APP_DISPLAY_NAME|" \
    -e "s|{{VERSION}}|$BASE_VERSION|" \
    -e "s|{{MIN_OS}}|$MACOS_MIN_OS|" \
    -e "s|{{CATEGORY}}|$MACOS_CATEGORY|" \
    "$PLIST_TMPL" > "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist" >/dev/null || die "Info.plist 不是合法 plist"

# .icns：只用系统自带 sips 缩放（源图 1024×1024），iconutil 打包。
ICON_SRC="$REPO_ROOT/src/Desktop/Assets/mythology-mark.png"
[ -f "$ICON_SRC" ] || die "缺少图标源文件：$ICON_SRC"
ICONSET="$WORK/AppIcon.iconset"
mkdir -p "$ICONSET"
for spec in 16:icon_16x16.png 32:icon_16x16@2x.png 32:icon_32x32.png 64:icon_32x32@2x.png \
            128:icon_128x128.png 256:icon_128x128@2x.png 256:icon_256x256.png 512:icon_256x256@2x.png \
            512:icon_512x512.png 1024:icon_512x512@2x.png; do
  px="${spec%%:*}"
  name="${spec#*:}"
  sips -s format png -z "$px" "$px" "$ICON_SRC" --out "$ICONSET/$name" >/dev/null
done
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

# ad-hoc 签名（不公证）：Apple Silicon 强制要求可执行的 Mach-O 带签名，否则 exec 即 SIGKILL。
#
# 用 --deep 而不是"逐个签 Mach-O 再签 bundle"：载荷里 plugins/**/*.dll 与 exe 同处
# Contents/MacOS，codesign 把这个目录当作嵌套代码区，单独签 exe 会直接失败
# （实机报 "code object is not signed at all / In subcomponent: …Plugins.ContextContinuation.dll"）。
# --deep 递归处理嵌套项，是 .NET 单文件 app 的标准做法。仍不加 --options=runtime：
# 加固运行时的库校验会与 .NET 自解压出来的未签名 dylib 冲突。
log "ad-hoc 签名 $APP"
if ! codesign --force --deep --sign - --timestamp=none "$APP" 2>"$WORK/codesign-deep.err"; then
  log "--deep 失败，退回仅签主程序 + bundle"
  cat "$WORK/codesign-deep.err" >&2
  codesign --force --sign - --timestamp=none "$APP/Contents/MacOS/$APP_NAME" \
    || die "签名主程序失败：$APP/Contents/MacOS/$APP_NAME"
  codesign --force --sign - --timestamp=none "$APP" || die "签名 bundle 失败"
fi
codesign --verify --verbose "$APP" || die "codesign --verify 失败"

log "打 dmg → $DMG"
STAGING="$WORK/staging"
mkdir -p "$STAGING"
cp -R "$APP" "$STAGING/$MACOS_APP_NAME"
ln -s /Applications "$STAGING/Applications"
# 未公证，首次启动会被 Gatekeeper 拦：把放行办法（右键打开 / xattr -dr com.apple.quarantine）
# 随 dmg 发给用户，省得他们以为 App 坏了。
NOTE="$SCRIPT_DIR/macos/安装说明.txt"
[ -f "$NOTE" ] || die "缺少安装说明：$NOTE"
cp "$NOTE" "$STAGING/安装说明.txt"
rm -f "$DMG"
hdiutil create -srcfolder "$STAGING" -volname "$MACOS_BUNDLE_NAME" \
  -fs HFS+ -format UDZO -imagekey zlib-level=9 "$DMG" >/dev/null || die "hdiutil create 失败"

# 挂载回来验一遍：dmg 才是发行物，只验本地 .app 不算验证。
mkdir -p "$MNT"
hdiutil attach -nobrowse -readonly -mountpoint "$MNT" "$DMG" >/dev/null || die "hdiutil attach 失败：$DMG"
for want in "$MACOS_APP_NAME/Contents/MacOS/$APP_NAME" \
            "$MACOS_APP_NAME/Contents/MacOS/plugins/novel-writing-tools/Plugins.NovelTools.dll" \
            "$MACOS_APP_NAME/Contents/MacOS/Resources/dict.txt" \
            "$MACOS_APP_NAME/Contents/Info.plist" \
            "$MACOS_APP_NAME/Contents/Resources/AppIcon.icns" \
            "安装说明.txt" \
            "Applications"; do
  [ -e "$MNT/$want" ] || die "dmg 内缺少 ${want}：$DMG"
done
[ -x "$MNT/$MACOS_APP_NAME/Contents/MacOS/$APP_NAME" ] || die "dmg 内主可执行文件没有执行位"
codesign --verify "$MNT/$MACOS_APP_NAME" || die "dmg 内的 app 签名校验失败"
plutil -lint "$MNT/$MACOS_APP_NAME/Contents/Info.plist" >/dev/null || die "dmg 内 Info.plist 非法"
hdiutil detach "$MNT" -quiet || die "hdiutil detach 失败"

write_sha256 "$DMG"
log "完成。安装：挂载 dmg 后把 $MACOS_APP_NAME 拖进 Applications（未公证，首次需右键→打开）"
