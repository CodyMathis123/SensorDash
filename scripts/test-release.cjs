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
// A PR that changes shipped code or installer content must advance release identity.
if(process.env.GITHUB_EVENT_NAME==='pull_request'){
 const event=JSON.parse(fs.readFileSync(process.env.GITHUB_EVENT_PATH));
 const base=event.pull_request.base.sha,head=event.pull_request.head.sha;
 const changed=cp.execFileSync('git',['diff','--name-only',base,head],{encoding:'utf8'}).trim().split('\n');
 let previous;
 try {previous=cp.execFileSync('git',['show',`${base}:Directory.Build.props`],{encoding:'utf8',stdio:['ignore','pipe','ignore']});}
 catch {previous=cp.execFileSync('git',['show',`${base}:src/SensorDash/SensorDash.csproj`],{encoding:'utf8'});}
 const old=value(previous,'SensorDashVersion')||value(previous,'Version');
 if(changed.some(p=>/^(src\/|installer\/|Directory.Build.props$)/.test(p))){
  const a=old.split('.').map(Number);assert.ok(parts[0]>a[0]||parts[0]===a[0]&&(parts[1]>a[1]||parts[1]===a[1]&&parts[2]>a[2]),'Shipped changes require a new MSI version');
  assert.notEqual(product,value(previous,'SensorDashProductCode'),'A major upgrade requires a new ProductCode');
 }
}
console.log('PASS: MSI release identity and version policy');
