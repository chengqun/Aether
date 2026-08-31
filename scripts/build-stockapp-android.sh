#!/bin/bash
# 在任意平台（macOS/Linux/Windows-WSL）下把 Aether 手机端（Stockapp 行情版）编译成可直接 adb install 的自包含 Android APK。
# 对齐 scripts/build-stockapp-*.sh 的 Android 版打包脚本。本仓库 GitHub 只同步 Stockapp 租户，故脚本固定打包 Stockapp Mobile。
#
# 自包含 Debug APK：程序集 + .NET 运行时打入 APK（MAUI Debug 默认 Fast Deployment 只装壳、
# 程序集靠 -t:Install 单独推到 .__override__，已在 csproj 用 AndroidUseSharedRuntime=false +
# EmbedAssembliesIntoApk=true 关闭，故 Debug build 也产自包含 APK）。
# 用 MAUI 自带 debug.keystore 自动签名（Debug build 产 com.aether.stockapp-Signed.apk），
# 任何人 clone 即可 build+install，无需 keystore secret——与桌面端 workflow「无 baked-in 密钥」理念一致。
#
# 用法:
#   ./scripts/build-stockapp-android.sh
#   CONFIG=Release ./scripts/build-stockapp-android.sh   # Release（开链接器，产 unsigned，需自行 apksigner 签）
#   APP_VERSION_NAME=1.0.0 ./scripts/build-stockapp-android.sh
#
# 产物: dist/android-apk/Aether-Stockapp-android-{version}.apk
# 安装: adb install dist/android-apk/Aether-Stockapp-android-*.apk
#
# 前置依赖:
# - .NET 10 SDK + maui-android workload（maui workload 包：dotnet workload install maui-android）
# - Android SDK（含 platform-tools、build-tools）。macOS 默认在 ~/Library/Android/sdk；
#   CI 由 setup-android action 装。ANDROID_SDK_ROOT / ANDROID_HOME 任一环境变量可覆盖。
# - JDK 17（MAUI Android 构建 JDK 要求）。
#
# 注:
# - Stockapp.Mobile 不引 externals/Lean（回测引擎）也不引 CTP wrapper（手机端期货走桩页禁用），
#   故本脚本无 submodule init、无 C++ 原生编译，比桌面端脚本轻。
# - Release 正式分发（更小 APK + 正式签名）后续再开：Release 开链接器可能裁反射依赖需逐项调，
#   且需 release keystore（建议经 GitHub Secret 注入，勿入库）。脚本已预留 unsigned 回退 + 注释。
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
CONFIG="${CONFIG:-Debug}"
DIST="${ROOT}/dist/android-apk"
VERSION="${APP_VERSION_NAME:-1.0.0}"
PROJ="apps/Stockapp/Acme.Stockapp.Mobile/Acme.Stockapp.Mobile.csproj"
APPID="com.aether.stockapp"

# Android SDK 根：环境变量优先，否则回退 macOS 默认路径。仅用于提示缺失，不替用户装。
ANDROID_SDK="${ANDROID_SDK_ROOT:-${ANDROID_HOME:-$HOME/Library/Android/sdk}}"
if [ ! -d "${ANDROID_SDK}" ]; then
  echo "  ! 未找到 Android SDK（试过 ANDROID_SDK_ROOT/ANDROID_HOME/~/Library/Android/sdk）"
  echo "  ! 装 Android Studio 或 sdkmanager；CI 由 setup-android action 装。"
  exit 1
fi
command -v java >/dev/null 2>&1 || { echo "  ! 未找到 java（MAUI Android 需 JDK 17）"; exit 1; }

echo "==> [Stockapp Android] dotnet build -f net10.0-android (${CONFIG}, self-contained)"
# Stockapp.Mobile 多目标 net10.0-ios;net10.0-android，须 -f 指定 android TFM（NETSDK1129）。
# 自包含配置在 csproj 的 android 条件 PropertyGroup（对 Debug/Release 都生效，iOS 不受影响），
# 故这里只调 build，不传 self-contained 标志（Android APK 无此概念，运行时随包）。
dotnet build "${ROOT}/${PROJ}" \
  -f net10.0-android \
  -c "${CONFIG}"

# MAUI Debug build 自动用本机 debug.keystore（~/.local/share/Xamarin/Mono for Android/debug.keystore）
# 签名，产出 {appid}-Signed.apk。Release build 默认产 unsigned {appid}.apk（未配 AndroidKeyStore）。
BIN_DIR="${ROOT}/apps/Stockapp/Acme.Stockapp.Mobile/bin/${CONFIG}/net10.0-android"
APK_SRC="${BIN_DIR}/${APPID}-Signed.apk"
SIGNED=1
if [ ! -f "${APK_SRC}" ]; then
  APK_SRC="${BIN_DIR}/${APPID}.apk"
  SIGNED=0
  [ -f "${APK_SRC}" ] || { echo "  ! 未找到 APK 产物: ${APK_SRC}"; exit 1; }
  echo "  ! Release unsigned APK（未签名）: ${APK_SRC}"
  echo "  ! 正式分发需签名: apksigner sign --ks <release.keystore> --ks-pass pass:<pwd> \"${APK_SRC}\""
  echo "  ! （release keystore 建议经 GitHub Secret 注入，勿入库）"
fi

mkdir -p "${DIST}"
APK_OUT="${DIST}/Aether-Stockapp-android-${VERSION}.apk"
cp "${APK_SRC}" "${APK_OUT}"

echo "==> 完成: ${APK_OUT}"
if [ "${SIGNED}" -eq 1 ]; then
  echo "    已用 debug.keystore 签名，可直接安装: adb install \"${APK_OUT}\""
else
  echo "    未签名（Release），安装前需 apksigner 签名"
fi
