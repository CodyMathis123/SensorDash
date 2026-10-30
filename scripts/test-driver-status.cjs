'use strict';
const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const nodes={driverWarning:{innerHTML:''},driverDetails:{innerHTML:''}};
const api=vm.runInNewContext(fs.readFileSync('src/SensorDash/wwwroot/driver-status.js','utf8')+'\nDriverStatus;',{document:{getElementById:id=>nodes[id]}});
assert.equal(api.warning(null),'');assert.match(api.details(null),/Waiting for collector/);
for(const state of ['missing','outdated','unavailable','unknown','restart-required']){
 const p={state,needsAttention:true,version:'2.2.0',message:'Helpful <message>'};
 assert.match(api.warning(p),/Helpful &lt;message&gt;/);
 assert.match(api.warning(p),/role="status"/);
 assert.equal(api.warning({...p,needsAttention:false}),'');
 if(state==='restart-required')assert.doesNotMatch(api.warning(p),/Get PawnIO/);
 else assert.match(api.warning(p),/https:\/\/pawnio.eu\//);
 api.paint(p);assert.equal(nodes.driverWarning.innerHTML,api.warning(p));
}
api.paint({state:'available',needsAttention:false,version:'2.2.0',message:'Accessible'});
assert.equal(nodes.driverWarning.innerHTML,'');assert.match(nodes.driverDetails.innerHTML,/Ready/);
assert.match(nodes.driverDetails.innerHTML,/2.2.0/);
const index=fs.readFileSync('src/SensorDash/wwwroot/index.html','utf8');
assert.ok(index.indexOf('/driver-status.js')>=0&&index.indexOf('/driver-status.js')<index.indexOf('/app.js'));
console.log('PASS: hardware driver warnings, status transitions, disabled scopes, escaping and module loading');
