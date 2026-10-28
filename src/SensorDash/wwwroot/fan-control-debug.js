'use strict';
// Diagnostics are read-only: no controller writes and no persistent poller.
// A user request fetches at most 250 collector-side trace lines.
const FanControlDebug=(()=>{
 const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
 let cached='',path='';
 const render=S=>{
  const d=S.fanControl?.diagnostics||{};
  path=d.logPath||'C:\\ProgramData\\SensorDash\\logs\\fan-control.log';
  return '<div class="panel fan-trace-panel">'+
   '<h3>Sleep / fan-control diagnostics</h3>'+
   '<p class="note">The collector records startup, resume-like polling gaps, approved control actions, PWM state changes, errors, and one RPM/command sample per channel per minute. No per-second log spam. This is a bounded, rotating plain-text file.</p>'+
   '<div class="w-live-status">Detected resume/rearm gaps since collector start: <b>'+
      esc(d.resumeRearms??'—')+'</b></div>'+
   '<p class="note">Collector log: <code>'+esc(path)+'</code> (up to 2 MB plus one rotated copy)</p>'+
   '<div class="w-actions"><button class="btn secondary" data-fan-debug="read">Show recent log</button>'+
   '<button class="btn secondary" data-fan-debug="copy">Copy recent log</button>'+
   '<button class="btn secondary" data-fan-debug="download">Save recent log</button></div>'+
   '<pre class="fan-trace-output" id="fanTraceOutput" aria-live="polite">'+
      esc(cached||'Select Show recent log to retrieve the latest collector diagnostics.')+'</pre>'+
   '<p class="note">The reported PWM mode/value is LibreHardwareMonitor state, not an independent physical PWM measurement. Compare the requested percentage with the mapped fan RPM after wake.</p>'+
   '</div>';
 };
 const get=async()=>{
  const response=await fetch('/api/fan-control/debug',{cache:'no-store'});
  if(!response.ok)throw Error('Collector diagnostics unavailable ('+response.status+').');
  const result=await response.json();
  path=result.path||path;
  cached=(result.lines||[]).join('\n')||'The collector has not recorded any fan diagnostics yet.';
  document.getElementById('fanTraceOutput').textContent=cached;
  return cached;
 };
 const handle=(e,h)=>{
  const el=e.target.closest('[data-fan-debug]');
  if(!el)return false;
  e.preventDefault();
  void (async()=>{
   const text=await get();
   if(el.dataset.fanDebug==='copy'){
    await navigator.clipboard.writeText(text);
    h.toast('Fan diagnostics copied.');
   }else if(el.dataset.fanDebug==='download'){
    const blob=new Blob([text+'\n'],{type:'text/plain;charset=utf-8'});
    const url=URL.createObjectURL(blob);
    try{
     const a=document.createElement('a');
     a.href=url;a.download='SensorDash-fan-control.log';
     document.body.appendChild(a);a.click();a.remove();
    }finally{URL.revokeObjectURL(url);}
   }
  })().catch(error=>h.toast(error.message));
  return true;
 };
 return {render,handle};
})();
