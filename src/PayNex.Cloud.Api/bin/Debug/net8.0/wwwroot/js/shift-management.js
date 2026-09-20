async function init(){
  const who = document.getElementById('smWho');
  const redirectText = document.getElementById('smRedirectText');
  const redirectError = document.getElementById('smRedirectError');
  try{
    const me = await api.get('/api/me');
    if(who) who.textContent = `${me.companyName || ''} | ${me.displayName || ''} | ${me.roleName || ''}`;
  }catch{
    location.href = '/login.html?returnUrl=' + encodeURIComponent('/shift-management.html');
    return;
  }

  try{
    if(redirectText) redirectText.textContent = 'Checking whether a shift is currently open...';
    const current = await api.get('/api/shifts/current');
    const shiftId = current.shiftId || current.ShiftId;
    if(shiftId){
      location.replace('/shift-card.html?shiftId=' + encodeURIComponent(shiftId));
      return;
    }
    location.replace('/shift-list.html');
  }catch(e){
    const message = (e && e.message) ? String(e.message) : '';
    if(/no open shift/i.test(message) || /not found/i.test(message) || /404/.test(message)){
      location.replace('/shift-list.html');
      return;
    }
    if(redirectError) redirectError.textContent = message || 'Unable to load shift status.';
    if(redirectText) redirectText.textContent = 'Could not redirect automatically. Open Shift List from Workspace.';
  }
}

init();
