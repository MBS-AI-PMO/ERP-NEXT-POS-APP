<#
Builds the TillPOS production release: one single TillPOS.exe per till PC, with that till's own ERPNext login built in.
Usage (from the tillpos folder):
  powershell -File tools\publish-release.ps1 -Version 1.0.0            -> ..\publish\TillPOS-1.0.0-till1\, ..\publish\TillPOS-1.0.0-till2\
  powershell -File tools\publish-release.ps1 -Version 1.0.0 -Till 2    -> only Till 2
Credentials: ..\publish\TillPOS.Production\till<N>.json ({ BaseUrl, ApiKey, ApiSecret, TillNumber }; local, git-ignored, never
printed). The package: Environment Production, BaseUrl = the production ERPNext, no local test cashiers (cashiers come from
ERPNext's POS Cashier list), no sample QR (the real TRN and tax QR come from ERPNext), Upload Off until a supervisor switches it
to Live on the Settings screen (logged as an approval). Counters: Al Ain Counter 1 and 2, the till's own counter first.
Data lives in C:\ProgramData\TillPOS. The built-in plain API secret cannot be removed from the exe: treat each exe like that
till's credentials (only on that till PC).
#>
param(
    [string]$Version = "1.0.0",
    [ValidateSet(0, 1, 2)]
    [int]$Till = 0    # 0 = every till that has a credentials file
)
$ErrorActionPreference = "Stop"

$prodHost    = "erp.quickgroc.com"
$tillposRoot = Split-Path -Parent $PSScriptRoot
$repoRoot    = Split-Path -Parent $tillposRoot
$publishRoot = Join-Path $repoRoot "publish"
$credRoot    = Join-Path $publishRoot "TillPOS.Production"
$guideFile   = Join-Path $PSScriptRoot "release\START HERE.txt"
$dotnet      = "C:\Program Files\dotnet\dotnet.exe"
if (-not (Test-Path $dotnet)) { $dotnet = "dotnet" }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "A release version is three numbers, e.g. 1.0.0 (no -test suffix)." }
if (-not (Test-Path $guideFile)) { throw "Missing $guideFile" }

# The till's own counter first (it is the default on Open Shift); the other stays available.
$counters = @{
    1 = [ordered]@{ PosProfile = "Al Ain Counter 1"; Label = "Counter 1"; CashMode = "Cash Counter 1"; CardMode = "Credit Card" }
    2 = [ordered]@{ PosProfile = "Al Ain Counter 2"; Label = "Counter 2"; CashMode = "Cash Counter 2"; CardMode = "Credit Card" }
}

function Remove-AppReleaseBuild {
    foreach ($dir in (Join-Path $tillposRoot "src\TillPOS.App\obj\Release"), (Join-Path $tillposRoot "src\TillPOS.App\bin\Release")) {
        if (Test-Path $dir) { Remove-Item -Recurse -Force $dir -ErrorAction Stop }
    }
}

$tills = if ($Till -eq 0) { 1, 2 | Where-Object { Test-Path (Join-Path $credRoot "till$_.json") } } else { @($Till) }
if (-not $tills) { throw "No credentials in $credRoot (till1.json, till2.json)." }
$utf8 = New-Object System.Text.UTF8Encoding($false)

