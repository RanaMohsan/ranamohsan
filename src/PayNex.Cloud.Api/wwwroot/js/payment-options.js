/** Shared Cash / Bank payment helpers for POS and Picture Sales. */
window.PayNexPayment = (() => {
  let options = { banks: [], cashAccount: '1000', defaultBankAccount: '1010', paymentMethods: [] };

  async function load(){
    try{
      options = await api.get('/api/pos/payment-options');
      options.banks = options.banks || options.Banks || [];
      options.paymentMethods = options.paymentMethods || options.PaymentMethods || [];
      options.cashAccount = options.cashAccount || options.CashAccount || '1000';
      options.defaultBankAccount = options.defaultBankAccount || options.DefaultBankAccount || '1010';
    }catch{
      options = { banks: [], cashAccount: '1000', defaultBankAccount: '1010', paymentMethods: [] };
    }
    return options;
  }

  function fillBankSelect(selectEl){
    if(!selectEl) return;
    const banks = options.banks || [];
    if(!banks.length){
      selectEl.innerHTML = '<option value="">No bank configured — add in Bank Accounts</option>';
      return;
    }
    selectEl.innerHTML = banks.map(b => {
      const id = b.bankAccountId || b.BankAccountId;
      const code = b.bankCode || b.BankCode || '';
      const name = b.bankName || b.BankName || '';
      const acct = b.accountNo || b.AccountNo || '';
      return `<option value="${id}" data-account="${acct}">${code} — ${name} (${acct})</option>`;
    }).join('');
  }

  function toggleBankRow(methodSelectId, bankWrapId){
    const method = document.getElementById(methodSelectId)?.value || 'Cash';
    const wrap = document.getElementById(bankWrapId);
    if(wrap) wrap.style.display = method === 'Bank' ? '' : 'none';
  }

  function buildPayment(methodSelectId, bankSelectId, amount){
    const method = document.getElementById(methodSelectId)?.value || 'Cash';
    const paid = Number(amount || 0);
    const methods = options.paymentMethods || [];
    const findId = name => {
      const m = methods.find(x => String(x.paymentMethodName || x.PaymentMethodName).toLowerCase() === name.toLowerCase());
      return Number(m?.paymentMethodId || m?.PaymentMethodId || 0);
    };

    if(method === 'Bank'){
      const bankSel = document.getElementById(bankSelectId);
      const bankId = Number(bankSel?.value || 0);
      const accountNo = bankSel?.selectedOptions?.[0]?.dataset?.account || options.defaultBankAccount;
      if(!bankId && !(options.banks || []).length){
        throw new Error('Add at least one bank account under Bank Accounts before taking bank payment.');
      }
      if(!bankId) throw new Error('Select the bank account for this payment.');
      return {
        paymentMethodId: findId('Bank') || findId('Bank Transfer') || 4,
        paymentMethodName: 'Bank',
        amount: paid,
        referenceNo: '',
        accountNo,
        bankAccountId: bankId
      };
    }

    if(method === 'Credit'){
      return {
        paymentMethodId: findId('Credit') || 5,
        paymentMethodName: 'Credit',
        amount: paid,
        referenceNo: '',
        accountNo: '',
        bankAccountId: 0
      };
    }

    return {
      paymentMethodId: findId('Cash') || 1,
      paymentMethodName: 'Cash',
      amount: paid,
      referenceNo: '',
      accountNo: options.cashAccount || '',
      bankAccountId: 0
    };
  }

  return { load, fillBankSelect, toggleBankRow, buildPayment, getOptions: () => options };
})();
