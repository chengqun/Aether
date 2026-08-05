<#
.SYNOPSIS
  在 Windows 下把 Aether 桌面端（Stockapp 股票版）编译成可直接双击运行的程序目录。

.DESCRIPTION
  与 build-stockapp-macos.sh / build-stockapp-linux.sh 对齐的 Windows 版打包脚本。
  本仓库提交到 GitHub 的只有 Stockapp 租户，故脚本固定打包 Stockapp，无需参数。
  - self-contained：不需要目标机器安装 .NET 运行时。
  - 非单文件：Avalonia 原生库 + CTP 的 .dll + Lean 数据直接随目录分发，最稳
    （单文件发布对原生库的运行时提取在某些环境下不稳，故沿用 mac 脚本的思路）。
  - 产物是一个文件夹，里面是 Acme.Stockapp.Desktop.exe，双击即可运行。
  - 额外打成一个 zip 方便分发（Windows 自带 Compress-Archive）。

  CTP 原生库（ctp_native.dll，C++ wrapper）说明：
  - Windows 版的 wrapper 不预编译入库，clone 后用 CMake 从 externals/Ctp.Net.Core/Ctp.Native
    源码 + CTP 官方 6.7.2 Windows SDK 现场编译。
  - Windows SDK 已随仓库分发于 externals/Ctp.Net.Core/ctp-sdk/win-x64（入库），
    无需额外放置。当且仅当本机同时具备 [CMake] + [MSVC] 时，脚本会自动编译
    ctp_native.dll 并随包分发（Stockapp 的 CTP 期货功能可用）。
  - 两者缺任一，脚本跳过 wrapper 编译并打印醒目警告：此时产物可双击启动，
    聊天/专家/回测/Lean 等均可用，唯独 CTP 期货行情/交易调用时会因缺 ctp_native.dll 报错。
    （启动本身不会崩溃，只有点 CTP 相关功能时才失败。）

  Windows 没有 .app bundle 的概念，"双击运行"最自然的形态就是「一个含 .exe 的文件夹」。

.PARAMETER Version
  版本号，仅用于命名 zip。默认 1.0.0。环境变量 APP_VERSION_NAME 也可覆盖。

.PARAMETER SkipNativeBuild
  跳过 ctp_native.dll 编译（即使工具链就绪）。用于只想快速出包、暂不碰 CTP 的场景。

.EXAMPLE
  .\scripts\build-stockapp-windows.ps1
  .\scripts\build-stockapp-windows.ps1 -Version 1.2.0
  .\scripts\build-stockapp-windows.ps1 -SkipNativeBuild
#>
[CmdletBinding()]
param(
  [string]$Version = '',
  [switch]$SkipNativeBuild
)

$ErrorActionPreference = 'Stop'

# PowerShell 5.1 不支持参数默认值里的内联 if 表达式，故在此处理
if ([string]::IsNullOrEmpty($Version)) {
  $Version = if ($env:APP_VERSION_NAME) { $env:APP_VERSION_NAME } else { '1.0.0' }
}

$Root    = Resolve-Path (Join-Path $PSScriptRoot '..')
$Runtime = 'win-x64'
$Config  = 'Release'
$Dist    = Join-Path $Root 'dist'

if (-not (Test-Path $Dist)) { New-Item -ItemType Directory -Path $Dist | Out-Null }

# 用本地 NuGet 全局包目录：默认 %USERPROFILE%\.nuget\packages 在某些环境下有
# 杀软/权限问题导致 restore 失败，本地化可规避。
$env:NUGET_PACKAGES = Join-Path $Root '.nuget-packages'
New-Item -ItemType Directory -Path $env:NUGET_PACKAGES -Force | Out-Null

# 关闭 Avalonia 构建遥测：构建任务尝试写 %LOCALAPPDATA%\AvaloniaUI\BuildServices\
# 时可能抛 UnauthorizedAccessException。
$env:AVALONIA_BUILD_SERVICES_TELEMETRY_ENABLED = 'false'

# ---------------------------------------------------------------------------
# 工具链探测：CMake + MSVC + CTP SDK，决定能否编译 ctp_native.dll
# ---------------------------------------------------------------------------
function Test-Command([string]$Name) {
  $c = Get-Command $Name -ErrorAction SilentlyContinue
  return $null -ne $c
}

