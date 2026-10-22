#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD_CONFIG="${1:-Release}"
OUTPUT_DIR="$SCRIPT_DIR/dist"

echo "=== NovelAgentCLI Build Script ==="
echo "Configuration: $BUILD_CONFIG"

# 先构建插件（Host 不再直接引用插件项目）
echo "Building plugins..."
dotnet build "$SCRIPT_DIR/src/Plugins/NovelTools/Plugins.NovelTools.csproj" -c "$BUILD_CONFIG"
dotnet build "$SCRIPT_DIR/src/Plugins/SemanticRetrievalTool/Plugins.SemanticRetrievalTool.csproj" -c "$BUILD_CONFIG"
dotnet build "$SCRIPT_DIR/src/Plugins/WebSearchTools/Plugins.WebSearchTools.csproj" -c "$BUILD_CONFIG"
echo "✅ Plugins built"

build_target() {
	local rid="$1"
	local name="$2"
	echo ""
	echo "Building for $name ($rid)..."
	dotnet publish "$SCRIPT_DIR/src/Host/Host.csproj" \
		-c "$BUILD_CONFIG" \
		-r "$rid" \
		--self-contained true \
		-p:PublishSingleFile=true \
		-p:PublishTrimmed=true \
		-p:TrimMode=partial \
		-o "$OUTPUT_DIR/$name"

	# Copy plugins
	echo "Copying plugins..."
	mkdir -p "$OUTPUT_DIR/$name/plugins"
	cp "$SCRIPT_DIR/src/Host/bin/$BUILD_CONFIG/net10.0/plugins/"*.dll "$OUTPUT_DIR/$name/plugins/" 2>/dev/null || true

	# Copy config template
	cp "$SCRIPT_DIR/src/Host/appsettings.template.json" "$OUTPUT_DIR/$name/appsettings.template.json" 2>/dev/null || true

	local size
	size=$(du -sh "$OUTPUT_DIR/$name/Host" | cut -f1)
	echo "✅ $name built successfully (size: $size)"
}

case "${1:-all}" in
macos-arm64)
	build_target osx-arm64 macos-arm64
	;;
macos-x64)
	build_target osx-x64 macos-x64
	;;
linux-arm64)
	build_target linux-arm64 linux-arm64
	;;
linux-x64)
	build_target linux-x64 linux-x64
	;;
win-x64)
	build_target win-x64 win-x64
	;;
win-arm64)
	build_target win-arm64 win-arm64
	;;
all)
	build_target osx-arm64 macos-arm64
	build_target linux-x64 linux-x64
	build_target win-x64 win-x64
	;;
*)
	echo "Usage: $0 {macos-arm64|macos-x64|linux-arm64|linux-x64|win-x64|win-arm64|all}"
	echo "  or:  $0 (defaults to all)"
	exit 1
	;;
esac

echo ""
echo "=== Build Complete ==="
echo "Output directory: $OUTPUT_DIR"
