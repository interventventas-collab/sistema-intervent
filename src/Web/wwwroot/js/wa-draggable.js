// 2026-08-03: hace que una ventanita flotante se pueda arrastrar tomándola de su encabezado.
// Se usa en la pantalla de WhatsApp para el "preview" del pedido antes de crear la venta.
// Funciona con mouse y con dedo (celular/tablet).
window.waDraggable = {
    attach: function (handleId, panelId) {
        const handle = document.getElementById(handleId);
        const panel = document.getElementById(panelId);
        if (!handle || !panel) return;

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
            dragging = false;
            document.removeEventListener('mousemove', onMove);
            document.removeEventListener('mouseup', onUp);
            document.removeEventListener('touchmove', onMove);
            document.removeEventListener('touchend', onUp);
        }

        handle.addEventListener('mousedown', onDown);
        handle.addEventListener('touchstart', onDown, { passive: false });
        handle.style.cursor = 'move';
    }
};