function Find-MSVC {
  # 按优先级探测 VS 安装：vswhere > 常见目录 > PATH 上的 cl.exe
  # 验证标准：VC\Auxiliary\Build\vcvars64.bat（或 vcvarsall.bat）存在
  $testVcvars = {
    param($p)
    (Test-Path (Join-Path $p 'VC\Auxiliary\Build\vcvars64.bat')) -or
    (Test-Path (Join-Path $p 'VC\Auxiliary\Build\vcvarsall.bat'))
  }
  $vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
  if (Test-Path $vswhere) {
    $vsPath = & $vswhere -latest -products * -property installationPath 2>$null
    if ($vsPath -and (Test-Path $vsPath) -and (& $testVcvars $vsPath)) {
      return @{ Found = $true; VsPath = $vsPath; Source = 'vswhere' }
    }
  }
  foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
    foreach ($ver in @('2022', '2019')) {
      $candidates = @(
        Join-Path $base "Microsoft Visual Studio\$ver\Community"
        Join-Path $base "Microsoft Visual Studio\$ver\Professional"
        Join-Path $base "Microsoft Visual Studio\$ver\Enterprise"
        Join-Path $base "Microsoft Visual Studio\$ver\BuildTools"
      )
      foreach ($p in $candidates) {
        if (& $testVcvars $p) { return @{ Found = $true; VsPath = $p; Source = 'dir' } }
      }
    }
  }
  if (Test-Command cl) { return @{ Found = $true; VsPath = $null; Source = 'path' } }
  return @{ Found = $false; VsPath = $null; Source = $null }
}

function Find-CMake {
  # 优先 PATH，兜底常见安装位置（winget 装的 CMake 不一定进了当前会话 PATH）
  $c = Get-Command cmake -ErrorAction SilentlyContinue
  if ($c) { return $c.Source }
  $candidates = @(
    'C:\Program Files\CMake\bin\cmake.exe',
    'C:\Program Files (x86)\CMake\bin\cmake.exe'
  )
  foreach ($p in $candidates) { if (Test-Path $p) { return $p } }
  return $null
}

function Invoke-VcVars([string]$VsPath) {
  # 用 vcvars64.bat 初始化环境后回灌到当前会话（输出 set 命令并解析）
  $vcvars = Join-Path $VsPath 'VC\Auxiliary\Build\vcvars64.bat'
  if (-not (Test-Path $vcvars)) { $vcvars = Join-Path $VsPath 'VC\Auxiliary\Build\vcvarsall.bat' }
  if (-not (Test-Path $vcvars)) { return $false }
  $bat = @"
@echo off
call "$vcvars" x64 >nul
set
"@
  $tmpBat = Join-Path $env:TEMP ("vcvars_{0}.bat" -f [Guid]::NewGuid().ToString('N'))
  Set-Content -Path $tmpBat -Value $bat -Encoding ASCII
  try {
    # cmd/vcvars 会向 stderr 写正常信息，在 $ErrorActionPreference=Stop 下会被误判为
    # 致命错误，故调用期间临时改为 Continue，用退出码判断成败。
    $prev = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $out = & cmd.exe /c $tmpBat 2>&1
    $rc = $LASTEXITCODE
    $ErrorActionPreference = $prev
    if ($rc -ne 0) { return $false }
    foreach ($line in $out) {
      if ($line -match '^([^=]+)=(.*)$') {
        [Environment]::SetEnvironmentVariable($matches[1], $matches[2], 'Process')
      }
    }
    return $true
  } finally {
    Remove-Item $tmpBat -ErrorAction SilentlyContinue
  }
}

