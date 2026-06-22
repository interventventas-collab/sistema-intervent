// 2026-06-22: helper para canvas de firma con el dedo (mobile) o mouse (desktop).
// Lo usa el modal de "Confirmar entrega con firma" en MisPedidos.razor.
window.firmaCanvas = (function () {
  const state = {};

  function setup(canvasId) {
    const canvas = document.getElementById(canvasId);
    if (!canvas) return;
    // Ajustar el tamaño del canvas al ancho del contenedor (devolvera pixeles reales)
    const ratio = window.devicePixelRatio || 1;
    const rect = canvas.getBoundingClientRect();
    canvas.width = rect.width * ratio;
    canvas.height = rect.height * ratio;
    const ctx = canvas.getContext('2d');
    ctx.scale(ratio, ratio);
    ctx.lineWidth = 2;
    ctx.lineCap = 'round';
    ctx.lineJoin = 'round';
    ctx.strokeStyle = '#111827';
    // Fondo blanco para que el PDF se vea limpio
    ctx.fillStyle = '#ffffff';
    ctx.fillRect(0, 0, rect.width, rect.height);

    state[canvasId] = { drawing: false, hasInk: false, ctx, canvas, rect };

    function getPos(e) {
      const r = canvas.getBoundingClientRect();
      if (e.touches && e.touches.length > 0) {
        return { x: e.touches[0].clientX - r.left, y: e.touches[0].clientY - r.top };
      }
      return { x: e.clientX - r.left, y: e.clientY - r.top };
    }

    function start(e) {
      e.preventDefault();
      const s = state[canvasId]; if (!s) return;
      s.drawing = true;
      const p = getPos(e);
      ctx.beginPath();
      ctx.moveTo(p.x, p.y);
    }
    function move(e) {
      const s = state[canvasId]; if (!s || !s.drawing) return;
      e.preventDefault();
      const p = getPos(e);
      ctx.lineTo(p.x, p.y);
      ctx.stroke();
      s.hasInk = true;
    }
    function end(e) {
      const s = state[canvasId]; if (!s) return;
      s.drawing = false;
    }

    canvas.addEventListener('mousedown', start);
    canvas.addEventListener('mousemove', move);
    canvas.addEventListener('mouseup', end);
    canvas.addEventListener('mouseleave', end);
    canvas.addEventListener('touchstart', start, { passive: false });
    canvas.addEventListener('touchmove', move, { passive: false });
    canvas.addEventListener('touchend', end, { passive: false });
  }

  function clear(canvasId) {
    const s = state[canvasId]; if (!s) return;
    const r = s.canvas.getBoundingClientRect();
    s.ctx.clearRect(0, 0, s.canvas.width, s.canvas.height);
    s.ctx.fillStyle = '#ffffff';
    s.ctx.fillRect(0, 0, r.width, r.height);
    s.hasInk = false;
  }

  function get(canvasId) {
    const s = state[canvasId]; if (!s) return null;
    if (!s.hasInk) return null;
    return s.canvas.toDataURL('image/png');
  }

  function hasInk(canvasId) {
    const s = state[canvasId]; if (!s) return false;
    return s.hasInk;
  }

  return { setup, clear, get, hasInk };
})();
