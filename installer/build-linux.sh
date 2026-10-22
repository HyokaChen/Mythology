#!/usr/bin/env bash
# 构建 Linux .deb（Desktop 客户端，自包含 linux-x64 载荷）。
# 用法：installer/build-linux.sh --version X.Y.Z [--rid linux-x64] [--publish-dir DIR] [--skip-publish]
# 产物：dist/Mythology-Desktop-<V>-linux-x64.deb (+ .sha256)
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=packaging.conf
. "$SCRIPT_DIR/packaging.conf"
# shellcheck source=lib/common.sh
. "$SCRIPT_DIR/lib/common.sh"

VERSION="" RID="linux-x64" PUBLISH_DIR="" SKIP_PUBLISH=0
parse_common_args "$@"
VERSION="$(resolve_version "$VERSION")"
[ "$RID" = "linux-x64" ] || die "本脚本只构建 linux-x64（其它架构需先确认 sqlite-vec 原生库与 Avalonia 支持），实际：$RID"
command -v dpkg-deb >/dev/null 2>&1 || die "缺少 dpkg-deb：请在 Linux（或 WSL）上运行本脚本"

PUBLISH_DIR="${PUBLISH_DIR:-$DIST_DIR/publish/desktop-$RID}"
if [ "$SKIP_PUBLISH" = "1" ]; then
  assert_payload "$PUBLISH_DIR"
else
  publish_desktop "$RID" "$PUBLISH_DIR" "$VERSION"
fi

case "$RID" in linux-x64) DEB_ARCH=amd64 ;; *) die "未知的 rid→Architecture 映射：$RID" ;; esac

# staging 必须在原生 Linux 文件系统上：NTFS/DrvFs 上 chmod 不生效，打出来的包权限位是歪的。
WORK="$(mktemp -d)"
cleanup() { rm -rf "$WORK"; }
trap cleanup EXIT INT TERM

DEB="$WORK/deb"
DATA="$DEB$LINUX_INSTALL_DIR"
mkdir -p "$DATA" "$DEB/usr/bin" "$DEB/usr/share/applications" "$DEB/usr/share/pixmaps" \
         "$DEB/usr/share/icons/hicolor/1024x1024/apps" "$DEB/usr/share/doc/$LINUX_PACKAGE_NAME" "$DEB/DEBIAN"

cp -R "$PUBLISH_DIR/." "$DATA/"
chmod 755 "$DATA/$APP_NAME"
find "$DATA" -mindepth 1 -type f ! -name "$APP_NAME" -exec chmod 644 {} +
find "$DATA" -mindepth 1 -type d -exec chmod 755 {} +

cat > "$DEB/usr/bin/$LINUX_BIN_NAME" <<EOF
#!/bin/sh
exec $LINUX_INSTALL_DIR/$APP_NAME "\$@"
EOF
chmod 755 "$DEB/usr/bin/$LINUX_BIN_NAME"

cat > "$DEB/usr/share/applications/$LINUX_PACKAGE_NAME.desktop" <<EOF
[Desktop Entry]
Type=Application
Name=$APP_DISPLAY_NAME
GenericName=Novel Writing Assistant
Comment=$APP_DESCRIPTION
Exec=$LINUX_BIN_NAME
Icon=$LINUX_ICON_NAME
Terminal=false
Categories=$LINUX_DESKTOP_CATEGORY;
StartupWMClass=$APP_NAME
Keywords=novel;writing;ai;mythology;
EOF
chmod 644 "$DEB/usr/share/applications/$LINUX_PACKAGE_NAME.desktop"

ICON_SRC="$REPO_ROOT/src/Desktop/Assets/mythology-mark.png"
[ -f "$ICON_SRC" ] || die "缺少图标源文件：$ICON_SRC"
cp "$ICON_SRC" "$DEB/usr/share/pixmaps/$LINUX_ICON_NAME.png"
cp "$ICON_SRC" "$DEB/usr/share/icons/hicolor/1024x1024/apps/$LINUX_ICON_NAME.png"
chmod 644 "$DEB/usr/share/pixmaps/$LINUX_ICON_NAME.png" \
          "$DEB/usr/share/icons/hicolor/1024x1024/apps/$LINUX_ICON_NAME.png"

