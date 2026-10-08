<#
Builds the TillPOS field-test package.
Usage (from the tillpos folder):
  powershell -File tools\publish-field.ps1 -Version 0.4.1              -> ..\publish\TillPOS-field-<Version>-dev.zip      (Dev, the default)
  powershell -File tools\publish-field.ps1 -Version 0.4.1 -SingleExe   -> ..\publish\TillPOS-exe-<Version>-dev\TillPOS.exe
  powershell -File tools\publish-field.ps1 -Version 0.4.1 [-SingleExe] -Environment Production [-CashierPin 4821] [-SupervisorPin 7350]
                                                                       -> ..\publish\TillPOS-field-<Version>.zip, ..\publish\TillPOS-exe-<Version>\
Zip: TillPOS folder + settings.json beside the exe + START HERE.txt.
-SingleExe: one self-contained TillPOS.exe with settings.json built in (imported on the first start into C:\ProgramData\TillPOS,
or C:\ProgramData\TillPOS-Dev for a Dev build) + "TillPOS <Version> - START HERE.txt". The built-in plain API secret cannot be
removed from the exe, so treat the exe like the credentials (test PCs only).
Both packages set SampleQr = true: receipts end with a QR code marked "SAMPLE QR - FOR TESTING ONLY" while there is no TRN.

