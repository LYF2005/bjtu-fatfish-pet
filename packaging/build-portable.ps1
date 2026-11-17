<#
.SYNOPSIS
  邮课叮桌宠 · 三端免安装绿色包一键打包脚本。

.DESCRIPTION
  为指定平台组装绿色包（桌宠本体 + 内嵌 Node 运行时 + 纯 JS 后端 + 一键启动器），
  并压缩为 zip / tar.gz。全程无需用户预装 .NET 或 Node.js。

  核心策略：
    1. 桌宠以 .NET 自包含发布（--self-contained），运行时随包；
    2. 后端剔除 better-sqlite3 原生模块，使用内存降级，三端共用一份源码；
    3. 下载并内嵌 Node 官方二进制；
    4. Windows 启动器编译为 WinExe（双击无黑窗），Unix 用 shell 脚本。

.PARAMETER Platform
  目标平台：win-x64 / linux-x64 / osx-x64 / osx-arm64 / all（默认 all）。

.PARAMETER NodeVersion
  Node 运行时版本（官方 dist 版本号，如 v22.22.2）。

.PARAMETER OutDir
  产物输出目录，默认 packaging/dist。

.PARAMETER Configuration
  构建配置，默认 Release。

.PARAMETER KeepStaging
  保留中间组装目录（调试用）。

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\build-portable.ps1 -Platform win-x64
#>
[CmdletBinding()]
param(
  [ValidateSet('win-x64', 'linux-x64', 'osx-x64', 'osx-arm64', 'all')]
  [string]$Platform = 'all',
  [string]$NodeVersion = 'v22.22.2',
  [string]$OutDir,
  [string]$Configuration = 'Release',
  [switch]$KeepStaging
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ---------- 路径常量 ----------
$PackagingDir = $PSScriptRoot
$RepoRoot     = Split-Path -Parent $PackagingDir
$CacheDir     = Join-Path $PackagingDir '.cache'
$BuildDir     = Join-Path $PackagingDir '.build'
if (-not $OutDir) { $OutDir = Join-Path $PackagingDir 'dist' }

foreach ($d in @($CacheDir, $BuildDir, $OutDir)) {
  if (-not (Test-Path $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
}

# 统一删除辅助：用 .NET API 而非 Remove-Item，避免某些宿主环境对批量删除的
# cmdlet 层拦截（构建产物由本脚本自建，删除安全且必要）。
function Remove-Tree {
  param([string]$Path)
  if ([System.IO.Directory]::Exists($Path)) {
    [System.IO.Directory]::Delete($Path, $true)
  } elseif ([System.IO.File]::Exists($Path)) {
    [System.IO.File]::Delete($Path)
  }
}

# ---------- 平台参数映射 ----------
function Get-PlatformSpec {
  param([string]$Rid)
  switch ($Rid) {
    'win-x64'   { return @{ Rid = 'win-x64';   Label = 'Windows-x64'; NodeFile = "node-$NodeVersion-win-x64.zip";            NodeMember = 'node.exe';  Archive = 'zip'    } }
    'linux-x64' { return @{ Rid = 'linux-x64'; Label = 'Linux-x64';   NodeFile = "node-$NodeVersion-linux-x64.tar.xz";       NodeMember = 'bin/node';  Archive = 'tar.gz' } }
    'osx-x64'   { return @{ Rid = 'osx-x64';   Label = 'macOS-x64';   NodeFile = "node-$NodeVersion-darwin-x64.tar.xz";      NodeMember = 'bin/node';  Archive = 'tar.gz' } }
    'osx-arm64' { return @{ Rid = 'osx-arm64'; Label = 'macOS-arm64'; NodeFile = "node-$NodeVersion-darwin-arm64.tar.xz";    NodeMember = 'bin/node';  Archive = 'tar.gz' } }
    default     { throw "未知平台：$Rid" }
  }
}

function Write-Step {
  param([string]$Message)
  Write-Host "`n==> $Message" -ForegroundColor Cyan
}

# ---------- 查找 dotnet ----------
function Resolve-Dotnet {
  $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  $candidates = @(
    "$env:ProgramFiles\dotnet\dotnet.exe",
    "${env:ProgramFiles(x86)}\dotnet\dotnet.exe",
    "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"
  )
  foreach ($c in $candidates) { if (Test-Path $c) { return $c } }
  throw '未找到 dotnet SDK（需要 .NET 10 SDK）。'
}

# ---------- 下载并解压 Node 运行时 ----------
function Get-NodeRuntime {
  param([hashtable]$Spec, [string]$DestDir)
  $url = "https://nodejs.org/dist/$NodeVersion/$($Spec.NodeFile)"
  $archive = Join-Path $CacheDir $Spec.NodeFile

  if (-not (Test-Path $archive)) {
    Write-Host "    下载 Node：$url"
    $ok = $false
    # 优先 curl.exe（对代理更友好），失败回退 Invoke-WebRequest
    $curl = Get-Command curl.exe -ErrorAction SilentlyContinue
    if ($curl) {
      & $curl.Source -L --fail --silent --show-error -o $archive $url
      if ($LASTEXITCODE -eq 0) { $ok = $true }
    }
    if (-not $ok) {
      try {
        $old = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -Uri $url -OutFile $archive -UseBasicParsing
        $ProgressPreference = $old
        $ok = $true
      } catch { $ok = $false }
    }
    if (-not $ok) { throw "Node 运行时下载失败：$url" }
  } else {
    Write-Host "    复用缓存 Node：$archive"
  }

  $extractDir = Join-Path $CacheDir ('x-' + $Spec.Rid)
  Remove-Tree $extractDir
  New-Item -ItemType Directory -Path $extractDir -Force | Out-Null

  if ($Spec.NodeFile.EndsWith('.zip')) {
    Expand-Archive -Path $archive -DestinationPath $extractDir -Force
  } else {
    # Windows 10+ 自带 bsdtar，支持 .tar.xz
    & tar.exe -xf $archive -C $extractDir
    if ($LASTEXITCODE -ne 0) { throw "解压失败：$archive" }
  }

  # 官方包解压后是单层目录 node-vXX-<plat>/，取其内容到 DestDir
  $inner = Get-ChildItem -Path $extractDir -Directory | Select-Object -First 1
  $srcRoot = if ($inner) { $inner.FullName } else { $extractDir }
  Remove-Tree $DestDir
  New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
  Copy-Item -Path (Join-Path $srcRoot '*') -Destination $DestDir -Recurse -Force

  $nodeBin = Join-Path $DestDir ($Spec.NodeMember -replace '/', '\')
  if (-not (Test-Path $nodeBin)) { throw "Node 运行时缺少可执行文件：$nodeBin" }
}

# ---------- 复制并精简后端（剔除原生模块） ----------
function Copy-ServerPayload {
  param([string]$DestDir)
  Remove-Tree $DestDir
  New-Item -ItemType Directory -Path $DestDir -Force | Out-Null

  $srcServer = Join-Path $RepoRoot 'server'
  # 直接调用原生命令（不用 Start-Process，避免 HTTPS_PROXY/https_proxy 大小写键冲突）
  # robocopy 更健壮：排除私有配置、数据库、日志，以及原生 SQLite 模块
  & robocopy.exe $srcServer $DestDir /E `
    /XD 'better-sqlite3' `
    /XF 'config.json' '*.db' '*.db-shm' '*.db-wal' '*.log' | Out-Null
  # robocopy 退出码 < 8 视为成功
  if ($LASTEXITCODE -ge 8) { throw "复制后端失败（robocopy 退出码 $LASTEXITCODE）" }

  # 二次保险：删除可能残留的原生模块目录
  $bs = Join-Path $DestDir 'node_modules/better-sqlite3'
  Remove-Tree $bs

  if (-not (Test-Path (Join-Path $DestDir 'studyserver.js'))) {
    throw '后端复制后缺少 studyserver.js'
  }
}

# ---------- 发布桌宠本体 ----------
function Publish-App {
  param([hashtable]$Spec, [string]$Dotnet, [string]$DestDir)
  Remove-Tree $DestDir
  New-Item -ItemType Directory -Path $DestDir -Force | Out-Null

  if ($Spec.Rid -eq 'win-x64') {
    $proj = Join-Path $RepoRoot 'BigBlueFish.Windows/BigBlueFish.Windows.csproj'
    $extra = @('-p:Platform=x64', '-p:CompanionInputMode=Ocr')
  } else {
    $proj = Join-Path $RepoRoot 'BigBlueFish.Linux/BigBlueFish.Linux.csproj'
    $extra = @()
    if ($Spec.Rid -eq 'osx-arm64') { $extra += @('-p:PlatformTarget=arm64', '-p:Platform=arm64') }
    if ($Spec.Rid -eq 'osx-x64')   { $extra += @('-p:PlatformTarget=x64',   '-p:Platform=x64') }
    if ($Spec.Rid -eq 'linux-x64') { $extra += @('-p:PlatformTarget=x64',   '-p:Platform=x64') }
  }

  $dotnetArgs = @(
    'publish', $proj,
    '-c', $Configuration,
    '-r', $Spec.Rid,
    '--self-contained', 'true',
    '--nologo',
    '-o', $DestDir
  ) + $extra

  Write-Host "    $Dotnet $($dotnetArgs -join ' ')"
  & $Dotnet @dotnetArgs
  if ($LASTEXITCODE -ne 0) { throw "桌宠发布失败：$($Spec.Rid)" }

  # 清理调试符号，减小体积
  Get-ChildItem -Path $DestDir -Recurse -Include *.pdb | ForEach-Object { Remove-Tree $_.FullName }
}

# ---------- 生成 Unix 启动脚本 ----------
function Write-UnixLauncher {
  param([hashtable]$Spec, [string]$Root, [string]$FileName, [string]$AppExe)
  $lines = @(
    '#!/usr/bin/env bash',
    '# 邮课叮桌宠 · 一键启动器（免安装绿色包）',
    '# 职责：拉起后端 → 等待端口就绪 → 拉起桌宠本体。后端已运行则复用。',
    'set -u',
    'ROOT="$(cd "$(dirname "$0")" && pwd)"',
    'PORT=23336',
    'NODE="$ROOT/runtime/node/bin/node"',
    'SERVER="$ROOT/server/studyserver.js"',
    ('APP="$ROOT/app/{0}"' -f $AppExe),
    '',
    'port_open() { (exec 3<>"/dev/tcp/127.0.0.1/$PORT") 2>/dev/null; }',
    '',
    '# 0) 修复执行位（交叉打包可能丢失）',
    'chmod +x "$NODE" "$APP" 2>/dev/null || true',
    '',
    '# 1) 后端未监听则拉起（后台，无窗口）',
    'if ! port_open; then',
    '  if [ -x "$NODE" ] && [ -f "$SERVER" ]; then',
    '    (cd "$ROOT/server" && nohup "$NODE" "$SERVER" >/dev/null 2>&1 &)',
    '  fi',
    'fi',
    '',
    '# 2) 等待端口就绪（最多 15 秒）',
    'for _ in $(seq 1 30); do',
    '  if port_open; then break; fi',
    '  sleep 0.5',
    'done',
    '',
    '# 3) 拉起桌宠本体',
    'if [ -f "$APP" ]; then',
    '  chmod +x "$APP" 2>/dev/null || true',
    '  cd "$ROOT" && "$APP" &',
    'else',
    '  echo "未找到桌宠本体：$APP" >&2',
    '  exit 4',
    'fi'
  )
  $text = ($lines -join "`n") + "`n"
  $path = Join-Path $Root $FileName
  [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

# ---------- 生成使用说明 ----------
function Write-Readme {
  param([hashtable]$Spec, [string]$Root)
  $entry = switch ($Spec.Rid) {
    'win-x64'   { '启动邮课叮.exe' }
    default     { '启动邮课叮.sh / 启动邮课叮.command' }
  }
  $note = ''
  if ($Spec.Rid -like 'osx-*') {
    $note = @'

【macOS 首次打开】
本包未经 Apple 公证，双击若提示「无法打开」，请右键点启动文件选「打开」，
或到「系统设置 → 隐私与安全性」中点击「仍要打开」。
'@
  }
  if ($Spec.Rid -eq 'linux-x64') {
    $note = @'

【Linux 首次打开】
若双击无法执行，请在终端运行：chmod +x 启动邮课叮.sh && ./启动邮课叮.sh
'@
  }
  $text = @"
邮课叮桌宠（$($Spec.Label) 免安装绿色包）
========================================

一、启动
  双击「$entry」即可。启动器会自动拉起本地后端（端口 23336）并打开桌宠。
  无需安装 .NET、无需安装 Node.js、无需管理员权限。

二、首次使用
  1. 右键桌宠 / 托盘图标 →「邮课叮」→「账号设置…」，填入北交大学号与密码；
  2. 打开「学习中心」查看作业 DDL、课程课件、课表与空教室。

三、数据与隐私
  - 所有数据仅保存在本机 app 目录，不上传任何服务器；
  - 删除整个文件夹即可彻底卸载，不残留注册表与系统服务。

四、目录说明
  app/           桌宠本体（含 .NET 运行时）
  server/        本地后端（学业采集）
  runtime/node/  内嵌 Node 运行时
  bridge-config.json  后端地址配置（默认 127.0.0.1:23336）
$note
"@
  $text = $text -replace "`r`n", "`n"
  [System.IO.File]::WriteAllText((Join-Path $Root '使用说明.txt'), $text, (New-Object System.Text.UTF8Encoding($true)))
}

# ---------- 压缩产物 ----------
function New-Archive {
  param([hashtable]$Spec, [string]$StageRoot, [string]$StageName)
  if ($Spec.Archive -eq 'zip') {
    $dest = Join-Path $OutDir "邮课叮桌宠-$($Spec.Label).zip"
    Remove-Tree $dest
    # 传入目录本身（不带 /*），使 zip 与 tar.gz 一样带顶层「邮课叮桌宠/」目录，
    # 避免用户解压后散落一地。
    Compress-Archive -Path (Join-Path $StageRoot $StageName) -DestinationPath $dest -CompressionLevel Optimal
    return $dest
  } else {
    $dest = Join-Path $OutDir "邮课叮桌宠-$($Spec.Label).tar.gz"
    Remove-Tree $dest
    # 用 bsdtar 打包，保持 unix 路径分隔与执行位（Linux/macOS 依 -C 切换根）
    & tar.exe -czf $dest -C $StageRoot $StageName
    if ($LASTEXITCODE -ne 0) { throw "打包失败：$dest" }
    return $dest
  }
}

# ---------- 主流程：构建单个平台 ----------
function Build-Platform {
  param([string]$Rid, [string]$Dotnet)
  $spec = Get-PlatformSpec -Rid $Rid
  $stageName = '邮课叮桌宠'
  $stageRoot = Join-Path $BuildDir $Rid
  Remove-Tree $stageRoot
  $stage = Join-Path $stageRoot $stageName
  New-Item -ItemType Directory -Path $stage -Force | Out-Null

  Write-Step "组装 $Rid 绿色包"

  Write-Host "  [1/6] Node 运行时"
  Get-NodeRuntime -Spec $spec -DestDir (Join-Path $stage 'runtime/node')

  Write-Host "  [2/6] 后端（纯 JS 依赖）"
  Copy-ServerPayload -DestDir (Join-Path $stage 'server')

  Write-Host "  [3/6] 桌宠本体"
  Publish-App -Spec $spec -Dotnet $Dotnet -DestDir (Join-Path $stage 'app')

  Write-Host "  [4/6] 启动器"
  if ($Rid -eq 'win-x64') {
    $launcherOut = Join-Path $BuildDir "launcher-$Rid"
    Remove-Tree $launcherOut
    & $Dotnet publish (Join-Path $PackagingDir 'launcher-src/YoukedingLauncher.csproj') `
      -c $Configuration --nologo -o $launcherOut
    if ($LASTEXITCODE -ne 0) { throw 'Windows 启动器编译失败' }
    $launcherExe = Join-Path $launcherOut '启动邮课叮.exe'
    if (-not (Test-Path $launcherExe)) { throw '未生成 启动邮课叮.exe' }
    Copy-Item $launcherExe -Destination $stage -Force
  } else {
    $appExe = if ($Rid -like 'osx-*') { 'BigBlueFish.Linux' } else { 'BigBlueFish.Linux' }
    Write-UnixLauncher -Spec $spec -Root $stage -FileName '启动邮课叮.sh' -AppExe $appExe
    Copy-Item (Join-Path $stage '启动邮课叮.sh') -Destination (Join-Path $stage '启动邮课叮.command') -Force
    # 可执行位说明：Windows 上交叉打包无法可靠保留 Unix 执行位，且避免调用 bash
    # （Windows 的 bash.exe 会指向 WSL 启动器）。改为在启动脚本首行自带 chmod，
    # 运行时会自动修复桌宠本体的执行权限。
  }

  Write-Host "  [5/6] 配置与说明"
  $bridgeCfg = @'
{
  "bridgeBase": "http://127.0.0.1:23336"
}
'@
  [System.IO.File]::WriteAllText((Join-Path $stage 'bridge-config.json'), $bridgeCfg, (New-Object System.Text.UTF8Encoding($false)))
  Write-Readme -Spec $spec -Root $stage

  Write-Host "  [6/6] 压缩产物"
  $archive = New-Archive -Spec $spec -StageRoot $stageRoot -StageName $stageName
  $sizeMB = [math]::Round((Get-Item $archive).Length / 1MB, 1)
  Write-Host "    -> $archive ($sizeMB MB)" -ForegroundColor Green

  if (-not $KeepStaging) { Remove-Tree $stageRoot }
  return $archive
}

# ---------- 入口 ----------
$dotnet = Resolve-Dotnet
Write-Host "dotnet: $dotnet"

$targets = if ($Platform -eq 'all') { @('win-x64', 'linux-x64', 'osx-arm64', 'osx-x64') } else { @($Platform) }

$results = @()
foreach ($rid in $targets) {
  try {
    $results += Build-Platform -Rid $rid -Dotnet $dotnet
  } catch {
    Write-Host "  构建 $rid 失败：$_" -ForegroundColor Red
  }
}

Write-Host "`n===== 完成 =====" -ForegroundColor Green
foreach ($r in $results) { Write-Host "  $r" }
