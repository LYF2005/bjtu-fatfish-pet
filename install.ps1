<#
.SYNOPSIS
  北交大肥鱼后端 · 开机自启安装脚本（可重复运行，幂等）。
.DESCRIPTION
  创建/更新一个用户级计划任务，开机登录后以隐藏窗口方式启动 server/studyserver.js。
  - 使用相对本脚本的目录解析路径，不硬编码任何用户目录。
  - 优先使用 PATH 中的 node.exe，找不到时探测常见安装位置。
  - 任务名与壁纸项目不同（Youkeding-StudyServer），两者互不干扰。
  - 说明：node.exe 是控制台程序，登录瞬间可能短暂闪一下窗口。若需完全无窗口，
    不要使用 VBS + wscript（中文路径易出错、文件缺失会导致自启失败）；改用编译为
    WinExe（GUI 子系统、不分配控制台）的启动器，原理见壁纸项目 launcher/README.md。
.PARAMETER TaskName
  计划任务名称，默认 Youkeding-StudyServer。
.PARAMETER Uninstall
  传入则删除该计划任务。
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File .\install.ps1
  powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall
#>
param(
  [string]$TaskName = 'Youkeding-StudyServer',
  [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$base = Split-Path -Parent $MyInvocation.MyCommand.Path
$serverDir = Join-Path $base 'server'
$entry = Join-Path $serverDir 'studyserver.js'

function Find-Node {
  $cmd = Get-Command node.exe -ErrorAction SilentlyContinue
  if ($cmd) { return $cmd.Source }
  foreach ($c in @(
      "$env:ProgramFiles\nodejs\node.exe",
      "${env:ProgramFiles(x86)}\nodejs\node.exe",
      "$env:LOCALAPPDATA\Programs\nodejs\node.exe")) {
    if (Test-Path $c) { return $c }
  }
  return $null
}

if ($Uninstall) {
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
  Write-Host "已删除计划任务：$TaskName"
  exit 0
}

if (-not (Test-Path $entry)) { throw "找不到后端入口：$entry" }
$node = Find-Node
if (-not $node) { throw '未找到 node.exe，请先安装 Node.js >= 18 并确保它在 PATH 中。' }

# node.exe 直接跟脚本路径即可，用引号包裹避免空格问题。
$arg = "`"$entry`""
$action = New-ScheduledTaskAction -Execute $node -Argument $arg -WorkingDirectory $serverDir
$trigger = New-ScheduledTaskTrigger -AtLogOn
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Limited

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger `
  -Settings $settings -Principal $principal -Force | Out-Null

Write-Host "已安装开机自启：$TaskName"
Write-Host "  入口：$entry"
Write-Host "  Node：$node"
Write-Host "卸载：powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall"
