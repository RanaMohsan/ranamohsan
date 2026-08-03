(()=>{
  const $=id=>document.getElementById(id);
  let passwordConfigured=false;
  async function ensureOwner(){
    const me=await api.get('/api/me');
    if(!(me.isPlatformOwner||me.IsPlatformOwner)){location.href='/workspace.html';throw new Error('PayNex Owner access is required.');}
    $('who').textContent=`${me.displayName||me.DisplayName||'PayNex Owner'} | Platform Super Admin | Confidential security settings`;
    $('testEmail').value=me.email||me.Email||localStorage.getItem('paynex_last_email')||'';
  }
  function setBusy(button,on,label){if(!button)return;button.disabled=on;if(label)button.textContent=on?label:button.dataset.label;}
  function payload(){return{
    fromEmail:$('fromEmail').value.trim(),fromName:$('fromName').value.trim(),smtpHost:$('smtpHost').value.trim()||'smtp.gmail.com',smtpPort:Number($('smtpPort').value||587),smtpUser:$('smtpUser').value.trim()||$('fromEmail').value.trim(),smtpPassword:$('smtpPassword').value||null,clearStoredPassword:$('clearPassword').checked,enableSsl:$('enableSsl').checked,returnDevOtp:$('returnDevOtp').checked,enableLoginOtp:!!$('enableLoginOtp')?.checked,loginOtpExpiryMinutes:Number($('otpMinutes').value||10),trustedDeviceDays:Number($('trustedDays').value||30)
  };}
  async function load(){
    const x=await api.get('/api/platform/security/email-settings');
    $('fromEmail').value=x.fromEmail||'';$('fromName').value=x.fromName||'PayNex Cloud ERP';$('smtpHost').value=x.smtpHost||'smtp.gmail.com';$('smtpPort').value=x.smtpPort||587;$('smtpUser').value=x.smtpUser||'';$('enableSsl').checked=x.enableSsl!==false;$('returnDevOtp').checked=!!x.returnDevOtp;if($('enableLoginOtp'))$('enableLoginOtp').checked=x.enableLoginOtp!==false;$('otpMinutes').value=x.loginOtpExpiryMinutes||10;$('trustedDays').value=x.trustedDeviceDays||30;
    passwordConfigured=!!x.passwordConfigured;$('passwordState').textContent=passwordConfigured?'An App Password is stored securely. Leave the field blank to keep it.':'No App Password is stored yet.';$('passwordState').className='password-state '+(passwordConfigured?'ok':'muted');
    msg('status',x.isDatabaseConfigured?'Owner-managed database configuration loaded.':'Application configuration loaded. Save to create owner-managed settings.',true);
  }
  async function save(){
    const buttons=[$('saveBtn'),$('saveBtnBottom')];buttons.forEach(b=>setBusy(b,true,'Saving...'));
    try{const r=await api.put('/api/platform/security/email-settings',payload());$('smtpPassword').value='';$('clearPassword').checked=false;msg('status',r.message,true);await load();}
    catch(e){msg('status',e.message,false)}finally{buttons.forEach(b=>setBusy(b,false))}
  }
  async function test(){
    const buttons=[$('testBtn'),$('sendTestBtn')];buttons.forEach(b=>setBusy(b,true,'Sending...'));
    try{
      const r=await api.post('/api/platform/security/email-settings/test',{toEmail:$('testEmail').value.trim()});
      const code=r.testOtp||r.otpCode||'';
      msg('testStatus',code?`Test OTP sent: ${code}. Open the email and confirm the same code arrived.`:(r.message||'Test email sent.'),true);
      if($('status')&&code) msg('status',`Test OTP ${code} emailed to ${$('testEmail').value.trim()}.`,true);
    }
    catch(e){msg('testStatus',e.message,false)}finally{buttons.forEach(b=>setBusy(b,false))}
  }
  async function init(){
    [$('saveBtn'),$('saveBtnBottom'),$('testBtn'),$('sendTestBtn')].forEach(b=>b.dataset.label=b.textContent);
    $('saveBtn').onclick=save;$('saveBtnBottom').onclick=save;$('testBtn').onclick=test;$('sendTestBtn').onclick=test;
    try{await ensureOwner();await load();}catch(e){msg('status',e.message,false)}
  }
  init();
})();
