// 2026-11-26: telefono dentro del sistema.
// El navegador se conecta a la central telefonica (Asterisk, carpeta telefonia/) como
// interno 103, por wss://DOMINIO/telefono-ws (Caddy lo pasa a la central).
// La pantalla (Shared/TelefonoSistema.razor) llama a estas funciones y recibe avisos
// por dotnet.invokeMethodAsync: OnEstado, OnEntrante, OnLlamando, OnHablando, OnFin.
(function () {
    const LIB = 'js/lib/jssip-3.13.9.min.js';
    let ua = null, sesion = null, dotnet = null, cfg = null;
    let audioRemoto = null, ring = null;

    function cargarLib() {
        if (window.JsSIP) return Promise.resolve();
        return new Promise((ok, mal) => {
            const s = document.createElement('script');
            s.src = LIB; s.onload = ok; s.onerror = () => mal(new Error('no cargo jssip'));
            document.head.appendChild(s);
        });
    }

    function avisar(metodo, ...args) {
        if (!dotnet) return;
        try { dotnet.invokeMethodAsync(metodo, ...args); } catch (e) { /* pantalla cerrada */ }
    }

    // ---- timbre: tono de llamada hecho con el parlante de la compu (sin archivos) ----
    function timbreOn() {
        timbreOff();
        try {
            const ctx = new (window.AudioContext || window.webkitAudioContext)();
            const gain = ctx.createGain(); gain.gain.value = 0; gain.connect(ctx.destination);
            [440, 480].forEach(f => { const o = ctx.createOscillator(); o.frequency.value = f; o.connect(gain); o.start(); });
            let on = false;
            const t = setInterval(() => { on = !on; gain.gain.value = on ? 0.15 : 0; }, 1000);
            gain.gain.value = 0.15; on = true;
            ring = { ctx, t };
        } catch (e) { ring = null; }
    }
    function timbreOff() {
        if (!ring) return;
        clearInterval(ring.t);
        try { ring.ctx.close(); } catch (e) { }
        ring = null;
    }

    function nombreDe(s) {
        const id = s.remote_identity;
        const num = (id && id.uri && id.uri.user) || '';
        const nom = (id && id.display_name) || '';
        return { num, nom };
    }

    function opcionesMedia() {
        return {
            mediaConstraints: { audio: true, video: false },
            pcConfig: { iceServers: [{ urls: 'stun:stun.l.google.com:19302' }] },
            rtcOfferConstraints: { offerToReceiveAudio: true, offerToReceiveVideo: false }
        };
    }

    function engancharAudio(s) {
        s.on('peerconnection', ev => {
            ev.peerconnection.addEventListener('track', t => {
                if (!audioRemoto) {
                    audioRemoto = document.createElement('audio');
                    audioRemoto.autoplay = true;
                    document.body.appendChild(audioRemoto);
                }
                audioRemoto.srcObject = t.streams[0];
                audioRemoto.play().catch(() => { });
            });
        });
        // si la llamada es saliente la peerconnection ya existe
        if (s.connection) {
            s.connection.addEventListener('track', t => {
                if (!audioRemoto) {
                    audioRemoto = document.createElement('audio');
                    audioRemoto.autoplay = true;
                    document.body.appendChild(audioRemoto);
                }
                audioRemoto.srcObject = t.streams[0];
                audioRemoto.play().catch(() => { });
            });
        }
    }

    function nuevaSesion(s, entrante) {
        if (sesion) { // ya hay una llamada: rechazar la nueva como "ocupado"
            if (entrante) s.terminate({ status_code: 486 });
            return;
        }
        sesion = s;
        const { num, nom } = nombreDe(s);
        engancharAudio(s);
        // JsSIP espera a juntar TODAS las direcciones de red antes de mandar la llamada y eso
        // puede tardar mucho (o no terminar). Con la primera direccion publica, o a los 1,5 s,
        // alcanza: la central esta en una IP publica.
        let listo = null;
        s.on('icecandidate', e => {
            if (!listo) listo = setTimeout(() => { try { e.ready(); } catch (x) { } }, 1500);
            if (e.candidate && e.candidate.type === 'srflx') { clearTimeout(listo); try { e.ready(); } catch (x) { } }
        });
        s.on('accepted', () => { timbreOff(); avisar('OnHablando', num, nom); });
        s.on('confirmed', () => { timbreOff(); });
        const fin = (e) => {
            timbreOff();
            sesion = null;
            if (audioRemoto) audioRemoto.srcObject = null;
            avisar('OnFin', (e && e.cause) || '');
        };
        s.on('ended', fin);
        s.on('failed', fin);
        if (entrante) { timbreOn(); avisar('OnEntrante', num, nom); }
        else avisar('OnLlamando', num, nom);
    }

    window.telefono = {
        init: async function (config, dotnetRef) {
            dotnet = dotnetRef; cfg = config;
            if (ua) return;
            try { await cargarLib(); } catch (e) { avisar('OnEstado', 'error'); return; }
            JsSIP.debug.disable('JsSIP:*');
            const socket = new JsSIP.WebSocketInterface(cfg.wsUrl);
            ua = new JsSIP.UA({
                sockets: [socket],
                uri: 'sip:' + cfg.usuario + '@' + cfg.dominio,
                password: cfg.clave,
                display_name: 'Sistema',
                register: true,
                register_expires: 120,
                session_timers: false,
                connection_recovery_min_interval: 2,
                connection_recovery_max_interval: 30
            });
            ua.on('registered', () => avisar('OnEstado', 'conectado'));
            ua.on('unregistered', () => avisar('OnEstado', 'desconectado'));
            ua.on('registrationFailed', () => avisar('OnEstado', 'desconectado'));
            ua.on('disconnected', () => avisar('OnEstado', 'desconectado'));
            ua.on('newRTCSession', ev => nuevaSesion(ev.session, ev.originator === 'remote'));
            avisar('OnEstado', 'conectando');
            ua.start();
        },
        llamar: function (numero) {
            if (!ua || sesion) return false;
            const n = String(numero || '').replace(/[^0-9*#]/g, '');
            if (!n) return false;
            ua.call('sip:' + n + '@' + cfg.dominio, opcionesMedia());
            return true;
        },
        atender: function () {
            if (sesion && sesion.direction === 'incoming' && !sesion.isEstablished()) {
                timbreOff();
                sesion.answer(opcionesMedia());
            }
        },
        cortar: function () {
            timbreOff();
            if (sesion) { try { sesion.terminate(); } catch (e) { sesion = null; } }
        },
        silenciar: function (si) {
            if (!sesion) return;
            if (si) sesion.mute({ audio: true }); else sesion.unmute({ audio: true });
        },
        tecla: function (t) {
            if (sesion && sesion.isEstablished()) { try { sesion.sendDTMF(String(t)); } catch (e) { } }
        },
        apagar: function () {
            timbreOff();
            try { if (sesion) sesion.terminate(); } catch (e) { }
            try { if (ua) ua.stop(); } catch (e) { }
            ua = null; sesion = null; dotnet = null;
        }
    };
})();
