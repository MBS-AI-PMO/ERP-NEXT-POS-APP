<#
Builds the TillPOS field-test package.
Usage (from the tillpos folder):
  powershell -File tools\publish-field.ps1 -Version 0.3.6 [-CashierPin 4821] [-SupervisorPin 7350]             -> ..\publish\TillPOS-field-<Version>.zip
  powershell -File tools\publish-field.ps1 -Version 0.3.6 -SingleExe [-CashierPin 4821] [-SupervisorPin 7350]  -> ..\publish\TillPOS-exe-<Version>\TillPOS.exe
Zip: TillPOS folder + settings.json beside the exe + START HERE.txt.
-SingleExe: one self-contained TillPOS.exe with settings.json built in (imported into C:\ProgramData\TillPOS on the first
start) + "TillPOS <Version> - START HERE.txt". The built-in plain API secret cannot be removed from the exe, so treat the exe
like the credentials (test PCs only).
Both packages set SampleQr = true: receipts end with a QR code marked "SAMPLE QR - FOR TESTING ONLY" while there is no TRN.
Credentials are read from ..\publish\TillPOS.SyncCli\tillpos.cli.json (local, git-ignored) and are never printed.
Test PINs: without -CashierPin / -SupervisorPin, random 4-digit PINs are generated with a CSPRNG and printed at the end.
They are written only into the package (settings and START HERE), never into the repo.
#>
param(
    [string]$Version = "0.3.6",
    [string]$CashierPin,
    [string]$SupervisorPin,
    [switch]$SingleExe
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
        # ASCII digits only and no trailing newline (\d would accept other scripts' digits, $ a final "\n").
        if ($given -notmatch '^[0-9]{4,6}\z') { throw "$role PIN must be 4 to 6 digits." }
        if (Test-WeakPin $given) { Write-Warning "$role PIN $given is easy to guess." }
        return $given
    }
    do { $pin = New-RandomPin } while ((Test-WeakPin $pin) -or $pin -eq $other)
    return $pin
}

# The App's Release build output. Removed before and after every publish: an incremental build may not notice that the
# built-in settings resource was added or removed, and the -SingleExe build's TillPOS.dll holds the plain secret.
function Remove-AppReleaseBuild {
    foreach ($dir in (Join-Path $tillposRoot "src\TillPOS.App\obj\Release"), (Join-Path $tillposRoot "src\TillPOS.App\bin\Release")) {
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir -ErrorAction Stop }
    }
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
$exeRoot     = Join-Path $publishRoot ("TillPOS-exe-{0}" -f $Version)
$exeGuide    = "TillPOS $Version - START HERE.txt"
$tempName    = "packaged-settings-{0}.json" -f [guid]::NewGuid().ToString("N")
$tempSettings = Join-Path $publishRoot $tempName
$cliJson     = Join-Path $publishRoot "TillPOS.SyncCli\tillpos.cli.json"
$startHere   = Join-Path $PSScriptRoot "field\START HERE.txt"

# Refuse to run unless the output paths are git-ignored (the package holds credentials).
Push-Location $repoRoot
try {
    foreach ($path in "publish/TillPOS-field/TillPOS/settings.json", "publish/TillPOS-exe-$Version/TillPOS.exe", "publish/$tempName") {
        & git check-ignore -q $path
        if ($LASTEXITCODE -ne 0) { throw "$path is not git-ignored; refusing to build a package that contains credentials." }
    }
} finally { Pop-Location }

if (-not (Test-Path $cliJson)) { throw "Missing $cliJson" }
if (-not (Test-Path $startHere)) { throw "Missing $startHere" }
$cli = Get-Content -Raw -Path $cliJson -ErrorAction Stop | ConvertFrom-Json
foreach ($f in "BaseUrl","ApiKey","ApiSecret","PosProfile") {
    if ([string]::IsNullOrWhiteSpace($cli.$f)) { throw "tillpos.cli.json has no $f" }
}

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
    SampleQr            = $true
    # Field-test packages never write to ERPNext: a supervisor can switch a till to DryRun or Live in Settings.
    Upload              = "Off"
    # The counters a cashier can open a shift at (the first is the default). "Test Counter" rounds nothing (its POS Profile
    # disables the rounded total); "Al Ain Counter 1" rounds cash to 0.25. If the test API user cannot read a counter's
    # POS Profile, that counter shows as "Not available" on the Open Shift screen.
    Counters            = @(
        [ordered]@{ PosProfile = "Test Counter";     Label = "Test Counter"; CashMode = "Cash Counter 2"; CardMode = "Credit Card" },
        [ordered]@{ PosProfile = "Al Ain Counter 1"; Label = "Counter 1";    CashMode = "Cash Counter 1"; CardMode = "Credit Card" }
    )
}
$json = $settings | ConvertTo-Json -Depth 5
$utf8 = New-Object System.Text.UTF8Encoding($false)

