#!/bin/bash
# 在 Linux 下把 Aether 桌面端（Qifu 360 版）编译成可直接运行的可执行目录。
# 与 build-stockapp-linux.sh 对齐的 Qifu 版打包脚本（2026-09-03 Qifu 解除隐藏、随 CI 发布）。
# - self-contained：不需要目标机器安装 .NET 运行时。
# - 非单文件：Avalonia 原生库 + Playwright 驱动直接随目录分发，最稳。
# - 产物是一个文件夹，里面是可执行文件，命令行直接运行；额外打 tar.gz 方便分发。
#
# Qifu 与 Stockapp 的差异（本脚本因此大幅简化）：
# - 不引 externals/Lean（无回测引擎）→ 无 submodule init。
# - 不引 CTP wrapper（无期货）→ 无 cmake/gcc 现场编译、无 rpath 归一化。
# - 引 Playwright（云文档登录）→ .playwright node 驱动随包。
#
# 用法:
#   ./scripts/build-qifu-linux.sh
#
# 产物: dist/Aether-Qifu/  +  dist/Aether-Qifu-<ver>-linux-x64.tar.gz
# 运行: ./dist/Aether-Qifu/Acme.Qifu.Desktop
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RUNTIME="linux-x64"
CONFIG="Release"
DIST="${ROOT}/dist"
VERSION="${APP_VERSION_NAME:-1.0.0}"

build_one() {
  local variant="Qifu"
  local lower="qifu"
  local proj="apps/${variant}/Acme.${variant}.Desktop/Acme.${variant}.Desktop.csproj"
  local exe="Acme.${variant}.Desktop"   # dotnet 默认 AssemblyName = 项目名
  local pubdir="${DIST}/publish-${lower}"
  local appname="Aether-${variant}"
  local outdir="${DIST}/${appname}"
  local display_name="Aether - 360版"

  echo "==> [${variant}] dotnet publish (${RUNTIME}, self-contained)"
  # Qifu 是单数 TargetFramework=net10.0，-f 兼容（与 stockapp 脚本参数形态对齐）。
  dotnet publish "${ROOT}/${proj}" \
    -f net10.0 \
    -c "${CONFIG}" \
    -r "${RUNTIME}" \
    --self-contained true \
    -o "${pubdir}"

  echo "==> [${variant}] 组装运行目录 ${outdir}"
  rm -rf "${outdir}"
  mkdir -p "${outdir}"
  # 用 /. 而非 /*：/* 不匹配点开头文件，会漏掉 .playwright 驱动目录（含 node 二进制），导致云文档登录失败
  cp -R "${pubdir}/." "${outdir}/"

  # 清掉对 Linux 无用的 Windows/macOS 原生库（.dll/.dylib），减小体积
  find "${outdir}" -type f \( -name '*.dll' -o -name '*.dylib' \) -delete 2>/dev/null || true

  # 兜底确保 Playwright node 驱动可执行（tar 分发后可能丢权限）
  find "${outdir}/.playwright" -type f -name "node" -exec chmod +x {} \; 2>/dev/null || true

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
