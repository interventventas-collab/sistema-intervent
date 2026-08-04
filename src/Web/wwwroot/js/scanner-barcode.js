// 2026-07-24: lector de códigos de barras / QR con la cámara del celular (usa html5-qrcode).
// Lo usa la pantalla de prueba /scan-prueba para descubrir qué número traen las etiquetas
// de Flex. A futuro va a alimentar la carga de paradas al Mapeo por escaneo.
window.scannerBarcode = (function () {
  let scanner = null;
  let dotnetRef = null;
  let lastText = null;
  let lastAt = 0;

  async function start(elementId, ref) {
    dotnetRef = ref;
    if (scanner) { try { await stop(); } catch (e) {} }

    if (typeof Html5Qrcode === 'undefined' || typeof Html5QrcodeSupportedFormats === 'undefined') {
      if (dotnetRef) dotnetRef.invokeMethodAsync('OnScanError', 'No se pudo cargar el lector de códigos (revisá la conexión).');
      return;
    }

    // Formatos que aceptamos: QR y los códigos de barra 1D más comunes en etiquetas.
    const formats = [
      Html5QrcodeSupportedFormats.QR_CODE,
      Html5QrcodeSupportedFormats.CODE_128,
      Html5QrcodeSupportedFormats.CODE_39,
      Html5QrcodeSupportedFormats.CODE_93,
      Html5QrcodeSupportedFormats.EAN_13,
      Html5QrcodeSupportedFormats.EAN_8,
      Html5QrcodeSupportedFormats.ITF,
      Html5QrcodeSupportedFormats.DATA_MATRIX,
      Html5QrcodeSupportedFormats.PDF_417
    ];

    try {
      scanner = new Html5Qrcode(elementId, { formatsToSupport: formats, verbose: false });
      const config = { fps: 10, qrbox: { width: 280, height: 180 } };
      await scanner.start({ facingMode: 'environment' }, config, onSuccess, onFailure);
    } catch (err) {
      const msg = (err && err.message) ? err.message : ('' + err);
      if (dotnetRef) dotnetRef.invokeMethodAsync('OnScanError', 'No se pudo abrir la cámara: ' + msg);
    }
  }

  function onSuccess(decodedText, decodedResult) {
    const now = Date.now();
    // Evitar que el mismo código dispare 20 veces por segundo.
    if (decodedText === lastText && (now - lastAt) < 2500) return;
    lastText = decodedText; lastAt = now;

    let fmt = '';
    try {
      if (decodedResult && decodedResult.result && decodedResult.result.format) {
        fmt = decodedResult.result.format.formatName || '';
      }
    } catch (e) {}

    if (dotnetRef) dotnetRef.invokeMethodAsync('OnScanDetected', decodedText, fmt);
  }

  // Se dispara continuamente cuando NO hay código en cuadro. Lo ignoramos.
  function onFailure(err) {}

  async function stop() {
    if (scanner) {
      try { await scanner.stop(); } catch (e) {}
      try { await scanner.clear(); } catch (e) {}
      scanner = null;
    }
  }

  return { start, stop };
})();

