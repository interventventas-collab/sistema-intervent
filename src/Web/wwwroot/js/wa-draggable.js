// 2026-08-03: hace que una ventanita flotante se pueda arrastrar tomándola de su encabezado.
// Se usa en la pantalla de WhatsApp para el "preview" del pedido antes de crear la venta.
// Funciona con mouse y con dedo (celular/tablet).
window.waDraggable = {
    // 2026-10-27: recordarKey (opcional) = se acuerda de DÓNDE quedó y de qué TAMAÑO (esquina) en esta
    // computadora (localStorage) y la próxima vez abre igual. Si la pantalla es más chica, la mete adentro.
    attach: function (handleId, panelId, recordarKey) {
        const handle = document.getElementById(handleId);
        const panel = document.getElementById(panelId);
        if (!handle || !panel) return;

        const lsKey = recordarKey ? 'wad-pos-' + recordarKey : null;
        function guardar() {
            if (!lsKey || !panel.isConnected) return;
            const r = panel.getBoundingClientRect();
            if (r.width < 50 || r.height < 50) return;
            try { localStorage.setItem(lsKey, JSON.stringify({ l: Math.round(r.left), t: Math.round(r.top), w: Math.round(r.width), h: Math.round(r.height) })); } catch (_) { }
        }
        if (lsKey) {
            let g = null;
            try { g = JSON.parse(localStorage.getItem(lsKey) || 'null'); } catch (_) { }
            if (g && g.w > 0 && g.h > 0) {
                const w = Math.min(g.w, window.innerWidth), h = Math.min(g.h, window.innerHeight);
                panel.style.transform = 'none';
                panel.style.boxSizing = 'border-box';   // lo guardado incluye el borde: si no, crece 4px cada vez
                panel.style.width = w + 'px';
                panel.style.height = h + 'px';
                panel.style.left = Math.max(0, Math.min(g.l, window.innerWidth - w)) + 'px';
                panel.style.top = Math.max(0, Math.min(g.t, window.innerHeight - h)) + 'px';
            }
            // agrandar/achicar desde la esquina: se guarda un ratito después de soltar
            if (window.ResizeObserver) {
                let tmr = null;
                new ResizeObserver(() => { clearTimeout(tmr); tmr = setTimeout(guardar, 300); }).observe(panel);
            }
        }

        let startX = 0, startY = 0, startLeft = 0, startTop = 0, dragging = false;

        function pointer(e) { return e.touches && e.touches.length ? e.touches[0] : e; }

        function onDown(e) {
            const ev = pointer(e);
            const rect = panel.getBoundingClientRect();
            // pasamos de "centrado con transform" a posición fija en px para poder moverlo
            panel.style.transform = 'none';
            panel.style.left = rect.left + 'px';
            panel.style.top = rect.top + 'px';
            startX = ev.clientX; startY = ev.clientY;
            startLeft = rect.left; startTop = rect.top;
            dragging = true;
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
            document.addEventListener('touchmove', onMove, { passive: false });
            document.addEventListener('touchend', onUp);
            e.preventDefault();
        }

        function onMove(e) {
            if (!dragging) return;
            const ev = pointer(e);
            let nl = startLeft + (ev.clientX - startX);
            let nt = startTop + (ev.clientY - startY);
            // que no se escape del todo de la pantalla
            nl = Math.max(0, Math.min(nl, window.innerWidth - 80));
            nt = Math.max(0, Math.min(nt, window.innerHeight - 40));
            panel.style.left = nl + 'px';
            panel.style.top = nt + 'px';
            if (e.cancelable) e.preventDefault();
        }

        function onUp() {
            if (dragging) guardar();
            dragging = false;
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.removeEventListener('touchmove', onMove);
            document.removeEventListener('touchend', onUp);
        }

        handle.addEventListener('mousedown', onDown);
        handle.addEventListener('touchstart', onDown, { passive: false });
        handle.style.cursor = 'move';
    },

    // 2026-08-07: hace que un BOTÓN (ej. el globito de WhatsApp) sea arrastrable SIN romper el
    // toque para abrir. Un tap normal (sin mover) dispara el click; si lo arrastrás (>6px), lo
    // mueve y anula ese click. Se puede llamar varias veces sobre el mismo id sin duplicar.
    attachSelf: function (id) {
        const el = document.getElementById(id);
        if (!el || el.dataset.dragSelf === '1') return;
        el.dataset.dragSelf = '1';
        let sx = 0, sy = 0, sl = 0, st = 0, moved = false, active = false;
        function pointer(e) { return e.touches && e.touches.length ? e.touches[0] : e; }
        function onDown(e) {
            const ev = pointer(e); const r = el.getBoundingClientRect();
            sx = ev.clientX; sy = ev.clientY; sl = r.left; st = r.top; moved = false; active = true;
            document.addEventListener('mousemove', onMove);
            document.addEventListener('mouseup', onUp);
            document.addEventListener('touchmove', onMove, { passive: false });
            document.addEventListener('touchend', onUp);
        }
        function onMove(e) {
            if (!active) return;
            const ev = pointer(e); const dx = ev.clientX - sx, dy = ev.clientY - sy;
            if (!moved && (Math.abs(dx) + Math.abs(dy)) < 6) return;
            if (!moved) { moved = true; el.style.transform = 'none'; el.style.right = 'auto'; el.style.bottom = 'auto'; el.style.left = sl + 'px'; el.style.top = st + 'px'; }
            let nl = Math.max(0, Math.min(sl + dx, window.innerWidth - 62));
            let nt = Math.max(0, Math.min(st + dy, window.innerHeight - 62));
            el.style.left = nl + 'px'; el.style.top = nt + 'px';
            if (e.cancelable) e.preventDefault();
        }
        function onUp() {
            active = false;
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.removeEventListener('touchmove', onMove);
            document.removeEventListener('touchend', onUp);
            if (moved) {
                // Anular el click que sigue al arrastre (para que NO abra el chat al soltar).
                const stop = function (ev) { ev.stopPropagation(); ev.preventDefault(); };
                el.addEventListener('click', stop, true);
                setTimeout(function () { try { el.removeEventListener('click', stop, true); } catch (_) {} }, 350);
            }
        }
        el.addEventListener('mousedown', onDown);
        el.addEventListener('touchstart', onDown, { passive: true });
    }
};
