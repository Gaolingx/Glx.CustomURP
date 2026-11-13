param(
    [Parameter(Mandatory = $true)]
    [string]$ExecuteMethod,
    [Parameter(Mandatory = $true)]
    [string]$LogPath,
    [string]$ReportPath = (Join-Path $PSScriptRoot "SettingsRestoration.json"),
    [string]$UnityPath = "D:\Program Files\Unity\Hub\Editor\2022.3.62f1\Editor\Unity.exe",
    [switch]$Quit,
    [switch]$InteractiveEditor,
    [string[]]$AdditionalArguments = @()
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\..")).Path
$runningUnity = Get-Process -Name Unity -ErrorAction SilentlyContinue
if ($runningUnity) {
    throw "Close all Unity Editor processes before running the T1 wrapper."
}
if (-not (Test-Path -LiteralPath $UnityPath -PathType Leaf)) {
    throw "Unity executable not found: $UnityPath"
}

function Get-FileSha256([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $null
    }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-AbsolutePath([string]$Path) {
    if ([IO.Path]::IsPathRooted($Path)) {
        return [IO.Path]::GetFullPath($Path)
    }
    return [IO.Path]::GetFullPath((Join-Path $projectRoot $Path))
}

$LogPath = Get-AbsolutePath $LogPath
$ReportPath = Get-AbsolutePath $ReportPath
$backupRoot = Join-Path ([IO.Path]::GetTempPath()) ("SslT1Settings-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $backupRoot | Out-Null
$settingNames = @("GraphicsSettings.asset", "QualitySettings.asset", "ProjectSettings.asset", "EditorSettings.asset")
$snapshots = @{}
$before = @{}
foreach ($name in $settingNames) {
    $path = Join-Path $projectRoot "ProjectSettings\$name"
    $bytes = [IO.File]::ReadAllBytes($path)
    $snapshots[$name] = $bytes
    $before[$name] = Get-FileSha256 $path
    [IO.File]::WriteAllBytes((Join-Path $backupRoot $name), $bytes)
}

$unityExitCode = $null
$invocationError = $null
$startedUtc = [DateTime]::UtcNow
try {
    $unityArguments = @()
    if (-not $InteractiveEditor) {
        $unityArguments += "-batchmode"
    }
    $unityArguments += @(
        "-projectPath", ('"{0}"' -f $projectRoot),
        "-executeMethod", $ExecuteMethod,
        "-logFile", ('"{0}"' -f $LogPath)
    )
    if ($Quit) {
        $unityArguments += "-quit"
    }
    $unityArguments += $AdditionalArguments
    # Wait only for the editor, not for Unity helper processes (licensing client, Hub helpers) that
    # can outlive it; Start-Process -Wait would block until every descendant exits.
    $process = Start-Process -FilePath $UnityPath -ArgumentList $unityArguments -PassThru
    $process.WaitForExit()
    $unityExitCode = $process.ExitCode
}
catch {
    $invocationError = $_
}
finally {
    $results = @()
    $allRestored = $true
    foreach ($name in $settingNames) {
        $path = Join-Path $projectRoot "ProjectSettings\$name"
        $afterUnity = Get-FileSha256 $path
        $restoreError = $null
        try {
            [IO.File]::WriteAllBytes($path, $snapshots[$name])
        }
        catch {
            $restoreError = $_.Exception.ToString()
        }
        $restoredHash = Get-FileSha256 $path
        $restored = $restoredHash -eq $before[$name]
        if (-not $restored) {
            $allRestored = $false
        }
        $results += [ordered]@{
            file = "ProjectSettings/$name"
            beforeSha256 = $before[$name]
            afterUnitySha256 = $afterUnity
            restoredSha256 = $restoredHash
            restored = $restored
            error = $restoreError
        }
    }
    $report = [ordered]@{
        startedUtc = $startedUtc.ToString("O")
        finishedUtc = [DateTime]::UtcNow.ToString("O")
        executeMethod = $ExecuteMethod
        unityExitCode = $unityExitCode
        invocationError = if ($invocationError) { $invocationError.Exception.ToString() } else { $null }
        allSettingsRestored = $allRestored
        files = $results
    }
    [IO.File]::WriteAllText($ReportPath, ($report | ConvertTo-Json -Depth 5))
    if ($allRestored) {
        Remove-Item -LiteralPath $backupRoot -Recurse -Force
    }
}

if (-not $allRestored) {
    throw "Project settings restoration failed. See $ReportPath; byte backups remain in $backupRoot."
}
if ($invocationError) {
    throw $invocationError
}
exit $unityExitCode
