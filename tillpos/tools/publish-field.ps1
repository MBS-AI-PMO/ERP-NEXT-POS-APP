<#
Builds the TillPOS field-test package: ..\publish\TillPOS-field-<Version>.zip
Usage (from the tillpos folder):  powershell -File tools\publish-field.ps1 -Version 0.3.1 [-CashierPin 4821] [-SupervisorPin 7350]
Credentials are read from ..\publish\TillPOS.SyncCli\tillpos.cli.json (local, git-ignored) and are never printed.
Test PINs: without -CashierPin / -SupervisorPin, random 4-digit PINs are generated with a CSPRNG and printed at the end.
They are written only into the package (settings.json and START HERE.txt), never into the repo.
#>
param(
    [string]$Version = "0.3.1",
    [string]$CashierPin,
    [string]$SupervisorPin
)
$ErrorActionPreference = "Stop"

# 1111, 1234, 9876 and the like are the first PINs anyone tries.
function Test-WeakPin([string]$pin) {
    $digits = $pin.ToCharArray() | ForEach-Object { [int][string]$_ }
    $steps = for ($i = 1; $i -lt $digits.Count; $i++) { $digits[$i] - $digits[$i - 1] }
    $distinct = @($steps | Sort-Object -Unique)
    return ($distinct.Count -eq 1 -and [math]::Abs($distinct[0]) -le 1)
}

# Uniform random digits from the OS CSPRNG (bytes >= 250 are rejected so every digit is equally likely).
function New-RandomPin {
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $buffer = New-Object byte[] 1
        $pin = ""
        while ($pin.Length -lt 4) {
            $rng.GetBytes($buffer)
            if ($buffer[0] -lt 250) { $pin += [string]($buffer[0] % 10) }
        }
        return $pin
    } finally { $rng.Dispose() }
}

function Resolve-Pin([string]$given, [string]$role, [string]$other) {
    if ($given) {
        if ($given -notmatch '^\d{4,6}$') { throw "$role PIN must be 4 to 6 digits." }
        if (Test-WeakPin $given) { Write-Warning "$role PIN $given is easy to guess." }
        return $given
    }
    do { $pin = New-RandomPin } while ((Test-WeakPin $pin) -or $pin -eq $other)
    return $pin
}

$CashierPin    = Resolve-Pin $CashierPin "Cashier" $SupervisorPin
$SupervisorPin = Resolve-Pin $SupervisorPin "Supervisor" $CashierPin
if ($CashierPin -eq $SupervisorPin) { throw "The cashier and supervisor PINs must be different." }

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
    # The login screen shows the informational version; keep it equal to the zip name and START HERE.
    & $dotnet publish src/TillPOS.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
        "-p:Version=$Version" "-p:InformationalVersion=$Version-field" -o $appOut
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
        [ordered]@{ Id = "cashier1";    Name = "Test Cashier";    Pin = $CashierPin;    IsSupervisor = $false },
        [ordered]@{ Id = "supervisor1"; Name = "Test Supervisor"; Pin = $SupervisorPin; IsSupervisor = $true }
    )
    SetupDone           = $false
}
$json = $settings | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $appOut "settings.json"), $json, (New-Object System.Text.UTF8Encoding($false)))

# START HERE.txt in the repo is a template; the package gets the filled copy.
$guide = [System.IO.File]::ReadAllText($startHere)
$guide = $guide.Replace("{{VERSION}}", "$Version-field").Replace("{{CASHIER_PIN}}", $CashierPin).Replace("{{SUPERVISOR_PIN}}", $SupervisorPin)
if ($guide -match '\{\{[A-Z_]+\}\}') { throw "START HERE.txt has a placeholder the script does not fill: $($Matches[0])" }
$guide = $guide -replace "`r?`n", "`r`n"   # Notepad-friendly line endings
[System.IO.File]::WriteAllText((Join-Path $fieldRoot "START HERE.txt"), $guide, (New-Object System.Text.UTF8Encoding($false)))

Compress-Archive -Path (Join-Path $fieldRoot "*") -DestinationPath $zipPath -ErrorAction Stop
$size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
Write-Host "Field package: $zipPath ($size MB)"
Write-Host "Test PINs in this package (also in its START HERE.txt):"
Write-Host "  Cashier:    $CashierPin"
Write-Host "  Supervisor: $SupervisorPin"
