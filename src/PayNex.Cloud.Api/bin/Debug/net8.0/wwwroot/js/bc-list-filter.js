(function (global, factory) {
  const api = factory();
  if (typeof module === 'object' && module.exports) module.exports = api;
  if (global && global.document) {
    global.PayNexListFilters = api;
    api.boot();
  }
})(typeof window !== 'undefined' ? window : null, function () {
  'use strict';

  const controllers = new WeakMap();
  const controllerList = new Set();
  const NUMBER_PATTERN = /^-?\d+(?:\.\d+)?$/;
  const DATE_PATTERN = /^(?:\d{4}-\d{1,2}-\d{1,2}|\d{1,2}[\/-]\d{1,2}[\/-]\d{2,4})(?:\s|$)/;
  const OPERATOR_LABELS = {
    contains: 'Contains',
    equals: 'Is exactly',
    notEquals: 'Does not equal',
    startsWith: 'Begins with',
    endsWith: 'Ends with',
    greaterThan: 'Greater than',
    greaterOrEqual: 'Greater than or equal to',
    lessThan: 'Less than',
    lessOrEqual: 'Less than or equal to',
    between: 'Between',
    empty: 'Is empty',
    notEmpty: 'Is not empty',
    expression: 'Business Central expression'
  };

  let booted = false;
  let documentObserver = null;
  let openLayer = null;
  let tableSequence = 0;

  function normalizeText(value) {
    return String(value == null ? '' : value)
      .replace(/\u00a0/g, ' ')
      .replace(/\s+/g, ' ')
      .trim();
  }

  function normalizeLower(value) {
    return normalizeText(value).toLocaleLowerCase();
  }

  function parseNumberValue(value) {
    let text = normalizeText(value);
    if (!text) return null;
    let negative = false;
    if (text.startsWith('(') && text.endsWith(')')) {
      negative = true;
      text = text.slice(1, -1);
    }
    text = text
      .replace(/^(?:rs\.?|pkr|usd|eur|gbp)\s*/i, '')
      .replace(/^[\$€£]\s*/, '')
      .replace(/%$/, '')
      .replace(/[\s,]/g, '');
    if (!NUMBER_PATTERN.test(text)) return null;
    const number = Number(text);
    if (!Number.isFinite(number)) return null;
    return negative ? -number : number;
  }

  function parseDateValue(value) {
    const text = normalizeText(value);
    if (!DATE_PATTERN.test(text)) return null;
    const milliseconds = Date.parse(text);
    return Number.isNaN(milliseconds) ? null : milliseconds;
  }

  function compareValues(left, right) {
    const leftNumber = parseNumberValue(left);
    const rightNumber = parseNumberValue(right);
    if (leftNumber != null && rightNumber != null) return leftNumber === rightNumber ? 0 : leftNumber < rightNumber ? -1 : 1;

    const leftDate = parseDateValue(left);
    const rightDate = parseDateValue(right);
    if (leftDate != null && rightDate != null) return leftDate === rightDate ? 0 : leftDate < rightDate ? -1 : 1;

    return normalizeText(left).localeCompare(normalizeText(right), undefined, {
      sensitivity: 'base',
      numeric: true
    });
  }

  function escapeRegExp(value) {
    return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  function wildcardMatches(value, pattern) {
    const expression = String(pattern)
      .split('')
      .map(character => character === '*' ? '.*' : character === '?' ? '.' : escapeRegExp(character))
      .join('');
    return new RegExp(`^${expression}$`, 'iu').test(normalizeText(value));
  }

  function matchAtomicExpression(value, expression) {
    let term = normalizeText(expression);
    if (!term) return true;
    if (term.startsWith('@')) term = term.slice(1);

    const operatorMatch = /^(<>|>=|<=|>|<|=)(.*)$/.exec(term);
    if (operatorMatch) {
      const operator = operatorMatch[1];
      const expected = normalizeText(operatorMatch[2]);
      const comparison = compareValues(value, expected);
      if (operator === '<>') return expected.includes('*') || expected.includes('?')
        ? !wildcardMatches(value, expected)
        : comparison !== 0;
      if (operator === '>=') return comparison >= 0;
      if (operator === '<=') return comparison <= 0;
      if (operator === '>') return comparison > 0;
      if (operator === '<') return comparison < 0;
      return expected.includes('*') || expected.includes('?')
        ? wildcardMatches(value, expected)
        : comparison === 0;
    }

    const rangeAt = term.indexOf('..');
    if (rangeAt >= 0) {
      const minimum = normalizeText(term.slice(0, rangeAt));
      const maximum = normalizeText(term.slice(rangeAt + 2));
      return (!minimum || compareValues(value, minimum) >= 0) && (!maximum || compareValues(value, maximum) <= 0);
    }

    if (term.includes('*') || term.includes('?')) return wildcardMatches(value, term);
    return compareValues(value, term) === 0;
  }

  function matchesExpression(value, expression) {
    const raw = normalizeText(expression);
    if (!raw) return true;
    return raw.split('|').some(orPart => orPart.split('&').every(andPart => matchAtomicExpression(value, andPart)));
  }

  function matchesFilter(value, filter) {
    const actual = normalizeText(value);
    const actualLower = normalizeLower(actual);
    const expected = normalizeText(filter && filter.value);
    const expectedLower = normalizeLower(expected);
    const operator = filter && filter.operator ? filter.operator : 'contains';

    if (operator === 'values') {
      const accepted = Array.isArray(filter.values) ? filter.values.map(normalizeLower) : [];
      return accepted.includes(actualLower);
    }
    if (operator === 'empty') return actual === '';
    if (operator === 'notEmpty') return actual !== '';
    if (operator === 'contains') return actualLower.includes(expectedLower);
    if (operator === 'equals') return compareValues(actual, expected) === 0;
    if (operator === 'notEquals') return compareValues(actual, expected) !== 0;
    if (operator === 'startsWith') return actualLower.startsWith(expectedLower);
    if (operator === 'endsWith') return actualLower.endsWith(expectedLower);
    if (operator === 'greaterThan') return compareValues(actual, expected) > 0;
    if (operator === 'greaterOrEqual') return compareValues(actual, expected) >= 0;
    if (operator === 'lessThan') return compareValues(actual, expected) < 0;
    if (operator === 'lessOrEqual') return compareValues(actual, expected) <= 0;
    if (operator === 'between') {
      const maximum = normalizeText(filter.value2);
      return compareValues(actual, expected) >= 0 && compareValues(actual, maximum) <= 0;
    }
    if (operator === 'expression') return matchesExpression(actual, expected);
    return true;
  }

  function describeFilter(filter) {
    if (!filter) return '';
    if (filter.operator === 'values') {
      const values = Array.isArray(filter.values) ? filter.values : [];
      if (values.length === 1) return `Is ${values[0] || '(blank)'}`;
      if (values.length <= 3) return `Is one of ${values.map(value => value || '(blank)').join(', ')}`;
      return `Is one of ${values.length} selected values`;
    }
    if (filter.operator === 'empty') return 'Is empty';
    if (filter.operator === 'notEmpty') return 'Is not empty';
    if (filter.operator === 'between') return `Between ${filter.value} and ${filter.value2}`;
    if (filter.operator === 'expression') return `Expression: ${filter.value}`;
    return `${OPERATOR_LABELS[filter.operator] || 'Contains'} ${filter.value}`;
  }

  function getCellText(cell) {
    if (!cell) return '';
    if (cell.dataset && cell.dataset.filterValue != null) return normalizeText(cell.dataset.filterValue);
    const clone = cell.cloneNode(true);
    clone.querySelectorAll('button,svg,.bc-column-filter-trigger,.bc-row-action,input[type="radio"],input[type="checkbox"]').forEach(element => element.remove());
    clone.querySelectorAll('input,select,textarea').forEach(control => {
      const replacement = document.createTextNode(control.value || control.options?.[control.selectedIndex]?.text || '');
      control.replaceWith(replacement);
    });
    return normalizeText(clone.textContent);
  }

  function headerText(header) {
    if (!header) return '';
    if (header.dataset && header.dataset.filterLabel) return normalizeText(header.dataset.filterLabel);
    const clone = header.cloneNode(true);
    clone.querySelectorAll('.bc-column-filter-trigger').forEach(element => element.remove());
    return normalizeText(clone.textContent);
  }

  function isPlaceholderRow(row) {
    if (!row || row.dataset.bcFilterEmpty === '1') return true;
    if (row.classList.contains('bc-empty-row')) return true;
    if (row.cells.length !== 1) return false;
    const cell = row.cells[0];
    return cell.hasAttribute('colspan') || cell.classList.contains('bc-empty-row') || cell.classList.contains('muted');
  }

  function isEligibleTable(table) {
    if (!(table instanceof HTMLTableElement)) return false;
    if (table.dataset.bcFilter === 'off') return false;
    if (table.closest('.bc-lookup-modal,.bc-column-filter-editor,.bc-column-context-menu,.modal-backdrop')) return false;
    if (table.matches('.bc-lookup-table,.bc-lines-table,.dashboard-table') || table.closest('.dashboard-card')) return false;
    if (!table.tHead || !table.tBodies.length) return false;
    const headers = [...table.tHead.querySelectorAll('th')];
    if (headers.length < 2 && table.dataset.bcFilter !== 'on') return false;
    return headers.some(header => headerText(header) && !header.classList.contains('bc-select-col'));
  }

  function closeOpenLayer() {
    if (!openLayer) return;
    openLayer.remove();
    openLayer = null;
  }

  function positionLayer(layer, anchor, point) {
    layer.style.visibility = 'hidden';
    document.body.appendChild(layer);
    const width = layer.offsetWidth || 360;
    const height = layer.offsetHeight || 360;
    const anchorRect = anchor && anchor.getBoundingClientRect ? anchor.getBoundingClientRect() : null;
    let left = point ? point.x : anchorRect ? anchorRect.left : Math.max(12, (window.innerWidth - width) / 2);
    let top = point ? point.y : anchorRect ? anchorRect.bottom + 5 : Math.max(12, (window.innerHeight - height) / 2);
    left = Math.max(8, Math.min(left, window.innerWidth - width - 8));
    top = Math.max(8, Math.min(top, window.innerHeight - height - 8));
    layer.style.left = `${left}px`;
    layer.style.top = `${top}px`;
    layer.style.visibility = 'visible';
    openLayer = layer;
  }

  function showContextMenu(items, point) {
    closeOpenLayer();
    const menu = document.createElement('div');
    menu.className = 'bc-column-context-menu';
    menu.setAttribute('role', 'menu');
    items.forEach(item => {
      if (item.separator) {
        const separator = document.createElement('div');
        separator.className = 'bc-column-context-separator';
        menu.appendChild(separator);
        return;
      }
      const button = document.createElement('button');
      button.type = 'button';
      button.className = 'bc-column-context-action secondary';
      button.disabled = !!item.disabled;
      button.setAttribute('role', 'menuitem');
      const icon = document.createElement('span');
      icon.className = 'bc-column-context-icon';
      icon.textContent = item.icon || '';
      const label = document.createElement('span');
      label.textContent = item.label;
      button.append(icon, label);
      button.addEventListener('click', () => {
        closeOpenLayer();
        if (item.action) item.action();
      });
      menu.appendChild(button);
    });
    positionLayer(menu, null, point);
    menu.querySelector('button:not([disabled])')?.focus();
  }

  function writeClipboard(value) {
    const text = normalizeText(value);
    if (navigator.clipboard && navigator.clipboard.writeText) return navigator.clipboard.writeText(text).catch(() => {});
    const area = document.createElement('textarea');
    area.value = text;
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    try { document.execCommand('copy'); } catch {}
    area.remove();
    return Promise.resolve();
  }

  class TableFilterController {
    constructor(table, pageIndex) {
      this.table = table;
      this.pageIndex = pageIndex;
      this.id = table.id || table.dataset.bcFilterId || `table-${pageIndex}`;
      this.storageKey = `paynex:list-filters:${location.pathname}:${this.id}`;
      this.filters = new Map();
      this.sort = null;
      this.columns = [];
      this.selectedCell = null;
      this.rowOrder = new WeakMap();
      this.nextRowOrder = 1;
      this.applyTimer = null;
      this.observer = null;
      this.toolbar = null;
      this.pane = null;
      this.recordText = null;
      this.activeBadge = null;
      this.clearButton = null;
      this.filterList = null;
      this.addField = null;
      this.restoreState();
      this.refreshColumns();
      this.createToolbar();
      this.bindTableEvents();
      this.observe();
      this.apply();
      table.dataset.bcListFilterReady = '1';
    }

    restoreState() {
      try {
        const state = JSON.parse(sessionStorage.getItem(this.storageKey) || '{}');
        Object.entries(state.filters || {}).forEach(([columnIndex, filter]) => {
          if (filter && filter.operator) this.filters.set(Number(columnIndex), filter);
        });
        if (state.sort && Number.isInteger(Number(state.sort.columnIndex))) {
          this.sort = { columnIndex: Number(state.sort.columnIndex), direction: state.sort.direction === 'desc' ? 'desc' : 'asc' };
        }
      } catch {}
    }

    saveState() {
      try {
        const filters = {};
        this.filters.forEach((filter, columnIndex) => { filters[columnIndex] = filter; });
        sessionStorage.setItem(this.storageKey, JSON.stringify({ filters, sort: this.sort }));
      } catch {}
    }

    refreshColumns() {
      const headers = [...this.table.tHead.querySelectorAll('th')];
      this.columns = headers.map((header, columnIndex) => ({
        header,
        columnIndex,
        label: headerText(header)
      })).filter(column => column.label && !column.header.classList.contains('bc-select-col') && !column.header.classList.contains('bc-row-action'));

      this.columns.forEach(column => {
        const { header, columnIndex, label } = column;
        header.classList.add('bc-filterable-column');
        header.dataset.filterLabel = label;
        let trigger = header.querySelector(':scope > .bc-column-filter-trigger');
        if (!trigger) {
          trigger = document.createElement('button');
          trigger.type = 'button';
          trigger.className = 'bc-column-filter-trigger';
          trigger.innerHTML = '<span aria-hidden="true">&#9663;</span>';
          header.appendChild(trigger);
        }
        trigger.title = `Filter or sort ${label}`;
        trigger.setAttribute('aria-label', `Filter or sort ${label}`);
        trigger.onclick = event => {
          event.preventDefault();
          event.stopPropagation();
          this.showHeaderMenu(columnIndex, trigger);
        };
      });
      this.updateHeaderStates();
    }

    createToolbar() {
      const toolbar = document.createElement('div');
      toolbar.className = 'bc-column-filter-toolbar';
      toolbar.dataset.bcFilterFor = this.id;

      const toggle = document.createElement('button');
      toggle.type = 'button';
      toggle.className = 'bc-column-filter-toggle secondary';
      toggle.innerHTML = '<span class="bc-filter-funnel" aria-hidden="true">▽</span><span>Filters</span>';
      toggle.setAttribute('aria-expanded', 'false');

      const badge = document.createElement('span');
      badge.className = 'bc-column-filter-badge';
      badge.hidden = true;
      toggle.appendChild(badge);

      const recordText = document.createElement('span');
      recordText.className = 'bc-column-filter-records';
      recordText.setAttribute('aria-live', 'polite');
      recordText.textContent = '0 records';

      const clear = document.createElement('button');
      clear.type = 'button';
      clear.className = 'bc-column-filter-clear secondary';
      clear.textContent = 'Clear all column filters';
      clear.hidden = true;
      clear.addEventListener('click', () => this.clearAllFilters());

      toolbar.append(toggle, recordText, clear);

      const pane = document.createElement('section');
      pane.className = 'bc-column-filter-pane';
      pane.hidden = true;
      pane.innerHTML = '<div class="bc-column-filter-pane-head"><div><strong>Filter pane</strong><span>Filter this list by one or more fields.</span></div></div>';

      const filterList = document.createElement('div');
      filterList.className = 'bc-column-active-filters';
      const addRow = document.createElement('div');
      addRow.className = 'bc-column-add-filter';
      const field = document.createElement('select');
      field.dataset.noLookup = '1';
      field.setAttribute('aria-label', 'Choose a field to filter');
      const add = document.createElement('button');
      add.type = 'button';
      add.className = 'bc-column-add-filter-button secondary';
      add.textContent = '+ Add filter';
      add.addEventListener('click', () => {
        const columnIndex = Number(field.value);
        if (Number.isInteger(columnIndex)) this.openEditor(columnIndex, add);
      });
      addRow.append(field, add);
      const help = document.createElement('p');
      help.className = 'bc-column-filter-help';
      help.textContent = 'Tip: select the arrow in any column heading, or right-click a value. Use Alt+F3 to filter to the selected cell.';
      pane.append(filterList, addRow, help);

      const tableContainer = this.table.closest('.bc-table-wrap') || this.table;
      tableContainer.parentNode.insertBefore(toolbar, tableContainer);
      tableContainer.parentNode.insertBefore(pane, tableContainer);

      toggle.addEventListener('click', () => {
        pane.hidden = !pane.hidden;
        toggle.setAttribute('aria-expanded', String(!pane.hidden));
        if (!pane.hidden) field.focus();
      });

      this.toolbar = toolbar;
      this.pane = pane;
      this.recordText = recordText;
      this.activeBadge = badge;
      this.clearButton = clear;
      this.filterList = filterList;
      this.addField = field;
      this.renderFilterPane();
    }

    bindTableEvents() {
      this.table.addEventListener('click', event => {
        const cell = event.target.closest('tbody td');
        if (!cell || !this.table.contains(cell)) return;
        this.selectCell(cell);
      }, true);

      this.table.addEventListener('contextmenu', event => {
        if (event.target.closest('input,select,textarea,button,a')) return;
        const cell = event.target.closest('tbody td');
        if (!cell || !this.table.contains(cell)) return;
        const row = cell.parentElement;
        if (isPlaceholderRow(row)) return;
        const columnIndex = cell.cellIndex;
        if (!this.column(columnIndex)) return;
        event.preventDefault();
        this.selectCell(cell);
        this.showCellMenu(columnIndex, getCellText(cell), { x: event.clientX, y: event.clientY });
      });
    }

    observe() {
      if (this.observer) this.observer.disconnect();
      this.observer = new MutationObserver(() => this.scheduleApply());
      this.observer.observe(this.table, { childList: true, subtree: true, characterData: true });
    }

    scheduleApply() {
      clearTimeout(this.applyTimer);
      this.applyTimer = setTimeout(() => {
        this.refreshColumns();
        this.renderFilterPane();
        this.apply();
      }, 25);
    }

    column(columnIndex) {
      return this.columns.find(column => column.columnIndex === Number(columnIndex));
    }

    rows() {
      return [...this.table.tBodies].flatMap(body => [...body.rows]).filter(row => !isPlaceholderRow(row));
    }

    selectCell(cell) {
      if (!cell || isPlaceholderRow(cell.parentElement)) return;
      this.table.querySelectorAll('.bc-filter-cell-active').forEach(element => element.classList.remove('bc-filter-cell-active'));
      cell.classList.add('bc-filter-cell-active');
      this.selectedCell = {
        columnIndex: cell.cellIndex,
        value: getCellText(cell),
        cell
      };
    }

    showHeaderMenu(columnIndex, anchor) {
      const column = this.column(columnIndex);
      if (!column) return;
      const selectedForColumn = this.selectedCell && this.selectedCell.columnIndex === columnIndex;
      const items = [
        { icon: '↑', label: `Sort ${column.label} ascending`, action: () => this.setSort(columnIndex, 'asc') },
        { icon: '↓', label: `Sort ${column.label} descending`, action: () => this.setSort(columnIndex, 'desc') },
        { icon: '×', label: 'Clear sorting', disabled: !this.sort, action: () => this.clearSort() },
        { separator: true },
        { icon: '▽', label: `Filter ${column.label}...`, action: () => this.openEditor(columnIndex, anchor) },
        {
          icon: '=',
          label: selectedForColumn ? `Filter to “${this.selectedCell.value || '(blank)'}”` : 'Filter to selected cell value',
          disabled: !selectedForColumn,
          action: () => this.setFilter(columnIndex, { operator: 'values', values: [this.selectedCell.value] })
        },
        { icon: '⌫', label: `Clear ${column.label} filter`, disabled: !this.filters.has(columnIndex), action: () => this.removeFilter(columnIndex) }
      ];
      const rect = anchor.getBoundingClientRect();
      showContextMenu(items, { x: rect.right - 8, y: rect.bottom + 3 });
    }

    showCellMenu(columnIndex, value, point) {
      const column = this.column(columnIndex);
      if (!column) return;
      showContextMenu([
        { icon: '=', label: `Filter to this value: ${value || '(blank)'}`, action: () => this.setFilter(columnIndex, { operator: 'values', values: [value] }) },
        { icon: '≠', label: `Exclude this value`, action: () => this.setFilter(columnIndex, value ? { operator: 'notEquals', value } : { operator: 'notEmpty' }) },
        { icon: '▽', label: `Open ${column.label} filter...`, action: () => this.openEditor(columnIndex, null) },
        { icon: '⌫', label: `Clear ${column.label} filter`, disabled: !this.filters.has(columnIndex), action: () => this.removeFilter(columnIndex) },
        { separator: true },
        { icon: '⧉', label: 'Copy value', action: () => writeClipboard(value) }
      ], point);
    }

    distinctValues(columnIndex) {
      const counts = new Map();
      this.rows().forEach(row => {
        const value = getCellText(row.cells[columnIndex]);
        counts.set(value, (counts.get(value) || 0) + 1);
      });
      return [...counts.entries()]
        .map(([value, count]) => ({ value, count }))
        .sort((left, right) => compareValues(left.value, right.value));
    }

    openEditor(columnIndex, anchor) {
      const column = this.column(columnIndex);
      if (!column) return;
      closeOpenLayer();

      const current = this.filters.get(columnIndex) || { operator: 'contains', value: '', value2: '' };
      const selectedValues = new Set(current.operator === 'values' && Array.isArray(current.values) ? current.values : []);
      const values = this.distinctValues(columnIndex);

      const editor = document.createElement('section');
      editor.className = 'bc-column-filter-editor';
      editor.setAttribute('role', 'dialog');
      editor.setAttribute('aria-modal', 'false');
      editor.setAttribute('aria-label', `Filter ${column.label}`);

      const head = document.createElement('div');
      head.className = 'bc-column-filter-editor-head';
      const heading = document.createElement('div');
      const eyebrow = document.createElement('span');
      eyebrow.textContent = 'Filter field';
      const title = document.createElement('strong');
      title.textContent = column.label;
      heading.append(eyebrow, title);
      const close = document.createElement('button');
      close.type = 'button';
      close.className = 'bc-column-filter-editor-close secondary';
      close.textContent = '×';
      close.setAttribute('aria-label', 'Close filter');
      close.onclick = closeOpenLayer;
      head.append(heading, close);

      const criteria = document.createElement('div');
      criteria.className = 'bc-column-filter-criteria';
      const operatorLabel = document.createElement('label');
      operatorLabel.textContent = 'Condition';
      const operator = document.createElement('select');
      operator.dataset.noLookup = '1';
      Object.entries(OPERATOR_LABELS).forEach(([value, text]) => {
        const option = document.createElement('option');
        option.value = value;
        option.textContent = text;
        operator.appendChild(option);
      });
      operator.value = current.operator === 'values' ? 'contains' : current.operator;
      operatorLabel.appendChild(operator);

      const valueLabel = document.createElement('label');
      valueLabel.textContent = 'Filter value';
      const valueInput = document.createElement('input');
      valueInput.type = 'text';
      valueInput.value = current.operator === 'values' ? '' : current.value || '';
      valueInput.placeholder = 'Enter a value or filter expression';
      valueLabel.appendChild(valueInput);

      const value2Label = document.createElement('label');
      value2Label.textContent = 'To value';
      const value2Input = document.createElement('input');
      value2Input.type = 'text';
      value2Input.value = current.value2 || '';
      value2Label.appendChild(value2Input);
      criteria.append(operatorLabel, valueLabel, value2Label);

      const expressionHelp = document.createElement('p');
      expressionHelp.className = 'bc-column-expression-help';
      expressionHelp.textContent = 'Expression examples: A*  |  <>Closed  |  100..500  |  >=01/01/2026  |  Red|Blue';

      const valuesSection = document.createElement('div');
      valuesSection.className = 'bc-column-distinct-section';
      const valuesHead = document.createElement('div');
      valuesHead.className = 'bc-column-distinct-head';
      const valuesTitle = document.createElement('strong');
      valuesTitle.textContent = 'Select values';
      const valuesCount = document.createElement('span');
      valuesHead.append(valuesTitle, valuesCount);
      const valueSearch = document.createElement('input');
      valueSearch.type = 'search';
      valueSearch.placeholder = 'Search values...';
      valueSearch.setAttribute('aria-label', `Search ${column.label} values`);
      const selectActions = document.createElement('div');
      selectActions.className = 'bc-column-value-actions';
      const selectVisible = document.createElement('button');
      selectVisible.type = 'button';
      selectVisible.className = 'secondary';
      selectVisible.textContent = 'Select visible';
      const clearValues = document.createElement('button');
      clearValues.type = 'button';
      clearValues.className = 'secondary';
      clearValues.textContent = 'Clear selection';
      selectActions.append(selectVisible, clearValues);
      const valuesList = document.createElement('div');
      valuesList.className = 'bc-column-distinct-list';
      valuesSection.append(valuesHead, valueSearch, selectActions, valuesList);

      const error = document.createElement('p');
      error.className = 'bc-column-filter-error';
      error.setAttribute('aria-live', 'polite');

      const footer = document.createElement('div');
      footer.className = 'bc-column-filter-editor-footer';
      const clearFilter = document.createElement('button');
      clearFilter.type = 'button';
      clearFilter.className = 'secondary';
      clearFilter.textContent = 'Clear filter';
      clearFilter.disabled = !this.filters.has(columnIndex);
      const cancel = document.createElement('button');
      cancel.type = 'button';
      cancel.className = 'secondary';
      cancel.textContent = 'Cancel';
      const apply = document.createElement('button');
      apply.type = 'button';
      apply.className = 'bc-column-filter-apply success';
      apply.textContent = 'Apply filter';
      footer.append(clearFilter, cancel, apply);

      editor.append(head, criteria, expressionHelp, valuesSection, error, footer);

      function syncCriteria() {
        const noValue = operator.value === 'empty' || operator.value === 'notEmpty';
        valueLabel.hidden = noValue;
        value2Label.hidden = operator.value !== 'between';
        expressionHelp.hidden = operator.value !== 'expression';
      }

      function visibleValues() {
        const search = normalizeLower(valueSearch.value);
        return values.filter(item => !search || normalizeLower(item.value).includes(search));
      }

      function renderValues() {
        const visible = visibleValues();
        valuesCount.textContent = `${selectedValues.size} selected • ${values.length} values`;
        valuesList.replaceChildren();
        if (!values.length) {
          const empty = document.createElement('div');
          empty.className = 'bc-column-distinct-empty';
          empty.textContent = 'No values are currently loaded.';
          valuesList.appendChild(empty);
          return;
        }
        if (!visible.length) {
          const empty = document.createElement('div');
          empty.className = 'bc-column-distinct-empty';
          empty.textContent = 'No values match your search.';
          valuesList.appendChild(empty);
          return;
        }
        visible.slice(0, 250).forEach(item => {
          const label = document.createElement('label');
          label.className = 'bc-column-distinct-value';
          const checkbox = document.createElement('input');
          checkbox.type = 'checkbox';
          checkbox.checked = selectedValues.has(item.value);
          checkbox.addEventListener('change', () => {
            if (checkbox.checked) selectedValues.add(item.value);
            else selectedValues.delete(item.value);
            valuesCount.textContent = `${selectedValues.size} selected • ${values.length} values`;
          });
          const valueText = document.createElement('span');
          valueText.textContent = item.value || '(blank)';
          const count = document.createElement('small');
          count.textContent = String(item.count);
          label.append(checkbox, valueText, count);
          valuesList.appendChild(label);
        });
        if (visible.length > 250) {
          const more = document.createElement('div');
          more.className = 'bc-column-distinct-empty';
          more.textContent = `${visible.length - 250} more values. Refine the value search to display them.`;
          valuesList.appendChild(more);
        }
      }

      operator.addEventListener('change', syncCriteria);
      valueSearch.addEventListener('input', renderValues);
      valueInput.addEventListener('input', () => {
        if (valueInput.value && selectedValues.size) {
          selectedValues.clear();
          renderValues();
        }
      });
      selectVisible.addEventListener('click', () => {
        visibleValues().forEach(item => selectedValues.add(item.value));
        valueInput.value = '';
        renderValues();
      });
      clearValues.addEventListener('click', () => {
        selectedValues.clear();
        renderValues();
      });
      clearFilter.addEventListener('click', () => {
        this.removeFilter(columnIndex);
        closeOpenLayer();
      });
      cancel.addEventListener('click', closeOpenLayer);
      apply.addEventListener('click', () => {
        error.textContent = '';
        if (selectedValues.size) {
          this.setFilter(columnIndex, { operator: 'values', values: [...selectedValues] });
          closeOpenLayer();
          return;
        }
        const selectedOperator = operator.value;
        const noValue = selectedOperator === 'empty' || selectedOperator === 'notEmpty';
        if (!noValue && !normalizeText(valueInput.value)) {
          error.textContent = 'Enter a filter value or select one or more values.';
          valueInput.focus();
          return;
        }
        if (selectedOperator === 'between' && !normalizeText(value2Input.value)) {
          error.textContent = 'Enter the ending value for the range.';
          value2Input.focus();
          return;
        }
        this.setFilter(columnIndex, {
          operator: selectedOperator,
          value: valueInput.value,
          value2: value2Input.value
        });
        closeOpenLayer();
      });
      editor.addEventListener('keydown', event => {
        if (event.key === 'Escape') closeOpenLayer();
        if (event.key === 'Enter' && event.target !== valueSearch && !event.target.closest('.bc-column-distinct-list')) {
          event.preventDefault();
          apply.click();
        }
      });

      syncCriteria();
      renderValues();
      positionLayer(editor, anchor || column.header, null);
      setTimeout(() => (selectedValues.size ? valueSearch : valueInput).focus(), 0);
    }

    setFilter(columnIndex, filter) {
      if (!this.column(columnIndex)) return;
      this.filters.set(Number(columnIndex), filter);
      this.saveState();
      this.updateHeaderStates();
      this.renderFilterPane();
      this.apply();
      if (this.pane) {
        this.pane.hidden = false;
        this.toolbar.querySelector('.bc-column-filter-toggle')?.setAttribute('aria-expanded', 'true');
      }
    }

    removeFilter(columnIndex) {
      this.filters.delete(Number(columnIndex));
      this.saveState();
      this.updateHeaderStates();
      this.renderFilterPane();
      this.apply();
    }

    clearAllFilters() {
      this.filters.clear();
      this.saveState();
      this.updateHeaderStates();
      this.renderFilterPane();
      this.apply();
    }

    setSort(columnIndex, direction) {
      this.sort = { columnIndex: Number(columnIndex), direction: direction === 'desc' ? 'desc' : 'asc' };
      this.saveState();
      this.updateHeaderStates();
      this.apply();
    }

    clearSort() {
      this.sort = null;
      this.saveState();
      this.updateHeaderStates();
      this.apply();
    }

    updateHeaderStates() {
      this.columns.forEach(column => {
        const active = this.filters.has(column.columnIndex);
        const sortDirection = this.sort && this.sort.columnIndex === column.columnIndex ? this.sort.direction : '';
        column.header.classList.toggle('bc-column-filter-active', active);
        column.header.classList.toggle('bc-column-sort-asc', sortDirection === 'asc');
        column.header.classList.toggle('bc-column-sort-desc', sortDirection === 'desc');
        const trigger = column.header.querySelector(':scope > .bc-column-filter-trigger');
        if (trigger) {
          trigger.classList.toggle('active', active || !!sortDirection);
          trigger.querySelector('span').innerHTML = active ? '&#9662;' : sortDirection === 'asc' ? '&#8593;' : sortDirection === 'desc' ? '&#8595;' : '&#9663;';
        }
      });
    }

    renderFilterPane() {
      if (!this.filterList || !this.addField) return;
      const currentSelection = this.addField.value;
      this.addField.replaceChildren();
      this.columns.forEach(column => {
        const option = document.createElement('option');
        option.value = String(column.columnIndex);
        option.textContent = column.label;
        this.addField.appendChild(option);
      });
      if ([...this.addField.options].some(option => option.value === currentSelection)) this.addField.value = currentSelection;

      this.filterList.replaceChildren();
      if (!this.filters.size) {
        const empty = document.createElement('div');
        empty.className = 'bc-column-no-filters';
        empty.textContent = 'No column filters are applied to this list.';
        this.filterList.appendChild(empty);
      } else {
        [...this.filters.entries()].forEach(([columnIndex, filter]) => {
          const column = this.column(columnIndex);
          if (!column) return;
          const card = document.createElement('div');
          card.className = 'bc-column-active-filter';
          const copy = document.createElement('div');
          const label = document.createElement('strong');
          label.textContent = column.label;
          const description = document.createElement('span');
          description.textContent = describeFilter(filter);
          copy.append(label, description);
          const actions = document.createElement('div');
          const edit = document.createElement('button');
          edit.type = 'button';
          edit.className = 'secondary';
          edit.textContent = 'Edit';
          edit.onclick = () => this.openEditor(columnIndex, edit);
          const remove = document.createElement('button');
          remove.type = 'button';
          remove.className = 'secondary';
          remove.textContent = '×';
          remove.title = `Remove ${column.label} filter`;
          remove.setAttribute('aria-label', `Remove ${column.label} filter`);
          remove.onclick = () => this.removeFilter(columnIndex);
          actions.append(edit, remove);
          card.append(copy, actions);
          this.filterList.appendChild(card);
        });
      }

      this.activeBadge.textContent = String(this.filters.size);
      this.activeBadge.hidden = !this.filters.size;
      this.clearButton.hidden = !this.filters.size;
      this.toolbar.classList.toggle('has-filters', this.filters.size > 0);
    }

    sortRows() {
      [...this.table.tBodies].forEach(body => {
        const rows = [...body.rows].filter(row => !isPlaceholderRow(row));
        rows.forEach(row => {
          if (!this.rowOrder.has(row)) this.rowOrder.set(row, this.nextRowOrder++);
        });
        const sorted = [...rows].sort((left, right) => {
          if (!this.sort) return this.rowOrder.get(left) - this.rowOrder.get(right);
          const comparison = compareValues(getCellText(left.cells[this.sort.columnIndex]), getCellText(right.cells[this.sort.columnIndex]));
          if (comparison === 0) return this.rowOrder.get(left) - this.rowOrder.get(right);
          return this.sort.direction === 'desc' ? -comparison : comparison;
        });
        sorted.forEach(row => body.appendChild(row));
      });
    }

    updateEmptyRow(total, visible) {
      let emptyRow = this.table.querySelector('tbody tr[data-bc-filter-empty="1"]');
      if (this.filters.size && total > 0 && visible === 0) {
        if (!emptyRow) {
          emptyRow = document.createElement('tr');
          emptyRow.dataset.bcFilterEmpty = '1';
          const cell = document.createElement('td');
          cell.colSpan = Math.max(1, this.table.tHead.rows[0]?.cells.length || this.columns.length);
          cell.className = 'bc-empty-row bc-column-filter-no-results';
          cell.textContent = 'No records match the applied column filters.';
          emptyRow.appendChild(cell);
          this.table.tBodies[0].appendChild(emptyRow);
        }
        emptyRow.hidden = false;
      } else if (emptyRow) {
        emptyRow.remove();
      }
    }

    apply() {
      if (!this.table.isConnected) return;
      if (this.observer) this.observer.disconnect();
      try {
        this.sortRows();
        const rows = this.rows();
        let visible = 0;
        rows.forEach(row => {
          const matches = [...this.filters.entries()].every(([columnIndex, filter]) => matchesFilter(getCellText(row.cells[columnIndex]), filter));
          row.classList.toggle('bc-column-filter-hidden', !matches);
          if (matches) visible++;
        });
        this.updateEmptyRow(rows.length, visible);
        this.recordText.textContent = this.filters.size
          ? `${visible} of ${rows.length} record${rows.length === 1 ? '' : 's'}`
          : `${rows.length} record${rows.length === 1 ? '' : 's'}`;

        const detail = {
          tableId: this.id,
          totalRecords: rows.length,
          visibleRecords: visible,
          filters: [...this.filters.entries()].map(([columnIndex, filter]) => ({
            columnIndex,
            field: this.column(columnIndex)?.label || '',
            ...filter
          })),
          sort: this.sort
        };
        this.table.dispatchEvent(new CustomEvent('paynex:list-filter-changed', { bubbles: true, detail }));
      } finally {
        this.observe();
      }
    }

    filterSelectedCell() {
      if (!this.selectedCell || !this.column(this.selectedCell.columnIndex)) return false;
      this.setFilter(this.selectedCell.columnIndex, { operator: 'values', values: [this.selectedCell.value] });
      return true;
    }

    togglePane() {
      if (!this.pane) return;
      this.pane.hidden = !this.pane.hidden;
      this.toolbar.querySelector('.bc-column-filter-toggle')?.setAttribute('aria-expanded', String(!this.pane.hidden));
      if (!this.pane.hidden) this.addField?.focus();
    }

    destroy() {
      if (this.observer) this.observer.disconnect();
      clearTimeout(this.applyTimer);
      this.toolbar?.remove();
      this.pane?.remove();
      this.table.querySelectorAll('.bc-column-filter-trigger').forEach(button => button.remove());
      this.table.querySelectorAll('.bc-column-filter-hidden,.bc-filter-cell-active').forEach(element => {
        element.classList.remove('bc-column-filter-hidden', 'bc-filter-cell-active');
      });
      delete this.table.dataset.bcListFilterReady;
      controllerList.delete(this);
    }
  }

  function enhanceTable(table) {
    if (controllers.has(table) || !isEligibleTable(table)) return controllers.get(table) || null;
    const controller = new TableFilterController(table, ++tableSequence);
    controllers.set(table, controller);
    controllerList.add(controller);
    return controller;
  }

  function scan(root) {
    if (typeof document === 'undefined') return [];
    const found = [];
    if (root instanceof HTMLTableElement) found.push(root);
    if (root && root.querySelectorAll) found.push(...root.querySelectorAll('table'));
    const ancestorTable = root && root.closest ? root.closest('table') : null;
    if (ancestorTable) found.push(ancestorTable);
    return [...new Set(found)].map(enhanceTable).filter(Boolean);
  }

  function controllerForActiveElement() {
    const table = document.activeElement?.closest?.('table') || document.querySelector('.bc-filter-cell-active')?.closest('table');
    if (table && controllers.has(table)) return controllers.get(table);
    return [...controllerList].find(controller => controller.table.isConnected) || null;
  }

  function bindGlobalEvents() {
    document.addEventListener('click', event => {
      if (openLayer && !openLayer.contains(event.target) && !event.target.closest('.bc-column-filter-trigger')) closeOpenLayer();
    }, true);
    document.addEventListener('keydown', event => {
      if (event.key === 'Escape') closeOpenLayer();
      if (event.altKey && event.key === 'F3') {
        const controller = controllerForActiveElement();
        if (controller && controller.filterSelectedCell()) event.preventDefault();
      }
      if (event.shiftKey && event.key === 'F3') {
        const controller = controllerForActiveElement();
        if (controller) {
          controller.togglePane();
          event.preventDefault();
        }
      }
    });
    window.addEventListener('resize', closeOpenLayer);
    window.addEventListener('scroll', closeOpenLayer, true);
  }

  function boot() {
    if (booted || typeof document === 'undefined') return;
    booted = true;
    const start = () => {
      scan(document);
      documentObserver = new MutationObserver(mutations => {
        mutations.forEach(mutation => mutation.addedNodes.forEach(node => {
          if (node.nodeType === 1) scan(node);
        }));
      });
      documentObserver.observe(document.body, { childList: true, subtree: true });
      bindGlobalEvents();
      document.documentElement.classList.add('bc-list-filtering-enabled');
    };
    if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start, { once: true });
    else start();
  }

  return {
    boot,
    scan,
    enhanceTable,
    normalizeText,
    parseNumberValue,
    parseDateValue,
    compareValues,
    wildcardMatches,
    matchesExpression,
    matchesFilter,
    describeFilter
  };
});