# START HERE.txt in the repo is a template; the package gets the filled copy. Lines starting with #ZIP# / #EXE# belong to
# one package kind only.
function Get-Guide([string]$kind, [string]$label, [string]$shownVersion) {
    $keep = "#$kind#"
    $lines = foreach ($line in ([System.IO.File]::ReadAllText($startHere) -split "`r?`n")) {
        if ($line.StartsWith($keep)) { $line.Substring($keep.Length) }
        elseif ($line -notmatch '^#(ZIP|EXE)#') { $line }
    }
    $guide = ($lines -join "`r`n")   # Notepad-friendly line endings
    $guide = $guide.Replace("{{VERSION}}", $shownVersion).Replace("{{PACKAGE_KIND}}", $label)
    $guide = $guide.Replace("{{CASHIER_PIN}}", $CashierPin).Replace("{{SUPERVISOR_PIN}}", $SupervisorPin)
    if ($guide -match '\{\{[A-Z_]+\}\}') { throw "START HERE.txt has a placeholder the script does not fill: $($Matches[0])" }
    return $guide
}

$dotnet = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }

if ($SingleExe) {
    if (Test-Path $exeRoot) { Remove-Item -Recurse -Force $exeRoot -ErrorAction Stop }
    Push-Location $tillposRoot
    try {
        [System.IO.File]::WriteAllText($tempSettings, $json, $utf8)
        Remove-AppReleaseBuild
        # The login screen shows the informational version; keep it equal to the folder name and START HERE.
        & $dotnet publish src/TillPOS.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
            "-p:PackagedSettings=$tempSettings" "-p:Version=$Version" "-p:InformationalVersion=$Version-test" -o $exeRoot
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    } finally {
        # The settings file and the intermediate TillPOS.dll hold the plain secret; only the exe may keep it.
        if (Test-Path $tempSettings) { Remove-Item -Force $tempSettings }
        Remove-AppReleaseBuild
        Pop-Location
    }

    Get-ChildItem -Path $exeRoot -Filter *.pdb -File | Remove-Item -Force -ErrorAction Stop
    [System.IO.File]::WriteAllText((Join-Path $exeRoot $exeGuide), (Get-Guide "EXE" "single TillPOS.exe" "$Version-test"), $utf8)

    $unexpected = @(Get-ChildItem -Path $exeRoot -Force | Where-Object { $_.Name -ne "TillPOS.exe" -and $_.Name -ne $exeGuide })
    if ($unexpected.Count -gt 0) { throw "Unexpected files next to TillPOS.exe: $(($unexpected | ForEach-Object Name) -join ', ')" }
    if (-not (Test-Path (Join-Path $exeRoot "TillPOS.exe"))) { throw "TillPOS.exe was not produced." }

    $exe = Get-Item (Join-Path $exeRoot "TillPOS.exe")
    $size = [math]::Round($exe.Length / 1MB, 1)
    Write-Host "Single exe: $($exe.FullName) ($size MB)"
    Write-Host "Guide:      $(Join-Path $exeRoot $exeGuide)"
} else {
    if (Test-Path $fieldRoot) { Remove-Item -Recurse -Force $fieldRoot -ErrorAction Stop }
    if (Test-Path $zipPath)   { Remove-Item -Force $zipPath -ErrorAction Stop }

    Push-Location $tillposRoot
    try {
        Remove-AppReleaseBuild
        # The login screen shows the informational version; keep it equal to the zip name and START HERE.
        & $dotnet publish src/TillPOS.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false `
            "-p:Version=$Version" "-p:InformationalVersion=$Version-field" -o $appOut
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    } finally { Pop-Location }

    [System.IO.File]::WriteAllText((Join-Path $appOut "settings.json"), $json, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $fieldRoot "START HERE.txt"), (Get-Guide "ZIP" "zip package" "$Version-field"), $utf8)

    Compress-Archive -Path (Join-Path $fieldRoot "*") -DestinationPath $zipPath -ErrorAction Stop
    $size = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "Field package: $zipPath ($size MB)"
}
Write-Host "Test PINs in this package (also in its START HERE):"
Write-Host "  Cashier:    $CashierPin"
Write-Host "  Supervisor: $SupervisorPin"
