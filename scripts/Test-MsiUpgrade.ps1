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
    Write-Host "Starting $label"
    $p = Start-Process msiexec.exe -WindowStyle Hidden -ArgumentList "$operation `"$package`" /qn /norestart /L*v `"$log`"" -PassThru
    if (!$p.WaitForExit(180000)) {
        Get-Content $log -Tail 100
        throw "$label exceeded three minutes; inspect the MSI log."
    }
    $p.Refresh()
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
function Assert-Closed($desktop) {
    if (!$desktop.WaitForExit(15000)) { throw 'MSI left the desktop dashboard running.' }
}
if ((Products).Count) { throw 'Runner already has SensorDash installed.' }
# Frozen historical authoring reproduces the released random-ProductCode 0.8.2
# bug. Current payload is intentional: this tests MSI identity/servicing, not old application behavior.
$oldSource = git -C $root show '24d127ef68043776cf6575de9ca4b27c85d998ed:installer/SensorDash.wxs' | Out-String
if ($LASTEXITCODE -ne 0) { throw 'Could not read historical installer fixture.' }
$oldPackages = @()
foreach ($n in 1,2) {
    $dir = Join-Path $scratch "old-$n"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content "$dir/old.wxs" $oldSource
    $publish = [System.Security.SecurityElement]::Escape("$root/dist")
    Set-Content "$dir/old.wixproj" @"
<Project Sdk="WixToolset.Sdk/5.0.2"><PropertyGroup><OutputType>Package</OutputType><Platform>x64</Platform><OutputName>old-$n</OutputName><DefineConstants>PublishDir=$publish</DefineConstants><OutputPath>$dir/out/</OutputPath></PropertyGroup></Project>
"@
    dotnet build "$dir/old.wixproj" -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Historical fixture build failed.' }
    $oldPackages += (Get-ChildItem "$dir/out" -Filter *.msi -Recurse | Select-Object -First 1).FullName
}
Install-Msi $oldPackages[0] '/i' 'old-first'
Install-Msi $oldPackages[1] '/i' 'old-duplicate'
if ((Products).Count -ne 2) { throw 'Fixture did not reproduce duplicate registrations.' }
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
if ((Get-Item $exe).VersionInfo.ProductVersion -notlike "$($release.SensorDashVersion)*") { throw 'Installed executable version mismatch.' }
$desktop = Start-Desktop $exe
Install-Msi $current '/i' 'repeat-install'
Assert-Closed $desktop
Install-Msi $current '/fa' 'repair'
if ((Get-Service SensorDashCollector).Status -ne 'Running') { throw 'Maintenance did not restore the collector service.' }
if ((Products).Count -ne 1) { throw 'Maintenance created a duplicate registration.' }
Install-Msi $oldPackages[0] '/i' 'blocked-downgrade' $true
if ((Products).Count -ne 1) { throw 'Downgrade changed product registration.' }
$desktop = Start-Desktop $exe
Install-Msi $current '/x' 'remove-upgraded-product'
Assert-Closed $desktop
if ((Products).Count -ne 0) { throw 'Uninstall left product registrations.' }
if (!(Test-Path $marker) -or (Get-Content $marker) -ne 'user data must survive') { throw 'Servicing removed user data.' }
Write-Host 'PASS: duplicate 0.8.2 recovery, major upgrade, repeat install, repair, downgrade rejection and data preservation'
