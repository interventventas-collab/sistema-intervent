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