foreach ($n in $tills) {
    $credJson = Join-Path $credRoot "till$n.json"
    if (-not (Test-Path $credJson)) { throw "Missing $credJson" }
    $cred = Get-Content -Raw -Path $credJson | ConvertFrom-Json
    foreach ($f in "BaseUrl", "ApiKey", "ApiSecret") { if ([string]::IsNullOrWhiteSpace($cred.$f)) { throw "till$n.json has no $f" } }
    $credHost = try { ([Uri]$cred.BaseUrl.Trim()).Host } catch { "" }
    if ($credHost -ne $prodHost) { throw "till$n.json is for '$credHost', not $prodHost; refusing to build a production package with it." }

    $exeRoot  = Join-Path $publishRoot ("TillPOS-{0}-till{1}" -f $Version, $n)
    $tempName = "packaged-settings-{0}.json" -f [guid]::NewGuid().ToString("N")
    $tempSettings = Join-Path $publishRoot $tempName
    # Refuse unless the outputs are git-ignored (the exe and the temp settings hold the till's credentials).
    Push-Location $repoRoot
    try {
        foreach ($path in "publish/TillPOS-$Version-till$n/TillPOS.exe", "publish/$tempName") {
            & git check-ignore -q $path
            if ($LASTEXITCODE -ne 0) { throw "$path is not git-ignored; refusing to build a package that contains credentials." }
        }
    } finally { Pop-Location }

    $own   = $counters[$n]
    $other = $counters[3 - $n]
    $settings = [ordered]@{
        Environment         = "Production"
        BaseUrl             = "https://$prodHost/"
        ApiKey              = $cred.ApiKey
        ApiSecret           = $cred.ApiSecret
        PosProfile          = $own.PosProfile
        TillNumber          = $n
        CashMode            = $own.CashMode
        CardMode            = $own.CardMode
        PrinterName         = ""
        PaperWidth          = "Mm80"
        ReceiptFooter       = "Thank you for shopping with us"
        Precision           = 3
        Rounding            = "Bankers"
        DbPath              = ""
        SyncIntervalSeconds = 90
        ShowReceiptPreview  = $true
        SetupDone           = $false
        SampleQr            = $false
        Upload              = "Off"
        Counters            = @($own, $other)
    }
    $json = $settings | ConvertTo-Json -Depth 5

    if (Test-Path $exeRoot) { Remove-Item -Recurse -Force $exeRoot -ErrorAction Stop }
    Push-Location $tillposRoot
    try {
        [System.IO.File]::WriteAllText($tempSettings, $json, $utf8)
        Remove-AppReleaseBuild
        & $dotnet publish src/TillPOS.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
            -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
            "-p:PackagedSettings=$tempSettings" "-p:Version=$Version" "-p:InformationalVersion=$Version" -o $exeRoot
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }
    } finally {
        # The settings file and the intermediate TillPOS.dll hold the plain secret; only the exe may keep it.
        if (Test-Path $tempSettings) { Remove-Item -Force $tempSettings }
        Remove-AppReleaseBuild
        Pop-Location
    }
    Get-ChildItem -Path $exeRoot -Filter *.pdb -File | Remove-Item -Force -ErrorAction Stop

    $guideName = "TillPOS $Version - Till $n - START HERE.txt"
    $guide = ([System.IO.File]::ReadAllText($guideFile) -split "`r?`n") -join "`r`n"
    $guide = $guide.Replace("{{VERSION}}", $Version).Replace("{{TILL}}", [string]$n).Replace("{{COUNTER}}", $own.PosProfile).Replace("{{OTHER_COUNTER}}", $other.PosProfile)
    if ($guide -match '\{\{[A-Z_]+\}\}') { throw "release START HERE.txt has a placeholder the script does not fill: $($Matches[0])" }
    [System.IO.File]::WriteAllText((Join-Path $exeRoot $guideName), $guide, $utf8)

    $unexpected = @(Get-ChildItem -Path $exeRoot -Force | Where-Object { $_.Name -ne "TillPOS.exe" -and $_.Name -ne $guideName })
    if ($unexpected.Count -gt 0) { throw "Unexpected files next to TillPOS.exe: $(($unexpected | ForEach-Object Name) -join ', ')" }
    $exe = Get-Item (Join-Path $exeRoot "TillPOS.exe")
    Write-Host ("Till {0}: {1} ({2} MB), default counter {3}" -f $n, $exe.FullName, [math]::Round($exe.Length / 1MB, 1), $own.PosProfile)
}
Write-Host "Production release $Version`: uploads Off until a supervisor switches them to Live in Settings; data in C:\ProgramData\TillPOS."
