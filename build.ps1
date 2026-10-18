# MultiAudioOutput build script
# UTF-8 WITH BOM is required: this script contains the Chinese exe name. Windows PowerShell 5.1
# parses a BOM-less .ps1 as ANSI and would mangle it.
#
# Builds build\并川听澜.exe from src\*.cs.
#
# ICON HANDLING (two separate mechanisms, both required):
#   /win32icon:app.ico   -> Win32 resource section. Explorer / taskbar pinning read this.
#   /resource:app.ico,AppIcon.ico -> managed resource. AppIcon.cs loads this and
#                                    assigns Form.Icon, which is what the window itself
#                                    actually draws. Without it WinForms silently falls
#                                    back to its built-in default icon.
#
# MANIFEST HANDLING:
#   /win32manifest:src\app.manifest -> declares permonitorv2 DPI awareness. Without it
#   the process is DPI-UNAWARE and Windows bitmap-stretches the whole window at
#   125%/150% scaling (blurred text, fat 1px lines, stair-stepped rounded corners).
#   Supplying our own manifest also means we must restate requestedExecutionLevel
#   (asInvoker), otherwise Windows applies UAC virtualisation to file/registry writes.
#
# Output goes to build\. NAudio.dll must sit next to the exe: .NET does NOT probe
# parent directories for assemblies, so a missing copy there means a hard
# FileNotFoundException inside AudioEngine's constructor at startup.
# The build output is therefore self-contained: build\并川听澜.exe + build\NAudio.dll.

$ErrorActionPreference = 'Stop'

$Root     = $PSScriptRoot
$SrcDir   = Join-Path $Root 'src'
$OutDir   = Join-Path $Root 'build'
$OutExe   = Join-Path $OutDir '并川听澜.exe'
$Csc      = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$Icon     = Join-Path $Root 'app.ico'
$NAudio   = Join-Path $Root 'lib\NAudio.dll'
$Manifest = Join-Path $SrcDir 'app.manifest'
# 角色头像（三个表情）。由 design\make_badges.py 生成 192x192 圆形 PNG，
# AvatarArt.cs 按 Avatar0/1/2.png 三个名字从托管资源里取。
$ResDir   = Join-Path $Root 'res'

if (-not (Test-Path $Csc))     { throw "csc.exe not found: $Csc" }
if (-not (Test-Path $Icon))    { throw "app.ico not found: $Icon" }
if (-not (Test-Path $NAudio))  { throw "NAudio.dll not found: $NAudio" }
if (-not (Test-Path $Manifest)){ throw "app.manifest not found: $Manifest" }
foreach ($n in @('avatar_calm.png','avatar_focus.png','avatar_smile.png')) {
    if (-not (Test-Path (Join-Path $ResDir $n))) { throw "avatar not found: $ResDir\$n" }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

# IconStudio.cs is a design-time icon generator with its OWN entry point. It must be
# excluded: linking it in adds ~100 KB of dead code to the shipped exe. Regenerate
# icons by compiling it separately (see design\icons\).
$Exclude = @('IconStudio.cs')
$srcs = Get-ChildItem (Join-Path $SrcDir '*.cs') |
        Where-Object { $Exclude -notcontains $_.Name } |
        ForEach-Object { $_.FullName }
if ($srcs.Count -eq 0) { throw "no .cs files in $SrcDir" }

# AppIcon.cs / AvatarArt.cs look these names up via Assembly.GetManifestResourceStream,
# keep in sync. One /resource: switch per file.
$ResourceSpecs = @(
    "$Icon,AppIcon.ico"
    "$(Join-Path $ResDir 'avatar_calm.png'),Avatar0.png"
    "$(Join-Path $ResDir 'avatar_focus.png'),Avatar1.png"
    "$(Join-Path $ResDir 'avatar_smile.png'),Avatar2.png"
)

$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/warn:4'
    '/main:MultiAudioOutput.Program'
    "/out:$OutExe"
    '/reference:System.dll'
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    '/reference:System.Core.dll'
    '/reference:System.Web.Extensions.dll'
    "/reference:$NAudio"
    "/win32icon:$Icon"
    "/win32manifest:$Manifest"
) + ($ResourceSpecs | ForEach-Object { "/resource:$_" }) + $srcs

Write-Host "SRC_COUNT=$($srcs.Count)"
& $Csc @cscArgs
$cscExit = $LASTEXITCODE
Write-Host "CSC_EXIT=$cscExit"
if ($cscExit -ne 0) { throw "compile failed with exit code $cscExit" }

# The exe cannot load NAudio from the parent directory; copy it in.
Copy-Item $NAudio (Join-Path $OutDir 'NAudio.dll') -Force

$exeInfo = Get-Item $OutExe
Write-Host "EXE_BYTES=$($exeInfo.Length)"
Write-Host "EXE_TIME=$($exeInfo.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))"

# Verify the managed icon resource really made it into the assembly. This is the
# check that would have caught the "icon embedded but window shows default" bug.
$asm = [System.Reflection.Assembly]::LoadFrom($OutExe)
$names = $asm.GetManifestResourceNames()
$hasIcon = $names -contains 'AppIcon.ico'
$missingAvatar = @('Avatar0.png','Avatar1.png','Avatar2.png') | Where-Object { $names -notcontains $_ }
Write-Host "MANAGED_RESOURCES=$($names -join ',')"
Write-Host "ICON_RESOURCE_PRESENT=$hasIcon"
if (-not $hasIcon) { throw "AppIcon.ico managed resource missing from $OutExe" }
if ($missingAvatar.Count -gt 0) { throw "avatar managed resource(s) missing from $OutExe : $($missingAvatar -join ', ')" }

# Verify the manifest really landed. Reflection cannot see the Win32 resource section,
# so scan the raw bytes for the marker strings instead. Both spellings must be present:
# they come from the same file, so seeing only one means the file was edited unevenly.
$bytes = [System.IO.File]::ReadAllBytes($OutExe)
$ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
$hasDpiAware     = $ascii.Contains('permonitorv2')
$hasDpiAwareOld  = $ascii.Contains('true/pm')
$hasAsInvoker    = $ascii.Contains('asInvoker')
Write-Host "MANIFEST_DPI_AWARE=$hasDpiAware"
Write-Host "MANIFEST_DPI_FALLBACK=$hasDpiAwareOld"
Write-Host "MANIFEST_AS_INVOKER=$hasAsInvoker"
if (-not $hasDpiAware) { throw "permonitorv2 missing from $OutExe -- /win32manifest did not take effect" }
if (-not $hasAsInvoker) { throw "asInvoker missing from $OutExe -- UAC virtualisation would apply" }

Write-Host "BUILD_OK"