-Environment Dev (the default): the package uploads Live to the DEV ERPNext (https://dev.quickgroc.com) from the first start;
the till refuses Live for a Dev build pointed at any other host, and keeps its data in C:\ProgramData\TillPOS-Dev. Credentials
are read from ..\publish\TillPOS.Sandbox\till-dev.json (BaseUrl, ApiKey, ApiSecret, PosProfile; local, git-ignored, never
printed). Cashiers come from the dev ERPNext's POS Cashier list ("Sandbox Cashier" 1234, "Sandbox Supervisor" 9876, written
into START HERE); the package has no local test cashiers. Folder and zip names get a "-dev" suffix.
-Environment Production: the field package as before: Upload Off, credentials from ..\publish\TillPOS.SyncCli\tillpos.cli.json
(local, git-ignored, never printed), local test cashiers. Without -CashierPin / -SupervisorPin, random 4-digit PINs are
generated with a CSPRNG and printed at the end. They are written only into the package (settings and START HERE), never into
the repo.
#>
param(
    [string]$Version = "0.4.1",
    [string]$CashierPin,
    [string]$SupervisorPin,
    [switch]$SingleExe,
    [ValidateSet("Dev", "Production")]
    [string]$Environment = "Dev"
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

$isDev   = $Environment -eq "Dev"
$devHost = "dev.quickgroc.com"   # TillSettings.DevHost: the only ERPNext a Dev build may write to
if ($isDev) {
    if ($CashierPin -or $SupervisorPin) { throw "-CashierPin / -SupervisorPin are for -Environment Production; a Dev build uses the dev ERPNext's cashiers." }
    # Not secrets: the dev ERPNext's sandbox cashiers (its POS Cashier list), named in START HERE.
    $CashierPin    = "1234"
    $SupervisorPin = "9876"
} else {
    $CashierPin    = Resolve-Pin $CashierPin "Cashier" $SupervisorPin
    $SupervisorPin = Resolve-Pin $SupervisorPin "Supervisor" $CashierPin
    if ($CashierPin -eq $SupervisorPin) { throw "The cashier and supervisor PINs must be different." }
}
$suffix     = if ($isDev) { "-dev" } else { "" }
$dataFolder = if ($isDev) { "C:\ProgramData\TillPOS-Dev" } else { "C:\ProgramData\TillPOS" }

$tillposRoot = Split-Path -Parent $PSScriptRoot
$repoRoot    = Split-Path -Parent $tillposRoot
$publishRoot = Join-Path $repoRoot "publish"
$fieldRoot   = Join-Path $publishRoot "TillPOS-field$suffix"
$appOut      = Join-Path $fieldRoot "TillPOS"
$zipPath     = Join-Path $publishRoot ("TillPOS-field-{0}{1}.zip" -f $Version, $suffix)
$exeRoot     = Join-Path $publishRoot ("TillPOS-exe-{0}{1}" -f $Version, $suffix)
$exeGuide    = "TillPOS $Version$suffix - START HERE.txt"
$tempName    = "packaged-settings-{0}.json" -f [guid]::NewGuid().ToString("N")
$tempSettings = Join-Path $publishRoot $tempName
$credJson    = if ($isDev) { Join-Path $publishRoot "TillPOS.Sandbox\till-dev.json" } else { Join-Path $publishRoot "TillPOS.SyncCli\tillpos.cli.json" }
$credName    = Split-Path -Leaf $credJson
$startHere   = Join-Path $PSScriptRoot "field\START HERE.txt"

# Refuse to run unless the output paths are git-ignored (the package holds credentials).
Push-Location $repoRoot
try {
    foreach ($path in "publish/TillPOS-field$suffix/TillPOS/settings.json", "publish/TillPOS-exe-$Version$suffix/TillPOS.exe", "publish/$tempName") {
        & git check-ignore -q $path
        if ($LASTEXITCODE -ne 0) { throw "$path is not git-ignored; refusing to build a package that contains credentials." }
    }
} finally { Pop-Location }

if (-not (Test-Path $credJson)) { throw "Missing $credJson" }
if (-not (Test-Path $startHere)) { throw "Missing $startHere" }
$cli = Get-Content -Raw -Path $credJson -ErrorAction Stop | ConvertFrom-Json
foreach ($f in "BaseUrl","ApiKey","ApiSecret","PosProfile") {
    if ([string]::IsNullOrWhiteSpace($cli.$f)) { throw "$credName has no $f" }
}
if ($isDev) {
    # The dev credentials must belong to the dev ERPNext (the till would refuse Live with any other host anyway).
    $credHost = try { ([Uri]$cli.BaseUrl.Trim()).Host } catch { "" }
    if ($credHost -ne $devHost) { throw "$credName is for '$credHost', not $devHost; refusing to build a Dev package with it." }
}

$settings = [ordered]@{
    Environment         = $Environment
    BaseUrl             = $(if ($isDev) { "https://$devHost/" } else { $cli.BaseUrl })
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
    SetupDone           = $false
    SampleQr            = $true
    # Production field packages never write to ERPNext (Off; the test build refuses Live). Dev packages upload Live from the
    # first start, to the dev ERPNext only.
    Upload              = $(if ($isDev) { "Live" } else { "Off" })
    # The counters a cashier can open a shift at (the first is the default). "Test Counter" rounds nothing (its POS Profile
    # disables the rounded total); "Al Ain Counter 1" rounds cash to 0.25. If the test API user cannot read a counter's
    # POS Profile, that counter shows as "Not available" on the Open Shift screen.
    Counters            = @(
        [ordered]@{ PosProfile = "Test Counter";     Label = "Test Counter"; CashMode = "Cash Counter 2"; CardMode = "Credit Card" },
        [ordered]@{ PosProfile = "Al Ain Counter 1"; Label = "Counter 1";    CashMode = "Cash Counter 1"; CardMode = "Credit Card" }
    )
}
if ($isDev) {
    # The dev ERPNext's third counter. Its cashiers come from its POS Cashier list: no local test cashiers.
    $settings.Counters += [ordered]@{ PosProfile = "Al Ain Counter 2"; Label = "Counter 2"; CashMode = "Cash Counter 2"; CardMode = "Credit Card" }
} else {
    $settings.LocalTestCashiers = @(
        [ordered]@{ Id = "cashier1";    Name = "Test Cashier";    Pin = $CashierPin;    IsSupervisor = $false },
        [ordered]@{ Id = "supervisor1"; Name = "Test Supervisor"; Pin = $SupervisorPin; IsSupervisor = $true }
    )
}
$json = $settings | ConvertTo-Json -Depth 5
$utf8 = New-Object System.Text.UTF8Encoding($false)

# START HERE.txt in the repo is a template; the package gets the filled copy. Lines starting with #ZIP# / #EXE# belong to
# one package kind only, lines starting with #DEV# / #PROD# to one environment only (tags can be combined, e.g. #DEV##ZIP#).
function Get-Guide([string]$kind, [string]$label, [string]$shownVersion) {
    $allowed = @($kind, $(if ($isDev) { "DEV" } else { "PROD" }))
    $lines = foreach ($line in ([System.IO.File]::ReadAllText($startHere) -split "`r?`n")) {
        $rest = $line
        $keep = $true
        while ($rest -match '^#(ZIP|EXE|DEV|PROD)#') {
            if ($allowed -notcontains $Matches[1]) { $keep = $false }
            $rest = $rest.Substring($Matches[0].Length)
        }
        if ($keep) { $rest }
    }
    $guide = ($lines -join "`r`n")   # Notepad-friendly line endings
    $guide = $guide.Replace("{{VERSION}}", $shownVersion).Replace("{{PACKAGE_KIND}}", $label).Replace("{{DATA_FOLDER}}", $dataFolder)
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
if ($isDev) {
    Write-Host "Dev package: uploads Live to https://$devHost/ only; data in $dataFolder."
    Write-Host "Cashiers come from the dev ERPNext (also in its START HERE):"
    Write-Host "  Sandbox Cashier:    $CashierPin"
    Write-Host "  Sandbox Supervisor: $SupervisorPin"
} else {
    Write-Host "Test PINs in this package (also in its START HERE):"
    Write-Host "  Cashier:    $CashierPin"
    Write-Host "  Supervisor: $SupervisorPin"
}
