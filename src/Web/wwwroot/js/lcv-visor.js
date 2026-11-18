// 18/11/2026: visor flotante de "Listas" (Shared/ListasCatalogosVentana.razor). Pedido del dueño: poder
// mirar la lista MIENTRAS trabaja. La ventana no tapa la pantalla: se arrastra desde la barra de arriba,
// se estira desde la esquina (CSS resize) y con "Al costado" se pega a la derecha.
// Blazor no le pone style a esa caja, así que lo que se toca acá no lo pisa al redibujar.
window.lcvVisor = (function () {
  function anchoPantalla() { return document.documentElement.clientWidth || window.innerWidth; }
  function altoPantalla() { return document.documentElement.clientHeight || window.innerHeight; }

  function fijarCaja(el) {
    // Pasa de "centrado con transform" a posición en píxeles, para poder moverla.
    const r = el.getBoundingClientRect();
    el.style.transform = 'none';
    el.style.left = r.left + 'px';
    el.style.top = r.top + 'px';
    el.style.width = r.width + 'px';
    el.style.height = r.height + 'px';
  }

  function arrastrable(id) {
    const el = document.getElementById(id);
    if (!el || el.dataset.lcvListo) return;
    el.dataset.lcvListo = '1';
    const barra = el.querySelector('.lcv-ver-h');
    if (!barra) return;
    barra.addEventListener('pointerdown', function (e) {
      if (e.button !== 0 || e.target.closest('button, a, input')) return;
      if (el.classList.contains('completa')) return;
      fijarCaja(el);
      const dx = e.clientX - el.getBoundingClientRect().left;
      const dy = e.clientY - el.getBoundingClientRect().top;
      barra.setPointerCapture(e.pointerId);
      el.classList.add('moviendo');
      function mover(ev) {
        const w = el.offsetWidth;
        let x = ev.clientX - dx, y = ev.clientY - dy;
        x = Math.min(Math.max(x, 80 - w), anchoPantalla() - 80);   // que siempre quede un pedazo a la vista
        y = Math.min(Math.max(y, 0), altoPantalla() - 40);
        el.style.left = x + 'px';
        el.style.top = y + 'px';
      }
      function soltar() {
        barra.removeEventListener('pointermove', mover);
        barra.removeEventListener('pointerup', soltar);
        barra.removeEventListener('pointercancel', soltar);
        el.classList.remove('moviendo');
      }
      barra.addEventListener('pointermove', mover);
      barra.addEventListener('pointerup', soltar);
      barra.addEventListener('pointercancel', soltar);
      e.preventDefault();
    });
  }

  // Pegada a la derecha, alto completo: se sigue usando la pantalla de la izquierda.
  function costado(id) {
    const el = document.getElementById(id);
    if (!el) return;
    const W = anchoPantalla(), H = altoPantalla();
    const w = Math.max(380, Math.round(W * 0.38));
    el.style.transform = 'none';
    el.style.width = w + 'px';
    el.style.height = (H - 16) + 'px';
    el.style.left = (W - w - 8) + 'px';
    el.style.top = '8px';
  }

  // Volver al lugar de arranque (centrada).
  function centrar(id) {
    const el = document.getElementById(id);
    if (!el) return;
    ['transform', 'left', 'top', 'width', 'height'].forEach(p => el.style.removeProperty(p));
  }

  return { arrastrable, costado, centrar };
})();
