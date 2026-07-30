param(
    [Parameter(Mandatory = $true)]
    [string]$PlanPath
)

$ErrorActionPreference = 'Stop'
$script:Choice = 'install'

function Write-UpdateLog([string]$Message) {
    try {
        $stamp = (Get-Date).ToUniversalTime().ToString('o')
        Add-Content -LiteralPath $script:Plan.LogPath -Value "$stamp [AUTO Battle Updater] $Message" -Encoding UTF8
    } catch {
        # A missing log must never prevent the game from being restarted.
    }
}

function Write-UpdateState([string]$Reason) {
    try {
        $state = [ordered]@{
            ignoredVersion = $script:Plan.TargetVersion
            reason = $Reason
            recordedUtc = (Get-Date).ToUniversalTime().ToString('o')
        } | ConvertTo-Json
        $parent = Split-Path -Parent $script:Plan.StatePath
        New-Item -ItemType Directory -Force -Path $parent | Out-Null
        Set-Content -LiteralPath $script:Plan.StatePath -Value $state -Encoding UTF8
    } catch {
        Write-UpdateLog "Could not record updater state: $($_.Exception.Message)"
    }
}

function Show-UpdateNotice {
    try {
        Add-Type -AssemblyName System.Windows.Forms
        Add-Type -AssemblyName System.Drawing
        $form = New-Object System.Windows.Forms.Form
        $form.Text = 'Legend of Keepers ? AUTO Battle update'
        $form.StartPosition = 'CenterScreen'
        $form.Size = New-Object System.Drawing.Size(530, 188)
        $form.FormBorderStyle = 'FixedDialog'
        $form.MaximizeBox = $false
        $form.MinimizeBox = $false
        $form.TopMost = $true
        $form.ShowInTaskbar = $true

        $title = New-Object System.Windows.Forms.Label
        $title.Text = "A new AUTO Battle version v$($script:Plan.TargetVersion) was found."
        $title.Font = New-Object System.Drawing.Font('Segoe UI', 11, [System.Drawing.FontStyle]::Bold)
        $title.AutoSize = $true
        $title.Location = New-Object System.Drawing.Point(22, 18)
        $form.Controls.Add($title)

        $text = New-Object System.Windows.Forms.Label
        $text.Text = 'The game will close, verify the official update, and restart automatically. No saves or game files are changed.'
        $text.Font = New-Object System.Drawing.Font('Segoe UI', 9)
        $text.Size = New-Object System.Drawing.Size(480, 42)
        $text.Location = New-Object System.Drawing.Point(24, 49)
        $form.Controls.Add($text)

        $status = New-Object System.Windows.Forms.Label
        $status.Text = 'Installing automatically in 6 seconds?'
        $status.Font = New-Object System.Drawing.Font('Segoe UI', 9)
        $status.AutoSize = $true
        $status.Location = New-Object System.Drawing.Point(24, 109)
        $form.Controls.Add($status)

        $install = New-Object System.Windows.Forms.Button
        $install.Text = 'Update now'
        $install.Size = New-Object System.Drawing.Size(110, 28)
        $install.Location = New-Object System.Drawing.Point(270, 126)
        $install.Add_Click({ $script:Choice = 'install'; $form.Close() })
        $form.Controls.Add($install)

        $skip = New-Object System.Windows.Forms.Button
        $skip.Text = 'Skip this version'
        $skip.Size = New-Object System.Drawing.Size(130, 28)
        $skip.Location = New-Object System.Drawing.Point(385, 126)
        $skip.Add_Click({ $script:Choice = 'skip'; $form.Close() })
        $form.Controls.Add($skip)

        $remaining = 6
        $timer = New-Object System.Windows.Forms.Timer
        $timer.Interval = 1000
        $timer.Add_Tick({
            $remaining--
            if ($remaining -le 0) {
                $script:Choice = 'install'
                $timer.Stop()
                $form.Close()
            } else {
                $status.Text = "Installing automatically in $remaining seconds?"
            }
        })
        $form.Add_Shown({ $timer.Start() })
        [void][System.Windows.Forms.Application]::Run($form)
        $timer.Dispose()
    } catch {
        Write-UpdateLog "Update notification could not be displayed: $($_.Exception.Message)"
        Start-Sleep -Seconds 4
    }
}

