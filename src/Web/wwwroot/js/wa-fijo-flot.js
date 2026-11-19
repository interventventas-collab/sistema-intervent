// 19/11/2026: ventanita flotante de "💬 WhatsApp Fijo" (Shared/WaFijoPastilla.razor). Se arrastra desde la
// barra de arriba, se estira desde la esquina (CSS resize) y recuerda lugar y tamaño en ESTE aparato
// (localStorage "wafijo.caja"). Blazor no le pone style a la caja, así que lo que se toca acá no lo pisa.
window.waFijoFlot = (function () {
  const KEY = 'wafijo.caja';
  function W() { return document.documentElement.clientWidth || window.innerWidth; }
  function H() { return document.documentElement.clientHeight || window.innerHeight; }
  function guardar(el) {
    try {
      const r = el.getBoundingClientRect();
      localStorage.setItem(KEY, JSON.stringify({ x: r.left, y: r.top, w: r.width, h: r.height }));
    } catch (e) { }
  }
  function restaurar(el) {
    let c = null;
    try { c = JSON.parse(localStorage.getItem(KEY) || 'null'); } catch (e) { }
    if (!c || W() < 700) return;   // en el celu va a pantalla casi completa (CSS)
    const w = Math.min(Math.max(c.w, 340), W() - 16), h = Math.min(Math.max(c.h, 320), H() - 16);
    el.style.width = w + 'px';
    el.style.height = h + 'px';
    el.style.left = Math.min(Math.max(c.x, 8), W() - w - 8) + 'px';
    el.style.top = Math.min(Math.max(c.y, 8), H() - 60) + 'px';
    el.style.right = 'auto';
    el.style.bottom = 'auto';
  }
  function armar(id) {
    const el = document.getElementById(id);
    if (!el || el.dataset.wffListo) return;
    el.dataset.wffListo = '1';
    restaurar(el);
    const barra = el.querySelector('.wff-bar');
    if (barra && W() >= 700) {
      barra.addEventListener('pointerdown', function (e) {
        if (e.button !== 0 || e.target.closest('button, a, input')) return;
        const r = el.getBoundingClientRect();
        el.style.left = r.left + 'px'; el.style.top = r.top + 'px';
        el.style.right = 'auto'; el.style.bottom = 'auto';
        const dx = e.clientX - r.left, dy = e.clientY - r.top;
        barra.setPointerCapture(e.pointerId);
        function mover(ev) {
          const w = el.offsetWidth;
          el.style.left = Math.min(Math.max(ev.clientX - dx, 80 - w), W() - 80) + 'px';
          el.style.top = Math.min(Math.max(ev.clientY - dy, 0), H() - 40) + 'px';
        }
        function soltar() {
          barra.removeEventListener('pointermove', mover);
          barra.removeEventListener('pointerup', soltar);
          barra.removeEventListener('pointercancel', soltar);
          guardar(el);
        }
        barra.addEventListener('pointermove', mover);
        barra.addEventListener('pointerup', soltar);
        barra.addEventListener('pointercancel', soltar);
        e.preventDefault();
      });
    }
    // Estirar desde la esquina (resize de CSS): guardar el tamaño cuando termina.
    if (window.ResizeObserver) {
      let t = null;
      new ResizeObserver(function () { clearTimeout(t); t = setTimeout(function () { if (el.isConnected) guardar(el); }, 400); }).observe(el);
    }
  }
  return { armar };
})();
