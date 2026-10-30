'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const read=name=>fs.readFileSync(path.join(__dirname,'../src/SensorDash/wwwroot',name),'utf8');
const requests=[],timers=new Map(),fields=[];
let timerId=0,renders=0,server;
const status={textContent:''},retry={hidden:true};
const context={
 CoolingInsights:{roles:[]},WorkspaceLocks:{cooling:(S,id)=>!!S.settings.coolingSystems.find(s=>s.id===id)?.locked},
 document:{querySelectorAll:selector=>selector==='[data-cooling-save]'?[status]:
   selector==='[data-cooling-retry]'?[retry]:fields.filter(f=>selector.includes(f.selector))},
 setTimeout:fn=>{timers.set(++timerId,fn);return timerId;},clearTimeout:id=>timers.delete(id),
 fetch:(_url,opts)=>new Promise((resolve,reject)=>requests.push({body:JSON.parse(opts.body),resolve,reject})),
 console
};
const workspace=vm.runInNewContext(read('workspace.js')+'\nSDWorkspace;',context);
const S={view:'cooling',settings:{coolingSystems:[{id:'loop',name:'Loop',kind:'liquid',components:[
 {id:'pump',kind:'pump',name:'Pump',sensorIds:[]}]}],fanProfiles:[{id:'live',minimumPercent:70}]}};
const h={render:()=>renders++,toast:()=>{}};
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const part=()=>S.settings.coolingSystems[0].components[0];
function field(selector,dataset,value,tagName='INPUT'){
 const f={selector,dataset,value,tagName,validity:{valid:true},closest:()=>f};fields.push(f);return f;
}
const rpm=field('[data-electrical-field]',{electricalField:'rpmSensorId',electricalScope:'component:loop:pump'},'tach1','SELECT');
const name=field('[data-component-name]',{componentName:'component:loop:pump'},'Pump');
const edit=(f,type='change')=>workspace.handleCoolingInput({target:f,type},S,h);
async function succeed(index){
 const request=requests[index];server=structuredClone(request.body);
 request.resolve({ok:true,json:async()=>structuredClone(server)});await tick();
}
(async()=>{
 edit(rpm);await tick();assert.equal(requests.length,1);
 assert.equal(requests[0].body.coolingSystems[0].components[0].electrical.rpmSensorId,'tach1');
 assert.equal(status.textContent,'Saving…');assert.equal(renders,0);
 name.value='New pump';edit(name,'input');assert.equal(timers.size,1);
 await succeed(0);assert.equal(part().name,'New pump','Older response cannot discard typing');
 assert.equal(workspace.hasPendingCooling(S),true);
 // Navigation flushes the debounce; the save completes even after leaving Cooling.
 const navigation=workspace.flushCooling(S);S.view='overview';await tick();
 assert.equal(timers.size,0);assert.equal(requests.length,2);
 await succeed(1);await navigation;
 assert.equal(server.coolingSystems[0].components[0].name,'New pump');
 assert.equal(status.textContent,'Saved');assert.equal(workspace.hasPendingCooling(S),false);
 S.view='cooling';rpm.value='tach2';edit(rpm);await tick();
 rpm.value='tach3';edit(rpm);await tick();assert.equal(requests.length,3,'Only one request in flight');
 await succeed(2);assert.equal(part().electrical.rpmSensorId,'tach3');assert.equal(requests.length,4);
 await succeed(3);assert.equal(server.coolingSystems[0].components[0].electrical.rpmSensorId,'tach3');
 rpm.value='tach4';edit(rpm);await tick();requests[4].reject(Error('offline'));await tick();
 assert.match(status.textContent,/Save failed/);assert.equal(retry.hidden,false);
 assert.equal(part().electrical.rpmSensorId,'tach4');
 const retried=workspace.flushCooling(S);await tick();await succeed(5);await retried;
 assert.equal(retry.hidden,true);assert.equal(status.textContent,'Saved');
 // Another settings writer queues behind cooling and preserves the local draft.
 name.value='Final pump';edit(name,'input');
 const draft=structuredClone(S.settings);draft.aliases={tach4:'Pump tach'};
 const other=workspace.saveSettings(S,draft);await tick();await succeed(6);await other;
 assert.equal(server.aliases.tach4,'Pump tach');
 assert.equal(server.coolingSystems[0].components[0].name,'Final pump');
 assert.deepEqual(server.fanProfiles,[{id:'live',minimumPercent:70}],'Autosave never approves fan profiles');
 S.settings.coolingSystems[0].locked=true;rpm.value='blocked';edit(rpm);await tick();
 assert.equal(requests.length,7);assert.equal(part().electrical.rpmSensorId,'tach4');
 S.settings.coolingSystems[0].locked=false;
 const add=field('select[data-add-to]',{addTo:'component:loop:pump'},'tach4','SELECT');
 edit(add);await tick();await succeed(7);
 assert.deepEqual(Array.from(part().sensorIds),['tach4']);assert.equal(add.value,'');
 const quantity=field('[data-electrical-field]',{electricalField:'quantity',electricalScope:'component:loop:pump'},'-1');
 quantity.validity.valid=false;edit(quantity,'input');await tick();assert.equal(requests.length,8);
 assert.match(read('app.js'),/beforeunload/);
 assert.match(read('app.js'),/handleCoolingInput\(e,S/);
 console.log('PASS: cooling autosave, debounce, navigation, serialized writes, stale responses, failures/retry, locks and sensor selection');
})().catch(error=>{console.error(error);process.exitCode=1;});
