// 2026-08-26: el reloj flotante (hora de Argentina) y su alarma.
//
// Tres cosas viven acá y no en Blazor a propósito:
//   1) ARRASTRAR y RECORDAR dónde quedó: mover una caja con el dedo/mouse es cosa del navegador.
//   2) El SONIDO de despertador: se genera con osciladores, igual que los avisos del chat
//      (window.waSounds en index.html), así no hay que subir ningún archivo.
//   3) La INSISTENCIA: una alarma que suena una vez y se calla es una notificación, no una
//      alarma. Esta repite hasta que la apagan.
window.reloj = (function () {
    var loopId = null;

    // ── bip-bip-bip del radio reloj: tres pitidos cortos y secos ──────────────
    function bipBip(c) {
        var n = c.currentTime;
        for (var i = 0; i < 3; i++) {
            var o = c.createOscillator(), g = c.createGain(), t = n + i * 0.18;
            o.type = 'square';
            o.frequency.setValueAtTime(2000, t);
            g.gain.setValueAtTime(0.0001, t);
            g.gain.exponentialRampToValueAtTime(0.32, t + 0.01);
            g.gain.exponentialRampToValueAtTime(0.0001, t + 0.12);
            o.connect(g); g.connect(c.destination);
            o.start(t); o.stop(t + 0.15);
        }
    }
    // OJO con el orden de carga: este archivo se carga en el <head>, MUCHO antes del script
    // de abajo del index.html que define window.waSounds. Por eso el despertador no se registra
    // acá arriba (no existiría la lista todavía) sino recién al tocarlo, y se toca solo si hace
    // falta. Registrarlo igual sirve para que el ▶ del chat también lo pueda probar.
    var _ctx = null;
    function ctx() {
        if (!_ctx) { var C = window.AudioContext || window.webkitAudioContext; _ctx = new C(); }
        if (_ctx.state === 'suspended') { try { _ctx.resume(); } catch (e) { } }
        return _ctx;
    }

    function tocar(nombre) {
        try {
            nombre = nombre || 'despertador';
            if (window.waSounds && !window.waSounds.despertador) window.waSounds.despertador = bipBip;
            // El despertador lo tocamos nosotros (es nuestro). El resto, con el motor del chat.
            // A propósito NO respeta el silencio maestro: si alguien puso una alarma es porque
            // quiere que le suene. Silenciar los avisos no es silenciar el despertador.
            if (nombre === 'despertador') bipBip(ctx());
            else if (window.waPlaySoundCrudo) window.waPlaySoundCrudo(nombre);
            else bipBip(ctx());
        } catch (e) { console.warn('reloj: no pude sonar', e); }
    }

    return {
        probar: function (nombre) { tocar(nombre); },

        /// Suena ahora y sigue sonando cada 3 segundos hasta que la apaguen.
        sonarHasta: function (nombre) {
            window.reloj.callar();
            tocar(nombre);
            loopId = setInterval(function () { tocar(nombre); }, 3000);
        },

        callar: function () {
            if (loopId) { clearInterval(loopId); loopId = null; }
        },

        /// Arrastrar tomándolo del asa, y acordarse de dónde quedó (por navegador).
        attach: function (panelId, asaId, clave) {
            var panel = document.getElementById(panelId);
            var asa = document.getElementById(asaId);
            if (!panel || !asa) return;
            if (panel.dataset.relojListo === '1') return;   // no enganchar dos veces
            panel.dataset.relojListo = '1';

            var guardado = null;
            try { guardado = JSON.parse(localStorage.getItem(clave) || 'null'); } catch (e) { }
            if (guardado && typeof guardado.left === 'number') {
                // Si cambió el tamaño de la pantalla, lo traemos adentro en vez de perderlo afuera.
                panel.style.left = Math.max(0, Math.min(guardado.left, window.innerWidth - 90)) + 'px';
                panel.style.top = Math.max(0, Math.min(guardado.top, window.innerHeight - 50)) + 'px';
                panel.style.right = 'auto';
            }

            var sx = 0, sy = 0, sl = 0, st = 0, moviendo = false;
            function pt(e) { return (e.touches && e.touches.length) ? e.touches[0] : e; }

            function abajo(e) {
                var ev = pt(e), r = panel.getBoundingClientRect();
                panel.style.left = r.left + 'px'; panel.style.top = r.top + 'px'; panel.style.right = 'auto';
                sx = ev.clientX; sy = ev.clientY; sl = r.left; st = r.top; moviendo = true;
                document.addEventListener('mousemove', mover);
                document.addEventListener('mouseup', arriba);
                document.addEventListener('touchmove', mover, { passive: false });
                document.addEventListener('touchend', arriba);
                e.preventDefault();
            }
            function mover(e) {
                if (!moviendo) return;
                var ev = pt(e);
                var nl = Math.max(0, Math.min(sl + (ev.clientX - sx), window.innerWidth - 90));
                var nt = Math.max(0, Math.min(st + (ev.clientY - sy), window.innerHeight - 50));
                panel.style.left = nl + 'px'; panel.style.top = nt + 'px';
                if (e.cancelable) e.preventDefault();
            }
            function arriba() {
                if (!moviendo) return;
                moviendo = false;
                document.removeEventListener('mousemove', mover);
                document.removeEventListener('mouseup', arriba);
                document.removeEventListener('touchmove', mover);
                document.removeEventListener('touchend', arriba);
                var r = panel.getBoundingClientRect();
                try { localStorage.setItem(clave, JSON.stringify({ left: Math.round(r.left), top: Math.round(r.top) })); } catch (e) { }
            }

            asa.addEventListener('mousedown', abajo);
            asa.addEventListener('touchstart', abajo, { passive: false });
        }
    };
})();