function Build-CtpNative {
  # 编译 Windows 版 ctp_native.dll（C++ wrapper），输出到 externals/Ctp.Net.Core/build/native
  $nativeSrc = Join-Path $Root 'externals/Ctp.Net.Core/Ctp.Native'
  $sdkWin    = Join-Path $Root 'externals/Ctp.Net.Core/ctp-sdk/win-x64'  # 已入库
  $buildDir  = Join-Path $nativeSrc 'build-win'

  if (-not (Test-Path (Join-Path $nativeSrc 'CMakeLists.txt'))) {
    Write-Warning "Ctp.Native 源码缺失: $nativeSrc —— 跳过 wrapper 编译"
    return $false
  }
  if (-not (Test-Path $sdkWin)) {
    Write-Warning "CTP 官方 SDK 缺失: $sdkWin —— 跳过 wrapper 编译（win-x64 应随仓库分发: thost*.h + thost*.lib + thost*.dll）"
    return $false
  }

  $cmakeExe = Find-CMake
  $msvc = Find-MSVC
  if (-not $cmakeExe) {
    Write-Warning "未找到 CMake —— 跳过 wrapper 编译（请安装 CMake 后重试）"
    return $false
  }
  if (-not $msvc.Found) {
    Write-Warning "未找到 MSVC（Visual Studio / Build Tools）—— 跳过 wrapper 编译"
    return $false
  }

  Write-Host "==> 编译 ctp_native.dll (CMake + MSVC, WIN32)" -ForegroundColor Cyan
  Write-Host "    CMake: $cmakeExe" -ForegroundColor DarkGray
  if ($msvc.Source -eq 'path') {
    Write-Host "    MSVC: cl.exe (已在 PATH)" -ForegroundColor DarkGray
  } elseif ($msvc.VsPath) {
    Write-Host "    MSVC: $($msvc.VsPath)" -ForegroundColor DarkGray
    if (-not (Invoke-VcVars $msvc.VsPath)) {
      Write-Warning "vcvars 初始化失败 —— 跳过 wrapper 编译"
      return $false
    }
  }

  if (Test-Path $buildDir) { Remove-Item -Recurse -Force $buildDir }
  New-Item -ItemType Directory -Path $buildDir -Force | Out-Null

  & $cmakeExe -S $nativeSrc -B $buildDir -G "NMake Makefiles" -DCMAKE_BUILD_TYPE=Release
  if ($LASTEXITCODE -ne 0) { Write-Warning "cmake configure 失败 —— 跳过 wrapper 编译"; return $false }

  & $cmakeExe --build $buildDir --config Release
  if ($LASTEXITCODE -ne 0) { Write-Warning "cmake build 失败 —— 跳过 wrapper 编译"; return $false }

  # 产物 ctp_native.dll 应已输出到 build/native（CMakeLists 设定的 RUNTIME_OUTPUT_DIRECTORY）
  $dll = Join-Path $Root 'externals/Ctp.Net.Core/build/native/ctp_native.dll'
  if (Test-Path $dll) {
    Write-Host "    ctp_native.dll 已生成: $dll" -ForegroundColor DarkGray
    return $true
  } else {
    # 兜底：CMake 有时把 dll 放在 build 目录
    $alt = Get-ChildItem $buildDir -Filter 'ctp_native.dll' -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($alt) {
      Copy-Item $alt.FullName (Join-Path $Root 'externals/Ctp.Net.Core/build/native/ctp_native.dll') -Force
      Write-Host "    ctp_native.dll 已生成(从 build 目录复制)" -ForegroundColor DarkGray
      return $true
    }
    Write-Warning "编译成功但未找到 ctp_native.dll 输出"
    return $false
  }
}

function Ensure-Lean {
  # Stockapp 引 externals/Lean（回测引擎源码），缺失则 init 子模块。
  $leanProj = Join-Path $Root 'externals/Lean/Engine/QuantConnect.Lean.Engine.csproj'
  if (-not (Test-Path $leanProj)) {
    Write-Host "==> externals/Lean 缺失，初始化子模块（shallow）..." -ForegroundColor Yellow
    & git -C $Root submodule update --init externals/Lean
    if ($LASTEXITCODE -ne 0) { throw "git submodule init 失败" }
  }
}