# 仓库没有 LICENSE → 不写 DEBIAN/copyright（臆造授权声明比没有更糟）。
gzip -9 -c "$REPO_ROOT/README.md" > "$DEB/usr/share/doc/$LINUX_PACKAGE_NAME/README.md.gz"

# deb 版本号里 "-" 是 upstream-debian 分隔符；预发布后缀按 Debian 惯例转 "~" 前缀
# （0.0.2-alpha.1 → 0.0.2~alpha.1，排序上正确地小于 0.0.2）。
DEB_VERSION="$(printf '%s' "$VERSION" | sed -E 's/^([0-9]+\.[0-9]+\.[0-9]+)-/\1~/')"
Installed_Size="$(du -sk "$DEB" | cut -f1)"
cat > "$DEB/DEBIAN/control" <<EOF
Package: $LINUX_PACKAGE_NAME
Version: $DEB_VERSION
Section: office
Priority: optional
Architecture: $DEB_ARCH
Installed-Size: $Installed_Size
Depends: libc6, libgcc-s1, libstdc++6, zlib1g, libx11-6, libice6, libsm6, libfontconfig1, libfreetype6, ca-certificates, tzdata, libicu74 | libicu72 | libicu70, libssl3 | libssl1.1
Maintainer: $MAINTAINER_NAME <$MAINTAINER_EMAIL>
Homepage: $COMPANY_URL
Description: $APP_DISPLAY_NAME - $APP_DESCRIPTION
 与 TUI（mythology dotnet tool）共用 ~/.mythology 数据目录，卸载不删除用户数据。
EOF
chmod 644 "$DEB/DEBIAN/control"

for hook in postinst postrm; do
  cat > "$DEB/DEBIAN/$hook" <<'EOF'
#!/bin/sh
if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database -q /usr/share/applications || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -q -t -f /usr/share/icons/hicolor || true
fi
exit 0
EOF
  chmod 755 "$DEB/DEBIAN/$hook"
done

( cd "$DEB" && find . -type f ! -path './DEBIAN/*' -exec md5sum {} + | sed 's|  \./|  |' ) \
  > "$DEB/DEBIAN/md5sums"
chmod 644 "$DEB/DEBIAN/md5sums"

DEB_FILE="$DIST_DIR/Mythology-Desktop-$VERSION-$RID.deb"
mkdir -p "$DIST_DIR"
log "dpkg-deb --build → $DEB_FILE"
dpkg-deb --root-owner-group --build "$DEB" "$DEB_FILE"

log "包元数据："
dpkg-deb --info "$DEB_FILE"
CONTENTS="$(dpkg-deb --contents "$DEB_FILE")"
for want in "./usr/bin/$LINUX_BIN_NAME" ".$LINUX_INSTALL_DIR/$APP_NAME" \
            "./usr/share/applications/$LINUX_PACKAGE_NAME.desktop" \
            "./usr/share/pixmaps/$LINUX_ICON_NAME.png"; do
  printf '%s\n' "$CONTENTS" | grep -Fq -- "$want" || die "包内缺少 ${want}：$DEB_FILE"
done
# 权限位必须在 tar 层面复核：dpkg-deb 尊重 staging 的 mode，在 Windows 文件系统上跑过就会露馅。
TARLIST="$(dpkg-deb --fsys-tarfile "$DEB_FILE" | tar -tv)"
printf '%s\n' "$TARLIST" | awk -v e=".$LINUX_INSTALL_DIR/$APP_NAME" '$NF == e { print $1 }' \
  | grep -qx -- '-rwxr-xr-x' || die "包内主可执行文件权限不是 755：$DEB_FILE"
# mode 字符串第 9 位是 other-write（-rwxr-xr-x 形式）
printf '%s\n' "$TARLIST" | awk '$1 ~ /^.{8}w/ { print; bad=1 } END { exit bad }' \
  || die "包内存在 world-writable 文件：$DEB_FILE"

write_sha256 "$DEB_FILE"
log "完成。安装：sudo apt install ./$(basename "$DEB_FILE")"
