// 2026-11-26: "Plano del depósito" (/deposito/plano). Maneja el dedo / mouse sobre el dibujo:
//  - agrandar/achicar con la ruedita, con los botones + / − o con dos dedos (cambia el viewBox);
//  - mover el plano arrastrando el fondo (un dedo en el celu);
//  - en modo editar, arrastrar una cosa del plano (los <g data-id>) y soltarla: avisa a Blazor
//    ElementoMovido(id, x, y) con las medidas del PLANO (metros, redondeado a 10 cm);
//  - tocar sin arrastrar: ElementoTocado(id) o FondoTocado().
// OJO con los ejes: en pantalla la calle queda a la izquierda, así que
//   X de pantalla = Y del plano (hacia el fondo)   ·   Y de pantalla = X del plano (a lo ancho).
window.planoDeposito = (function () {
  var estados = new WeakMap();
  var UMBRAL_PX = 5;

  function vb(svg) {
    var v = svg.viewBox && svg.viewBox.baseVal;
    if (v && v.width > 0) return { x: v.x, y: v.y, w: v.width, h: v.height };
    return { x: 0, y: 0, w: 44, h: 12 };
  }
  function setVb(svg, v) {
    svg.setAttribute('viewBox', v.x + ' ' + v.y + ' ' + v.w + ' ' + v.h);
  }
  function aSvg(svg, cx, cy, m) {
    var pt = svg.createSVGPoint();
    pt.x = cx; pt.y = cy;
    var mat = m || svg.getScreenCTM().inverse();
    return pt.matrixTransform(mat);
  }
  function snap(v) { return Math.round(v * 10) / 10; }

  function zoomEn(svg, factor, px, py) {
    var v = vb(svg);
    var nw = v.w / factor, nh = v.h / factor;
    if (nw < 1.5 || nw > 400) return;
    setVb(svg, { x: px - (px - v.x) / factor, y: py - (py - v.y) / factor, w: nw, h: nh });
  }

  function init(svg, ref) {
    if (!svg) return;
    dispose(svg);
    var st = { ref: ref, editar: false, punteros: new Map(), modo: null, handlers: {} };
    estados.set(svg, st);
    svg.style.touchAction = 'none';

    function down(e) {
      if (e.button !== undefined && e.button !== 0 && e.pointerType === 'mouse') return;
      st.punteros.set(e.pointerId, { x: e.clientX, y: e.clientY });
      try { svg.setPointerCapture(e.pointerId); } catch (_) { }
      if (st.punteros.size === 2) {
        // dos dedos: pellizco para agrandar/achicar (se cancela cualquier arrastre)
        cancelarArrastre();
        var ps = Array.from(st.punteros.values());
        st.modo = { tipo: 'pinza', dist: Math.hypot(ps[0].x - ps[1].x, ps[0].y - ps[1].y) };
        return;
      }
      if (st.punteros.size > 2) return;
      var g = e.target && e.target.closest ? e.target.closest('[data-id]') : null;
      var mat = svg.getScreenCTM().inverse();
      var p0 = aSvg(svg, e.clientX, e.clientY, mat);
      st.modo = {
        tipo: (st.editar && g && g.getAttribute('data-fijo') !== '1') ? 'arrastre' : 'mover',
        g: g, id: g ? parseInt(g.getAttribute('data-id'), 10) : null,
        x0: g ? parseFloat(g.getAttribute('data-x')) : 0,
        y0: g ? parseFloat(g.getAttribute('data-y')) : 0,
        cx: e.clientX, cy: e.clientY, mat: mat, p0: p0, vb0: vb(svg), movio: false, dx: 0, dy: 0
      };
    }

    function move(e) {
      if (!st.punteros.has(e.pointerId)) return;
      st.punteros.set(e.pointerId, { x: e.clientX, y: e.clientY });
      var m = st.modo;
      if (!m) return;
      if (m.tipo === 'pinza') {
        var ps = Array.from(st.punteros.values());
        if (ps.length < 2) return;
        var d = Math.hypot(ps[0].x - ps[1].x, ps[0].y - ps[1].y);
        if (m.dist > 0 && d > 0) {
          var c = aSvg(svg, (ps[0].x + ps[1].x) / 2, (ps[0].y + ps[1].y) / 2);
          zoomEn(svg, d / m.dist, c.x, c.y);
        }
        m.dist = d;
        return;
      }
      if (!m.movio && Math.hypot(e.clientX - m.cx, e.clientY - m.cy) < UMBRAL_PX) return;
      m.movio = true;
      var p = aSvg(svg, e.clientX, e.clientY, m.mat);
      if (m.tipo === 'mover') {
        setVb(svg, { x: m.vb0.x - (p.x - m.p0.x), y: m.vb0.y - (p.y - m.p0.y), w: m.vb0.w, h: m.vb0.h });
      } else if (m.tipo === 'arrastre' && m.g) {
        // pantalla X = plano Y ; pantalla Y = ancho − plano X (la pared izquierda va abajo).
        // Se acomoda a la cuadrícula de 10 cm.
        var ny = snap(m.y0 + (p.x - m.p0.x));
        var nx = snap(m.x0 - (p.y - m.p0.y));
        m.dx = ny - m.y0; m.dy = nx - m.x0;
        m.g.setAttribute('transform', 'translate(' + m.dx + ' ' + (-m.dy) + ')');
        m.g.classList.add('pd-arrastrando');
      }
    }

    function up(e) {
      var habia = st.punteros.has(e.pointerId);
      st.punteros.delete(e.pointerId);
      try { svg.releasePointerCapture(e.pointerId); } catch (_) { }
      var m = st.modo;
      if (!habia || !m) return;
      if (m.tipo === 'pinza') {
        if (st.punteros.size === 0) st.modo = null;
        else st.modo = null; // con un dedo que queda no seguimos moviendo (evita saltos)
        return;
      }
      st.modo = null;
      if (e.type === 'pointercancel') { cancelarArrastre(m); return; }
      if (!m.movio) {
        if (m.id != null && !isNaN(m.id)) st.ref.invokeMethodAsync('ElementoTocado', m.id);
        else st.ref.invokeMethodAsync('FondoTocado');
        return;
      }
      if (m.tipo === 'arrastre' && m.g) {
        var g = m.g;
        var nx = snap(m.x0 + m.dy), ny = snap(m.y0 + m.dx);
        if (nx === m.x0 && ny === m.y0) { limpiar(g); return; }
        st.ref.invokeMethodAsync('ElementoMovido', m.id, nx, ny)
          .then(function () { limpiar(g); }, function () { limpiar(g); });
      }
    }

    function limpiar(g) { g.removeAttribute('transform'); g.classList.remove('pd-arrastrando'); }
    function cancelarArrastre(m) {
      m = m || st.modo;
      if (m && m.tipo === 'arrastre' && m.g) limpiar(m.g);
    }

    function wheel(e) {
      e.preventDefault();
      var p = aSvg(svg, e.clientX, e.clientY);
      zoomEn(svg, e.deltaY < 0 ? 1.15 : 1 / 1.15, p.x, p.y);
    }

    st.handlers = { pointerdown: down, pointermove: move, pointerup: up, pointercancel: up, wheel: wheel };
    svg.addEventListener('pointerdown', down);
    svg.addEventListener('pointermove', move);
    svg.addEventListener('pointerup', up);
    svg.addEventListener('pointercancel', up);
    svg.addEventListener('wheel', wheel, { passive: false });
  }

  function setEditar(svg, on) {
    var st = estados.get(svg);
    if (st) st.editar = !!on;
  }

  // Encuadra el rectángulo (en coordenadas de PANTALLA del SVG) respetando la forma del recuadro,
  // así el zoom alrededor del mouse no se corre.
  function encuadrar(svg, x, y, w, h) {
    if (!svg) return;
    var r = svg.getBoundingClientRect();
    var asp = (r.width > 0 && r.height > 0) ? r.width / r.height : (w / h);
    var nw = w, nh = h;
    if (w / h > asp) nh = w / asp; else nw = h * asp;
    setVb(svg, { x: x - (nw - w) / 2, y: y - (nh - h) / 2, w: nw, h: nh });
  }

  function zoom(svg, factor) {
    if (!svg) return;
    var v = vb(svg);
    zoomEn(svg, factor, v.x + v.w / 2, v.y + v.h / 2);
  }

  // Centro de lo que se ve: [Y de pantalla, X de pantalla] (la página lo pasa a metros del plano).
  function centro(svg) {
    var v = vb(svg);
    return [snap(v.y + v.h / 2), snap(v.x + v.w / 2)];
  }

  function dispose(svg) {
    var st = svg && estados.get(svg);
    if (!st) return;
    Object.keys(st.handlers).forEach(function (k) { svg.removeEventListener(k, st.handlers[k]); });
    estados.delete(svg);
  }

  return { init: init, setEditar: setEditar, encuadrar: encuadrar, zoom: zoom, centro: centro, dispose: dispose };
})();
