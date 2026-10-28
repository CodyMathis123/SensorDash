// Hardware-independent browser diagnostic contracts. C# --self-test verifies
// the forced post-gap hardware transition and trace writer with fake channels.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const code=read('src/SensorDash/wwwroot/fan-control-debug.js');
const program=read('src/SensorDash/Program.cs');
const app=read('src/SensorDash/wwwroot/app.js');
const workspace=read('src/SensorDash/wwwroot/workspace.js');
const bridge=read('src/SensorDash/ServiceBridge.cs');
const writer=read('src/SensorDash/FanPwmChannels.cs');
const persistent=read('src/SensorDash/PersistentFanProfiles.cs');
const trace=read('src/SensorDash/FanControlTrace.cs');
const now=new Date().toISOString();
const visible={textContent:''};
const actions=[],toasts=[];
const fakeDocument={getElementById:()=>visible};
const scope={document:fakeDocument,console,fetch:async url=>{
 actions.push(url);return {ok:true,json:async()=>({path:'C:\\ProgramData\\SensorDash\\logs\\fan-control.log',
 lines:[now+' [REARM] resumed after 120s',now+' [PWM] profile=rad requested=10% rpm=489']})};
},navigator:{clipboard:{writeText:async t=>{scope.copied=t;}}}};
const debug=vm.runInNewContext(code+'\nFanControlDebug;',scope);
const html=debug.render({fanControl:{diagnostics:{logPath:'C:\\ProgramData\\SensorDash\\logs\\fan-control.log',resumeRearms:2}}});
assert.match(html,/Detected resume\/rearm gaps/);
assert.match(html,/Show recent log/);
assert.match(html,/Copy recent log/);
assert.match(html,/Save recent log/);
assert.match(html,/2<\/b>/);
const button=kind=>{
 const el={dataset:{fanDebug:kind},closest:selector=>selector==='[data-fan-debug]'?el:null};
 return {target:el,preventDefault(){}};
};
(async()=>{
 assert.equal(debug.handle(button('read'),{toast:x=>toasts.push(x)}),true);
 await new Promise(setImmediate);
 assert.ok(visible.textContent.includes('REARM'));
 assert.deepEqual(actions,['/api/fan-control/debug']);
 debug.handle(button('copy'),{toast:x=>toasts.push(x)});
 await new Promise(setImmediate);
 assert.match(scope.copied,/requested=10% rpm=489/);
 assert.ok(app.includes('FanControlDebug.handle(e,'));
 assert.ok(workspace.includes('FanControlDebug.render(S)'));
 assert.ok(program.includes('"/api/fan-control/debug"'));
 assert.ok(program.includes('FanResumeSelfTest.Run()'));
 assert.ok(bridge.includes('"fanDebug" => liveFan.DebugLog()'));
 assert.ok(persistent.includes('FanResumePolicy.NeedsRearm'));
 assert.ok(persistent.includes('_writer.ForceRearm'));
 assert.ok(writer.includes('internal void ForceRearm('));
 assert.ok(trace.includes('MaxFileBytes=2*1024*1024'));
 assert.ok(trace.includes('TimeSpan.FromSeconds(60)'));
 assert.ok(!trace.includes('File.AppendAllText(_path,') || trace.includes('if(!emit)return'),
    'No uncontrollable every-cycle logging');
 console.log('PASS: bounded fan trace, diagnostics endpoint and on-demand viewer/copy');
})().catch(e=>{console.error(e);process.exitCode=1});
