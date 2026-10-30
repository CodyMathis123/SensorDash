'use strict';
const assert=require('node:assert/strict');
const select=require('./select-msi-baseline.cjs');
const context={repo:{owner:'test',repo:'SensorDash'}};
const run={id:123,head_sha:'production',html_url:'https://example.test/run/123'};
function fixture(artifacts,runs=[run]){
 return {context,github:{rest:{actions:{
  listWorkflowRuns:async query=>{
   assert.equal(query.branch,'Production');assert.equal(query.event,'push');assert.equal(query.status,'success');
   return {data:{workflow_runs:runs}};
  },listWorkflowRunArtifacts:()=>{}
 }},paginate:async(_method,query)=>{assert.equal(query.run_id,123);return artifacts;}}};
}
(async()=>{
 for(const name of ['SensorDash-Setup-0.8.4-x64','SensorDash-Setup-0.8.249-x64.msi']){
  assert.deepEqual(await select(fixture([{id:42,name,expired:false},{id:43,name:'installer-diagnostics'}])),{runId:123,artifactId:42});
 }
 await assert.rejects(select(fixture([])),/exactly one MSI/);
 await assert.rejects(select(fixture([{id:42,name:'SensorDash-Setup-0.8.4-x64',expired:true}])),/exactly one MSI/);
 await assert.rejects(select(fixture([{id:42,name:'SensorDash-Setup-0.8.4-x64'},{id:43,name:'SensorDash-Setup-0.8.249-x64.msi'}])),/exactly one MSI/);
 await assert.rejects(select(fixture([],[])),/No successful Production/);
 console.log('PASS: raw/legacy MSI baseline selection, diagnostics excluded, missing/expired/ambiguous baselines rejected');
})().catch(error=>{console.error(error);process.exitCode=1;});
