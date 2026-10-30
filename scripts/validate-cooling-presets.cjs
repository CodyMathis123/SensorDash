'use strict';
const fs=require('node:fs'),path=require('node:path');
const directory=path.resolve(__dirname,'../presets/cooling');
const schema=JSON.parse(fs.readFileSync(path.join(directory,'../cooling.schema.json'),'utf8'));
// Dependency-free validator for the keywords used by this schema. Reject new
// unsupported keywords so extending the schema cannot silently weaken CI.
const keywords=new Set(['$schema','title','description','type','const','enum','required','properties','additionalProperties','items','minItems','maxItems','minLength','maxLength','minimum','exclusiveMinimum','maximum','pattern','anyOf']);
function validate(value,rule,where='$'){
 const fail=message=>{throw Error(`${where}: ${message}`);};
 for(const key of Object.keys(rule))if(!keywords.has(key))fail('unsupported schema keyword '+key);
 if(rule.const!==undefined&&value!==rule.const)fail('wrong constant');
 if(rule.enum&&!rule.enum.includes(value))fail('unsupported value');
 const matches=t=>t==='null'?value===null:t==='array'?Array.isArray(value):t==='object'?value!==null&&typeof value==='object'&&!Array.isArray(value):t==='integer'?Number.isInteger(value):t==='number'?typeof value==='number'&&Number.isFinite(value):typeof value===t;
 if(rule.type&&![].concat(rule.type).some(matches))fail('wrong type');
 if(typeof value==='string'){
  if(rule.minLength!==undefined&&value.length<rule.minLength||rule.maxLength!==undefined&&value.length>rule.maxLength)fail('invalid length');
  if(rule.pattern&&!new RegExp(rule.pattern).test(value))fail('invalid format');
 }
 if(typeof value==='number'){
  if(!Number.isFinite(value)||rule.minimum!==undefined&&value<rule.minimum||rule.exclusiveMinimum!==undefined&&value<=rule.exclusiveMinimum||rule.maximum!==undefined&&value>rule.maximum)fail('outside numeric limits');
 }
 if(Array.isArray(value)){
  if(rule.minItems!==undefined&&value.length<rule.minItems||rule.maxItems!==undefined&&value.length>rule.maxItems)fail('invalid number of items');
  if(rule.items)value.forEach((v,i)=>validate(v,rule.items,`${where}[${i}]`));
 }
 if(value!==null&&typeof value==='object'&&!Array.isArray(value)){
  for(const key of rule.required||[])if(!Object.hasOwn(value,key))fail('missing '+key);
  for(const [key,v] of Object.entries(value)){
   if(rule.properties?.[key])validate(v,rule.properties[key],`${where}.${key}`);
   else if(rule.additionalProperties===false)fail('unknown field '+key);
  }
 }
 if(rule.anyOf&&!rule.anyOf.some(r=>{try{validate(value,r,where);return true;}catch{return false;}}))fail('required field combination missing');
}
function validatePreset(p){
 validate(p,schema);
 for(const source of p.sources){const url=new URL(source.url);if(url.protocol!=='https:'||!url.hostname||url.username||url.password)throw Error('Sources must be public HTTPS URLs without credentials');}
 const curve=p.electrical.powerCurve;
 if(curve){
  if(!p.electrical.powerCurveSource)throw Error('Curve requires a source label');
  for(const point of curve)if(point.rpm==null&&point.dutyPercent==null)throw Error('Curve point needs RPM or PWM');
  let axes=0;
  for(const key of ['rpm','dutyPercent']){
   const coords=curve.map(p=>p[key]).filter(x=>x!=null);
   if(new Set(coords).size!==coords.length)throw Error('Duplicate '+key+' coordinates');
   if(coords.length>=2)axes++;
  }
  if(!axes)throw Error('Curve needs at least two points on one axis');
 }
 return p;
}
function readCatalog(dir=directory){
 const seen=new Set();
 const entries=fs.readdirSync(dir).filter(n=>n.endsWith('.json')).sort().map(name=>{
  const p=validatePreset(JSON.parse(fs.readFileSync(path.join(dir,name),'utf8')));
  if(name!==p.id+'.json'||seen.has(p.id))throw Error('Preset ID must be unique and match its filename: '+name);
  seen.add(p.id);return p;
 });
 if(!entries.length)throw Error('Catalog is empty');
 return entries;
}
module.exports={readCatalog,validatePreset};
if(require.main===module)console.log(`Validated ${readCatalog().length} cooling presets`);
