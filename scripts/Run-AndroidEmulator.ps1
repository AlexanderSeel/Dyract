[CmdletBinding()]
param(
    [string]$AndroidSdkDirectory = "C:\dev\android-sdk",
    [string]$JavaSdkDirectory = "C:\dev\jdk",
    [string]$AvdName = "Dyract_API_36",
    [ValidateRange(30, 900)]
    [int]$BootTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"

$emulator = Join-Path $AndroidSdkDirectory "emulator\emulator.exe"
$adb = Join-Path $AndroidSdkDirectory "platform-tools\adb.exe"
$appProject = Join-Path (Split-Path $PSScriptRoot -Parent) "src\Dyract.App\Dyract.App.csproj"

foreach ($path in @($emulator, $adb, $JavaSdkDirectory, $appProject)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path was not found: $path"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found on PATH."
}

function Get-RunningEmulatorSerial {
    foreach ($line in (& $adb devices)) {
        if ($line -match "^(emulator-\d+)\s+device$") {
            return $Matches[1]
        }
    }
}

$serial = Get-RunningEmulatorSerial
if (-not $serial) {
    $availableAvds = @(& $emulator -list-avds)
    if ($availableAvds -notcontains $AvdName) {
        throw "Android virtual device '$AvdName' was not found. Create it before running this script."
    }

    Write-Host "Starting Android virtual device '$AvdName'..."
    Start-Process -FilePath $emulator -ArgumentList @("-avd", $AvdName) | Out-Null
}

$deadline = (Get-Date).AddSeconds($BootTimeoutSeconds)
do {
    $serial = Get-RunningEmulatorSerial
    if ($serial) {
        $bootCompleted = (& $adb -s $serial shell getprop sys.boot_completed 2>$null).Trim()
        if ($bootCompleted -eq "1") {
            break
        }
    }

    Start-Sleep -Seconds 2
} while ((Get-Date) -lt $deadline)

if (-not $serial -or $bootCompleted -ne "1") {
    throw "The emulator did not finish booting within $BootTimeoutSeconds seconds."
}

Write-Host "Deploying Dyract.App to $serial..."
& dotnet build $appProject -t:Run -f net10.0-android `
    "-p:AndroidSdkDirectory=$AndroidSdkDirectory" `
    "-p:JavaSdkDirectory=$JavaSdkDirectory" `
    "-p:Device=$serial"

if ($LASTEXITCODE -ne 0) {
    throw "Dyract.App deployment failed with exit code $LASTEXITCODE."
}
