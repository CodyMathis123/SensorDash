'use strict';
module.exports=async({github,context})=>{
 const {owner,repo}=context.repo;
 const result=await github.rest.actions.listWorkflowRuns({owner,repo,workflow_id:'windows-build.yml',branch:'Production',event:'push',status:'success',per_page:100});
 const run=result.data.workflow_runs.find(r=>r.id!==Number(process.env.GITHUB_RUN_ID));
 if(!run)throw Error('No successful Production baseline; do not skip upgrade validation.');
 const artifacts=await github.paginate(github.rest.actions.listWorkflowRunArtifacts,{owner,repo,run_id:run.id,per_page:100});
 const msi=artifacts.filter(a=>/^SensorDash-Setup-.*-x64(?:\.msi)?$/.test(a.name)&&!a.expired);
 if(msi.length!==1)throw Error(`Latest successful Production run ${run.id} must retain exactly one MSI artifact.`);
 console.log(`Upgrade baseline: ${msi[0].name}, Production commit ${run.head_sha}, run ${run.html_url}`);
 return {runId:run.id,artifactId:msi[0].id};
};