// 2026-08-03: "Modo pistola" — lector de código de barras/QR FÍSICO (el escáner USB que se
// enchufa a la compu). Esos aparatos funcionan como un teclado: "escriben" el contenido del
// código muy rápido y al final mandan Enter. Acá lo escuchamos: mientras está prendido,
// juntamos las teclas y cuando llega el Enter le pasamos el código completo a Blazor.
// Se prende/apaga con el botón de la barra negra del Mapeo.
window.scannerWedge = (function () {
  let ref = null;
  let active = false;
  let buf = '';
  let lastAt = 0;

  // Tecla física (e.code) -> [caracter sin Shift, caracter con Shift] en un teclado US.
  // Cubre dígitos (fila y numérico) y todos los símbolos que aparecen en enlaces/QR
  // (barras, guiones, dos puntos, etc.). Las letras se resuelven aparte (Key*).
  const KEYMAP = {
    Digit0: ['0', ')'], Digit1: ['1', '!'], Digit2: ['2', '@'], Digit3: ['3', '#'], Digit4: ['4', '$'],
    Digit5: ['5', '%'], Digit6: ['6', '^'], Digit7: ['7', '&'], Digit8: ['8', '*'], Digit9: ['9', '('],
    Numpad0: ['0', '0'], Numpad1: ['1', '1'], Numpad2: ['2', '2'], Numpad3: ['3', '3'], Numpad4: ['4', '4'],
    Numpad5: ['5', '5'], Numpad6: ['6', '6'], Numpad7: ['7', '7'], Numpad8: ['8', '8'], Numpad9: ['9', '9'],
    NumpadDivide: ['/', '/'], NumpadMultiply: ['*', '*'], NumpadSubtract: ['-', '-'],
    NumpadAdd: ['+', '+'], NumpadDecimal: ['.', '.'],
    Minus: ['-', '_'], Equal: ['=', '+'],
    BracketLeft: ['[', '{'], BracketRight: [']', '}'], Backslash: ['\\', '|'],
    Semicolon: [';', ':'], Quote: ["'", '"'], Backquote: ['`', '~'],
    Comma: [',', '<'], Period: ['.', '>'], Slash: ['/', '?'], Space: [' ', ' ']
  };

  function onKey(e) {
    if (!active) return;
    // Mientras el modo está prendido, el teclado queda dedicado al escáner: no dejamos que
    // las teclas se escriban en ningún casillero ni disparen atajos de la página.
    e.preventDefault();
    e.stopPropagation();

    const now = (window.performance && performance.now) ? performance.now() : new Date().getTime();
    // El escáner tipea muy rápido (pocos ms entre teclas). Si pasó mucho tiempo desde la última,
    // arrancamos un código nuevo (así no se pega con restos de un escaneo anterior).
    if (now - lastAt > 300) buf = '';
    lastAt = now;

    // Fin de la lectura: el escáner manda Enter (de la fila o del teclado numérico).
    if (e.key === 'Enter' || e.code === 'Enter' || e.code === 'NumpadEnter') {
      const code = buf;
      buf = '';
      if (code && code.length >= 3 && ref) {
        ref.invokeMethodAsync('OnWedgeScan', code);
      }
      return;
    }

    // Leemos TODO por la TECLA FÍSICA (e.code), no por la letra/símbolo que "llega".
    // Así el código sale bien aunque la pistola esté configurada con otro idioma de
    // teclado (US, Latinoamérica, etc.) o el teclado numérico en modo flechas (NumLock
    // apagado). Los QR/enlaces son siempre ASCII de un teclado US, así que reconstruimos
    // exactamente eso: cada tecla física del layout US -> su caracter (sin Shift / con Shift).
    // Antes solo arreglábamos los NÚMEROS; por eso las etiquetas de MercadoLibre (que son
    // números) andaban, pero los comprobantes de venta/alquiler (enlaces con "/", "-", "_", etc.)
    // llegaban torcidos y el sistema no los reconocía.

    // Letras: por tecla física, mayúscula/minúscula según Shift.
    const letra = /^Key([A-Z])$/.exec(e.code || '');
    if (letra) { buf += e.shiftKey ? letra[1] : letra[1].toLowerCase(); return; }

    // Números y símbolos: por tecla física (layout US), variante sin Shift / con Shift.
    const par = KEYMAP[e.code];
    if (par) { buf += e.shiftKey ? par[1] : par[0]; return; }

    // Respaldo: si no reconocemos la tecla física, usamos el caracter que llegó.
    if (e.key && e.key.length === 1) buf += e.key;
  }

  function start(dotnetRef) {
    ref = dotnetRef;
    buf = '';
    lastAt = 0;
    if (!active) {
      active = true;
      document.addEventListener('keydown', onKey, true);
    }
  }

  function stop() {
    active = false;
    buf = '';
    try { document.removeEventListener('keydown', onKey, true); } catch (e) {}
    ref = null;
  }

  return { start, stop };
})();

// "Pip" al escanear: un beep corto + vibración. ok=true -> pip agudo/vibra corto;
// ok=false -> tono grave/vibración doble (para avisar que algo no anduvo).
window.scanBeep = function (ok) {
  try {
    var Ctx = window.AudioContext || window.webkitAudioContext;
    if (Ctx) {
      var ctx = new Ctx();
      var osc = ctx.createOscillator();
      var gain = ctx.createGain();
      var t = ctx.currentTime;
      osc.type = 'sine';
      osc.frequency.value = ok ? 1180 : 380;
      gain.gain.setValueAtTime(0.001, t);
      gain.gain.exponentialRampToValueAtTime(0.3, t + 0.01);
      gain.gain.exponentialRampToValueAtTime(0.001, t + (ok ? 0.14 : 0.28));
      osc.connect(gain); gain.connect(ctx.destination);
      osc.start(); osc.stop(t + (ok ? 0.15 : 0.3));
      setTimeout(function () { try { ctx.close(); } catch (e) {} }, 400);
    }
  } catch (e) {}
  try { if (navigator.vibrate) navigator.vibrate(ok ? 70 : [60, 40, 60]); } catch (e) {}
};
