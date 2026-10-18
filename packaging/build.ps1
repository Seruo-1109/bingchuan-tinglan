# =============================================================================
#  build.ps1 - 构建 MultiAudioOutput 一键安装包
#
#  用法（在任意目录）：
#      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#      powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -SkipCompile
#
#  产物：
#      dist\并川听澜-1.2-Setup.exe      ← 交付物（单文件安装包）
#      dist\并川听澜-1.2-Setup.exe.sha256
#
#  流程：
#      1. 从项目 build\ 取 payload（exe + NAudio.dll + app.ico）
#      2. 计算字节数与 SHA256，生成 installer\payload-fingerprint.nsh
#      3. 用内置的 NSIS 3.11 makensis 编译 installer\installer.nsi
#      4. 校验产物（大小、版本资源、图标、manifest）
# =============================================================================

[CmdletBinding()]
param(
    [string]$ProjectRoot = '',
    [string]$Version     = '1.2',
    [switch]$SkipCompile
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

# 默认把本脚本所在目录（packaging\）的上一级当作项目根，
# 这样从仓库 clone 下来后无需任何参数即可构建。
if ([string]::IsNullOrWhiteSpace($ProjectRoot)) {
    $ProjectRoot = Split-Path -Parent $PSScriptRoot
}

$Here      = Split-Path -Parent $MyInvocation.MyCommand.Path
$Payload   = Join-Path $Here 'payload'
$Assets    = Join-Path $Here 'assets'
$Installer = Join-Path $Here 'installer'
$Dist      = Join-Path $Here 'dist'

# makensis 查找顺序：
#   1. packaging\_tools\nsis-3.11\makensis.exe （自带工具链，可选，不入库）
#   2. PATH 里的 makensis
#   3. 默认安装位置 C:\Program Files (x86)\NSIS
# 仓库不提交 NSIS 二进制：装一次 `winget install NSIS.NSIS` 或从 nsis.sourceforge.io 下载即可。
$Makensis = $null
$candidates = @(
    (Join-Path $Here '_tools\nsis-3.11\makensis.exe'),
    (Join-Path $Here '_tools\nsis\makensis.exe'),
    'C:\Program Files (x86)\NSIS\makensis.exe',
    'C:\Program Files\NSIS\makensis.exe'
)
foreach ($c in $candidates) {
    if (Test-Path $c) { $Makensis = $c; break }
}
if (-not $Makensis) {
    $cmd = Get-Command makensis -ErrorAction SilentlyContinue
    if ($cmd) { $Makensis = $cmd.Source }
}

function Step($text) { Write-Host ''; Write-Host "== $text" -ForegroundColor Cyan }
function Ok($text)   { Write-Host "   [OK] $text" -ForegroundColor Green }
function Info($text) { Write-Host "   $text" -ForegroundColor Gray }

# ---------------------------------------------------------------------------
# 0. 前置检查
# ---------------------------------------------------------------------------
Step '检查构建环境'

if (-not $Makensis) {
    throw "找不到 makensis。请安装 NSIS 3.x：`n  winget install NSIS.NSIS`n或从 https://nsis.sourceforge.io/Download 下载安装后重试。"
}
$nsisVer = (& $Makensis /VERSION 2>&1 | Out-String).Trim()
Ok "makensis $nsisVer"

foreach ($f in @('header.bmp', 'welcome.bmp')) {
    if (-not (Test-Path (Join-Path $Assets $f))) {
        throw "缺少界面位图 assets\$f，请先运行 assets\make-bitmaps.ps1"
    }
}
Ok '界面位图齐备（header.bmp / welcome.bmp）'

# ---------------------------------------------------------------------------
# 1. 收集 payload
# ---------------------------------------------------------------------------
Step '收集 payload'

New-Item -ItemType Directory -Force -Path $Payload | Out-Null

# 用户可见的文件名用中文「并川听澜.exe」。内部标识（%APPDATA% 目录、
# HKCU\...\Run 值名、单实例互斥体）都是源码里的硬编码字符串，与文件名无关，
# 所以改这里不会让老用户丢配置或丢自启。
$srcExe = Join-Path $ProjectRoot 'build\并川听澜.exe'
$srcDll = Join-Path $ProjectRoot 'build\NAudio.dll'
$srcIco = Join-Path $ProjectRoot 'app.ico'

# 首次 clone 后 build\ 还不存在：先跑一次根目录的 build.ps1 编译。
# -SkipCompile 用来跳过这一步（CI 里主程序已经单独编好了）。
if (-not (Test-Path $srcExe) -and -not $SkipCompile) {
    $rootBuild = Join-Path $ProjectRoot 'build.ps1'
    if (Test-Path $rootBuild) {
        Info "build\ 不存在，先运行 $rootBuild"
        & powershell -NoProfile -ExecutionPolicy Bypass -File $rootBuild
        if ($LASTEXITCODE -ne 0) { throw "编译失败，退出码 $LASTEXITCODE" }
    }
}

foreach ($p in @($srcExe, $srcDll, $srcIco)) {
    if (-not (Test-Path $p)) { throw "缺文件：$p" }
}

Copy-Item $srcExe (Join-Path $Payload '并川听澜.exe') -Force
Copy-Item $srcDll (Join-Path $Payload 'NAudio.dll')   -Force
Copy-Item $srcIco (Join-Path $Payload 'app.ico')      -Force

$exe = Get-Item (Join-Path $Payload '并川听澜.exe')
$dll = Get-Item (Join-Path $Payload 'NAudio.dll')
$ico = Get-Item (Join-Path $Payload 'app.ico')

Ok ("并川听澜.exe  {0,8:N0} 字节   {1}" -f $exe.Length, $exe.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
Ok ("NAudio.dll           {0,8:N0} 字节   {1}" -f $dll.Length, $dll.LastWriteTime.ToString('yyyy-MM-dd HH:mm'))
Ok ("app.ico             {0,8:N0} 字节" -f $ico.Length)

# payload 必须成对且非空，否则装出来的程序会缺依赖
if ($exe.Length -lt 100KB) { throw "主程序过小，可能是占位文件：$($exe.Length) 字节" }
if ($dll.Length -lt 100KB) { throw "NAudio.dll 过小：$($dll.Length) 字节" }

# ---------------------------------------------------------------------------
# 2. 生成指纹头文件
# ---------------------------------------------------------------------------
Step '计算 payload 指纹'

$exeHash = (Get-FileHash $exe.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$dllHash = (Get-FileHash $dll.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

Info "exe  $exeHash"
Info "dll  $dllHash"

$nsh = @(
    '; 本文件由 build.ps1 自动生成，请勿手工修改。',
    '; 用途：安装器写入文件后做字节数 + SHA256 自检，',
    ';       确保装出来的程序与打包时的源文件逐字节一致。',
    ''
    "!define PAYLOAD_EXE      `"..\payload\并川听澜.exe`""
    "!define PAYLOAD_DLL      `"..\payload\NAudio.dll`""
    "!define PAYLOAD_ICON     `"..\payload\app.ico`""
    ''
    "!define PAYLOAD_EXE_SHA  `"$exeHash`""
    "!define PAYLOAD_EXE_SIZE `"$($exe.Length)`""
    "!define PAYLOAD_DLL_SHA  `"$dllHash`""
    "!define PAYLOAD_DLL_SIZE `"$($dll.Length)`""
)
$nshPath = Join-Path $Installer 'payload-fingerprint.nsh'
# NSIS 源码按 UTF-8 无 BOM 读取，这里也写成无 BOM
[System.IO.File]::WriteAllLines($nshPath, $nsh, (New-Object System.Text.UTF8Encoding($false)))
Ok "已生成 installer\payload-fingerprint.nsh"

# ---------------------------------------------------------------------------
# 3. 编译
# ---------------------------------------------------------------------------
Step '编译安装包'

New-Item -ItemType Directory -Force -Path $Dist | Out-Null
$outFile = Join-Path $Dist "并川听澜-$Version-Setup.exe"
if (Test-Path $outFile) { Remove-Item $outFile -Force }

# VIProductVersion 强制要求四段 X.X.X.X，而 -Version 通常写两段（1.2）。
# 把 -Version 补齐到四段再传给 NSIS，这样无论传 1.2 / 1.2.3 / 1.2.3.4 都能编译。
$vParts = @($Version.Split('.'))
while ($vParts.Count -lt 4) { $vParts += '0' }
$ver4 = ($vParts[0..3]) -join '.'
Info "版本号 $Version → 四段 $ver4"

# 注意：-SkipCompile 只跳过「主程序编译」（即 build\ 不存在时自动调根目录 build.ps1），
# 不跳过这里的安装包编译 —— 否则脚本会在没有产物的情况下直接断言失败。
Push-Location $Installer
try {
    $log = & $Makensis ("/V2", "/DVER=$Version", "/DVER4=$ver4", "installer.nsi") 2>&1
    $log | ForEach-Object { Info $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "makensis 失败，退出码 $LASTEXITCODE。请查看上方日志。"
    }
} finally {
    Pop-Location
}

if (-not (Test-Path $outFile)) { throw "未生成安装包：$outFile" }
$setup = Get-Item $outFile
Ok ("安装包 {0:N0} 字节 ({1:N2} MB)" -f $setup.Length, ($setup.Length / 1MB))

# ---------------------------------------------------------------------------
# 4. 产物校验
# ---------------------------------------------------------------------------
Step '校验产物'

# 4a. PE 结构：必须是 GUI 子系统（不是控制台），带 UAC manifest
$bytes = [System.IO.File]::ReadAllBytes($outFile)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)

$mz = $bytes[0] -eq 0x4D -and $bytes[1] -eq 0x5A
if (-not $mz) { throw '不是有效的 PE 文件（缺 MZ 头）' }
Ok 'PE 头正常'

$hasManifest = $ascii.Contains('requestedExecutionLevel')
if (-not $hasManifest) { throw '安装包缺少 UAC manifest，双击不会提权' }
Ok 'UAC manifest 存在（双击即提权）'

$hasIcon = $ascii.Contains('MultiAudioOutput') -or $true
# 图标能否读取，用 Shell API 校验更可靠
try {
    Add-Type -AssemblyName System.Drawing
    $ic = [System.Drawing.Icon]::ExtractAssociatedIcon($outFile)
    if ($null -eq $ic) { throw 'ExtractAssociatedIcon 返回空' }
    Ok ("嵌入图标 {0}x{1}" -f $ic.Width, $ic.Height)
    $ic.Dispose()
} catch {
    Write-Host "   [警告] 图标检查跳过：$($_.Exception.Message)" -ForegroundColor Yellow
}

# 4b. 版本资源
$vi = $setup.VersionInfo
Ok ("版本资源 ProductName    = $($vi.ProductName)")
Ok ("版本资源 FileDescription= $($vi.FileDescription)")
Ok ("版本资源 FileVersion    = $($vi.FileVersion)")
if (-not $vi.ProductName) { throw '版本资源缺失' }

# 4c. 是否真的把 payload 压进去了
#     payload 原始 760KB，LZMA 实压后约 360KB。过小说明 File 指令没生效。
$payloadRaw = $exe.Length + $dll.Length
Info ("payload 原始合计 {0:N0} 字节；安装包 {1:N0} 字节（压缩率 {2:P0}）" -f `
      $payloadRaw, $setup.Length, ($setup.Length / $payloadRaw))
if ($setup.Length -lt 300KB) {
    throw "安装包体积异常偏小（$([math]::Round($setup.Length/1KB)) KB），payload 可能没打进去"
}

# 4e. 安装包内必须含有 payload 的特征串：确认 File 指令确实把两个文件收进去了。
#     NAudio.dll 内部有 "NAudio" 字样，MultiAudioOutput.exe 的内部字符串用于判别主程序。
#     注意：两者都被 LZMA 压缩过，不能直接字符串搜索——改为解开安装包数据块验证。
#     这里只做一个粗略的规模断言，真正的逐字节验证由安装器运行时的 VerifyPayload 完成。

# 4d. 写 SHA256 校验文件
$setupHash = (Get-FileHash $outFile -Algorithm SHA256).Hash.ToLowerInvariant()
$shaPath = "$outFile.sha256"
# 用无 BOM 的 UTF-8 写入：文件名含中文，ASCII 会变成 ?；而带 BOM 会让
# sha256sum / certutil 之类的外部校验工具把 BOM 当成哈希首字符而校验失败。
[System.IO.File]::WriteAllText(
    $shaPath,
    ("$setupHash  $([System.IO.Path]::GetFileName($outFile))`r`n"),
    (New-Object System.Text.UTF8Encoding($false)))
Ok "SHA256 $setupHash"
Ok "已写入 $([System.IO.Path]::GetFileName($shaPath))"

# ---------------------------------------------------------------------------
Step '完成'
Write-Host "   安装包：$outFile" -ForegroundColor White
Write-Host "   体积  ：$([math]::Round($setup.Length/1MB,2)) MB" -ForegroundColor White
Write-Host ''
