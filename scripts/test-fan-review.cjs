// Hardware-free review, layout, lock, and inherited PWM-to-tachometer tests.
'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const root=path.join(__dirname,'..');
const read=p=>fs.readFileSync(path.join(root,p),'utf8');
const src=['workspace-locks.js','fan-feedback.js','fan-control-ux.js']
 .map(n=>read('src/SensorDash/wwwroot/'+n)).join('\n');
const api=vm.runInNewContext(src+'\n({WorkspaceLocks,FanFeedback,FanControlUx});',{
  Date,document:{querySelectorAll:()=>[]},console
});
const now=new Date().toISOString();
const S={
 view:'curves',frame:{timestamp:now},
 sensors:[{id:'tach',type:'Fan',name:'Radiator RPM',value:734,observedAt:now}],
 settings:{
  fanControlLocked:false,
  coolingSystems:[{id:'loop',locked:false}],
  dashboards:[{id:'board',locked:true}],
  fanChannelMappings:[{channelId:'pwm',label:'Radiator bank',rpmSensorId:'tach',
    kind:'fan',verifiedAt:now}],
  fanProfiles:[{id:'profile',name:'Radiators',curveIds:['cpu'],channelIds:['pwm']}]
 },
 fanControl:{persistent:{profiles:[{id:'profile',name:'Radiators',needsReapproval:true,
  reviewReasons:['Temperature curve edited: CPU']}]}}
};
assert.equal(api.WorkspaceLocks.cooling(S,'loop'),false);
assert.equal(api.WorkspaceLocks.dashboard(S,'board'),true);
assert.equal(api.WorkspaceLocks.fan(S),false);
assert.equal(api.FanControlUx.profileSummary(S),'1 enabled · 1 awaiting reapproval');
S.fanControl.persistent.profiles[0].needsReapproval=false;
assert.equal(api.FanControlUx.profileSummary(S),'1 enabled');
S.settings.fanProfiles.push({id:'second',name:'Case fans'});
assert.equal(api.FanControlUx.profileSummary(S),'1 enabled · 1 not enabled');
S.fanControl.persistent.profiles.push({id:'second',name:'Case fans'});
assert.equal(api.FanControlUx.profileSummary(S),'2 enabled');
S.fanControl.persistent.profiles.pop();
S.settings.fanProfiles.pop();
S.fanControl.persistent.profiles[0].needsReapproval=true;
let banner=api.FanControlUx.banner(S);
assert.match(banner,/Saved changes not yet applied to live fans/);
assert.match(banner,/Approve saved changes/);
assert.match(banner,/Temperature curve edited/);
assert.match(banner,/data-approval-only="true"/);
const linked=api.FanControlUx.curve(S,{id:'cpu',name:'CPU'});
assert.match(linked,/SAVED CHANGES PENDING/);
assert.match(linked,/Radiators/);
assert.match(api.FanControlUx.section(S,'curves','Temperature curves','Two curves','body'),/data-fan-fold="curves"/);
assert.match(api.FanFeedback.render(S,['pwm']),/Radiator bank/);
assert.match(api.FanFeedback.render(S,['pwm']),/734 RPM/);
assert.equal(api.FanFeedback.reading(S,'pwm').mapped,true);
assert.equal(api.FanFeedback.label(S,'pwm'),'Radiator bank');
S.settings.fanChannelMappings[0].measurements=[
 {dutyPercent:10,rpm:490},{dutyPercent:40,rpm:1050},{dutyPercent:100,rpm:1980}];
S.fanControl.persistent.readings=[{id:'profile',targetPercent:40,state:'running'}];
assert.equal(api.FanFeedback.target(S,'profile','pwm').percent,40);
assert.equal(api.FanFeedback.estimate(S,'pwm',40).text,'≈1,050 RPM');
let profileFeedback=api.FanFeedback.render(S,['pwm'],'profile');
assert.match(profileFeedback,/Target PWM/);
assert.match(profileFeedback,/Estimated RPM/);
assert.match(profileFeedback,/Measured RPM/);
assert.match(profileFeedback,/≈1,050 RPM/);
S.fanControl.persistent.readings[0].targetPercent=25;
assert.equal(api.FanFeedback.estimate(S,'pwm',25).text,'≈770 RPM',
  'Calibrated physical fan interpolation follows live MAX profile target');
