'use strict';
// Layout/editor locks are shared presentation preferences, not authorization.
// Privileged fan writes ALWAYS use independent administrator approval.
const WorkspaceLocks=(()=>{
 const safe=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const cooling=(S,id)=>!!S.settings?.coolingSystems?.find(s=>s.id===id)?.locked;
 const fan=S=>!!S.settings?.fanControlLocked;
 const dashboard=(S,id)=>!!S.settings?.dashboards?.find(d=>d.id===id)?.locked;
 const action=(label,locked,id)=>'<button type="button" class="btn secondary" data-work="'+
   safe(label)+'"'+(id?' data-id="'+safe(id)+'"':'')+'>'+
   (locked?'🔓 Unlock editing':'🔒 Lock editing')+'</button>';
 const readonly=label=>'<p class="note">🔒 '+safe(label)+
  ' editor locked. Readings and active hardware control continue unchanged.</p>';
 return {cooling,fan,dashboard,action,readonly};
})();
