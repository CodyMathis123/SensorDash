'use strict';
// Compact fan-control layout and review warnings, kept separate from the
// workspace editor and from collector-side authorization.
const FanControlUx=(()=>{
 const safe=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const active=S=>S.fanControl?.persistent?.profiles||[];
 const review=(S,id)=>active(S).find(p=>p.id===id);
 const pending=S=>active(S).filter(p=>p.needsReapproval);
 const expanded=(S,id,defaultOpen=false)=>{
  const prefs=S.fanExpanded||{};
  return Object.prototype.hasOwnProperty.call(prefs,id)?prefs[id]:defaultOpen;
 };
 const section=(S,id,title,subtitle,body,defaultOpen=false)=>{
  return '<details class="w-fan-fold" data-fan-fold="'+safe(id)+'" '+
   (expanded(S,id,defaultOpen)?'open':'')+'><summary><span class="w-fan-fold-title">'+
   safe(title)+'</span><span class="w-fan-fold-subtitle">'+safe(subtitle)+'</span></summary>'+
   '<div class="w-fan-fold-body">'+body+'</div></details>';
 };
 const button=p=>'<button class="btn" type="button" data-work="start-profile" data-id="'+
    safe(p.id)+'" data-approval-only="true">Approve saved changes</button>';
 const banner=S=>{
  const changed=pending(S);
  if(!changed.length)return '';
  return '<aside class="w-fan-review" role="status"><strong>Saved changes not yet applied to live fans</strong>'+
   '<p>The collector is still using its last administrator-approved settings. Reapprove to apply saved edits.</p>'+
   changed.map(p=>'<div class="w-fan-review-row"><span>'+safe(p.name)+
      ' · '+safe((p.reviewReasons||[]).join('; '))+'</span>'+
      (WorkspaceLocks.fan(S)?'<span class="note">Unlock Fan Control to reapprove</span>':button(p))+'</div>').join('')+
   '</aside>';
 };
 const profile=(S,p)=>{
  const live=review(S,p.id);
  if(!live?.needsReapproval)return '';
  return '<div class="w-fan-review" role="status"><strong>Pending reapproval</strong>'+
   '<p>'+safe((live.reviewReasons||[]).join('; '))+
   '. Current fans still follow the previously approved snapshot.</p>'+
   (WorkspaceLocks.fan(S)?'<p class="note">Unlock Fan Control to reapprove.</p>':button(p))+'</div>';
 };
 const curve=(S,c)=>{
  const dependents=(S.settings?.fanProfiles||[])
    .filter(p=>(p.curveIds||[]).includes(c.id))
    .map(p=>({draft:p,live:review(S,p.id)})).filter(x=>x.live);
  if(!dependents.length)return '';
  return '<div class="w-fan-curve-deps" role="status"><strong>Used by live profile'+
    (dependents.length>1?'s':'')+':</strong> '+
    dependents.map(({draft,live})=>'<span>'+safe(draft.name)+
    (live.needsReapproval?' · SAVED CHANGES PENDING':' · approved version active')+'</span>').join(' · ')+
   '<p>Editing or saving this curve does not change currently running fan speeds until the affected profile is reapproved.</p>'+
   (WorkspaceLocks.fan(S)?'<p class="note">Unlock Fan Control to reapprove.</p>':
     dependents.filter(x=>x.live.needsReapproval).map(x=>button(x.draft)).join(''))+
   '</div>';
 };
 const remindUnsaved=target=>{
  const editor=target.closest('[data-fan-curve-editor]');
  if(!editor)return;
  const indicator=editor.querySelector('[data-fan-unsaved]');
  if(indicator){indicator.hidden=false;indicator.textContent='Unsaved curve edits — save the curve, then reapprove any affected live profiles.';}
 };
 return {section,banner,profile,curve,remindUnsaved};
})();
