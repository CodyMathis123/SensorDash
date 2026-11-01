// Check relative Markdown navigation as part of the existing Node regression suite.
'use strict';
const fs = require('fs');
const path = require('path');
const root = path.resolve(__dirname, '..');
function walk(directory) {
  if (!fs.existsSync(directory)) return [];
  return fs.readdirSync(directory, {withFileTypes:true}).flatMap(item =>
    item.isDirectory() ? walk(path.join(directory,item.name)) :
    item.isFile() && item.name.endsWith('.md') ? [path.join(directory,item.name)] : []
  );
}
const docs = [
  path.join(root,'README.md'),
  path.join(root,'AGENTS.md'),
  ...walk(path.join(root,'docs')),
  ...walk(path.join(root,'presets'))
];
const broken=[];
for (const file of docs) {
  const lines=fs.readFileSync(file,'utf8').split(/\r?\n/);
  lines.forEach((line,index) => {
    const links=line.matchAll(/(?<!!)\[[^\]\n]+\]\(([^)]+)\)/g);
    for (const match of links) {
      const target=match[1].trim().split(/\s+"/)[0].replace(/^<|>$/g,'');
      if (!target || /^(?:#|[a-z][a-z0-9+.-]*:|\/\/)/i.test(target)) continue;
      let pathname;
      try { pathname=decodeURIComponent(target.split('#')[0].split('?')[0]); }
      catch (_) { broken.push(file+':'+(index+1)+' malformed URL'); continue; }
      const resolved=path.resolve(path.dirname(file),pathname);
      if (!resolved.startsWith(root+path.sep) && resolved!==root) {
        broken.push(path.relative(root,file)+':'+(index+1)+' out-of-tree link '+target);
      } else if (!fs.existsSync(resolved)) {
        broken.push(path.relative(root,file)+':'+(index+1)+' missing '+target);
      }
    }
  });
}
if (broken.length) {
  console.error('Broken local Markdown links:\n'+broken.join('\n'));
  process.exitCode=1;
} else {
  console.log('PASS: relative links in '+docs.length+' SensorDash Markdown files');
}
