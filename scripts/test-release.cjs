'use strict';
const assert=require('node:assert/strict'),fs=require('node:fs'),cp=require('node:child_process');
const props=fs.readFileSync('Directory.Build.props','utf8');
const value=(s,k)=>s.match(new RegExp(`<${k}>(.*?)</${k}>`))?.[1];
const version=value(props,'SensorDashVersion'),product=value(props,'SensorDashProductCode');
assert.match(version,/^\d+\.\d+\.\d+$/);
const parts=version.split('.').map(Number);
assert.ok(parts[0]<=255&&parts[1]<=255&&parts[2]<=65535,'MSI version limits');
assert.match(product,/^[0-9A-F]{8}(?:-[0-9A-F]{4}){3}-[0-9A-F]{12}$/);
assert.equal(value(props,'SensorDashUpgradeCode'),'6D8F4B3B-3780-4A7C-AD1D-58343B1AD2CE');
assert.ok(fs.readFileSync('installer/SensorDash.wxs','utf8').includes('ProductCode="$(var.SensorDashProductCode)"'));
assert.ok(fs.readFileSync('installer/SensorDash.wixproj','utf8').includes('SensorDash-Setup-$(SensorDashVersion)-x64'));
const workflow=fs.readFileSync('.github/workflows/windows-build.yml','utf8');
const wix=fs.readFileSync('installer/SensorDash.wixproj','utf8');
assert.match(workflow,/push:\s+branches: \[Production\]/,'Production pushes must build an MSI');
assert.match(workflow,/pull_request:\s+branches: \[Production\]/,
 'Production-targeted pull requests must validate the MSI before merge');
assert.match(workflow,/workflow_dispatch:/,'Manual MSI builds must remain available');
assert.doesNotMatch(workflow,/gc_profile|SENSORDASH_GC_PROFILE|SensorDashGcMode|workstation-gc/,
 'CI must not expose legacy GC build variants');
assert.equal(fs.existsSync('.github/workflows/native-tray-development-ci.yml'),false,
 'Duplicate native-tray marker workflow must remain absent');
assert.match(workflow,/SENSORDASH_PERFORMANCE_DIAGNOSTICS/,
 'CI must opt into otherwise-disabled process diagnostics only for verification');
assert.match(workflow,/AllowUnencryptedAuthentication/,
 'Loopback Negotiate smoke test must explicitly allow HTTP Windows credentials');
assert.match(workflow,/Standard service-hosted web process must use Workstation GC/);
assert.match(workflow,/Service GC diagnostics disagree with the unified service status/);
assert.match(workflow,/SensorDash-Setup-/);
assert.match(wix,/SensorDash-Setup-\$\(SensorDashVersion\)-x64/);
assert.doesNotMatch(wix,/SensorDashMsiVariantSuffix/);
const gcProject=fs.readFileSync('src/SensorDash/SensorDash.csproj','utf8');
assert.match(gcProject,/<ServerGarbageCollection>false<\/ServerGarbageCollection>/,
 'Workstation GC must be default for every build');
assert.doesNotMatch(gcProject,/SensorDashGcMode|<ServerGarbageCollection[^>]+Condition=/,
 'There must be no selectable server/baseline GC mode');
const wxs=fs.readFileSync('installer/SensorDash.wxs','utf8');
const backend=fs.readFileSync('src/SensorDash/Program.cs','utf8');
const project=fs.readFileSync('src/SensorDash/SensorDash.csproj','utf8');
const tray=fs.readFileSync('tray/SensorDashTray.cpp','utf8');
assert.match(wxs,/Source="\$\(var.PublishDir\)\\SensorDash\.Tray\.exe"/);
assert.match(wxs,/SensorDash\.Tray\.exe&quot;/);
assert.match(wxs,/CloseSensorDashTray/);
const servicing=fs.readFileSync('scripts/Test-MsiUpgrade.ps1','utf8');
assert.doesNotMatch(servicing,/Start-Desktop|--desktop --no-browser/,
 'MSI upgrade tests must never launch the retired .NET desktop');
assert.match(servicing,/\$previousTray = /,
 'Previous Production must be tested with its installed native tray');
assert.match(servicing,/\$trayFixture = Start-Tray \$previousTray/,
 'Upgrade must exercise the running native tray process');
assert.match(servicing,/Assert-Closed \$trayFixture/,
 'The installer must terminate the running tray via its CloseApplication action');
assert.match(workflow,/Build native Win32 tray/);
assert.match(workflow,/UseDefaultCredentials/);
assert.match(backend,/AddWindowsService/);
assert.match(backend,/AddNegotiate\(\)/);
assert.match(backend,/RequireAuthorization\(\)/);
assert.match(backend,/\/api\/tray-state/);
assert.doesNotMatch(backend,/Verb="runas"/);
assert.doesNotMatch(project,/UseWindowsForms/);
assert.match(tray,/Shell_NotifyIconW/);
assert.match(tray,/WinHttpOpen/);
assert.match(tray,/AutomaticTextColor/,'Solid tray badges must automatically contrast bright accents');
assert.match(tray,/style == L"solid"/);
assert.match(tray,/style == L"outline"/);
assert.match(tray,/kSize - 4/,'Original accent-bar style must be available');
const model=fs.readFileSync('src/SensorDash/WorkspaceModels.cs','utf8');
assert.match(model,/public string Style \{ get; set; \} = "accent-bar"/);
assert.match(model,/public string TextColor \{ get; set; \} = "auto"/);
assert.match(backend,/\/api\/auth\/me/);
assert.match(backend,/\/api\/auth\/sign-in/);
assert.match(backend,/http:\/\/localhost:8765/);
assert.match(workflow,/api\/auth\/me/);
const {identity}=require('./prepare-ci-release.cjs');
assert.equal(identity('0.8',240).version,'0.8.240');
assert.notEqual(identity('0.8',240).product,identity('0.8',241).product);
assert.deepEqual(identity('0.8',240),identity('0.8',240));
assert.throws(()=>identity('0.8',65536));
assert.throws(()=>identity('256.8',1));
if(process.env.GITHUB_ACTIONS==='true'){
 const expected=identity(version.split('.').slice(0,2).join('.'),Number(process.env.GITHUB_RUN_NUMBER));
 assert.equal(version,expected.version,'CI version must use this workflow run number');
 assert.equal(product,expected.product,'CI ProductCode must match allocated version');
}
console.log('PASS: monotonic CI MSI versions and stable per-version ProductCodes');
