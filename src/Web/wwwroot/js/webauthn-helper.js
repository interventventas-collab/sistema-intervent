// 2026-06-03: WebAuthn helpers para el fichador.
// Convierten las options del backend (Base64Url strings) a ArrayBuffer que pide navigator.credentials,
// y al volver convierten el ArrayBuffer al formato que espera Fido2NetLib (Base64Url).

function base64UrlToArrayBuffer(b64u) {
    if (!b64u) return new ArrayBuffer(0);
    let s = b64u.replace(/-/g, '+').replace(/_/g, '/');
    while (s.length % 4) s += '=';
    const bin = atob(s);
    const arr = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) arr[i] = bin.charCodeAt(i);
    return arr.buffer;
}

function arrayBufferToBase64Url(buf) {
    const bytes = new Uint8Array(buf);
    let bin = '';
    for (let i = 0; i < bytes.length; i++) bin += String.fromCharCode(bytes[i]);
    return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

// Llama al browser para crear una credencial nueva (registro de huella)
window.webAuthnCreate = async function (optionsFromServer) {
    try {
        const options = JSON.parse(JSON.stringify(optionsFromServer));
        // Convierte campos Base64Url a ArrayBuffer
        options.challenge = base64UrlToArrayBuffer(options.challenge);
        options.user.id = base64UrlToArrayBuffer(options.user.id);
        if (options.excludeCredentials) {
            options.excludeCredentials = options.excludeCredentials.map(c => ({
                ...c, id: base64UrlToArrayBuffer(c.id)
            }));
        }

        const cred = await navigator.credentials.create({ publicKey: options });

        // Empaqueta para el backend (formato AuthenticatorAttestationRawResponse)
        return {
            id: cred.id,
            rawId: arrayBufferToBase64Url(cred.rawId),
            type: cred.type,
            extensions: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {},
            response: {
                attestationObject: arrayBufferToBase64Url(cred.response.attestationObject),
                clientDataJson: arrayBufferToBase64Url(cred.response.clientDataJSON)
            }
        };
    } catch (e) {
        return { _error: e.message || e.toString() };
    }
};

// 2026-09-14: el pedido de huella que está esperando (si hay uno). El teléfono acepta uno solo a
// la vez: si quedó uno colgado, el siguiente falla o también se cuelga.
let _webAuthnPendiente = null;

// Llama al browser para usar una credencial existente (login con huella).
// 2026-09-14: `esperaMaxMs` (opcional) corta la espera. En el WhatsApp del celu el lector a veces no
// aparecía y la pantalla quedaba "Tocá el lector…" hasta que el teléfono se rendía solo (~1 min).
// Ahora: cada pedido nuevo CANCELA el que haya quedado colgado, y si pasa el tiempo se corta y
// devuelve _error = "timeout". Sin el parámetro se comporta como antes (lo usa el fichador).
window.webAuthnGet = async function (optionsFromServer, esperaMaxMs) {
    let reloj = null;
    let control = null;
    try {
        const options = JSON.parse(JSON.stringify(optionsFromServer));
        options.challenge = base64UrlToArrayBuffer(options.challenge);
        if (options.allowCredentials) {
            options.allowCredentials = options.allowCredentials.map(c => ({
                ...c, id: base64UrlToArrayBuffer(c.id)
            }));
        }

        if (_webAuthnPendiente) { try { _webAuthnPendiente.abort('reemplazado'); } catch (_) { } }
        control = new AbortController();
        _webAuthnPendiente = control;
        let vencio = false;
        if (esperaMaxMs > 0) {
            reloj = setTimeout(() => { vencio = true; try { control.abort('timeout'); } catch (_) { } }, esperaMaxMs);
        }

        let cred;
        try {
            cred = await navigator.credentials.get({ publicKey: options, signal: control.signal });
        } catch (e) {
            if (vencio) return { _error: 'timeout', _nombre: 'timeout' };
            if (control.signal.aborted) return { _error: 'reemplazado', _nombre: 'reemplazado' };
            throw e;
        }

        return {
            id: cred.id,
            rawId: arrayBufferToBase64Url(cred.rawId),
            type: cred.type,
            extensions: cred.getClientExtensionResults ? cred.getClientExtensionResults() : {},
            response: {
                authenticatorData: arrayBufferToBase64Url(cred.response.authenticatorData),
                clientDataJson: arrayBufferToBase64Url(cred.response.clientDataJSON),
                signature: arrayBufferToBase64Url(cred.response.signature),
                userHandle: cred.response.userHandle ? arrayBufferToBase64Url(cred.response.userHandle) : null
            }
        };
    } catch (e) {
        return { _error: e.message || e.toString(), _nombre: e.name || '' };
    } finally {
        if (reloj) clearTimeout(reloj);
        if (control && _webAuthnPendiente === control) _webAuthnPendiente = null;
    }
};

// 2026-09-14: deja anotado en el registro del servidor web POR QUÉ falló la huella (el nombre del
// error que da el teléfono). Hasta hoy fallaba sin dejar rastro y no había cómo saber la causa.
// Es un pedido a una dirección que no existe: el servidor contesta cualquier cosa y lo anota.
window.webAuthnAnotarFallo = function (pantalla, nombre, detalle) {
    try {
        const q = new URLSearchParams({
            p: pantalla || '', n: nombre || '', d: (detalle || '').slice(0, 200),
            vis: document.visibilityState, foco: document.hasFocus() ? '1' : '0'
        });
        fetch('/diag/huella?' + q.toString(), { method: 'HEAD', keepalive: true, cache: 'no-store' }).catch(() => { });
    } catch (_) { }
};

// True si el browser soporta WebAuthn con autenticador de plataforma (huella, FaceID, Windows Hello)
window.webAuthnAvailable = async function () {
    if (!window.PublicKeyCredential) return false;
    try {
        return await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
    } catch (_) { return false; }
};
