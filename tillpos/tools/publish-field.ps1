<#
Builds the TillPOS field-test package: ..\publish\TillPOS-field-<Version>.zip
Usage (from the tillpos folder):  powershell -File tools\publish-field.ps1 -Version 0.3.1
Credentials are read from ..\publish\TillPOS.SyncCli\tillpos.cli.json (local, git-ignored) and are never printed.
#>
param([string]$Version = "0.3.1")
$ErrorActionPreference = "Stop"

$tillposRoot = Split-Path -Parent $PSScriptRoot
$repoRoot    = Split-Path -Parent $tillposRoot
$publishRoot = Join-Path $repoRoot "publish"
$fieldRoot   = Join-Path $publishRoot "TillPOS-field"
$appOut      = Join-Path $fieldRoot "TillPOS"
$zipPath     = Join-Path $publishRoot ("TillPOS-field-{0}.zip" -f $Version)
$cliJson     = Join-Path $publishRoot "TillPOS.SyncCli\tillpos.cli.json"
$startHere   = Join-Path $PSScriptRoot "field\START HERE.txt"

# Refuse to run unless the output folder is git-ignored (the package holds credentials).
Push-Location $repoRoot
try {
    & git check-ignore -q "publish/TillPOS-field/TillPOS/settings.json"
    if ($LASTEXITCODE -ne 0) { throw "publish/ is not git-ignored; refusing to build a package that contains credentials." }
} finally { Pop-Location }

if (-not (Test-Path $cliJson)) { throw "Missing $cliJson" }
if (-not (Test-Path $startHere)) { throw "Missing $startHere" }
$cli = Get-Content -Raw -Path $cliJson -ErrorAction Stop | ConvertFrom-Json
foreach ($f in "BaseUrl","ApiKey","ApiSecret","PosProfile") {
    if ([string]::IsNullOrWhiteSpace($cli.$f)) { throw "tillpos.cli.json has no $f" }
}

if (Test-Path $fieldRoot) { Remove-Item -Recurse -Force $fieldRoot -ErrorAction Stop }
if (Test-Path $zipPath)   { Remove-Item -Force $zipPath -ErrorAction Stop }

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
Push-Location $tillposRoot
try {
    & $dotnet publish src/TillPOS.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -o $appOut
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
} finally { Pop-Location }

$settings = [ordered]@{
    BaseUrl             = $cli.BaseUrl
    ApiKey              = $cli.ApiKey
    ApiSecret           = $cli.ApiSecret
    PosProfile          = $cli.PosProfile
    TillNumber          = 1
    CashMode            = "Cash Counter 2"
    CardMode            = "Credit Card"
    PrinterName         = ""
    PaperWidth          = "Mm80"
    ReceiptFooter       = "Thank you for shopping with us"
    Precision           = 3
    Rounding            = "Bankers"
    DbPath              = ""
    SyncIntervalSeconds = 90
    ShowReceiptPreview  = $true
    ShopAddress         = "Nuaimiya 1, Al Ain Market, Ajman, UAE"
    ShopPhone           = "+971 6 000 0000"
    LocalTestCashiers   = @(
        [ordered]@{ Id = "cashier1";    Name = "Test Cashier";    Pin = "1234"; IsSupervisor = $false },
        [ordered]@{ Id = "supervisor1"; Name = "Test Supervisor"; Pin = "9876"; IsSupervisor = $true }
    )
    SetupDone           = $false
}
$json = $settings | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $appOut "settings.json"), $json, (New-Object System.Text.UTF8Encoding($false)))

Copy-Item -Path $startHere -Destination $fieldRoot -ErrorAction Stop

Compress-Archive -Path (Join-Path $fieldRoot "*") -DestinationPath $zipPath -ErrorAction Stop
$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Field package: $zipPath ($size MB)"
