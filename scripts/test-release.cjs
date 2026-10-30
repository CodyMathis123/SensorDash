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