S.settings.fanChannelMappings[0].measurements=[
 {dutyPercent:70,rpm:1474},{dutyPercent:85,rpm:1718},{dutyPercent:100,rpm:1962}];
S.fanControl.persistent.readings[0].targetPercent=10;
assert.equal(api.FanFeedback.estimate(S,'pwm',10).text,'Not calibrated at 10%',
  'High-duty-only calibration cannot estimate low-duty fan RPM');
S.settings.fanChannelMappings[0].cutoff={
 dutyPercent:10,observedAt:new Date().toISOString(),temperatures:[]};
assert.equal(api.FanFeedback.estimate(S,'pwm',10).text,'0 RPM observed at 10%',
  'Observed zero RPM must be visible as a fan-stop event, not extrapolated from running speeds');
assert.match(api.FanFeedback.estimate(S,'pwm',10).hint,/not a tested startup/i);
assert.equal(api.FanFeedback.estimate(S,'pwm',5).text,'Below observed stop',
  'No RPM estimate may be invented for speeds below an observed fan stop');
S.settings.fanChannelMappings[0].cutoff=null;
assert.match(api.FanFeedback.render(S,['pwm'],'profile'),/Not calibrated at 10%/);
const priorActiveProfiles=S.fanControl.persistent.profiles;
S.fanControl.persistent.profiles=[];
assert.equal(api.FanFeedback.target(S,'profile','pwm'),null,
  'Stopped profile must not present stale curve targets as live PWM');
S.fanControl.persistent.profiles=priorActiveProfiles;
S.sensors[0].value=undefined;
assert.equal(api.FanFeedback.reading(S,'pwm').text,'RPM unavailable');
S.settings.fanControlLocked=true;
assert.equal(api.WorkspaceLocks.fan(S),true);
assert.doesNotMatch(api.FanControlUx.banner(S),/data-work="start-profile"/);
assert.match(api.FanControlUx.banner(S),/Unlock Fan Control/);
assert.match(api.WorkspaceLocks.action('toggle-fan-lock',true),/Unlock editing/);
assert.match(api.WorkspaceLocks.readonly('Cooling System'),/editing locked/);
const workspace=read('src/SensorDash/wwwroot/workspace.js');
const app=read('src/SensorDash/wwwroot/app.js');
const html=read('src/SensorDash/wwwroot/index.html');
const program=read('src/SensorDash/Program.cs');
const profiles=read('src/SensorDash/PersistentFanProfiles.cs');
const model=read('src/SensorDash/WorkspaceModels.cs');
assert.ok(workspace.includes('FanFeedback.render(S,p.channelIds,p.id)'));
assert.ok(!workspace.includes('data-curve-estimated-rpm'),
  'Reusable temperature curves must not display RPM for an unassigned fan');
assert.ok(!workspace.includes('Optional related tachometer/control reading'));
assert.ok(workspace.includes('toggle-cooling-lock')&&workspace.includes('toggle-fan-lock'));
assert.ok(workspace.includes('data-fan-unsaved'));
assert.ok(app.includes('FanFeedback.paint(S)'));
assert.ok(html.includes('/fan-feedback.js')&&html.includes('/fan-control-ux.js')&&
 html.includes('/workspace-locks.js'));
assert.ok(program.includes('FanProfileReviewSelfTest.Run()')&&
 program.includes('/fan-feedback.js'));
assert.ok(program.includes('Results.Ok(control.Snapshot(state.Settings))'));
assert.ok(profiles.includes('FanProfileReview.Compare'));
assert.ok(model.includes('FanCommissioningRules.Normalize'));
console.log('PASS: pending approval warnings, lock safety, collapsibles, and inherited physical PWM/RPM monitoring');
