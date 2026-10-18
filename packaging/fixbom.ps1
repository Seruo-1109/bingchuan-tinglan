# fixbom.ps1 - ASCII-only helper.
# Windows PowerShell 5.1 reads .ps1 as ANSI/GBK unless the file starts with a UTF-8 BOM,
# which mangles every Chinese literal into garbage. This re-adds the BOM for any file
# that is valid UTF-8 with non-ASCII bytes but missing the marker. Safe to re-run.
param([Parameter(Mandatory=$true)][string[]]$Path)

$utf8 = New-Object System.Text.UTF8Encoding($false)
$utf8Bom = New-Object System.Text.UTF8Encoding($true)

foreach ($p in $Path) {
    if (-not (Test-Path $p)) { Write-Host "MISSING: $p"; continue }
    $bytes = [System.IO.File]::ReadAllBytes($p)
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        Write-Host "OK (has BOM): $p"
        continue
    }
    # Reject files that are not valid UTF-8, so we never corrupt a real ANSI file.
    $strict = New-Object System.Text.UTF8Encoding($false, $true)
    try {
        $text = $strict.GetString($bytes)
    } catch {
        Write-Host "SKIP (not UTF-8): $p"
        continue
    }
    [System.IO.File]::WriteAllText($p, $text, $utf8Bom)
    Write-Host "BOM added: $p"
}
