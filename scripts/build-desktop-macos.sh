#!/bin/bash
# 在 macOS 下把 Aether 桌面端（Qifu / Stockapp）编译成可直接运行的 .app。
# self-contained：不需要安装 .NET 运行时；非单文件：Avalonia 原生库 + Lean 数据直接随 .app 分发，最稳。
#
# 用法:
#   ./scripts/build-desktop-macos.sh              # Qifu + Stockapp 都打
#   ./scripts/build-desktop-macos.sh stockapp     # 仅股票版（stockapp 租户）
#   ./scripts/build-desktop-macos.sh qifu         # 仅 360 版（qifu 租户）
#
# 产物: dist/Aether-Qifu.app、dist/Aether-Stockapp.app
# 运行: 双击 .app，或在 Finder 里打开；也可命令行: open dist/Aether-Qifu.app
#
# 注:
# - Stockapp 版引 externals/Lean（回测引擎），脚本会确保子模块已 init。
# - Qifu 版用 Playwright（云文档登录），首次使用云文档工具前需在产出目录执行:
#     <bundle>/Contents/MacOS/.playwright/node install chromium
#   （或本地 dotnet tool 与项目 Playwright 版本一致后 install）
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME="osx-x64"
CONFIG="Release"
DIST="${ROOT}/dist"
VERSION="${APP_VERSION_NAME:-1.0.0}"
TARGET="${1:-all}"

# 默认产出 osx-x64（Intel 原生 / Apple Silicon 走 Rosetta）。
# Apple Silicon 上如需 arm64 原生，改 RUNTIME=osx-arm64 后重跑。
ARCH="$(uname -m)"
if [ "${ARCH}" = "arm64" ]; then
  echo "检测到 Apple Silicon，产出 osx-x64（可经 Rosetta 运行）。如需 arm64 原生，改 RUNTIME=osx-arm64 后重跑。"
fi

ensure_lean() {
  # Stockapp 引 externals/Lean（回测引擎源码），缺失则 init 子模块。
  if [ ! -f "${ROOT}/externals/Lean/Engine/QuantConnect.Lean.Engine.csproj" ]; then
    echo "==> externals/Lean 缺失，初始化子模块（shallow）..."
    git -C "${ROOT}" submodule update --init externals/Lean
  fi
}

build_one() {
  local variant="$1"          # Qifu / Stockapp
  local lower                 # qifu / stockapp
  lower="$(echo "${variant}" | tr '[:upper:]' '[:lower:]')"
  local proj="apps/${variant}/Acme.${variant}.Desktop/Acme.${variant}.Desktop.csproj"
  local exe="Acme.${variant}.Desktop"   # dotnet 默认 AssemblyName = 项目名
  local pubdir="${DIST}/publish-${lower}"
  local appname="Aether-${variant}"
  local bundle="${DIST}/${appname}.app"
  local display_name
  case "${variant}" in
    Stockapp) display_name="Aether - 股票版" ;;
    Qifu)     display_name="Aether - 360版" ;;
  esac

  [ "${variant}" = "Stockapp" ] && ensure_lean

  echo "==> [${variant}] dotnet publish (${RUNTIME}, self-contained)"
  dotnet publish "${ROOT}/${proj}" \
    -c "${CONFIG}" \
    -r "${RUNTIME}" \
    --self-contained true \
    -o "${pubdir}"

  echo "==> [${variant}] 打包 .app bundle"
  rm -rf "${bundle}"
  mkdir -p "${bundle}/Contents/MacOS" "${bundle}/Contents/Resources"

  # 复制全部 publish 产物到 Contents/MacOS（可执行文件 + dll + 原生库 + 点开头目录如 .playwright）
  # 用 /. 而非 /*：/* 不匹配点开头文件，会漏掉 .playwright 驱动目录（含 node 二进制），导致云文档登录失败
  cp -R "${pubdir}/." "${bundle}/Contents/MacOS/"
  # 兜底确保 Playwright node 驱动可执行（.app 经 xattr/分发后可能丢权限）
  find "${bundle}/Contents/MacOS/.playwright" -type f -name "node" -exec chmod +x {} \; 2>/dev/null || true

  # 图标（可选）：项目 Assets/AppIcon.icns
  local icon="${ROOT}/apps/${variant}/Acme.${variant}.Desktop/Assets/AppIcon.icns"
  [ -f "${icon}" ] && cp "${icon}" "${bundle}/Contents/Resources/AppIcon.icns" && echo "    图标已复制"

  printf 'APPL????' > "${bundle}/Contents/PkgInfo"

  cat > "${bundle}/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>
    <string>${display_name}</string>
    <key>CFBundleDisplayName</key>
    <string>${display_name}</string>
    <key>CFBundleIdentifier</key>
    <string>com.aether.desktop.${lower}</string>
    <key>CFBundleVersion</key>
    <string>${VERSION}</string>
    <key>CFBundleShortVersionString</key>
    <string>${VERSION}</string>
    <key>CFBundleExecutable</key>
    <string>${exe}</string>
    <key>CFBundlePackageType</key>
    <string>APPL</string>
    <key>CFBundleSignature</key>
    <string>????</string>
    <key>LSMinimumSystemVersion</key>
    <string>12.0</string>
    <key>NSHighResolutionCapable</key>
    <true/>
    <key>CFBundleInfoDictionaryVersion</key>
    <string>6.0</string>
    <key>NSHumanReadableCopyright</key>
    <string>Copyright 2026 Aether Team. All rights reserved.</string>
</dict>
</plist>
PLIST

  # 去除 macOS 隔离属性（避免双击时 Gatekeeper 拦"来自身份不明开发者"的可执行权限丢失）
  xattr -cr "${bundle}" 2>/dev/null || true
  chmod +x "${bundle}/Contents/MacOS/${exe}"

  echo "==> [${variant}] 完成: ${bundle}"
  echo "    双击运行，或: open \"${bundle}\""
}

case "${TARGET}" in
  stockapp|Stockapp) build_one Stockapp ;;
  qifu|Qifu)         build_one Qifu ;;
  all|All)           build_one Qifu; build_one Stockapp ;;
  *) echo "用法: $0 [stockapp|qifu|all]"; exit 1 ;;
esac

echo ""
echo "全部完成。产物目录: ${DIST}"
