#!/bin/bash
# 在 macOS 下把 Aether 桌面端（Stockapp 股票版）编译成可直接运行的 .app。
# 与 build-stockapp-windows.ps1 / build-stockapp-linux.sh 对齐的 macOS 版打包脚本。
# 本仓库提交到 GitHub 的只有 Stockapp 租户，故脚本固定打包 Stockapp，无需参数。
# self-contained：不需要安装 .NET 运行时；非单文件：Avalonia 原生库 + Lean 数据直接随 .app 分发，最稳。
#
# 用法:
#   ./scripts/build-stockapp-macos.sh
#
# 产物: dist/Aether-Stockapp.app
# 运行: 双击 .app，或在 Finder 里打开；也可命令行: open dist/Aether-Stockapp.app
#
# 注:
# - Stockapp 引 externals/Lean（回测引擎），脚本会确保子模块已 init。
# - CTP wrapper（libctp_native.dylib）现场编译，需 cmake + clang（Xcode Command Line Tools）。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME="osx-x64"
CONFIG="Release"
DIST="${ROOT}/dist"
VERSION="${APP_VERSION_NAME:-1.0.0}"

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

build_ctp_native() {
  # 现场编译 macOS 版 libctp_native.dylib（C++ wrapper），输出到 build/native。
  # 与 build-stockapp-windows.ps1 的 Build-CtpNative 对齐：现场编，不依赖入库的预编译产物。
  # 依赖：cmake + clang（Xcode Command Line Tools）。
  local native="${ROOT}/externals/Ctp.Net.Core/Ctp.Native"
  local sdk="${ROOT}/externals/Ctp.Net.Core/ctp-sdk/macos-universal"
  local builddir="${native}/build-mac"

  [ -f "${native}/CMakeLists.txt" ] || { echo "  ! Ctp.Native 源码缺失，跳过 wrapper 编译"; return 1; }
  [ -d "${sdk}" ] || { echo "  ! CTP SDK 缺失: ${sdk}，跳过 wrapper 编译"; return 1; }
  command -v cmake >/dev/null 2>&1 || { echo "  ! 未找到 cmake，跳过 wrapper 编译（装 Xcode Command Line Tools: xcode-select --install）"; return 1; }

  echo "==> 编译 libctp_native.dylib (cmake + clang, APPLE)"
  rm -rf "${builddir}"
  cmake -S "${native}" -B "${builddir}" -DCMAKE_BUILD_TYPE=Release
  cmake --build "${builddir}" --config Release

  local dylib="${ROOT}/externals/Ctp.Net.Core/build/native/libctp_native.dylib"
  [ -f "${dylib}" ] && { echo "    libctp_native.dylib 已生成"; return 0; }
  echo "  ! 编译未产出 libctp_native.dylib"; return 1
}

build_one() {
  local variant="Stockapp"
  local lower="stockapp"
  local proj="apps/${variant}/Acme.${variant}.Desktop/Acme.${variant}.Desktop.csproj"
  local exe="Acme.${variant}.Desktop"   # dotnet 默认 AssemblyName = 项目名
  local pubdir="${DIST}/publish-${lower}"
  local appname="Aether-${variant}"
  local bundle="${DIST}/${appname}.app"
  local display_name="Aether - 股票版"

  ensure_lean
  # 混合模式：有 cmake+clang 就现场编 libctp_native.dylib（覆盖入库旧产物，与源码一致）；
  # 工具链不全则回退用 build/native 里入库的预编译产物兜底（clone 即用，无需编译器）。
  local dylib="${ROOT}/externals/Ctp.Net.Core/build/native/libctp_native.dylib"
  if build_ctp_native; then
    : # 现场编译成功
  elif [ -f "${dylib}" ]; then
    echo "  回退使用入库的预编译 libctp_native.dylib（未现场编译）"
  else
    echo "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
    echo "  !! libctp_native.dylib 既无现场编译也无入库兜底             !!"
    echo "  !! 产物可启动，但 CTP 期货行情/交易调用时会报错             !!"
    echo "  !! 其它功能（聊天/专家/回测/Lean/笔记等）不受影响           !!"
    echo "  !! 如需 CTP：装 Xcode Command Line Tools (xcode-select --install) + cmake !!"
    echo "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
  fi

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

build_one

echo ""
echo "全部完成。产物目录: ${DIST}"
