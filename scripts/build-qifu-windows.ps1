<#
.SYNOPSIS
  在 Windows 下把 Aether 桌面端（Qifu 360 版）编译成可直接双击运行的程序目录。

.DESCRIPTION
  与 build-stockapp-windows.ps1 对齐的 Qifu 版打包脚本（2026-09-03 Qifu 解除隐藏、随 CI 发布）。
  - self-contained：不需要目标机器安装 .NET 运行时。
  - 非单文件：Avalonia 原生库 + Playwright 驱动直接随目录分发，最稳
    （单文件发布对原生库的运行时提取在某些环境下不稳，故沿用 mac 脚本的思路）。
  - 产物是一个文件夹，里面是 Acme.Qifu.Desktop.exe，双击即可运行。
  - 额外打成一个 zip 方便分发（Windows 自带 Compress-Archive）。

  Qifu 与 Stockapp 的差异（本脚本因此大幅简化）：
  - 不引 externals/Lean（无回测引擎）→ 无 submodule init。
  - 不引 CTP wrapper（无期货）→ 无 CMake/MSVC 工具链探测与 ctp_native.dll 现场编译。
  - 引 Playwright（云文档登录，Qifu 核心功能）→ 复制产物用 Get-ChildItem -Force
    （PowerShell 的 Copy-Item -Path '*' 不匹配点开头文件，会漏 .playwright 驱动目录——
    这正是 stockapp windows 脚本注释提到但未处理的坑，Qifu 版修正）。

  Windows 没有 .app bundle 的概念，"双击运行"最自然的形态就是「一个含 .exe 的文件夹」。

.PARAMETER Version
  版本号，仅用于命名 zip。默认 1.0.0。环境变量 APP_VERSION_NAME 也可覆盖。

.EXAMPLE
  .\scripts\build-qifu-windows.ps1
  .\scripts\build-qifu-windows.ps1 -Version 1.2.0
#>
[CmdletBinding()]
param(
  [string]$Version = ''
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

function Build-One {
  $Variant = 'Qifu'
  $lower   = $Variant.ToLower()
  $proj    = "apps/$Variant/Acme.$Variant.Desktop/Acme.$Variant.Desktop.csproj"
  $projFull = Join-Path $Root $proj
  $exe     = "Acme.$Variant.Desktop"   # dotnet 默认 AssemblyName = 项目名；WinExe → 产出 .exe
  $pubDir  = Join-Path $Dist "publish-$lower"
  $appName = "Aether-$Variant"
  $bundle  = Join-Path $Dist $appName  # 产物文件夹（可直接双击运行）
  $displayName = 'Aether - 360版'

  Write-Host "==> [$Variant] dotnet publish ($Runtime, self-contained)" -ForegroundColor Cyan
  # Qifu 是单数 TargetFramework=net10.0（无 net10.0-windows —— 不引 StockTrading.Infrastructure）。
  & dotnet publish $projFull -c $Config -f net10.0 -r $Runtime --self-contained true -o $pubDir
  if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败: $Variant" }

  Write-Host "==> [$Variant] 组装运行目录 $bundle" -ForegroundColor Cyan
  if (Test-Path $bundle) { Remove-Item -Recurse -Force $bundle }
  New-Item -ItemType Directory -Path $bundle -Force | Out-Null

  # 复制全部 publish 产物（exe + dll + 原生库 + 点开头目录如 .playwright）。
  # 关键：Get-ChildItem -Force 才枚举点开头文件；Copy-Item -Path '*' 会漏 .playwright
  # （含 node 二进制）导致云文档登录失败——与 mac/linux 脚本用 /. 而非 /* 同因。
  Get-ChildItem -Path $pubDir -Force | Copy-Item -Destination $bundle -Recurse -Force

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
  Compress-Archive -Path (Join-Path $bundle '*') -Destination $zip -CompressionLevel Optimal
  Write-Host "    分发包: $zip" -ForegroundColor DarkGray

  Write-Host "==> [$Variant] 完成: $bundle" -ForegroundColor Green
  Write-Host "    双击运行: $exePath"
}

Build-One

Write-Host ""
Write-Host "全部完成。产物目录: $Dist" -ForegroundColor Green