function Test-Sha256([string]$Path, [string]$Expected) {
    return ((Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Expected.ToUpperInvariant())
}

function Restart-Game {
    try {
        if (Test-Path -LiteralPath $script:Plan.GameExecutable) {
            Start-Process -FilePath $script:Plan.GameExecutable -WorkingDirectory $script:Plan.GameRoot
            Write-UpdateLog 'Legend of Keepers restarted.'
        } else {
            Write-UpdateLog "Game executable not found: $($script:Plan.GameExecutable)"
        }
    } catch {
        Write-UpdateLog "Could not restart the game: $($_.Exception.Message)"
    }
}

try {
    $expectedPluginDirectory = Join-Path $script:Plan.GameRoot 'BepInEx\plugins\LegendOfKeepers.BattleEventInspector'
    if ([IO.Path]::GetFullPath($script:Plan.PluginDirectory) -ne [IO.Path]::GetFullPath($expectedPluginDirectory) -or $script:Plan.PluginFileName -ne 'LegendOfKeepers.BattleEventInspector.dll') {
        throw 'Update plan requested a path outside the AUTO Battle plugin folder.'
    }

    $script:Plan = Get-Content -LiteralPath $PlanPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if (-not $script:Plan.TargetVersion -or -not $script:Plan.PackageUrl -or -not $script:Plan.PackageSha256) {
        throw 'Update plan is incomplete.'
    }

    Show-UpdateNotice
    try { Wait-Process -Id ([int]$script:Plan.ProcessId) -ErrorAction SilentlyContinue } catch { }

    if ($script:Choice -eq 'skip') {
        Write-UpdateState 'user-skipped'
        Write-UpdateLog "User skipped v$($script:Plan.TargetVersion)."
        Restart-Game
        exit 0
    }

    $updateRoot = Join-Path $script:Plan.GameRoot 'BepInEx\cache\LegendOfKeepers-AutoBattle\updates'
    $staging = Join-Path $updateRoot ("v" + $script:Plan.TargetVersion + '-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    New-Item -ItemType Directory -Force -Path $updateRoot | Out-Null

    Write-UpdateLog "Downloading v$($script:Plan.TargetVersion) from the official GitHub release."
    Invoke-WebRequest -Uri $script:Plan.PackageUrl -OutFile $zipPath -UseBasicParsing
    if (-not (Test-Sha256 $zipPath $script:Plan.PackageSha256)) {
        throw 'Downloaded update package SHA-256 did not match the published manifest.'
    }

    Expand-Archive -LiteralPath $zipPath -DestinationPath $staging -Force
    $packageManifestPath = Join-Path $staging 'update-manifest.json'
    $dllPath = Join-Path $staging $script:Plan.PluginFileName
    if (-not (Test-Path -LiteralPath $packageManifestPath) -or -not (Test-Path -LiteralPath $dllPath)) {
        throw 'Update package does not have the expected manifest and plugin DLL layout.'
    }

    $packageManifest = Get-Content -LiteralPath $packageManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($packageManifest.version -ne $script:Plan.TargetVersion -or $packageManifest.pluginFile -ne $script:Plan.PluginFileName -or -not $packageManifest.pluginSha256) {
        throw 'Update package manifest does not match the requested version.'
    }
    if (-not (Test-Sha256 $dllPath $packageManifest.pluginSha256)) {
        throw 'Plugin DLL SHA-256 did not match its package manifest.'
    }

    New-Item -ItemType Directory -Force -Path $script:Plan.PluginDirectory | Out-Null
    $target = Join-Path $script:Plan.PluginDirectory $script:Plan.PluginFileName
    $temporaryTarget = "$target.new"
    Copy-Item -LiteralPath $dllPath -Destination $temporaryTarget -Force
    Move-Item -LiteralPath $temporaryTarget -Destination $target -Force
    Remove-Item -LiteralPath $script:Plan.StatePath -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PlanPath -Force -ErrorAction SilentlyContinue
    Write-UpdateLog "v$($script:Plan.TargetVersion) installed successfully; only the AUTO Battle plugin DLL was replaced."
} catch {
    Write-UpdateState 'apply-failed'
    Write-UpdateLog "Update failed: $($_.Exception.Message). The existing mod DLL was left unchanged."
} finally {
    Restart-Game
}
