(() => {
  const $ = id => document.getElementById(id);
  const query = new URLSearchParams(location.search);
  const packageId = Number(query.get('id') || 0);
  const val = (o, a, b) => o?.[a] ?? o?.[b];
  const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  let currentUser = null;
  let preview = null;
  let selectedFile = null;
  let validatedImportId = 0;

  const can = key => currentUser && hasPermission(currentUser, key);

  function setStep(id, state) {
    const element = $(id);
    if (!element) return;
    element.classList.remove('done', 'active', 'error');
    if (state) element.classList.add(state);
  }

  function resetValidation() {
    validatedImportId = 0;
    $('saveBtn').disabled = true;
    $('validationBadge').textContent = selectedFile ? 'Waiting for validation' : 'Not validated';
    $('validationBadge').className = 'bc-status-pill draft';
    $('validationSummary').querySelectorAll('strong').forEach(x => x.textContent = '0');
    $('issuesBody').innerHTML = '<tr><td colspan="5" class="bc-empty-row">Import an Excel file, then select Validate.</td></tr>';
    setStep('stepImport', selectedFile ? 'done' : '');
    setStep('stepValidate', selectedFile ? 'active' : '');
    setStep('stepSave', '');
  }

  function rowText(row, columns) {
    return columns.map(column => String(row?.[column] ?? '')).join(' ').toLowerCase();
  }

  function renderPreview() {
    if (!preview) return;
    const columns = val(preview, 'columns', 'Columns') || [];
    const allRows = val(preview, 'rows', 'Rows') || [];
    const term = ($('recordSearch').value || '').trim().toLowerCase();
    const rows = term ? allRows.filter(row => rowText(row, columns).includes(term)) : allRows;
    $('currentCount').textContent = `${allRows.length} record${allRows.length === 1 ? '' : 's'}`;
    $('previewHead').innerHTML = `<tr>${columns.map(column => `<th>${esc(column)}</th>`).join('')}</tr>`;
    $('previewBody').innerHTML = rows.length
      ? rows.map(row => `<tr>${columns.map(column => `<td>${esc(row?.[column] ?? '')}</td>`).join('')}</tr>`).join('')
      : `<tr><td colspan="${Math.max(columns.length, 1)}" class="bc-empty-row">${term ? 'No record matches the search.' : 'No data exists yet. Export Excel to download the empty format and add new rows.'}</td></tr>`;
  }

  async function loadPreview() {
    preview = await api.get(`/api/configuration-packages/${packageId}/records`);
    const name = val(preview, 'packageName', 'PackageName');
    const code = val(preview, 'packageCode', 'PackageCode');
    const entity = val(preview, 'entityType', 'EntityType');
    $('cardTitle').textContent = `${name} Configuration Package`;
    $('breadcrumbName').textContent = name;
    $('recordNo').textContent = code;
    $('dataTitle').textContent = `Current ${entity} Records`;
    $('recordSearch').placeholder = `Search ${entity.toLowerCase()} records...`;
    document.title = `${name} Configuration Package`;
    renderPreview();
  }

  async function exportExcel() {
    try {
      const fileName = await api.download(`/api/configuration-packages/${packageId}/export?templateOnly=false`);
      msg('status', `${fileName} exported. The file contains current data, or blank column headings when no records exist.`, true);
    } catch (error) {
      msg('status', error.message, false);
    }
  }

  function fileToDataUrl(file) {
    return new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result);
      reader.onerror = () => reject(new Error('Unable to read the selected Excel file.'));
      reader.readAsDataURL(file);
    });
  }

  function selectImportFile() {
    $('packageFile').click();
  }

  function importedRows(result) {
    return Number(result.itemRows || 0) + Number(result.customerRows || 0) + Number(result.vendorRows || 0);
  }

  function showValidation(result) {
    const summary = [importedRows(result), Number(result.validRows || 0), Number(result.errorRows || 0)];
    $('validationSummary').querySelectorAll('strong').forEach((element, index) => element.textContent = String(summary[index] || 0));
    const issues = result.issues || [];
    $('issuesBody').innerHTML = issues.length
      ? issues.map(issue => `<tr><td>${esc(issue.sheetName)}</td><td>${Number(issue.rowNumber || 0)}</td><td>${esc(issue.fieldName)}</td><td>${esc(issue.message)}</td><td><span class="status-chip ${String(issue.severity).toLowerCase() === 'error' ? 'off' : 'ok'}">${esc(issue.severity)}</span></td></tr>`).join('')
      : '<tr><td colspan="5" class="bc-empty-row ok">No validation errors. Select Save to write the data into the ERP.</td></tr>';

    validatedImportId = Number(result.importId || 0);
    $('saveBtn').disabled = !result.canApply;
    $('validationBadge').textContent = result.canApply ? 'Validation successful' : 'Validation failed';
    $('validationBadge').className = `bc-status-pill ${result.canApply ? 'posted' : 'draft'}`;
    setStep('stepImport', 'done');
    setStep('stepValidate', result.canApply ? 'done' : 'error');
    setStep('stepSave', result.canApply ? 'active' : '');
  }

  async function validateImport() {
    if (!selectedFile) {
      msg('status', 'Select Import Excel and choose the edited .xlsx file first.', false);
      return;
    }
    try {
      $('validateBtn').disabled = true;
      msg('status', 'Validating imported Excel data...', true);
      const base64Content = await fileToDataUrl(selectedFile);
      const result = await api.post(`/api/configuration-packages/${packageId}/validate`, {
        fileName: selectedFile.name,
        base64Content
      });
      showValidation(result);
      msg('status', result.canApply ? 'Validation successful. Select Save to complete the import.' : 'Validation failed. Correct the Excel rows and import the file again.', !!result.canApply);
      await loadHistory();
    } catch (error) {
      msg('status', error.message, false);
      setStep('stepValidate', 'error');
    } finally {
      $('validateBtn').disabled = !selectedFile;
    }
  }

  async function saveImport() {
    if (!validatedImportId) {
      msg('status', 'Validate the imported Excel file before saving.', false);
      return;
    }
    if (!confirm('Save the validated Excel data into the ERP?')) return;
    try {
      $('saveBtn').disabled = true;
      msg('status', 'Saving validated records...', true);
      const result = await api.post(`/api/configuration-packages/${packageId}/apply`, { importId: validatedImportId });
      $('validationBadge').textContent = 'Saved';
      $('validationBadge').className = 'bc-status-pill posted';
      setStep('stepSave', 'done');
      msg('status', `${result.message} Items: ${result.itemsInserted} new, ${result.itemsUpdated} updated; Customers: ${result.customersInserted} new, ${result.customersUpdated} updated; Vendors: ${result.vendorsInserted} new, ${result.vendorsUpdated} updated.`, true);
      validatedImportId = 0;
      selectedFile = null;
      $('packageFile').value = '';
      $('selectedFileName').textContent = 'No Excel file imported';
      $('validateBtn').disabled = true;
      await Promise.all([loadPreview(), loadHistory()]);
    } catch (error) {
      msg('status', error.message, false);
      $('saveBtn').disabled = false;
    }
  }

  async function loadHistory() {
    const list = await api.get(`/api/configuration-packages/${packageId}/history`);
    $('historyBody').innerHTML = list.length ? list.map(row => {
      const itemRows = Number(val(row, 'itemRows', 'ItemRows') || 0);
      const customerRows = Number(val(row, 'customerRows', 'CustomerRows') || 0);
      const vendorRows = Number(val(row, 'vendorRows', 'VendorRows') || 0);
      const status = String(val(row, 'status', 'Status') || '');
      return `<tr>
        <td>${val(row, 'importId', 'ImportId')}</td>
        <td>${esc(val(row, 'fileName', 'FileName'))}</td>
        <td>${itemRows + customerRows + vendorRows}</td>
        <td>${val(row, 'validRows', 'ValidRows')}</td>
        <td>${val(row, 'errorRows', 'ErrorRows')}</td>
        <td><span class="status-chip ${status.toLowerCase() === 'applied' ? 'ok' : status.toLowerCase().includes('failed') ? 'off' : ''}">${esc(status === 'Applied' ? 'Saved' : status)}</span></td>
        <td>${fmtDate(val(row, 'importedAt', 'ImportedAt'))}</td>
        <td>${fmtDate(val(row, 'appliedAt', 'AppliedAt'))}</td>
      </tr>`;
    }).join('') : '<tr><td colspan="8" class="bc-empty-row">No imports have been made for this package.</td></tr>';
  }

  async function init() {
    if (!packageId) {
      location.href = '/configuration-packages.html';
      return;
    }
    try {
      currentUser = await api.get('/api/me');
      $('who').textContent = `${val(currentUser, 'companyName', 'CompanyName')} | ${val(currentUser, 'displayName', 'DisplayName')} | ${val(currentUser, 'roleName', 'RoleName')}`;
      if (!can('configurationPackages.export')) $('exportBtn').hidden = true;
      if (!can('configurationPackages.import')) {
        $('importBtn').hidden = true;
        $('validateBtn').hidden = true;
      }
      if (!can('configurationPackages.apply')) $('saveBtn').hidden = true;
      await Promise.all([loadPreview(), loadHistory()]);
      resetValidation();
    } catch (error) {
      if (String(error.message || '').toLowerCase().includes('unauthorized')) {
        location.href = `/login.html?returnUrl=${encodeURIComponent(location.pathname + location.search)}`;
      } else {
        msg('status', error.message, false);
      }
    }
  }

  $('backBtn').onclick = () => location.href = '/configuration-packages.html';
  $('exportBtn').onclick = exportExcel;
  $('importBtn').onclick = selectImportFile;
  $('validateBtn').onclick = validateImport;
  $('saveBtn').onclick = saveImport;
  $('refreshBtn').onclick = async () => { try { await Promise.all([loadPreview(), loadHistory()]); msg('status', 'Current records refreshed.', true); } catch (error) { msg('status', error.message, false); } };
  $('recordSearch').oninput = renderPreview;
  $('packageFile').onchange = () => {
    selectedFile = $('packageFile').files[0] || null;
    $('selectedFileName').textContent = selectedFile ? selectedFile.name : 'No Excel file imported';
    $('validateBtn').disabled = !selectedFile;
    resetValidation();
    if (selectedFile) msg('status', `${selectedFile.name} imported. Select Validate.`, true);
  };

  init();
})();