function Build-One {
  $Variant = 'Stockapp'
  $lower   = $Variant.ToLower()
  $proj    = "apps/$Variant/Acme.$Variant.Desktop/Acme.$Variant.Desktop.csproj"
  $projFull = Join-Path $Root $proj
  $exe     = "Acme.$Variant.Desktop"   # dotnet 默认 AssemblyName = 项目名；WinExe → 产出 .exe
  $pubDir  = Join-Path $Dist "publish-$lower"
  $appName = "Aether-$Variant"
  $bundle  = Join-Path $Dist $appName  # 产物文件夹（可直接双击运行）
  $displayName = 'Aether - 股票版'

  Ensure-Lean

  # 混合模式：有 CMake+MSVC 就现场编 ctp_native.dll（覆盖入库旧产物，与源码一致）；
  # 工具链不全则回退用 build/native 里入库的预编译产物兜底（clone 即用，无需编译器）。
  $nativeDll = Join-Path $Root 'externals/Ctp.Net.Core/build/native/ctp_native.dll'
  $nativeBuilt = $false
  if (-not $SkipNativeBuild) {
    $nativeBuilt = Build-CtpNative
  }
  if (-not $nativeBuilt) {
    if (Test-Path $nativeDll) {
      Write-Host "    回退使用入库的预编译 ctp_native.dll（未现场编译）" -ForegroundColor DarkGray
    } else {
      Write-Host ""
      Write-Host "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Yellow
      Write-Host "  !! ctp_native.dll 既无现场编译也无入库兜底                 !!" -ForegroundColor Yellow
      Write-Host "  !! 产物可双击启动，但 CTP 期货行情/交易功能调用时会报错      !!" -ForegroundColor Yellow
      Write-Host "  !! 其它功能（聊天/专家/回测/Lean/笔记等）不受影响           !!" -ForegroundColor Yellow
      Write-Host "  !! 如需 CTP：补齐 CMake + MSVC（SDK 已在 ctp-sdk/win-x64 入库） !!" -ForegroundColor Yellow
      Write-Host "  !!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!!" -ForegroundColor Yellow
      Write-Host ""
    }
  }

  Write-Host "==> [$Variant] dotnet publish ($Runtime, self-contained)" -ForegroundColor Cyan
  # Stockapp 多目标 net10.0-windows;net10.0 —— publish 必须指定 -f（NETSDK1129）。
  # Windows 取 net10.0-windows：这样才引得到 StockTrading.Infrastructure（FlaUI 同花顺下单，Windows-only），
  # 股票交易功能在产物里可用。net10.0 框架只在 mac/linux 打包时用（FlaUI 不引，走桩页提示）。
  & dotnet publish $projFull -c $Config -f net10.0-windows -r $Runtime --self-contained true -o $pubDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败: $Variant" }

  # 双保险：确保 build/native 里的 ctp_native.dll 进 publish 目录（Content glob 正常会带上）
  if (Test-Path $nativeDll) { Copy-Item $nativeDll $pubDir -Force }

  Write-Host "==> [$Variant] 组装运行目录 $bundle" -ForegroundColor Cyan
  if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle }
  New-Item -ItemType Directory -Path $bundle -Force | Out-Null

  # 复制全部 publish 产物到运行目录（exe + dll + 原生库 + 点开头目录如 .playwright）
  Copy-Item -Path (Join-Path $pubDir '*') -Destination $bundle -Recurse -Force

  # 清掉对 Windows 无用的 Mac/Linux 原生库（.dylib/.so），减小体积、避免混淆
  Get-ChildItem $bundle -Filter '*.dylib' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
  Get-ChildItem $bundle -Filter '*.so' -Recurse -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue

  # 图标（可选）：项目 Assets\AppIcon.ico
  $icon = Join-Path $Root "apps/$Variant/Acme.$Variant.Desktop/Assets/AppIcon.ico"
  if (Test-Path $icon) {
    Write-Host "    图标已复制（注：.exe 图标需在 csproj <ApplicationIcon> 中设置，此处仅随包附带）"
    Copy-Item $icon (Join-Path $bundle 'AppIcon.ico') -Force
  }

  $exePath = Join-Path $bundle "$exe.exe"
  if (-not (Test-Path $exePath)) {
    $alt = Get-ChildItem $bundle -Filter '*.exe' | Where-Object { $_.Name -like "$exe*" } | Select-Object -First 1
    if ($alt) { $exePath = $alt.FullName } else { throw "未找到可执行文件: $exe.exe" }
  }

  # 解除 Zone.Identifier（从网络下载/解压后可能带 Mark of the Web）
  Get-ChildItem $bundle -Recurse | Unblock-File -ErrorAction SilentlyContinue

  # 生成双击启动的快捷方式（放 dist 根目录，方便用户）
  $lnk = Join-Path $Dist "$appName.lnk"
  try {
    $wsh = New-Object -ComObject WScript.Shell
    $sc = $wsh.CreateShortcut($lnk)
    $sc.TargetPath = $exePath
    $sc.WorkingDirectory = $bundle
    $sc.WindowStyle = 1
    $sc.Description = $displayName
    if (Test-Path $icon) { $sc.IconLocation = $icon }
    $sc.Save()
    Write-Host "    快捷方式: $lnk" -ForegroundColor DarkGray
  } catch {
    Write-Host "    （跳过快捷方式：$($_.Exception.Message)）" -ForegroundColor DarkGray
  }

  # 打 zip 方便分发
  $zip = Join-Path $Dist "$appName-$Version-$Runtime.zip"
  if (Test-Path $zip) { Remove-Item $zip -Force }
  Compress-Archive -Path (Join-Path $bundle '*') -DestinationPath $zip -CompressionLevel Optimal
  Write-Host "    分发包: $zip" -ForegroundColor DarkGray

  Write-Host "==> [$Variant] 完成: $bundle" -ForegroundColor Green
  Write-Host "    双击运行: $exePath"
}

Build-One

Write-Host ""
Write-Host "全部完成。产物目录: $Dist" -ForegroundColor Green
