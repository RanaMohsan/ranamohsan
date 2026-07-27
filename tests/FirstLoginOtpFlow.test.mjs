import assert from 'node:assert/strict';
import fs from 'node:fs';

const read = relativePath => fs.readFileSync(new URL(`../${relativePath}`, import.meta.url), 'utf8');

const program = read('src/PayNex.Cloud.Api/Program.cs');
const models = read('src/PayNex.Cloud.Api/Models/Models.cs');
const authSecurity = read('src/PayNex.Cloud.Api/Services/AuthenticationSecurityService.cs');
const provisioning = read('src/PayNex.Cloud.Api/Services/CoreServices.cs');
const userCard = read('src/PayNex.Cloud.Api/wwwroot/user-card.html');
const userCardScript = read('src/PayNex.Cloud.Api/wwwroot/js/user-card.js');
const loginPage = read('src/PayNex.Cloud.Api/wwwroot/login.html');
const emailSetup = read('src/PayNex.Cloud.Api/wwwroot/owner-email-security.html');
const masterSchema = read('database/MasterSchema.sql');

// A user is saved immediately. The old pre-save email-code API and UI are gone.
for (const obsolete of [
  '/api/users/email/send-otp',
  '/api/users/email/verify',
  'SendUserEmailOtpRequest',
  'VerifyUserEmailOtpRequest',
  'EmailVerificationOtps'
]) {
  assert.equal(program.includes(obsolete), false, `${obsolete} must not remain in Program.cs`);
  assert.equal(models.includes(obsolete), false, `${obsolete} must not remain in Models.cs`);
  assert.equal(masterSchema.includes(obsolete), false, `${obsolete} must not remain in MasterSchema.sql`);
}
assert.equal(userCard.includes('id="emailCode"'), false, 'User Card must not ask for an OTP code');
assert.equal(userCardScript.includes('verifyOtp'), false, 'User Card must not run pre-save verification');

// New accounts start unverified and must complete OTP before their first authenticated session.
assert.match(provisioning, /VALUES\(@UserName,'System Admin',@Email,0,@Hash/);
assert.match(provisioning, /VALUES\(@TenantId,@CompanyCode,@UserId,@Email,@UserName,@DisplayName,@PasswordHash,0,'Admin'/);
assert.match(program, /forceOtp:\s*!directoryEmailVerified/);
assert.match(program, /forceOtp:\s*!legacyEmailVerified/);
assert.match(program, /var trusted = !forceOtp && await authSecurity\.IsTrustedDeviceAsync/);
assert.match(program, /EmailVerified=1/);

// A successful OTP always creates the configured trusted-browser record.
assert.match(program, /VerifyLoginChallengeAsync[\s\S]*TrustDeviceAsync[\s\S]*SetTrustedDeviceCookie/);
assert.equal(models.includes('public bool TrustDevice'), false, 'Trusting the browser must not be optional');
assert.match(models, /public int TrustedDeviceDays \{ get; set; \} = 30;/);

// Company admins and new tenant admins both receive credentials through the owner mailbox service.
assert.match(program, /if \(isNewUser\)[\s\S]*SendNewUserCredentialsEmailAsync/);
assert.match(provisioning, /SendNewUserCredentialsEmailAsync\([\s\S]*adminPassword/);
assert.match(authSecurity, /SendNewUserCredentialsEmailAsync[\s\S]*GetEffectiveEmailSettingsAsync\(\)[\s\S]*SendEmailAsync/);
assert.match(authSecurity, /Initial password: \{initialPassword\}/);
assert.match(emailSetup, /new-user login details/);

// The browser UI describes the same behavior and does not offer a trust bypass.
assert.match(loginPage, /expired 30-day trust period/);
assert.match(loginPage, /automatically remain trusted for 30 days/);
assert.equal(loginPage.includes('trustDevice'), false);

console.log('First-login OTP and credential-email source checks passed.');
