'use strict';
const DriverStatus=(()=>{
 const esc=v=>String(v??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 const link='<a href="https://pawnio.eu/" target="_blank" rel="noopener noreferrer">Get PawnIO</a>';
 const labels={available:'Ready',missing:'Not installed',outdated:'Update needed',unavailable:'Unavailable',unknown:'Status unknown','restart-required':'Restart needed'};
 const actions=p=>p?.state==='available'?'':(p?.state==='restart-required'?'':link);
 const warning=p=>p?.needsAttention?'<div class="help driver-warning" role="status"><strong>Hardware driver: PawnIO</strong><span>'+esc(p.message)+'</span>'+actions(p)+'</div>':'';
 const details=p=>'<p><strong>PawnIO · '+esc(p?labels[p.state]||'Status unknown':'Waiting for collector')+'</strong>'+(p?.version?' · '+esc(p.version):'')+'</p>'+
  (p?'<p>'+esc(p.message)+'</p>':'')+actions(p)+
  '<p class="note">Needed for some CPU and motherboard sensors. Other supported sensors can work without it. Install the official signed edition, then restart the collector or Windows.</p>';
 const paint=p=>{
  for(const [id,html] of [['driverWarning',warning(p)],['driverDetails',details(p)]]){
   const el=document.getElementById(id);if(el&&el.innerHTML!==html)el.innerHTML=html;
  }
 };
 return {warning,details,paint};
})();
