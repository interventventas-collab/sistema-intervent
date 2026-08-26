// 2026-08-26: el sonido de la alarma del reloj (hora de Argentina).
//
// Dos cosas viven acá y no en Blazor a propósito:
//   1) El SONIDO de despertador: se genera con osciladores, igual que los avisos del chat
//      (window.waSounds en index.html), así no hay que subir ningún archivo.
//   2) La INSISTENCIA: una alarma que suena una vez y se calla es una notificación, no una
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
        }
    };
})();
