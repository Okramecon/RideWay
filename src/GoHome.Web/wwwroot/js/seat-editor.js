// Редактор схемы салона. Используется в Mini App (вкладка водителя «Машина») и в админке (страница «Машины»).
// Формат схемы: ряды через «/», ячейки через «,»; ячейка — «D» (водитель), «.» (пусто) или номер места. Пример: "D,.,1/2,3,4".
// Номера мест закреплены за сиденьями: новое сиденье получает наименьший свободный номер, остальные не меняются.
window.SeatEditor = (function () {
  const MAX_ROWS = 8, MAX_COLS = 5, MAX_SEATS = 20;
  let styled = false;

  function injectStyle() {
    if (styled) return;
    styled = true;
    const s = document.createElement('style');
    s.textContent = `
      .se-tools { display:flex; flex-wrap:wrap; gap:6px; margin-bottom:8px; }
      .se-tools button { font:inherit; font-size:13px; border:1px solid var(--sep, rgba(128,128,128,.35)); border-radius:8px; padding:6px 10px;
        background:var(--bg2, #f1f2f5); color:var(--text, #111); cursor:pointer; }
      .se-tools button.on { background:var(--accent, #2481cc); border-color:var(--accent, #2481cc); color:var(--accent-text, #fff); }
      .se-tools .se-gap { flex-basis:100%; height:0; }
      .se-grid { display:grid; gap:8px; justify-content:center; width:fit-content; margin:6px auto; padding:16px 18px;
        border:2px solid var(--sep, rgba(128,128,128,.35)); border-radius:34px 34px 16px 16px; background:var(--bg2, #f1f2f5); }
      .se-cell { width:48px; height:48px; border-radius:10px; display:flex; align-items:center; justify-content:center;
        font:700 16px/1 system-ui, sans-serif; cursor:pointer; user-select:none; }
      .se-cell.empty { border:2px dashed var(--sep, rgba(128,128,128,.45)); color:transparent; }
      .se-cell.seat { border:2px solid var(--ok, #2ea043); color:var(--ok, #2ea043); background:var(--section, #fff); }
      .se-cell.driver { border:2px solid var(--hint, #8a8f98); color:var(--hint, #8a8f98); font-size:11px; background:var(--section, #fff); }
      .se-info { text-align:center; font-size:13px; color:var(--hint, #8a8f98); margin-top:4px; }
    `;
    document.head.appendChild(s);
  }

  function parse(text) {
    const rows = String(text || '').split('/').map(r => r.split(',').map(c => c.trim() || '.'));
    const cols = Math.max(1, ...rows.map(r => r.length));
    rows.forEach(r => { while (r.length < cols) r.push('.'); });
    return rows.length ? rows : [['D', '.', '1']];
  }

  const serialize = grid => grid.map(r => r.join(',')).join('/');
  const isSeat = c => /^\d+$/.test(c);

  /** Монтирует редактор в element. onChange(layoutString) вызывается после каждой правки. */
  function mount(el, layout, onChange) {
    injectStyle();
    let grid = parse(layout);
    let tool = 'seat';

    const seatCount = () => grid.flat().filter(isSeat).length;
    const nextNumber = () => {
      const used = new Set(grid.flat().filter(isSeat).map(Number));
      let n = 1;
      while (used.has(n)) n++;
      return n;
    };
    const changed = () => { render(); onChange && onChange(serialize(grid)); };

    function render() {
      const cols = grid[0].length;
      el.innerHTML = `
        <div class="se-tools">
          <button type="button" data-tool="seat" class="${tool === 'seat' ? 'on' : ''}">Сиденье</button>
          <button type="button" data-tool="driver" class="${tool === 'driver' ? 'on' : ''}">Водитель</button>
          <button type="button" data-tool="empty" class="${tool === 'empty' ? 'on' : ''}">Убрать</button>
          <span class="se-gap"></span>
          <button type="button" data-op="addRow">+ ряд</button>
          <button type="button" data-op="delRow">− ряд</button>
          <button type="button" data-op="addCol">+ колонка</button>
          <button type="button" data-op="delCol">− колонка</button>
        </div>
        <div class="se-grid" style="grid-template-columns:repeat(${cols},48px)">
          ${grid.map((row, r) => row.map((c, i) =>
            `<div class="se-cell ${c === '.' ? 'empty' : c === 'D' ? 'driver' : 'seat'}" data-r="${r}" data-c="${i}">${c === '.' ? '·' : c === 'D' ? 'руль' : c}</div>`).join('')).join('')}
        </div>
        <div class="se-info">Мест: ${seatCount()}. Выберите «Сиденье», «Водитель» или «Убрать» и нажимайте на ячейки.</div>`;

      el.querySelectorAll('[data-tool]').forEach(b => b.onclick = () => { tool = b.dataset.tool; render(); });
      el.querySelectorAll('[data-op]').forEach(b => b.onclick = () => operate(b.dataset.op));
      el.querySelectorAll('.se-cell').forEach(cell => cell.onclick = () => paint(+cell.dataset.r, +cell.dataset.c));
    }

    function paint(r, c) {
      const cur = grid[r][c];
      if (tool === 'seat') {
        if (isSeat(cur)) return;                          // уже сиденье — номер не трогаем
        if (seatCount() >= MAX_SEATS) return alert(`Не больше ${MAX_SEATS} мест`);
        grid[r][c] = String(nextNumber());
      } else if (tool === 'driver') {
        grid = grid.map(row => row.map(x => (x === 'D' ? '.' : x)));   // водитель один
        grid[r][c] = 'D';
      } else {
        grid[r][c] = '.';
      }
      changed();
    }

    function operate(op) {
      const cols = grid[0].length;
      if (op === 'addRow' && grid.length < MAX_ROWS) grid.push(Array(cols).fill('.'));
      else if (op === 'delRow' && grid.length > 1) grid.pop();
      else if (op === 'addCol' && cols < MAX_COLS) grid.forEach(r => r.push('.'));
      else if (op === 'delCol' && cols > 1) grid.forEach(r => r.pop());
      else return;
      changed();
    }

    render();
    return { get value() { return serialize(grid); } };
  }

  return { mount };
})();
