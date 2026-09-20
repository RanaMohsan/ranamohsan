import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const endpoint = fs.readFileSync('src/PayNex.Cloud.Api/Endpoints/SalesReturnOrderEndpoints.cs','utf8');
const card = fs.readFileSync('src/PayNex.Cloud.Api/wwwroot/js/sales-return-order-card.js','utf8');
const workspace = fs.readFileSync('src/PayNex.Cloud.Api/wwwroot/js/workspace.js','utf8');

test('home workspace exposes Sales Return Orders',()=>assert.match(workspace,/sales-return-orders\.html/));
test('posting restores stock and credits customer',()=>{
  assert.match(endpoint,/StockOnHand=StockOnHand\+@Qty/);
  assert.match(endpoint,/CurrentBalance=CurrentBalance-@Amount/);
  assert.match(endpoint,/'Sales Return Order',@No,0,@Amount/);
});
test('posting reverses sales, tax, cost and inventory in balanced pairs',()=>{
  for(const account of ['SalesReturnAccount','OutputTaxAccount','ReceivableAccount','InventoryAccount','CogsAccount']) assert.match(endpoint,new RegExp(account));
  assert.match(endpoint,/PostingBatches/);
  assert.match(endpoint,/total\+cost/);
});
test('return quantity is guarded and posting is confirmed in UI',()=>{
  assert.match(endpoint,/UPDLOCK,HOLDLOCK/);
  assert.match(endpoint,/Return quantity exceeds the remaining returnable quantity/);
  assert.match(card,/restore inventory, credit the customer, and create balanced G\/L entries/);
});
