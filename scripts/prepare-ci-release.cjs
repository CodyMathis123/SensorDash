'use strict';
const fs=require('node:fs'),crypto=require('node:crypto');
// Workflow run_number increases for each new PR, push or manual run. A retry
// retains the identity of the same source run; use a new run for a new package.
function identity(series,run){
 if(!/^\d+\.\d+$/.test(series)||!Number.isInteger(run)||run<1||run>65535)
  throw Error('Invalid MSI series/run number; advance the version series before exhausting the MSI build field.');
 const [major,minor]=series.split('.').map(Number);
 if(major>255||minor>255)throw Error('MSI series exceeds 255.255');
 const version=`${series}.${run}`;
 const namespace=Buffer.from('6D8F4B3B37804A7CAD1D58343B1AD2CE','hex');
 const bytes=crypto.createHash('sha1').update(namespace).update(`SensorDash/x64/${version}`).digest().subarray(0,16);
 bytes[6]=(bytes[6]&15)|80;bytes[8]=(bytes[8]&63)|128;
 const h=bytes.toString('hex').toUpperCase();
 return {version,product:`${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`};
}
if(require.main===module){
 if(process.env.GITHUB_ACTIONS!=='true')throw Error('CI release allocation requires GitHub Actions');
 const path='Directory.Build.props',source=fs.readFileSync(path,'utf8');
 const local=source.match(/<SensorDashVersion>(.*?)<\/SensorDashVersion>/)[1];
 const result=identity(local.split('.').slice(0,2).join('.'),Number(process.env.GITHUB_RUN_NUMBER));
 if(Number(result.version.split('.')[2])<=Number(local.split('.')[2]))throw Error('CI sequence must exceed the source development version.');
 fs.writeFileSync(path,source.replace(/<SensorDashVersion>.*?<\/SensorDashVersion>/,`<SensorDashVersion>${result.version}</SensorDashVersion>`)
  .replace(/<SensorDashProductCode>.*?<\/SensorDashProductCode>/,`<SensorDashProductCode>${result.product}</SensorDashProductCode>`));
 console.log(`MSI release ${result.version}; ProductCode ${result.product}; run ${process.env.GITHUB_RUN_ID}`);
}
module.exports={identity};
