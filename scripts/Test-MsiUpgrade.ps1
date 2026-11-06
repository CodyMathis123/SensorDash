# Destructive installer regression: run ONLY on a disposable Windows CI runner.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'Use a disposable GitHub Actions runner for installer integration tests.' }
$root = Split-Path $PSScriptRoot -Parent
$current = (Get-ChildItem "$root/installer-output" -Filter *.msi -Recurse | Select-Object -First 1).FullName
$release = ([xml](Get-Content "$root/Directory.Build.props" -Raw)).Project.PropertyGroup
$scratch = Join-Path $env:RUNNER_TEMP 'sensordash-upgrade-test'
New-Item -ItemType Directory -Force $scratch | Out-Null
function Install-Msi($package, $operation, $label, $expectFailure = $false) {
    $log = Join-Path $scratch "$label.log"
    Write-Host ("[{0:u}] Starting {1}" -f (Get-Date).ToUniversalTime(), $label)
    # PowerShell Start-Process can obscure startup stalls. Launch directly so
    # every argument is passed separately and we get a real bounded process
    # handle even when msiexec or Restart Manager is unresponsive.
    $start = [System.Diagnostics.ProcessStartInfo]::new('msiexec.exe')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    # The test deliberately keeps the desktop process running. Disable MSI
    # Restart Manager so unattended CI cannot wait for a FilesInUse shutdown
    # response before WiX's CloseApplication action runs. The MSI itself still
    # must close that process (and StopServices still controls the collector).
    # This is CI-only: do not force-stop any app on a real owner's machine.
    foreach ($argument in @($operation, $package, '/qn', '/norestart',
                           'MSIRESTARTMANAGERCONTROL=Disable',
                           'REBOOT=ReallySuppress', '/L*v', $log)) {
        [void]$start.ArgumentList.Add([string]$argument)
    }
    Write-Host "CI MSI servicing: Windows Installer Restart Manager disabled; WiX CloseApplication remains responsible for closing the running SensorDash desktop."
    $p = [System.Diagnostics.Process]::Start($start)
    if (!$p) { throw "Unable to launch $label msiexec" }
    try {
        Write-Host "Launched $label msiexec PID $($p.Id); verbose log: $log"
        $elapsed = [System.Diagnostics.Stopwatch]::StartNew()
        while (!$p.WaitForExit(10000)) {
            Write-Host ("[{0:u}] {1} still active after {2:n0}s (PID {3})" -f
                (Get-Date).ToUniversalTime(), $label, $elapsed.Elapsed.TotalSeconds, $p.Id)
            if ($elapsed.Elapsed.TotalSeconds -ge 180) {
                Write-Host "Last MSI log entries for stuck $label (if available):"
                if (Test-Path $log) { Get-Content $log -Tail 80 -ErrorAction Continue }
                throw "$label exceeded three minutes; MSI upgrade/servicing is hung"
            }
        }
        $p.Refresh()
        Write-Host ("[{0:u}] {1} returned exit code {2} after {3:n0}s" -f
            (Get-Date).ToUniversalTime(), $label, $p.ExitCode, $elapsed.Elapsed.TotalSeconds)
        if ($expectFailure) {
            if ($p.ExitCode -ne 1603 -or !(Select-String -Path $log -SimpleMatch 'A newer version of SensorDash is already installed.')) {
                Get-Content $log -Tail 80
                throw "Downgrade was not rejected by the upgrade rule: $($p.ExitCode)"
            }
        } elseif ($p.ExitCode -ne 0) {
            Get-Content $log -Tail 80
            throw "$label failed: $($p.ExitCode)"
        }
    }
    finally {
        $p.Dispose()
    }
}
function Products {
    @(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' |
        Where-Object { $_.DisplayName -eq 'SensorDash' -and $_.Publisher -eq 'CodyMathis' })
}
function Start-Desktop($path) {
    $desktop = Start-Process $path -ArgumentList '--desktop --no-browser' -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    if ($desktop.HasExited) { throw 'Desktop fixture failed to start.' }
    return $desktop
}
function Start-Tray($path) {
    if (!(Test-Path $path)) { throw "Native tray executable missing: $path" }
    $tray = Start-Process $path -WindowStyle Hidden -PassThru
    Start-Sleep -Seconds 3
    if ($tray.HasExited) { throw 'Native tray exited before servicing test.' }
    return $tray
}

