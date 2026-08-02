// ═══════════════════════════════════════════════════════════════════════════
// whatsapp-llamadas.js — "teléfono en la pantalla" (softphone WebRTC)
// Proyecto Llamadas de voz de WhatsApp (Meta Business Calling API), 2026-08-02.
//
// Qué hace: cuando entra una llamada, Meta ya nos mandó una "oferta SDP" (por el
// webhook). Acá, en el navegador, tomamos esa oferta, pedimos el micrófono, armamos
// la "respuesta SDP" y la devolvemos a C# (Blazor) para que el servidor se la reenvíe
// a Meta. A partir de ahí el audio viaja directo por WebRTC (STUN/TURN), no por C#.
//
// Ojo: esto SOLO funciona por HTTPS (o localhost). El micrófono y WebRTC están
// bloqueados en http:// por el navegador — igual que las notas de voz del chat.
// ═══════════════════════════════════════════════════════════════════════════
window.waLlamadas = (function () {
    let pc = null;              // RTCPeerConnection actual
    let localStream = null;     // micrófono
    let iceServers = [{ urls: "stun:stun.l.google.com:19302" }];
    let remoteAudioEl = null;   // <audio> donde suena la voz del cliente
    let ring = null;            // objeto del tono de llamada

    function setIceServers(list) {
        if (Array.isArray(list) && list.length) iceServers = list;
    }

    function ensureAudioEl() {
        if (!remoteAudioEl) {
            remoteAudioEl = document.getElementById("wa-remote-audio");
            if (!remoteAudioEl) {
                remoteAudioEl = document.createElement("audio");
                remoteAudioEl.id = "wa-remote-audio";
                remoteAudioEl.autoplay = true;
                document.body.appendChild(remoteAudioEl);
            }
        }
        return remoteAudioEl;
    }

    // Espera a que WebRTC termine de juntar las "rutas" de audio (ICE), así el SDP
    // que devolvemos ya viene completo (Meta espera el SDP entero, no de a pedazos).
    function waitIceComplete(peer) {
        return new Promise((resolve) => {
            if (peer.iceGatheringState === "complete") return resolve();
            const check = () => {
                if (peer.iceGatheringState === "complete") {
                    peer.removeEventListener("icegatheringstatechange", check);
                    resolve();
                }
            };
            peer.addEventListener("icegatheringstatechange", check);
            // Red de seguridad: si tarda demasiado, seguimos igual con lo que haya.
            setTimeout(resolve, 2500);
        });
    }

    // Recibe la oferta SDP del cliente y devuelve NUESTRA respuesta SDP (string).
    async function answer(sdpOffer) {
        await hangup(); // por las dudas, cerrar cualquier llamada previa

        localStream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });

        pc = new RTCPeerConnection({ iceServers });

        // Poner nuestro micrófono en la llamada
        localStream.getTracks().forEach((t) => pc.addTrack(t, localStream));

        // Cuando llega el audio del cliente, lo enchufamos al <audio> para escucharlo
        pc.ontrack = (ev) => {
            const el = ensureAudioEl();
            el.srcObject = ev.streams[0];
            el.play().catch(() => { /* algunos navegadores piden gesto del usuario */ });
        };

        await pc.setRemoteDescription({ type: "offer", sdp: sdpOffer });
        const ans = await pc.createAnswer();
        await pc.setLocalDescription(ans);
        await waitIceComplete(pc);

        return pc.localDescription.sdp;
    }

    async function hangup() {
        try { if (pc) { pc.getSenders().forEach(s => { try { s.track && s.track.stop(); } catch (e) {} }); pc.close(); } } catch (e) {}
        pc = null;
        try { if (localStream) localStream.getTracks().forEach(t => t.stop()); } catch (e) {}
        localStream = null;
        if (remoteAudioEl) { try { remoteAudioEl.srcObject = null; } catch (e) {} }
    }

    // ── Tono de "está sonando" (sin necesitar un archivo de audio) ──
    function ringStart() {
        ringStop();
        try {
            const ctx = new (window.AudioContext || window.webkitAudioContext)();
            const gain = ctx.createGain();
            gain.gain.value = 0.0001;
            gain.connect(ctx.destination);
            const osc = ctx.createOscillator();
            osc.type = "sine";
            osc.frequency.value = 480;
            osc.connect(gain);
            osc.start();
            // patrón: suena ~1s, calla ~1s (como un teléfono)
            let on = false;
            const beat = setInterval(() => {
                on = !on;
                gain.gain.setTargetAtTime(on ? 0.08 : 0.0001, ctx.currentTime, 0.02);
            }, 1000);
            ring = { ctx, osc, beat };
        } catch (e) { /* si el navegador bloquea el audio, no pasa nada */ }
    }

    function ringStop() {
        if (!ring) return;
        try { clearInterval(ring.beat); } catch (e) {}
        try { ring.osc.stop(); } catch (e) {}
        try { ring.ctx.close(); } catch (e) {}
        ring = null;
    }

    // ¿El navegador puede usar micrófono/WebRTC? (necesita HTTPS o localhost)
    function soportado() {
        return !!(navigator.mediaDevices && navigator.mediaDevices.getUserMedia && window.RTCPeerConnection);
    }

    return { setIceServers, answer, hangup, ringStart, ringStop, soportado };
})();
