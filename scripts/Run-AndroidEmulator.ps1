[CmdletBinding()]
param(
    [string]$AndroidSdkDirectory = "C:\dev\android-sdk",
    [string]$JavaSdkDirectory = "C:\dev\jdk",
    [string]$AvdName = "Dyract_API_36",
    [switch]$InstallEmulator,
    [ValidateRange(30, 900)]
    [int]$BootTimeoutSeconds = 300
)

$ErrorActionPreference = "Stop"
$runMutex = [System.Threading.Mutex]::new($false, "Dyract.RunAndroidEmulator")

if (-not $runMutex.WaitOne(0)) {
    $runMutex.Dispose()
    throw "Dyract Android deployment is already running. Wait for it to finish before starting another instance."
}

try {

$emulator = Join-Path $AndroidSdkDirectory "emulator\emulator.exe"
$adb = Join-Path $AndroidSdkDirectory "platform-tools\adb.exe"
$sdkManager = Join-Path $AndroidSdkDirectory "cmdline-tools\latest\bin\sdkmanager.bat"
$avdManager = Join-Path $AndroidSdkDirectory "cmdline-tools\latest\bin\avdmanager.bat"
$appProject = Join-Path (Split-Path $PSScriptRoot -Parent) "src\Dyract.App\Dyract.App.csproj"

foreach ($path in @($adb, $JavaSdkDirectory, $appProject)) {
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Required path was not found: $path"
    }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK was not found on PATH."
}

if (-not (Test-Path -LiteralPath $emulator)) {
    if (-not $InstallEmulator) {
        throw "Android Emulator was not found at '$emulator'. Run this script again with -InstallEmulator."
    }

    foreach ($path in @($sdkManager, $avdManager)) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Required Android SDK tool was not found: $path"
        }
    }

    Write-Host "Installing the Android emulator and Android 36 system image..."
    $originalJavaHome = $env:JAVA_HOME
    $env:JAVA_HOME = $JavaSdkDirectory
    try {
        1..100 | ForEach-Object { "y" } | & $sdkManager "--sdk_root=$AndroidSdkDirectory" --licenses
        if ($LASTEXITCODE -ne 0) {
            throw "Accepting Android SDK licenses failed with exit code $LASTEXITCODE."
        }

        & $sdkManager "--sdk_root=$AndroidSdkDirectory" --install "emulator" "system-images;android-36;google_apis;x86_64"
        if ($LASTEXITCODE -ne 0) {
            throw "Installing the Android emulator failed with exit code $LASTEXITCODE."
        }

        & $avdManager create avd --name $AvdName --package "system-images;android-36;google_apis;x86_64" --device "pixel_7" --force
        if ($LASTEXITCODE -ne 0) {
            throw "Creating Android virtual device '$AvdName' failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        if ($null -eq $originalJavaHome) {
            Remove-Item Env:JAVA_HOME -ErrorAction SilentlyContinue
        }
        else {
            $env:JAVA_HOME = $originalJavaHome
        }
    }
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
        if ($InstallEmulator) {
            & $avdManager create avd --name $AvdName --package "system-images;android-36;google_apis;x86_64" --device "pixel_7" --force
            if ($LASTEXITCODE -ne 0) {
                throw "Creating Android virtual device '$AvdName' failed with exit code $LASTEXITCODE."
            }
        }
        else {
        throw "Android virtual device '$AvdName' was not found. Create it before running this script."
        }
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
& dotnet build $appProject -t:Run -f net10.0-android -m:1 -nodeReuse:false `
    "-p:AndroidSdkDirectory=$AndroidSdkDirectory" `
    "-p:JavaSdkDirectory=$JavaSdkDirectory" `
    "-p:Device=$serial"

if ($LASTEXITCODE -ne 0) {
    throw "Dyract.App deployment failed with exit code $LASTEXITCODE."
}
}
finally {
    $runMutex.ReleaseMutex()
    $runMutex.Dispose()
}