function Assert-Closed($desktop) {
    # Retain the running-desktop regression: this is checked only AFTER
    # msiexec finishes, never pre-kill the test process before servicing.
    try {
        if (!$desktop.WaitForExit(15000)) {
            throw 'MSI failed to close the running desktop dashboard.'
        }
        Write-Host "PASS: installer closed running desktop process PID $($desktop.Id)"
    } finally {
        $desktop.Dispose()
    }
}
if ((Products).Count) { throw 'Runner already has SensorDash installed.' }
$baseline = Join-Path $env:RUNNER_TEMP 'sensordash-baseline'
$previous = @(Get-ChildItem $baseline -Filter *.msi -Recurse)
if ($previous.Count -ne 1) { throw 'Expected exactly one previous Production MSI.' }
Install-Msi $previous[0].FullName '/i' 'previous-production'
$oldProducts = Products
if ($oldProducts.Count -ne 1 -or [version]$oldProducts[0].DisplayVersion -ge [version]$release.SensorDashVersion) { throw 'Baseline must be one older Production version.' }
$data = Join-Path $env:ProgramData 'SensorDash'
New-Item -ItemType Directory -Force $data | Out-Null
$marker = Join-Path $data 'upgrade-preservation-test.txt'
Set-Content $marker 'user data must survive'
$desktop = Start-Desktop "$env:ProgramFiles/SensorDash/SensorDash.exe"
Install-Msi $current '/i' 'upgrade'
Assert-Closed $desktop
$products = Products
if ($products.Count -ne 1 -or $products[0].DisplayVersion -ne $release.SensorDashVersion -or
    $products[0].PSChildName -ne "{$($release.SensorDashProductCode)}") { throw 'Upgrade did not leave exactly the expected product.' }
if ((Get-Service SensorDashCollector).Status -ne 'Running') { throw 'Upgrade did not restart collector.' }
$exe = "$env:ProgramFiles/SensorDash/SensorDash.exe"
$tray = "$env:ProgramFiles/SensorDash/SensorDash.Tray.exe"
if (!(Test-Path $tray)) { throw 'Native tray executable not installed.' }
if ((Get-Item $exe).VersionInfo.ProductVersion -notlike "$($release.SensorDashVersion)*") { throw 'Installed executable version mismatch.' }
$desktop = Start-Tray $tray
Install-Msi $current '/i' 'repeat-install'
Assert-Closed $desktop
Install-Msi $current '/fa' 'repair'
if ((Get-Service SensorDashCollector).Status -ne 'Running') { throw 'Maintenance did not restore the collector service.' }
if ((Products).Count -ne 1) { throw 'Maintenance created a duplicate registration.' }
Install-Msi $previous[0].FullName '/i' 'blocked-downgrade' $true
if ((Products).Count -ne 1) { throw 'Downgrade changed product registration.' }
$startupKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run'
$startup = Get-ItemProperty -Path $startupKey -Name 'SensorDash' -ErrorAction Stop
if (!$startup.SensorDash -or $startup.SensorDash -notmatch 'SensorDash\.Tray\.exe') {
    throw 'Native tray sign-in autostart registration missing.'
}
$startLink = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SensorDash.lnk'
if (!(Test-Path $startLink)) { throw 'Missing all-users Start Menu shortcut.' }
$trayLink = Join-Path ([Environment]::GetFolderPath('CommonPrograms')) 'SensorDash Tray.lnk'
if (!(Test-Path $trayLink)) { throw 'Native tray Start Menu shortcut missing.' }
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut($startLink)
Write-Host "Installed shortcut icon: $($shortcut.IconLocation)"
$desktop = Start-Tray $tray
Install-Msi $current '/x' 'remove-upgraded-product'
Assert-Closed $desktop
if (Get-Service SensorDashCollector -ErrorAction SilentlyContinue) { throw 'Uninstall left collector service registered.' }
if (Get-ItemProperty -Path $startupKey -Name SensorDash -ErrorAction SilentlyContinue) { throw 'Uninstall left tray startup entry.' }
if (Test-Path $startLink) { throw 'Uninstall left Start Menu shortcut.' }
if (Test-Path $trayLink) { throw 'Uninstall left native tray shortcut.' }
if (Test-Path $exe) { throw 'Uninstall left installed executable.' }
if (Test-Path $tray) { throw 'Uninstall left native tray executable.' }
if ((Products).Count -ne 0) { throw 'Uninstall left product registrations.' }
if (!(Test-Path $marker) -or (Get-Content $marker) -ne 'user data must survive') { throw 'Servicing removed user data.' }
Write-Host 'PASS: previous Production major upgrade, repeat install, repair, downgrade rejection, uninstall and data preservation'
