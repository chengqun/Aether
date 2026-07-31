#!/bin/bash
# 在 Linux 下把 Aether 桌面端（Stockapp 股票版）编译成可直接运行的可执行目录。
# 与 build-stockapp-macos.sh / build-stockapp-windows.ps1 对齐的 Linux 版打包脚本。
# 本仓库提交到 GitHub 的只有 Stockapp 租户，故脚本固定打包 Stockapp，无需参数。
# - self-contained：不需要目标机器安装 .NET 运行时。
# - 非单文件：Avalonia 原生库 + CTP 的 .so + Lean 数据直接随目录分发，最稳。
# - 产物是一个文件夹，里面是可执行文件，命令行直接运行；额外打 tar.gz 方便分发。
# - 现场编 CTP wrapper（libctp_native.so），编出后随 publish 流出。
#
# 用法:
#   ./scripts/build-stockapp-linux.sh
#
# 产物: dist/Aether-Stockapp/  +  dist/Aether-Stockapp-<ver>-linux-x64.tar.gz
# 运行: ./dist/Aether-Stockapp/Acme.Stockapp.Desktop
#
# 依赖（本机）: dotnet SDK 10 + cmake + gcc/g++（CTP wrapper 需要）。
#   - Debian/Ubuntu: sudo apt install -y cmake build-essential
#   - Fedora/RHEL:   sudo dnf install -y cmake gcc-c++
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME="linux-x64"
CONFIG="Release"
DIST="${ROOT}/dist"
VERSION="${APP_VERSION_NAME:-1.0.0}"

ensure_lean() {
  # Stockapp 引 externals/Lean（回测引擎源码），缺失则 init 子模块。
  if [ ! -f "${ROOT}/externals/Lean/Engine/QuantConnect.Lean.Engine.csproj" ]; then
    echo "==> externals/Lean 缺失，初始化子模块（shallow）..."
    git -C "${ROOT}" submodule update --init externals/Lean
  fi
}

build_ctp_native() {
  # 现场编译 Linux 版 libctp_native.so（C++ wrapper），输出到 build/native。
  # 与 mac/windows 脚本对齐：现场编，不依赖入库的预编译产物。
  # 依赖：cmake + gcc/g++。
  local native="${ROOT}/externals/Ctp.Net.Core/Ctp.Native"
  local sdk="${ROOT}/externals/Ctp.Net.Core/ctp-sdk/linux-x64"
  local builddir="${native}/build-linux"

  [ -f "${native}/CMakeLists.txt" ] || { echo "  ! Ctp.Native 源码缺失，跳过 wrapper 编译"; return 1; }
  [ -d "${sdk}" ] || { echo "  ! CTP SDK 缺失: ${sdk}，跳过 wrapper 编译"; return 1; }
  command -v cmake >/dev/null 2>&1 || { echo "  ! 未找到 cmake，跳过 wrapper 编译（Debian/Ubuntu: sudo apt install cmake）"; return 1; }

  echo "==> 编译 libctp_native.so (cmake + gcc, UNIX)"
  rm -rf "${builddir}"
  cmake -S "${native}" -B "${builddir}" -DCMAKE_BUILD_TYPE=Release
  cmake --build "${builddir}" --config Release

  local so="${ROOT}/externals/Ctp.Net.Core/build/native/libctp_native.so"
  [ -f "${so}" ] || { echo "  ! 编译未产出 libctp_native.so"; return 1; }
  echo "    libctp_native.so 已生成"

  # rpath 归一化：cmake --build（非 --install）只写入绝对 BUILD_RPATH（指向源码树 ctp-sdk/linux-x64，
  # 不可移植）。发布包需要相对 $ORIGIN，由随包的 thost*.so 解析（CMakeLists POST_BUILD 已把 thost*.so
  # 拷到 build/native，csproj glob 再随包）。dev 模式 `dotnet run` 仍用默认绝对 rpath，不受影响。
  # 用 patchelf（mac 用 install_name_tool）；未装 patchelf 则仅警告——.so 在编译机上仍可跑，只是不可移植。
  if command -v patchelf >/dev/null 2>&1; then
    patchelf --set-rpath '$ORIGIN' "${so}"
    echo "    rpath 已归一化为 \$ORIGIN（发布包可移植）"
  else
    echo "    ! 未找到 patchelf，rpath 仍是绝对路径（发布包不可移植）"
    echo "      装 patchelf 后重跑: Debian/Ubuntu: sudo apt install patchelf | Fedora: sudo dnf install patchelf"
  fi
  return 0
}

build_one() {
  local variant="Stockapp"
  local lower="stockapp"
  local proj="apps/${variant}/Acme.${variant}.Desktop/Acme.${variant}.Desktop.csproj"
  local exe="Acme.${variant}.Desktop"   # dotnet 默认 AssemblyName = 项目名
  local pubdir="${DIST}/publish-${lower}"
  local appname="Aether-${variant}"
  local outdir="${DIST}/${appname}"
  local display_name="Aether - 股票版"

  ensure_lean
  # 混合模式：有 cmake+gcc 就现场编 libctp_native.so（覆盖入库旧产物，与源码一致）；
  # 工具链不全则回退用 build/native 里入库的预编译产物兜底（clone 即用，无需编译器）。
  local so="${ROOT}/externals/Ctp.Net.Core/build/native/libctp_native.so"
  if build_ctp_native; then
    : # 现场编译成功
  elif [ -f "${so}" ]; then
    echo "  回退使用入库的预编译 libctp_native.so（未现场编译）"
  else
    echo "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
    echo "  !! libctp_native.so 既无现场编译也无入库兜底                !!"
    echo "  !! 产物可启动，但 CTP 期货行情/交易调用时会报错             !!"
    echo "  !! 其它功能（聊天/专家/回测/Lean/笔记等）不受影响           !!"
    echo "  !! 如需 CTP：装 cmake + gcc/g++                              !!"
    echo "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!"
  fi

  echo "==> [${variant}] dotnet publish (${RUNTIME}, self-contained)"
  dotnet publish "${ROOT}/${proj}" \
    -c "${CONFIG}" \
    -r "${RUNTIME}" \
    --self-contained true \
    -o "${pubdir}"

  echo "==> [${variant}] 组装运行目录 ${outdir}"
  rm -rf "${outdir}"
  mkdir -p "${outdir}"
  cp -R "${pubdir}/." "${outdir}/"

  # 清掉对 Linux 无用的 Windows/macOS 原生库（.dll/.dylib），减小体积
  find "${outdir}" -type f \( -name '*.dll' -o -name '*.dylib' \) -delete 2>/dev/null || true

  # 写版本信息
  cat > "${outdir}/VERSION.txt" <<EOF
${display_name}
Version: ${VERSION}
Runtime: ${RUNTIME} (.NET self-contained)
Entry: ${exe}
EOF

  # 可执行权限
  chmod +x "${outdir}/${exe}" 2>/dev/null || true

  # 打 tar.gz
  local tarball="${DIST}/${appname}-${VERSION}-${RUNTIME}.tar.gz"
  tar -czf "${tarball}" -C "${DIST}" "${appname}"
  echo "    分发包: ${tarball}"

  echo "==> [${variant}] 完成: ${outdir}"
  echo "    运行: ${outdir}/${exe}"
}

build_one

echo ""
echo "全部完成。产物目录: ${DIST}"
