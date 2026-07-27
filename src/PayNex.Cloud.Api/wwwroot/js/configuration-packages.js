(() => {
  const $ = id => document.getElementById(id);
  const val = (o, a, b) => o?.[a] ?? o?.[b];
  const esc = value => String(value ?? '').replace(/[&<>'"]/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]));
  let rows = [];
  let selectedId = 0;

  function selectRow(id) {
    selectedId = Number(id || 0);
    document.querySelectorAll('#recordBody tr[data-id]').forEach(tr => {
      const selected = Number(tr.dataset.id) === selectedId;
      tr.classList.toggle('selected', selected);
      const radio = tr.querySelector('input[type="radio"]');
      if (radio) radio.checked = selected;
    });
    const row = rows.find(x => Number(val(x, 'packageId', 'PackageId')) === selectedId);
    $('selectedCaption').textContent = row ? `${val(row, 'packageName', 'PackageName')} selected` : 'No package selected';
  }

  function openSelected() {
    if (!selectedId) {
      msg('status', 'Select Customer, Vendor or Item first.', false);
      return;
    }
    location.href = `/configuration-package-card.html?id=${selectedId}`;
  }

  async function load() {
    try {
      const term = encodeURIComponent($('searchTerm').value || '');
      rows = await api.get(`/api/configuration-packages?term=${term}`);
      $('recordCount').textContent = `${rows.length} record${rows.length === 1 ? '' : 's'}`;
      $('recordBody').innerHTML = rows.length ? rows.map(row => {
        const id = val(row, 'packageId', 'PackageId');
        const name = val(row, 'packageName', 'PackageName');
        const code = val(row, 'packageCode', 'PackageCode');
        const recordCount = Number(val(row, 'recordCount', 'RecordCount') || 0);
        const lastStatus = val(row, 'lastImportStatus', 'LastImportStatus') || 'Not imported';
        const active = !!val(row, 'isActive', 'IsActive');
        return `<tr data-id="${id}" tabindex="0">
          <td><input type="radio" name="packageRow" aria-label="Select ${esc(name)}"></td>
          <td><a href="/configuration-package-card.html?id=${id}"><b>${esc(code)}</b></a></td>
          <td><a href="/configuration-package-card.html?id=${id}"><b>${esc(name)}</b></a></td>
          <td>${esc(val(row, 'description', 'Description') || '')}</td>
          <td><b>${recordCount}</b></td>
          <td>${esc(lastStatus)}<div class="mini muted">${fmtDate(val(row, 'lastImportAt', 'LastImportAt'))}</div></td>
          <td>${active ? '<span class="status-chip ok">Ready</span>' : '<span class="status-chip off">Inactive</span>'}</td>
        </tr>`;
      }).join('') : '<tr><td colspan="7" class="bc-empty-row">No matching package found.</td></tr>';

      document.querySelectorAll('#recordBody tr[data-id]').forEach(tr => {
        tr.onclick = event => { if (!event.target.closest('a')) selectRow(tr.dataset.id); };
        tr.ondblclick = () => location.href = `/configuration-package-card.html?id=${tr.dataset.id}`;
        tr.onkeydown = event => { if (event.key === 'Enter') location.href = `/configuration-package-card.html?id=${tr.dataset.id}`; };
      });

      if (rows.length && !rows.some(x => Number(val(x, 'packageId', 'PackageId')) === selectedId)) {
        selectRow(val(rows[0], 'packageId', 'PackageId'));
      } else {
        selectRow(selectedId);
      }
    } catch (error) {
      msg('status', error.message, false);
    }
  }

  async function init() {
    try {
      const user = await api.get('/api/me');
      $('who').textContent = `${val(user, 'companyName', 'CompanyName')} | ${val(user, 'displayName', 'DisplayName')} | ${val(user, 'roleName', 'RoleName')}`;
      await load();
    } catch {
      location.href = `/login.html?returnUrl=${encodeURIComponent(location.pathname)}`;
    }
  }

  $('openBtn').onclick = openSelected;
  $('refreshBtn').onclick = load;
  $('applyFilterBtn').onclick = load;
  $('clearFilterBtn').onclick = () => { $('searchTerm').value = ''; load(); };
  $('searchTerm').onkeydown = event => { if (event.key === 'Enter') load(); };
  init();
})();
