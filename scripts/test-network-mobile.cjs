'use strict';
// Browser/network integration guardrails. C# --self-test covers actual
// IP classification and preference persistence; this covers UI wiring.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const read=p=>fs.readFileSync(path.join(__dirname,'..',p),'utf8');
const app=read('src/SensorDash/wwwroot/app.js');
const html=read('src/SensorDash/wwwroot/index.html');
const css=read('src/SensorDash/wwwroot/style.css');
const backend=read('src/SensorDash/Program.cs');
const policy=read('src/SensorDash/DashboardNetworkOptions.cs');

assert.match(html, /name="viewport"[^>]+width=device-width/);
assert.match(css, /@media\(max-width:700px\)[\s\S]*\.sidebar #navigation\{display:flex/);
assert.match(css, /\.sidebar #navigation\{[^}]*overflow-x:auto/);
assert.match(css, /\.table-container\{[^}]*overflow-x:auto/);
assert.match(css, /\.remote-view \.sidebar nav:not\(#navigation\)\{display:none/);
assert.match(app, /fetchJSON\('\/api\/network'\)/);
assert.match(app, /#saveNetwork/);
assert.match(app, /network\.remote/);
assert.match(app, /Read-only/);
assert.match(backend, /UseUrls\(networkOptions\.LanEnabled \?/);
assert.match(backend, /DashboardNetworkOptions\.IsTrustedClient\(remoteAddress\)/);
assert.match(backend, /!DashboardNetworkOptions\.CanMutate\(remoteAddress\)/);
assert.match(backend, /app\.MapPut\("\/api\/network"/);
assert.match(policy, /public bool LanEnabled \{ get; set; \}/);
assert.match(policy, /DashboardNetworkOptionsSelfTest/);
console.log('Phone layout, network toggle and read-only boundary contracts passed');
