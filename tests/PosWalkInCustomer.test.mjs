import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const read = path => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

test('POS and Picture Sales lock checkout to protected Walk-in Customer', async () => {
  const [posHtml, pictureHtml, posJs, pictureJs, program, posSql] = await Promise.all([
    read('src/PayNex.Cloud.Api/wwwroot/pos.html'),
    read('src/PayNex.Cloud.Api/wwwroot/picture-sales.html'),
    read('src/PayNex.Cloud.Api/wwwroot/js/pos.js'),
    read('src/PayNex.Cloud.Api/wwwroot/js/picture-sales.js'),
    read('src/PayNex.Cloud.Api/Program.cs'),
    read('src/PayNex.Cloud.Api/Services/PosSql.cs')
  ]);

  assert.match(posHtml, /<select id="customer" data-no-lookup="1" disabled/);
  assert.match(pictureHtml, /<select id="customer" data-no-lookup="1" disabled/);
  assert.match(posJs, /lockCustomerToWalkIn/);
  assert.match(pictureJs, /lockCustomerToWalkIn/);
  assert.match(posJs, /customerId:\s*0/);
  assert.match(pictureJs, /customerId:\s*0/);
  assert.match(program, /var customerId = await PosSql\.GetWalkInCustomerIdAsync\(con, tran\)/);
  assert.doesNotMatch(program, /request\.CustomerId <= 0 \? await PosSql\.GetWalkInCustomerIdAsync/);
  assert.match(program, /Walk-in Customer is a protected system customer/);
  assert.match(posSql, /UPDATE Customers SET CustomerName='Walk-in Customer', IsActive=1 WHERE CustomerCode='WALKIN'/);
});
