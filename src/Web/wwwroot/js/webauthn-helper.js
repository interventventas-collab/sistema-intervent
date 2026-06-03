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

// Llama al browser para usar una credencial existente (login con huella)
window.webAuthnGet = async function (optionsFromServer) {
    try {
        const options = JSON.parse(JSON.stringify(optionsFromServer));
        options.challenge = base64UrlToArrayBuffer(options.challenge);
        if (options.allowCredentials) {
            options.allowCredentials = options.allowCredentials.map(c => ({
                ...c, id: base64UrlToArrayBuffer(c.id)
            }));
        }

        const cred = await navigator.credentials.get({ publicKey: options });

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
        return { _error: e.message || e.toString() };
    }
};

// True si el browser soporta WebAuthn con autenticador de plataforma (huella, FaceID, Windows Hello)
window.webAuthnAvailable = async function () {
    if (!window.PublicKeyCredential) return false;
    try {
        return await PublicKeyCredential.isUserVerifyingPlatformAuthenticatorAvailable();
    } catch (_) { return false; }
};
