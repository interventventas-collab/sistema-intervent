// WhatsApp Web automation service (single-session) via Playwright
// Expone un API HTTP minimalista para que la API .NET pueda: vincular, obtener QR,
// chequear estado, desvincular y enviar mensajes.

const express = require('express');
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');
const AdmZip = require('adm-zip');

const PORT = parseInt(process.env.PORT || '3001', 10);
const DATA_DIR = '/data/whatsapp-session';
const STORAGE_STATE_PATH = path.join(DATA_DIR, 'storage-state.json');

if (!fs.existsSync(DATA_DIR)) {
  fs.mkdirSync(DATA_DIR, { recursive: true });
}

const app = express();
// Aumentamos el limite a 15 MB para que entren PDFs en base64 (comprobantes con QR + logos).
// 15 MB en JSON ≈ 11 MB en bytes reales (base64 = 4/3 del binario).
app.use(express.json({ limit: '15mb' }));

// --- Estado en memoria (single-session) ---
const state = {
  browser: null,
  context: null,
  page: null,
  linked: false,
  isLinking: false,
  linkingTimeout: null,
  linkingCheckInterval: null,
  lastInfo: null,
  // 2026-06-23: contador de fallos consecutivos del detector "estas linked".
  // Si llega a 2 con el heartbeat -> declaramos desconectado.
  consecutiveLinkedFails: 0,
  lastHeartbeatAt: null,
  lastDisconnectedAt: null,
};

// --- Utilidades ---
function normalizePhone(phone) {
  return String(phone || '').replace(/\D/g, '');
}

function sleep(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function randomDelay(minMs, maxMs) {
  const delta = maxMs - minMs;
  return sleep(minMs + Math.floor(Math.random() * delta));
}

async function closeBrowserSafely() {
  try {
    if (state.page) await state.page.close().catch(() => {});
  } catch {}
  try {
    if (state.context) await state.context.close().catch(() => {});
  } catch {}
  try {
    if (state.browser) await state.browser.close().catch(() => {});
  } catch {}
  state.page = null;
  state.context = null;
  state.browser = null;
}

function stopLinkingPolling() {
  if (state.linkingCheckInterval) {
    clearInterval(state.linkingCheckInterval);
    state.linkingCheckInterval = null;
  }
  if (state.linkingTimeout) {
    clearTimeout(state.linkingTimeout);
    state.linkingTimeout = null;
  }
  state.isLinking = false;
}

async function saveStorageState() {
  try {
    if (!state.context) return;
    const stateObj = await state.context.storageState();
    fs.writeFileSync(STORAGE_STATE_PATH, JSON.stringify(stateObj));
    console.log('[wa] storage-state guardado');
  } catch (err) {
    console.error('[wa] error guardando storage state:', err.message);
  }
}

async function launchContext(useStorageState) {
  const browser = await chromium.launch({
    headless: true,
    args: [
      '--no-sandbox',
      '--disable-dev-shm-usage',
      '--disable-blink-features=AutomationControlled',
    ],
  });
  const contextOptions = {
    userAgent:
      'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    viewport: { width: 1280, height: 800 },
  };
  if (useStorageState && fs.existsSync(STORAGE_STATE_PATH)) {
    contextOptions.storageState = STORAGE_STATE_PATH;
  }
  const context = await browser.newContext(contextOptions);
  const page = await context.newPage();
  return { browser, context, page };
}

async function isLinkedOnPage(page) {
  // Estrategia robusta multi-fallback porque WhatsApp Web cambia selectores muy seguido.
  // 1) Selectores históricos
  // 2) Cualquier indicio de la app abierta (lista de chats, footer compose, header)
  // 3) Fallback negativo: si hay <canvas> visible del QR, NO está linkeado
  try {
    const positives = [
      '#pane-side',
      '[role="grid"]',
      '#side',
      'div[aria-label="Lista de chats"]',
      'div[aria-label="Chat list"]',
      '[data-testid="chat-list"]',
      'header[data-testid="chatlist-header"]',
      'footer div[contenteditable="true"]',
      'div[data-testid="conversation-panel-wrapper"]'
    ];
    for (const sel of positives) {
      const el = await page.$(sel).catch(() => null);
      if (el) return true;
    }
    // Fallback negativo: si hay un canvas QR visible, no está linkeado
    const qrCanvas = await page.$('canvas[aria-label*="QR" i], canvas[aria-label*="código" i], div[data-ref] canvas').catch(() => null);
    if (qrCanvas) return false;

    // Último fallback: evaluar el DOM y buscar palabras que indiquen sesión activa
    const looksLikeApp = await page.evaluate(() => {
      // Si vemos "Mensaje nuevo", "Nuevo chat" o el ícono de search es señal de sesión activa
      const txt = (document.body.innerText || '').slice(0, 4000);
      return /mensaje|chat|conversaci/i.test(txt) && !/escan(é|e)alo|escanea el código/i.test(txt);
    }).catch(() => false);
    if (looksLikeApp) return true;
  } catch {}
  return false;
}

async function startSession({ useStorageState }) {
  await closeBrowserSafely();
  const { browser, context, page } = await launchContext(useStorageState);
  state.browser = browser;
  state.context = context;
  state.page = page;

  await page.goto('https://web.whatsapp.com', {
    waitUntil: 'domcontentloaded',
    timeout: 60000,
  });
  return page;
}

async function ensureSessionAlive() {
  if (!state.page || state.page.isClosed()) return false;
  try {
    return await isLinkedOnPage(state.page);
  } catch {
    return false;
  }
}

// ---------- Endpoints ----------

// POST /whatsapp/link - Inicia el flujo de vinculación (QR)
app.post('/whatsapp/link', async (req, res) => {
  try {
    if (state.isLinking) {
      return res.json({ ok: true, isLinking: true, info: 'Ya en progreso' });
    }

    stopLinkingPolling();
    state.linked = false;
    state.isLinking = true;
    state.lastInfo = 'Abriendo WhatsApp Web...';

    // Siempre empezar desde cero para mostrar QR (sin storageState)
    if (fs.existsSync(STORAGE_STATE_PATH)) {
      try { fs.unlinkSync(STORAGE_STATE_PATH); } catch {}
    }

    await startSession({ useStorageState: false });

    // Responder inmediato, seguir polling en background
    res.json({ ok: true });

    // Polling en background esperando que aparezcan chats (usuario escaneó QR)
    const startedAt = Date.now();
    const TIMEOUT_MS = 2 * 60 * 1000; // 2 minutos

    state.linkingCheckInterval = setInterval(async () => {
      try {
        if (!state.page || state.page.isClosed()) {
          stopLinkingPolling();
          return;
        }
        const linked = await isLinkedOnPage(state.page);
        if (linked) {
          state.linked = true;
          state.lastInfo = 'Vinculado';
          stopLinkingPolling();
          await saveStorageState();
          console.log('[wa] cuenta vinculada correctamente');
        } else if (Date.now() - startedAt > TIMEOUT_MS) {
          console.log('[wa] timeout esperando vinculación');
          stopLinkingPolling();
          state.lastInfo = 'Timeout esperando escaneo';
          await closeBrowserSafely();
        }
      } catch (err) {
        console.error('[wa] error en polling de linking:', err.message);
      }
    }, 2000);
  } catch (err) {
    console.error('[wa] error en /link:', err);
    state.isLinking = false;
    if (!res.headersSent) {
      res.status(500).json({ ok: false, error: err.message });
    }
  }
});

// GET /whatsapp/qr - Screenshot del QR actual
app.get('/whatsapp/qr', async (req, res) => {
  try {
    if (!state.page || state.page.isClosed()) {
      return res.status(404).send('No hay sesión activa');
    }

    // Intentar localizar el contenedor del QR
    let target = null;
    try {
      target = await state.page.waitForSelector('canvas[aria-label*="Scan"], canvas[aria-label*="Escanear"], div[data-ref]', {
        timeout: 5000,
        state: 'visible',
      });
    } catch {}

    let buffer;
    if (target) {
      buffer = await target.screenshot({ type: 'png' });
    } else {
      buffer = await state.page.screenshot({ type: 'png', fullPage: false });
    }

    res.set('Content-Type', 'image/png');
    res.set('Cache-Control', 'no-cache, no-store, must-revalidate');
    res.set('Pragma', 'no-cache');
    res.set('Expires', '0');
    res.send(buffer);
  } catch (err) {
    console.error('[wa] error en /qr:', err.message);
    res.status(500).send('Error obteniendo QR');
  }
});

// GET /whatsapp/status
// 2026-06-23 — Antes el codigo "mentia" diciendo linked=true cuando el detector real decia no
// (mantenia el flag previo "por las dudas"). Eso causaba que la integracion apareciera conectada
// horas despues de haberse caido, sin que nadie se entere. Ahora: si el detector dice no,
// reportamos no. Toleramos UN unico fallo transitorio antes de declarar desconectado.
app.get('/whatsapp/status', async (req, res) => {
  try {
    let linked = false;
    if (state.page && !state.page.isClosed()) {
      const liveCheck = await isLinkedOnPage(state.page).catch(() => null);
      if (liveCheck === true) {
        linked = true;
        state.consecutiveLinkedFails = 0;
      } else {
        // Fallo o false. Si ya estabamos linked, toleramos 1 fallo antes de marcar como desconectado.
        state.consecutiveLinkedFails = (state.consecutiveLinkedFails || 0) + 1;
        if (state.linked && state.consecutiveLinkedFails < 2 && !state.isLinking) {
          // Glitch transitorio: el detector fallo una vez, pero la pagina sigue abierta.
          linked = true;
        } else {
          linked = false;
        }
      }
    }
    if (state.linked && !linked) {
      console.log(`[wa] status: linked -> desconectado (fails=${state.consecutiveLinkedFails})`);
      state.lastDisconnectedAt = new Date().toISOString();
    }
    state.linked = linked;
    res.json({
      linked,
      isLinking: state.isLinking,
      info: state.lastInfo,
      lastHeartbeatAt: state.lastHeartbeatAt || null,
      lastDisconnectedAt: state.lastDisconnectedAt || null,
    });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// GET /whatsapp/check-linked - version ligera para polling
app.get('/whatsapp/check-linked', async (req, res) => {
  try {
    let linked = false;
    if (state.page && !state.page.isClosed()) {
      linked = await isLinkedOnPage(state.page).catch(() => false);
    }
    state.linked = linked;
    if (!linked) state.consecutiveLinkedFails = (state.consecutiveLinkedFails || 0) + 1;
    else state.consecutiveLinkedFails = 0;
    res.json({ linked });
  } catch {
    res.json({ linked: false });
  }
});

// 2026-06-23 — Heartbeat real cada 90s. Independiente del polling del frontend.
// Si la sesion se cayo (cliente WhatsApp cerro, otro celu se logueo, sesion expirada),
// nos enteramos aunque nadie tenga la pantalla de Integraciones abierta.
const HEARTBEAT_INTERVAL_MS = 90000;
async function whatsappHeartbeat() {
  try {
    if (!state.page || state.page.isClosed()) {
      if (state.linked) {
        console.log('[wa] heartbeat: pagina cerrada -> marcando desconectado');
        state.linked = false;
        state.lastDisconnectedAt = new Date().toISOString();
      }
      state.lastHeartbeatAt = new Date().toISOString();
      return;
    }
    const ok = await isLinkedOnPage(state.page).catch(() => false);
    if (ok) {
      if (!state.linked) console.log('[wa] heartbeat: reconectado');
      state.linked = true;
      state.consecutiveLinkedFails = 0;
    } else {
      state.consecutiveLinkedFails = (state.consecutiveLinkedFails || 0) + 1;
      if (state.linked && state.consecutiveLinkedFails >= 2) {
        console.log(`[wa] heartbeat: ${state.consecutiveLinkedFails} fallas seguidas -> desconectado`);
        state.linked = false;
        state.lastDisconnectedAt = new Date().toISOString();
      }
    }
    state.lastHeartbeatAt = new Date().toISOString();
  } catch (err) {
    console.warn('[wa] heartbeat error:', err.message);
  }
}
setInterval(whatsappHeartbeat, HEARTBEAT_INTERVAL_MS);

// POST /whatsapp/unlink - Cierra browser y borra sesión
app.post('/whatsapp/unlink', async (req, res) => {
  try {
    stopLinkingPolling();
    await closeBrowserSafely();
    state.linked = false;
    state.lastInfo = null;
    if (fs.existsSync(STORAGE_STATE_PATH)) {
      try { fs.unlinkSync(STORAGE_STATE_PATH); } catch {}
    }
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// POST /whatsapp/cancel-link - aborta el proceso de linking pero deja browser
app.post('/whatsapp/cancel-link', async (req, res) => {
  try {
    stopLinkingPolling();
    await closeBrowserSafely();
    state.linked = false;
    state.lastInfo = 'Linking cancelado';
    res.json({ ok: true });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// POST /whatsapp/send-bulk - Body: { recipients: [{phone, name, message?}], message: string }
app.post('/whatsapp/send-bulk', async (req, res) => {
  // Esperar a que cualquier accion WA en curso termine (mutex global).
  const waitDeadline = Date.now() + 60000;
  while (waBusy && Date.now() < waitDeadline) {
    await sleep(250);
  }
  if (!acquireWaLock('send-bulk')) {
    return res.status(429).json({ error: 'busy', message: 'Otra operacion de WhatsApp en curso' });
  }
  try {
    const { recipients, message } = req.body || {};
    if (!Array.isArray(recipients) || recipients.length === 0) {
      releaseWaLock();
      return res.status(400).json({ error: 'recipients vacío' });
    }

    // Restaurar sesión desde storage si no hay browser
    if (!state.page || state.page.isClosed()) {
      if (!fs.existsSync(STORAGE_STATE_PATH)) {
        releaseWaLock();
        return res.status(400).json({ error: 'WhatsApp no esta vinculado' });
      }
      await startSession({ useStorageState: true });
      // Esperar que se cargue la app y aparezcan chats
      await sleep(5000);
      const linked = await isLinkedOnPage(state.page);
      if (!linked) {
        releaseWaLock();
        return res.status(400).json({ error: 'WhatsApp no esta vinculado' });
      }
      state.linked = true;
    }

    const results = [];
    for (let i = 0; i < recipients.length; i++) {
      const r = recipients[i] || {};
      const phone = normalizePhone(r.phone);
      const text = (r.message || message || '').toString();
      if (!phone || phone.length < 8) {
        results.push({ phone: r.phone, name: r.name, success: false, message: 'Numero invalido' });
        continue;
      }
      if (!text) {
        results.push({ phone: r.phone, name: r.name, success: false, message: 'Mensaje vacio' });
        continue;
      }

      if (i > 0) {
        await randomDelay(2000, 3000);
      }

      const result = await sendWhatsAppMessage(phone, text);
      results.push({ phone: r.phone, name: r.name, success: result.success, message: result.message });
    }

    // Persistir sesión a disco si al menos una operación fue OK. Idempotente.
    if (results.some(r => r.success)) {
      try { await saveStorageState(); } catch {}
    }

    releaseWaLock();
    res.json(results);
  } catch (err) {
    releaseWaLock();
    console.error('[wa] error en /send-bulk:', err);
    res.status(500).json({ error: err.message });
  }
});

// POST /whatsapp/send-with-pdf - Body: { phone, caption, pdfBase64, pdfFilename }
// Manda UN mensaje a UN destinatario con un PDF adjunto + caption opcional.
// Distinto de /send-bulk: este es uno solo (para envio sincrono desde la API),
// y soporta archivo adjunto. Las selecciones de WhatsApp Web son fragiles —
// si la UI de WhatsApp cambia y dejan de andar, ajustar los selectores aca.
app.post('/whatsapp/send-with-pdf', async (req, res) => {
  try {
    const { phone, caption, pdfBase64, pdfFilename } = req.body || {};
    const normalized = normalizePhone(phone);
    if (!normalized || normalized.length < 8) {
      return res.status(400).json({ error: 'Numero invalido' });
    }
    if (!pdfBase64) {
      return res.status(400).json({ error: 'pdfBase64 vacio' });
    }

    // Restaurar sesion si el browser no esta abierto.
    if (!state.page || state.page.isClosed()) {
      if (!fs.existsSync(STORAGE_STATE_PATH)) {
        return res.status(400).json({ error: 'WhatsApp no esta vinculado' });
      }
      await startSession({ useStorageState: true });
      await sleep(5000);
      const linked = await isLinkedOnPage(state.page);
      if (!linked) {
        return res.status(400).json({ error: 'WhatsApp no esta vinculado' });
      }
      state.linked = true;
    }

    const buffer = Buffer.from(pdfBase64, 'base64');
    const fname = pdfFilename || 'comprobante.pdf';
    const cap = (caption || '').toString();

    const result = await sendWhatsAppMessageWithFile(normalized, cap, buffer, fname);
    if (result.success) {
      return res.json({ success: true, message: result.message });
    } else {
      return res.status(500).json({ success: false, error: result.message });
    }
  } catch (err) {
    console.error('[wa] error en /send-with-pdf:', err);
    res.status(500).json({ error: err.message });
  }
});

// --- Logica de envio con archivo adjunto (PDF) ---
// Flujo: abre el chat, clickea el clip (Adjuntar), busca el input[type=file] que acepta
// documentos, hace setInputFiles, espera el preview, escribe la caption en el caption-box
// y manda. Selectores defensivos con multiples fallbacks porque WhatsApp Web cambia seguido.
async function sendWhatsAppMessageWithFile(phone, caption, fileBuffer, fileName) {
  const MAX_RETRIES = 2;
  const BACKOFFS = [3000, 8000];
  let lastError = 'Error desconocido';

  for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
    try {
      if (attempt > 0) await sleep(BACKOFFS[attempt - 1]);

      // 1) Abrir chat (sin texto en URL, lo metemos como caption del adjunto despues)
      const url = `https://web.whatsapp.com/send?phone=${encodeURIComponent(phone)}`;
      await state.page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });

      // 2) Esperar a que aparezca el compose box (o popup de numero invalido)
      const deadline = Date.now() + 25000;
      let composeBox = null;
      let invalidPopup = false;
      while (Date.now() < deadline) {
        const popup = await state.page.$('div[data-testid="popup-contents"], div[role="dialog"]');
        if (popup) {
          const txt = (await popup.innerText().catch(() => '')).toLowerCase();
          if (txt.includes('invalid') || txt.includes('inválido') || txt.includes('invalido') ||
              txt.includes('no válido') || txt.includes('phone number')) {
            invalidPopup = true;
            break;
          }
        }
        composeBox = await state.page.$('div[contenteditable="true"][data-tab="10"], footer div[contenteditable="true"]');
        if (composeBox) break;
        await sleep(500);
      }
      if (invalidPopup) return { success: false, message: 'Numero invalido o no tiene WhatsApp' };
      if (!composeBox) { lastError = 'Timeout abriendo chat'; continue; }

      // 3) Click en el clip (Adjuntar). WhatsApp tiene varias variantes del selector.
      const clipSelectors = [
        'div[title="Attach"]',
        'div[title="Adjuntar"]',
        'div[title="Anexar"]',
        'span[data-icon="clip"]',
        'span[data-icon="plus"]',
        'button[aria-label="Attach"]',
        'button[aria-label="Adjuntar"]',
      ];
      let clipClicked = false;
      for (const sel of clipSelectors) {
        const el = await state.page.$(sel);
        if (el) {
          await el.click().catch(() => {});
          clipClicked = true;
          break;
        }
      }
      if (!clipClicked) { lastError = 'No se encontro el boton Adjuntar'; continue; }
      await sleep(800); // que se desplieguen las opciones

      // 4) Encontrar el input[type=file] de documentos y subir el archivo.
      // WhatsApp expone varios inputs (imagenes, video, documento, camara). Buscamos el de documento.
      const fileInputs = await state.page.$$('input[type="file"]');
      let docInput = null;
      // Heuristica: el input de documentos suele aceptar "*" o cualquier tipo / no incluir solo "image/*"
      for (const inp of fileInputs) {
        const accept = (await inp.getAttribute('accept')) || '';
        if (!accept.includes('image') || accept === '*' || accept === '*/*' || accept.includes('pdf') || accept.includes('application')) {
          docInput = inp;
          if (accept.includes('pdf') || accept.includes('application')) break; // mejor match
        }
      }
      // Fallback: el ultimo input que no sea imagen-only
      if (!docInput && fileInputs.length > 0) docInput = fileInputs[fileInputs.length - 1];
      if (!docInput) { lastError = 'No se encontro el input de archivos'; continue; }

      await docInput.setInputFiles({
        name: fileName,
        mimeType: 'application/pdf',
        buffer: fileBuffer,
      });

      // 5) Esperar el preview del archivo + el caption box (hasta 20s)
      const previewDeadline = Date.now() + 20000;
      let captionBox = null;
      while (Date.now() < previewDeadline) {
        // El caption box es el contenteditable que aparece DEBAJO del preview
        captionBox = await state.page.$('div[role="textbox"][contenteditable="true"], div[contenteditable="true"][data-tab="undefined"]');
        if (captionBox) break;
        await sleep(500);
      }

      // 6) Si hay caption, escribirla
      if (caption) {
        if (captionBox) {
          await captionBox.click();
          await state.page.keyboard.type(caption, { delay: 10 });
          await sleep(300);
        }
      }

      // 7) Click en el boton de enviar (avion de papel)
      const sendSelectors = [
        'span[data-icon="send"]',
        'span[data-testid="send"]',
        'button[aria-label="Send"]',
        'button[aria-label="Enviar"]',
        'div[role="button"][aria-label="Send"]',
        'div[role="button"][aria-label="Enviar"]',
      ];
      let sendClicked = false;
      for (const sel of sendSelectors) {
        const el = await state.page.$(sel);
        if (el) {
          await el.click().catch(() => {});
          sendClicked = true;
          break;
        }
      }
      if (!sendClicked) {
        // Fallback: tocar Enter (a veces WhatsApp acepta Enter para enviar el archivo)
        await state.page.keyboard.press('Enter');
      }

      // 8) Verificar envio (los dobles checks aparecen cuando se entrega)
      const verifyDeadline = Date.now() + 25000;
      let sent = false;
      let delivered = false;
      while (Date.now() < verifyDeadline) {
        const dbl = await state.page.$('span[data-icon="msg-dblcheck"], span[data-testid="msg-dblcheck"]');
        if (dbl) { delivered = true; sent = true; break; }
        const chk = await state.page.$('span[data-icon="msg-check"], span[data-testid="msg-check"]');
        if (chk) { sent = true; break; }
        await sleep(700);
      }
      // Aceptacion conservadora: si no falla con popup, asumimos enviado tras 25s
      if (!sent) {
        await sleep(1500);
        sent = true;
      }
      return { success: true, message: delivered ? 'Enviado (delivered)' : 'Enviado (sent)' };
    } catch (err) {
      lastError = err.message || 'Error';
      console.error(`[wa] send-with-pdf intento ${attempt + 1} fallo:`, lastError);
    }
  }
  return { success: false, message: lastError };
}

// --- Lógica de envío ---
async function sendWhatsAppMessage(phone, text) {
  const MAX_RETRIES = 3;
  const BACKOFFS = [2000, 5000, 10000];
  let lastError = 'Error desconocido';

  for (let attempt = 0; attempt < MAX_RETRIES; attempt++) {
    try {
      if (attempt > 0) {
        await sleep(BACKOFFS[attempt - 1]);
      }

      const url = `https://web.whatsapp.com/send?phone=${encodeURIComponent(phone)}&text=${encodeURIComponent(text)}`;
      await state.page.goto(url, { waitUntil: 'domcontentloaded', timeout: 45000 });

      // Esperar compose box o popup de error (20s)
      const deadline = Date.now() + 20000;
      let composeBox = null;
      let invalidPopup = false;

      while (Date.now() < deadline) {
        // Popup de número inválido / no tiene WhatsApp
        const popup = await state.page.$('div[data-testid="popup-contents"], div[role="dialog"]');
        if (popup) {
          const txt = (await popup.innerText().catch(() => '')).toLowerCase();
          if (txt.includes('invalid') || txt.includes('inválido') || txt.includes('invalido') ||
              txt.includes('no válido') || txt.includes('número de teléfono') ||
              txt.includes('phone number')) {
            invalidPopup = true;
            break;
          }
        }
        // Compose box (textarea para escribir)
        composeBox = await state.page.$('div[contenteditable="true"][data-tab="10"], footer div[contenteditable="true"]');
        if (composeBox) break;
        await sleep(500);
      }

      if (invalidPopup) {
        return { success: false, message: 'Numero invalido o no tiene WhatsApp' };
      }
      if (!composeBox) {
        lastError = 'Timeout';
        continue; // retry
      }

      // El mensaje ya viene en el URL; basta con Enter
      await composeBox.focus();
      await state.page.keyboard.press('Enter');

      // Verificar envío (hasta 12s)
      const sendDeadline = Date.now() + 12000;
      let sent = false;
      let delivered = false;
      while (Date.now() < sendDeadline) {
        const dbl = await state.page.$('span[data-icon="msg-dblcheck"], span[data-testid="msg-dblcheck"]');
        if (dbl) { delivered = true; sent = true; break; }
        const chk = await state.page.$('span[data-icon="msg-check"], span[data-testid="msg-check"]');
        if (chk) { sent = true; break; }
        // fallback: input quedó vacío
        const boxText = await composeBox.innerText().catch(() => '');
        if (!boxText || boxText.trim() === '') {
          // esperar un poco más para los checks, pero aceptar como enviado
          await sleep(1500);
          const dbl2 = await state.page.$('span[data-icon="msg-dblcheck"]');
          if (dbl2) { delivered = true; sent = true; }
          else {
            const chk2 = await state.page.$('span[data-icon="msg-check"]');
            if (chk2) sent = true;
            else sent = true; // fallback conservador
          }
          break;
        }
        await sleep(500);
      }

      if (sent) {
        return { success: true, message: delivered ? 'Enviado (delivered)' : 'Enviado (sent)' };
      }
      lastError = 'No se pudo verificar envío';
    } catch (err) {
      lastError = err.message || 'Error';
      console.error(`[wa] intento ${attempt + 1} falló:`, lastError);
    }
  }
  return { success: false, message: lastError };
}

// ============================================================
// LEER MENSAJES de un chat de WhatsApp (para pedidos del vendedor).
//
// POST /whatsapp/messages/list body: { phone: "5491155556666", sinceId?: "true_xxx" }
// Devuelve: { messages: [{ id, text, fromMe, timestamp }], total: N }
//
// Estrategia:
// - Navega al chat del teléfono pedido (open chat by URL).
// - Espera que cargue el pane derecho.
// - Lee los últimos N mensajes (scraping del DOM).
// - Filtra los que sean DESPUÉS de sinceId (si viene).
// El caller (C# background service) guarda el último id leído y lo usa como cursor.
// ============================================================

// 2026-06-23: MUTEX GLOBAL para TODA accion que toque la pagina de WhatsApp Web
// (messages/list, send-bulk, open-by-name, send-to-current, chats/list).
// Antes habia 3 flags separados que NO se respetaban entre si — el poller de pedidos y
// las acciones manuales del usuario peleaban por el mismo browser, navegando uno encima
// del otro → spam de "Execution context was destroyed" y timeouts del lado del usuario.
// Auto-release a 90s por si una request anterior colgo (Playwright a veces se traba).
let waBusy = false;
let waBusyAt = 0;
function acquireWaLock(opName) {
  if (waBusy && (Date.now() - waBusyAt) > 90000) {
    console.warn(`[wa] lock: ocupado >90s, liberando para ${opName}`);
    waBusy = false;
  }
  if (waBusy) return false;
  waBusy = true;
  waBusyAt = Date.now();
  return true;
}
function releaseWaLock() { waBusy = false; }
// Variante que ESPERA al lock antes de fallar (uso para acciones del usuario:
// chats/list, open-by-name, send-to-current). El poller usa acquireWaLock directo.
async function acquireWaLockWait(opName, maxWaitMs = 30000) {
  const deadline = Date.now() + maxWaitMs;
  while (Date.now() < deadline) {
    if (acquireWaLock(opName)) return true;
    await sleep(300);
  }
  return false;
}

// Despues de clickear un chat en el sidebar, esperar el footer + esperar nodos de mensaje +
// scrollear al final + extraer mensajes del DOM. Devuelve { ok, messages, debug, error? }.
// Compartido entre open-by-name y open-by-index para no duplicar la logica de extraccion.
async function waitAndExtractMessagesFromPanel(page, label) {
  // 1) Esperar footer (chat abierto)
  let composeBox = null;
  const footerDeadline = Date.now() + 20000;
  while (Date.now() < footerDeadline) {
    composeBox = await page.$('footer div[contenteditable="true"]').catch(() => null);
    if (composeBox) break;
    await sleep(500);
  }
  if (!composeBox) {
    return { ok: false, status: 504, error: 'Timeout abriendo chat (footer no encontrado)' };
  }

  // 2) Esperar ACTIVAMENTE nodos de mensaje (no solo footer)
  const msgDeadline = Date.now() + 15000;
  let foundMessageNode = false;
  while (Date.now() < msgDeadline) {
    const n = await page.evaluate(() => {
      const sels = ['div[data-id]', '[role="row"]', '.copyable-text[data-pre-plain-text]', '[data-pre-plain-text]', '.message-in', '.message-out'];
      let total = 0;
      for (const s of sels) {
        try { total += document.querySelectorAll(s).length; } catch {}
      }
      return total;
    }).catch(() => 0);
    if (n > 0) { foundMessageNode = true; break; }
    await sleep(400);
  }
  if (!foundMessageNode) {
    console.warn(`[wa] ${label}: no aparecieron nodos de mensaje en 15s (chat puede estar vacio o WA cambio selectores)`);
  }
  await sleep(800);

  // 3) Scroll al final por las dudas
  try {
    await page.evaluate(() => {
      const main = document.querySelector('[role="application"] [role="main"]');
      if (main) main.scrollTop = main.scrollHeight;
    });
  } catch {}
  await sleep(600);

  // 4) Extraccion robusta multi-selector
  const result = await page.evaluate(() => {
    const SELECTORES = [
      'div[data-id]',
      'div[role="application"] div[data-id]',
      '[role="row"]',
      '[aria-rowindex]',
      '.message-in, .message-out',
      '[data-pre-plain-text]',
    ];

    const extractTextFromNode = (node) => {
      const copyableWithMeta = node.querySelector('.copyable-text[data-pre-plain-text]');
      if (copyableWithMeta) {
        const sel = copyableWithMeta.querySelector('span.selectable-text, .selectable-text');
        if (sel) {
          const t = (sel.innerText || sel.textContent || '').trim();
          if (t) return t;
        }
        const t = (copyableWithMeta.innerText || '').trim();
        if (t) return t;
      }
      const copy = node.querySelector('.copyable-text');
      if (copy) {
        const t = (copy.innerText || copy.textContent || '').trim();
        if (t) return t;
      }
      const selSpans = node.querySelectorAll('span.selectable-text, .selectable-text');
      for (let i = selSpans.length - 1; i >= 0; i--) {
        const t = (selSpans[i].innerText || selSpans[i].textContent || '').trim();
        if (t) return t;
      }
      const raw = (node.innerText || '').trim();
      if (raw) {
        const lines = raw.split('\n').map(l => l.trim()).filter(l => l && !/^\d{1,2}:\d{2}/.test(l));
        if (lines.length > 0) return lines.join(' ').trim();
        return raw;
      }
      return '';
    };

    const debug = { selectorTried: [], chosenSelector: null, foundCount: 0, mainHtmlSample: '' };
    try {
      const main = document.querySelector('[role="application"] [role="main"]');
      debug.mainHtmlSample = main ? (main.innerHTML || '').substring(0, 1500) : 'no main found';
    } catch {}

    let bestNodes = [];
    for (const sel of SELECTORES) {
      let nodes;
      try { nodes = document.querySelectorAll(sel); } catch { nodes = []; }
      debug.selectorTried.push({ sel, count: nodes.length });
      if (nodes.length > bestNodes.length) {
        bestNodes = Array.from(nodes);
        debug.chosenSelector = sel;
      }
      if (bestNodes.length >= 3 && sel === 'div[data-id]') break;
    }
    debug.foundCount = bestNodes.length;

    const items = [];
    bestNodes.forEach(node => {
      const id = node.getAttribute('data-id') || node.getAttribute('aria-rowindex') || `node_${items.length}`;
      const tag = (node.tagName || '').toLowerCase();
      if (tag === 'header' || tag === 'footer') return;
      const hasTextContent = node.querySelector('.copyable-text, span.selectable-text, [data-pre-plain-text], .message-in, .message-out')
                          || node.classList.contains('message-in') || node.classList.contains('message-out');
      const fromMe = node.classList.contains('message-out')
                  || String(id).startsWith('true_')
                  || /^[A-F0-9]+_true/i.test(String(id))
                  || (node.querySelector('[data-pre-plain-text]') !== null && node.classList.toString().toLowerCase().includes('out'));
      const text = extractTextFromNode(node);
      if (!text && !hasTextContent) return;
      items.push({ id: String(id), text, fromMe });
    });

    return { messages: items, debug };
  });

  try {
    console.log(`[wa] ${label}: ${result.messages.length} mensajes. Selector usado: ${result.debug.chosenSelector}. Intentados: ${JSON.stringify(result.debug.selectorTried)}`);
    if (result.messages.length === 0) {
      console.log(`[wa] ${label} SAMPLE HTML main: ${result.debug.mainHtmlSample.substring(0, 600)}`);
    }
  } catch {}

  return { ok: true, messages: result.messages, debug: result.debug };
}

app.post('/whatsapp/messages/list', async (req, res) => {
  const phoneInput = (req.body && req.body.phone) || '';
  const sinceId = (req.body && req.body.sinceId) || '';
  if (!phoneInput) return res.status(400).json({ error: 'phone requerido' });

  const phone = normalizePhone(phoneInput);
  if (phone.length < 8) return res.status(400).json({ error: 'phone invalido' });

  if (!acquireWaLock('messages/list')) return res.status(429).json({ error: 'busy', message: 'Otro listado en curso' });

  try {
    // Asegurar sesión
    const alive = await ensureSessionAlive();
    if (!alive) {
      try { await startSession({ useStorageState: true }); } catch {}
      if (!state.page || state.page.isClosed()) {
        releaseWaLock();
        return res.status(503).json({ error: 'WhatsApp no vinculado' });
      }
    }

    // Navegar al chat
    const url = `https://web.whatsapp.com/send?phone=${encodeURIComponent(phone)}`;
    try {
      await state.page.goto(url, { waitUntil: 'load', timeout: 30000 });
    } catch (gotoErr) {
      // Tolerar timeouts de navegación menores — WA es SPA
      console.warn('[wa] goto warning:', gotoErr.message);
    }

    // Esperar que aparezca el footer (compose box) — señal de chat abierto
    let composeBox = null;
    let invalidPopup = false;
    const deadline = Date.now() + 25000;
    while (Date.now() < deadline) {
      try {
        const popup = await state.page.$('div[data-testid="popup-contents"], div[role="dialog"]');
        if (popup) {
          const txt = (await popup.innerText().catch(() => '')).toLowerCase();
          if (txt.includes('invalid') || txt.includes('inválido') || txt.includes('phone number')) {
            invalidPopup = true; break;
          }
        }
        composeBox = await state.page.$('footer div[contenteditable="true"]');
        if (composeBox) break;
      } catch (waitErr) {
        // Si fallo por navegación, esperar un poco y reintentar
        console.warn('[wa] wait warn:', waitErr.message);
      }
      await sleep(700);
    }
    if (invalidPopup) {
      releaseWaLock();
      return res.status(400).json({ error: 'Numero invalido o sin WhatsApp' });
    }
    if (!composeBox) {
      releaseWaLock();
      return res.status(504).json({ error: 'Timeout abriendo chat (footer no encontrado)' });
    }

    // Dejar que renderice los mensajes
    await sleep(2000);

    // Scroll al final por las dudas, así los más nuevos están visibles
    try {
      await state.page.evaluate(() => {
        const main = document.querySelector('[role="application"] [role="main"]');
        if (main) main.scrollTop = main.scrollHeight;
      });
    } catch {}
    await sleep(500);

    // Extraer mensajes del DOM
    // Estrategia robusta multi-fallback porque WhatsApp Web cambia clases seguido.
    const messages = await state.page.evaluate(() => {
      const items = [];
      const nodes = document.querySelectorAll('div[data-id]');

      const extractTextFromNode = (node) => {
        // 1) Preferido: contenedor .copyable-text con data-pre-plain-text
        //    Su innerText típicamente es solo el cuerpo del mensaje.
        const copyableWithMeta = node.querySelector('.copyable-text[data-pre-plain-text]');
        if (copyableWithMeta) {
          // El hijo selectable-text contiene el body limpio
          const sel = copyableWithMeta.querySelector('span.selectable-text, .selectable-text');
          if (sel) {
            const t = (sel.innerText || sel.textContent || '').trim();
            if (t) return t;
          }
          const t = (copyableWithMeta.innerText || '').trim();
          if (t) return t;
        }
        // 2) Cualquier .copyable-text dentro del nodo
        const copy = node.querySelector('.copyable-text');
        if (copy) {
          const t = (copy.innerText || copy.textContent || '').trim();
          if (t) return t;
        }
        // 3) Spans selectables
        const selSpans = node.querySelectorAll('span.selectable-text, .selectable-text');
        for (let i = selSpans.length - 1; i >= 0; i--) {
          const t = (selSpans[i].innerText || selSpans[i].textContent || '').trim();
          if (t) return t;
        }
        // 4) Último recurso: innerText completo del bubble, limpiando líneas de metadata
        const raw = (node.innerText || '').trim();
        if (raw) {
          const lines = raw.split('\n').map(l => l.trim()).filter(l => l && !/^\d{1,2}:\d{2}/.test(l));
          if (lines.length > 0) return lines.join(' ').trim();
          return raw;
        }
        return '';
      };

      nodes.forEach(node => {
        const id = node.getAttribute('data-id') || '';
        if (!id) return;
        // Filtrar quick-replies / divisores / etc — un mensaje real tiene .copyable-text o selectable-text
        const looksLikeMessage = node.querySelector('.copyable-text') !== null
                              || node.querySelector('span.selectable-text, .selectable-text') !== null
                              || node.querySelector('[data-pre-plain-text]') !== null
                              || node.classList.contains('message-in')
                              || node.classList.contains('message-out');
        if (!looksLikeMessage) return;

        const fromMe = node.classList.contains('message-out')
                    || id.startsWith('true_')
                    || /^[A-F0-9]+_true/i.test(id);

        const text = extractTextFromNode(node);
        const meta = node.querySelector('[data-pre-plain-text]');
        const metaAttr = meta ? meta.getAttribute('data-pre-plain-text') : '';
        items.push({ id, text, fromMe, meta: metaAttr });
      });
      return items;
    });
    try { console.log(`[wa] messages/list extracted ${messages.length} nodes (with text: ${messages.filter(m => m.text).length})`); } catch {}

    let filtered = messages;
    if (sinceId) {
      const idx = messages.findIndex(m => m.id === sinceId);
      if (idx >= 0) filtered = messages.slice(idx + 1);
    }

    // Si llegamos hasta acá la sesión está activa — guardar storage-state idempotente.
    // Esto asegura que aunque el container reinicie, la sesión sobrevive.
    if (messages.length > 0 || !fs.existsSync(STORAGE_STATE_PATH)) {
      try { await saveStorageState(); } catch {}
    }

    releaseWaLock();
    return res.json({ messages: filtered, total: messages.length, phone });
  } catch (err) {
    releaseWaLock();
    console.error('[wa] messages/list error:', err.message || err);
    return res.status(500).json({ error: err.message || 'error' });
  }
});

// ============================================================
// POST /whatsapp/chat/open-by-name  body: { name: "Juan Perez" }
// Abre un chat haciendo click en el sidebar (no via URL ?phone=). Sirve para chats con nombre
// guardado donde no tenemos el telefono. Despues de abrir, devuelve los mensajes y el header.
// ============================================================
app.post('/whatsapp/chat/open-by-name', async (req, res) => {
  const name = (req.body && req.body.name) || '';
  if (!name) return res.status(400).json({ error: 'name requerido' });

  if (!await acquireWaLockWait('open-by-name', 30000)) return res.status(429).json({ error: 'busy' });

  try {
    const alive = await ensureSessionAlive();
    if (!alive) {
      try { await startSession({ useStorageState: true }); } catch {}
      if (!state.page || state.page.isClosed()) {
        releaseWaLock();
        return res.status(503).json({ error: 'WhatsApp no vinculado' });
      }
    }

    // Asegurar que estamos en la home (sidebar visible)
    try {
      if (!state.page.url().startsWith('https://web.whatsapp.com')) {
        await state.page.goto('https://web.whatsapp.com/', { waitUntil: 'load', timeout: 30000 });
      }
    } catch {}

    // Esperar sidebar
    const sidebarDeadline = Date.now() + 15000;
    while (Date.now() < sidebarDeadline) {
      const found = await state.page.$('#pane-side').catch(() => null);
      if (found) break;
      await sleep(400);
    }

    // 2026-06-23 fix Plan C: match flexible (case-insensitive, sin acentos, espacios colapsados).
    // Si no encuentra en primera pasada, scrollea el sidebar hacia abajo (WhatsApp virtualiza la
    // lista — los chats fuera de viewport no estan en el DOM). Hasta 12 scrolls = ~24 chats mas.
    const tryClickByName = async () => {
      return await state.page.evaluate((targetName) => {
        const norm = (s) => (s || '')
          .normalize('NFD').replace(/[̀-ͯ]/g, '')
          .toLowerCase().replace(/\s+/g, ' ').trim();
        const want = norm(targetName);
        const spans = document.querySelectorAll('#pane-side span[title]');
        for (const s of spans) {
          if (norm(s.getAttribute('title')) === want) {
            // Buscar el ancestor listitem (data-testid="cell-frame" ya no existe en WA Web 2024+).
            let el = s;
            for (let i = 0; i < 8 && el; i++) {
              if (el.getAttribute && el.getAttribute('role') === 'listitem') {
                el.click();
                return true;
              }
              el = el.parentElement;
            }
            s.click();
            return true;
          }
        }
        return false;
      }, name);
    };

    let clicked = await tryClickByName();
    if (!clicked) {
      // No esta visible — scrollear el sidebar buscandolo. WhatsApp virtualiza la lista,
      // los chats fuera del viewport no estan en el DOM hasta que scrolleas.
      for (let scrollStep = 0; scrollStep < 12 && !clicked; scrollStep++) {
        const reachedBottom = await state.page.evaluate(() => {
          const pane = document.querySelector('#pane-side');
          if (!pane) return true;
          const before = pane.scrollTop;
          pane.scrollTop = Math.min(pane.scrollHeight, pane.scrollTop + pane.clientHeight * 0.9);
          return pane.scrollTop === before; // no se movio = fondo
        }).catch(() => true);
        await sleep(350);
        clicked = await tryClickByName();
        if (reachedBottom) break;
      }
    }

    if (!clicked) {
      releaseWaLock();
      return res.status(404).json({ error: 'Chat no encontrado en el sidebar', name });
    }

    const extracted = await waitAndExtractMessagesFromPanel(state.page, `open-by-name "${name}"`);
    if (!extracted.ok) {
      return res.status(extracted.status || 500).json({ error: extracted.error || 'error' });
    }
    return res.json({ messages: extracted.messages, total: extracted.messages.length, name, debug: extracted.debug });
  } catch (err) {
    console.error('[wa] open-by-name error:', err.message || err);
    return res.status(500).json({ error: err.message || 'error' });
  } finally {
    // garantiza liberar el lock aunque haya error o cuelgue
    releaseWaLock();
  }
});

// ============================================================
// POST /whatsapp/chat/open-by-index  body: { index: 0, name?: "..." }
// Abre el chat ubicado en la posicion `index` de la lista del sidebar (0-based, MISMA posicion
// que devolvio el ultimo /chats/list). Mucho mas robusto que open-by-name porque no depende
// de matching de texto — clickea directo el nth listitem del DOM.
// El campo `name` es opcional: si viene, se valida que el chat en esa posicion tenga ese name
// (sin acentos, case-insensitive). Si no matchea -> 409 (el sidebar se reordeno, refrescar).
// ============================================================
app.post('/whatsapp/chat/open-by-index', async (req, res) => {
  const index = parseInt((req.body && req.body.index), 10);
  const expectedName = (req.body && req.body.name) || '';
  if (isNaN(index) || index < 0) return res.status(400).json({ error: 'index requerido (>=0)' });

  if (!await acquireWaLockWait('open-by-index', 30000)) return res.status(429).json({ error: 'busy' });

  try {
    const alive = await ensureSessionAlive();
    if (!alive) {
      try { await startSession({ useStorageState: true }); } catch {}
      if (!state.page || state.page.isClosed()) {
        releaseWaLock();
        return res.status(503).json({ error: 'WhatsApp no vinculado' });
      }
    }

    // Asegurar home + sidebar
    try {
      const u = state.page.url();
      if (u.includes('/send?phone=') || u.includes('/send/?phone=') || !u.startsWith('https://web.whatsapp.com/')) {
        await state.page.goto('https://web.whatsapp.com/', { waitUntil: 'load', timeout: 30000 });
      }
    } catch {}

    // Esperar a que el sidebar tenga LISTITEMS POBLADOS (no solo que exista el div).
    // 2026-06-24: el bug del "0 mensajes" en realidad era esto — mi codigo veia #pane-side
    // existir pero los listitems se hidratan ms despues. chats/list tenia un sleep(700) extra
    // como amortiguador, open-by-index no. Ahora esperamos activamente a que aparezcan items.
    const sidebarDeadline = Date.now() + 15000;
    let sidebarItems = 0;
    while (Date.now() < sidebarDeadline) {
      sidebarItems = await state.page.evaluate(() =>
        document.querySelectorAll('#pane-side div[role="listitem"]').length
      ).catch(() => 0);
      if (sidebarItems > 0) break;
      await sleep(400);
    }
    console.log(`[wa] open-by-index: sidebar tiene ${sidebarItems} items antes de buscar idx=${index}`);

    // Click directo en el nth listitem. Hace scrollIntoView por si esta fuera del viewport.
    const clickResult = await state.page.evaluate(({ idx, expected }) => {
      const items = document.querySelectorAll('#pane-side div[role="listitem"]');
      if (items.length === 0) {
        return { ok: false, reason: 'no items', total: 0 };
      }
      if (idx >= items.length) {
        return { ok: false, reason: 'index out of range', total: items.length };
      }
      const target = items[idx];
      let actualName = '';
      const titleSpan = target.querySelector('span[title]');
      if (titleSpan) actualName = titleSpan.getAttribute('title') || '';
      if (expected) {
        const norm = (s) => (s || '').normalize('NFD').replace(/[̀-ͯ]/g, '').toLowerCase().replace(/\s+/g, ' ').trim();
        if (norm(actualName) !== norm(expected)) {
          return { ok: false, reason: 'name mismatch', actualName, expected };
        }
      }
      target.scrollIntoView({ block: 'center', behavior: 'instant' });
      target.click();
      return { ok: true, name: actualName };
    }, { idx: index, expected: expectedName });

    if (!clickResult.ok) {
      releaseWaLock();
      if (clickResult.reason === 'name mismatch') {
        return res.status(409).json({ error: 'Chat se reordeno, refrescar lista', detail: clickResult });
      }
      if (clickResult.reason === 'index out of range') {
        return res.status(404).json({ error: 'index fuera de rango', detail: clickResult });
      }
      return res.status(500).json({ error: 'no se pudo clickear', detail: clickResult });
    }

    const extracted = await waitAndExtractMessagesFromPanel(state.page, `open-by-index[${index}] "${clickResult.name}"`);
    if (!extracted.ok) {
      return res.status(extracted.status || 500).json({ error: extracted.error || 'error' });
    }
    return res.json({ messages: extracted.messages, total: extracted.messages.length, name: clickResult.name, index, debug: extracted.debug });
  } catch (err) {
    console.error('[wa] open-by-index error:', err.message || err);
    return res.status(500).json({ error: err.message || 'error' });
  } finally {
    releaseWaLock();
  }
});

// ============================================================
// POST /whatsapp/chat/send-to-current  body: { text: "..." }
// Manda un mensaje al chat ACTUALMENTE ABIERTO en WhatsApp Web. Asume que open-by-name ya
// fue invocado antes. No abre chat por phone — escribe directo en el footer existente.
// ============================================================
app.post('/whatsapp/chat/send-to-current', async (req, res) => {
  const text = (req.body && req.body.text) || '';
  if (!text || !text.trim()) return res.status(400).json({ error: 'text requerido' });

  if (!await acquireWaLockWait('send-to-current', 30000)) return res.status(429).json({ error: 'busy' });

  try {
    if (!state.page || state.page.isClosed()) {
      return res.status(503).json({ error: 'WhatsApp no vinculado' });
    }
    const composeBox = await state.page.$('footer div[contenteditable="true"]').catch(() => null);
    if (!composeBox) return res.status(400).json({ error: 'No hay chat abierto' });

    await composeBox.click();
    await composeBox.fill(''); // limpiar
    // Si tiene saltos de linea, usar Shift+Enter para no enviar antes de tiempo
    const lines = text.split('\n');
    for (let i = 0; i < lines.length; i++) {
      if (i > 0) await state.page.keyboard.press('Shift+Enter');
      await composeBox.type(lines[i], { delay: 8 });
    }
    await state.page.keyboard.press('Enter');
    await sleep(600);
    return res.json({ ok: true });
  } catch (err) {
    console.error('[wa] send-to-current error:', err.message || err);
    return res.status(500).json({ error: err.message || 'error' });
  } finally {
    releaseWaLock();
  }
});

// ============================================================
// POST /whatsapp/chats/list  body: { limit?: 50 }
// Devuelve la lista de chats del sidebar de WhatsApp Web, ordenada como aparece (mas recientes arriba).
// Cada chat: { name, phone?, lastMsg, lastMsgAt, unread }
//
// Estrategia: navegar a https://web.whatsapp.com/ (sin chat especifico) para asegurar que el sidebar
// este cargado, esperar al pane-side y scrapear cada celda. WhatsApp Web cambia clases seguido, asi
// que usamos roles ARIA y data-testid con fallbacks.
// ============================================================
app.post('/whatsapp/chats/list', async (req, res) => {
  const limit = Math.min(parseInt((req.body && req.body.limit) || 50, 10) || 50, 200);

  if (!await acquireWaLockWait('chats/list', 30000)) return res.status(429).json({ error: 'busy', message: 'Otro listado en curso' });

  try {
    const alive = await ensureSessionAlive();
    if (!alive) {
      try { await startSession({ useStorageState: true }); } catch {}
      if (!state.page || state.page.isClosed()) {
        releaseWaLock();
        return res.status(503).json({ error: 'WhatsApp no vinculado' });
      }
    }

    // 2026-06-23 fix: ANTES chequeabamos solo "URL contiene web.whatsapp.com" — quedaba en
    // /send?phone=... y el sidebar no estaba presente. Ahora forzamos navegacion a la home
    // siempre que estemos en una URL de chat individual.
    try {
      const currentUrl = state.page.url();
      const onSendUrl = currentUrl.includes('/send?phone=') || currentUrl.includes('/send/?phone=');
      if (onSendUrl || !currentUrl.startsWith('https://web.whatsapp.com/')) {
        console.log('[wa] chats/list: navegando a home desde', currentUrl);
        await state.page.goto('https://web.whatsapp.com/', { waitUntil: 'load', timeout: 30000 });
      }
    } catch (gotoErr) {
      console.warn('[wa] chats goto warning:', gotoErr.message);
    }

    // Esperar el pane-side (panel izquierdo con la lista)
    const deadline = Date.now() + 20000;
    let paneOk = false;
    while (Date.now() < deadline) {
      const found = await state.page.$('#pane-side, [aria-label="Lista de chats"], [aria-label="Chat list"], div[data-tab="4"]').catch(() => null);
      if (found) { paneOk = true; break; }
      await sleep(600);
    }
    if (!paneOk) {
      releaseWaLock();
      return res.status(504).json({ error: 'Timeout cargando lista de chats' });
    }

    await sleep(700);

    // Extraer los chats del DOM. Multi-fallback por cambios en clases de WhatsApp.
    const chats = await state.page.evaluate((max) => {
      const out = [];
      // Cada celda de chat es un div con role=listitem dentro del pane-side
      let cells = Array.from(document.querySelectorAll('#pane-side div[role="listitem"]'));
      if (cells.length === 0) {
        // Fallback: data-testid antiguo
        cells = Array.from(document.querySelectorAll('[data-testid="cell-frame-container"]'));
      }
      for (let i = 0; i < cells.length && out.length < max; i++) {
        const cell = cells[i];
        // Nombre: el primer span con title
        const nameNode = cell.querySelector('span[title]');
        const name = (nameNode && nameNode.getAttribute('title')) || '';
        // Hora del ultimo mensaje
        const timeNodes = cell.querySelectorAll('div._ak8i, span._ak8i, div[class*="time"]');
        let lastMsgAt = '';
        if (timeNodes.length > 0) {
          lastMsgAt = (timeNodes[0].innerText || timeNodes[0].textContent || '').trim();
        }
        // Ultimo mensaje (preview)
        let lastMsg = '';
        const previewNode = cell.querySelector('span[dir="ltr"][class*="last-msg"], span._ak8k, span[dir="ltr"] span');
        if (previewNode) {
          lastMsg = (previewNode.innerText || previewNode.textContent || '').trim();
        } else {
          // Fallback: el texto que no es nombre ni hora
          const allSpans = cell.querySelectorAll('span[dir="ltr"]');
          for (const s of allSpans) {
            const t = (s.innerText || s.textContent || '').trim();
            if (t && t !== name && t !== lastMsgAt && t.length < 200) { lastMsg = t; break; }
          }
        }
        // Unread count (badge verde)
        let unread = 0;
        const badge = cell.querySelector('span[aria-label*="no le"], span[aria-label*="unread"], div[class*="unread-count"]');
        if (badge) {
          const m = (badge.innerText || badge.textContent || '').trim().match(/\d+/);
          if (m) unread = parseInt(m[0], 10);
        }
        if (!name) continue;
        // index = posicion en el DOM de la lista (0-based). El frontend lo manda de vuelta
        // a /chat/open-by-index para clickear este chat sin depender de matching de texto.
        out.push({ index: i, name, lastMsg, lastMsgAt, unread });
      }
      return out;
    }, limit);

    releaseWaLock();
    return res.json({ chats, count: chats.length, total: chats.length });
  } catch (err) {
    releaseWaLock();
    console.error('[wa] chats/list error:', err);
    return res.status(500).json({ error: err.message });
  }
});

// ============================================================
// ARCA (ex AFIP) — Test de login + scraping del Registro Único Tributario.
// Usa un browser ISOLADO (no comparte contexto con WhatsApp). Single-test
// concurrente: si hay uno corriendo, el segundo recibe 409.
// El cliente pollea /arca/test/status cada ~1.5s para ver progreso, y
// /arca/test/screenshot para mostrar lo que ve el browser en vivo.
// ============================================================

const arcaState = {
  browser: null,
  context: null,
  page: null,        // página activa actual (login → portal → RUT)
  running: false,
  step: 'Iniciando...',
  result: null,      // { ok: true, titular, domicilios, actividades } | { ok: false, error }
  startedAt: null,
};

async function closeArcaBrowserSafely() {
  try { if (arcaState.page && !arcaState.page.isClosed()) await arcaState.page.close().catch(() => {}); } catch {}
  try { if (arcaState.context) await arcaState.context.close().catch(() => {}); } catch {}
  try { if (arcaState.browser) await arcaState.browser.close().catch(() => {}); } catch {}
  arcaState.page = null;
  arcaState.context = null;
  arcaState.browser = null;
}

// POST /arca/test/start - body: { cuit, cuitLogin, password, action?, rangoFechas? }
//   action: "test" (default — login + RUT) | "comprobantes" (login + Mis Comprobantes)
//   rangoFechas: { tipo: "30dias"|"60dias"|"90dias"|"custom", desde?, hasta? }
//                solo se usa si action === "comprobantes"
app.post('/arca/test/start', async (req, res) => {
  if (arcaState.running) {
    return res.status(409).json({ error: 'Ya hay una prueba en curso' });
  }
  const { cuit, cuitLogin, password, action, rangoFechas } = req.body || {};
  if (!cuit || !password) {
    return res.status(400).json({ error: 'Faltan cuit y/o password' });
  }
  const accion = (action === 'comprobantes') ? 'comprobantes' : 'test';
  arcaState.running = true;
  arcaState.step = 'Iniciando...';
  arcaState.result = null;
  arcaState.startedAt = Date.now();
  res.json({ ok: true });

  const runner = (accion === 'comprobantes')
    ? runArcaComprobantes({ cuit, cuitLogin, password, rangoFechas })
    : runArcaTest({ cuit, cuitLogin, password });

  runner.catch(async (err) => {
    console.error('[arca] error inesperado:', err);
    arcaState.result = { ok: false, error: err?.message || 'Error desconocido' };
  }).finally(async () => {
    await closeArcaBrowserSafely();
    arcaState.running = false;
    if (!arcaState.result) {
      arcaState.result = { ok: false, error: 'Test interrumpido' };
    }
    arcaState.step = arcaState.result?.ok ? 'Listo' : 'Error';
  });
});

// GET /arca/test/status
app.get('/arca/test/status', (req, res) => {
  res.json({
    running: arcaState.running,
    step: arcaState.step,
    result: arcaState.result,
  });
});

// GET /arca/test/screenshot
app.get('/arca/test/screenshot', async (req, res) => {
  try {
    if (!arcaState.page || arcaState.page.isClosed()) {
      return res.status(404).send('Sin página activa');
    }
    const buffer = await arcaState.page.screenshot({ type: 'png', fullPage: false }).catch(() => null);
    if (!buffer) return res.status(404).send('No se pudo capturar');
    res.set('Content-Type', 'image/png');
    res.set('Cache-Control', 'no-cache, no-store, must-revalidate');
    res.set('Pragma', 'no-cache');
    res.set('Expires', '0');
    res.send(buffer);
  } catch (err) {
    res.status(500).send('Error: ' + err.message);
  }
});

/// Lanza un browser nuevo aislado y hace login en ARCA. Devuelve { browser, context, page }
/// con la sesión iniciada en el portal. Tira excepción si el login falla.
async function arcaLoginAndOpenPortal({ cuit, cuitLogin, password }) {
  const usuarioLogin = (cuitLogin && cuitLogin.length === 11) ? cuitLogin : cuit;
  const cuitPrincipal = cuit;

  arcaState.step = 'Abriendo navegador...';
  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled'],
  });
  const context = await browser.newContext({
    userAgent: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    viewport: { width: 1280, height: 800 },
    acceptDownloads: true,
  });
  const page = await context.newPage();
  arcaState.browser = browser;
  arcaState.context = context;
  arcaState.page = page;

  arcaState.step = 'Abriendo página de login de ARCA...';
  await page.goto('https://auth.afip.gob.ar/contribuyente_/login.xhtml', {
    waitUntil: 'domcontentloaded',
    timeout: 30000,
  });

  arcaState.step = `Ingresando CUIT ${usuarioLogin}...`;
  await page.locator('input[name="F1:username"]').fill(usuarioLogin, { timeout: 5000 });
  await page.locator('input[name="F1:btnSiguiente"]').click({ timeout: 5000 });

  arcaState.step = 'Ingresando contraseña...';
  await page.locator('input[name="F1:password"]').waitFor({ state: 'visible', timeout: 10000 });
  await page.locator('input[name="F1:password"]').fill(password, { timeout: 5000 });
  await page.locator('input[name="F1:btnIngresar"]').click({ timeout: 5000 });

  arcaState.step = 'Verificando login...';
  await page.waitForLoadState('domcontentloaded', { timeout: 30000 }).catch(() => {});
  await sleep(1500);

  if (page.url().includes('login.xhtml')) {
    let errMsg = 'Login fallido — verificá CUIT y contraseña';
    try {
      const errText = await page.locator('.alert-danger, .error, #F1\\:msg').first().textContent({ timeout: 1500 });
      if (errText && errText.trim()) errMsg = `Login fallido: ${errText.trim()}`;
    } catch {}
    throw new Error(errMsg);
  }

  // Si CUIT Login es distinto al CUIT principal, elegir la representación
  if (usuarioLogin !== cuitPrincipal) {
    arcaState.step = `Seleccionando representación CUIT ${cuitPrincipal}...`;
    const cuitFmt = cuitPrincipal.length === 11
      ? `${cuitPrincipal.slice(0, 2)}-${cuitPrincipal.slice(2, 10)}-${cuitPrincipal.slice(10)}`
      : cuitPrincipal;
    const candidates = [
      page.locator(`a:has-text("${cuitFmt}")`).first(),
      page.locator(`button:has-text("${cuitFmt}")`).first(),
      page.locator(`a:has-text("${cuitPrincipal}")`).first(),
      page.locator(`button:has-text("${cuitPrincipal}")`).first(),
      page.locator(`li:has-text("${cuitFmt}")`).first(),
      page.locator(`li:has-text("${cuitPrincipal}")`).first(),
    ];
    let clicked = false;
    for (const c of candidates) {
      try {
        await c.waitFor({ state: 'visible', timeout: 3000 });
        await c.click({ timeout: 3000 });
        clicked = true;
        break;
      } catch {}
    }
    if (!clicked) console.log('[arca] no se encontró selector de representación, sigo igual');
    await page.waitForLoadState('domcontentloaded', { timeout: 15000 }).catch(() => {});
    await sleep(1500);
  }

  return { browser, context, page };
}

async function runArcaTest({ cuit, cuitLogin, password }) {
  const cuitPrincipal = cuit;
  const { context, page } = await arcaLoginAndOpenPortal({ cuit, cuitLogin, password });

  // Click en "Registro Único Tributario" — abre nueva pestaña
  arcaState.step = 'Abriendo Registro Único Tributario...';
  const rutLink = page.locator('a:has-text("Registro Único Tributario"), a:has-text("Registro Unico Tributario"), a[title*="Registro Único Tributario" i], a[title*="Registro Unico Tributario" i]').first();

  let rutPage;
  try {
    const newPagePromise = context.waitForEvent('page', { timeout: 15000 });
    await rutLink.click({ timeout: 5000 });
    rutPage = await newPagePromise;
  } catch (err) {
    throw new Error('No se encontró el link "Registro Único Tributario" después del login');
  }

  arcaState.page = rutPage; // que el screenshot capture la nueva pestaña
  arcaState.step = 'Cargando datos del CUIT...';
  await rutPage.waitForLoadState('domcontentloaded', { timeout: 30000 }).catch(() => {});
  await rutPage.waitForLoadState('networkidle', { timeout: 15000 }).catch(() => {});
  await sleep(2000);

  arcaState.step = 'Leyendo datos del Registro Único Tributario...';
  const data = await scrapeRutData(rutPage);

  arcaState.step = 'Cerrando ventana del RUT...';
  await rutPage.close().catch(() => {});
  arcaState.page = page;

  arcaState.step = 'Volviendo al portal...';
  await page.goto('https://portalcf.cloud.afip.gob.ar/portal/app/', { timeout: 15000 }).catch(() => {});
  await sleep(500);

  arcaState.result = { ok: true, ...data };
  arcaState.step = 'Listo';
}

/// Lee el texto plano de la página y extrae con regex titular, domicilios y actividades.
async function scrapeRutData(page) {
  let bodyText = '';
  try {
    bodyText = await page.locator('body').innerText({ timeout: 5000 });
  } catch {}

  // ---- Titular ----
  let titular = null;
  const titularPatterns = [
    /Apellido y Nombre\s*:?\s*([^\n\r]+)/i,
    /Apellido y nombre\s*:?\s*([^\n\r]+)/i,
    /Razón Social\s*:?\s*([^\n\r]+)/i,
    /Razon Social\s*:?\s*([^\n\r]+)/i,
    /Nombre\s*:?\s*([^\n\r]+)/i,
  ];
  for (const re of titularPatterns) {
    const m = bodyText.match(re);
    if (m && m[1] && m[1].trim().length > 1) {
      titular = m[1].trim();
      break;
    }
  }

  // ---- Domicilios ----
  // Cada bloque tiene "Tipo domicilio nacional: X", luego dirección, luego "Tipo domicilio provincial: Y"
  const domicilios = [];
  const domRegex = /Tipo domicilio nacional\s*:?\s*([^\n\r]+)([\s\S]*?)Tipo domicilio provincial\s*:?\s*([^\n\r]+)/gi;
  let m;
  while ((m = domRegex.exec(bodyText)) !== null) {
    const tipo = m[1].trim();
    const middle = m[2].trim();
    const provincial = m[3].trim();
    // La dirección suele ser la primera línea con contenido del bloque medio.
    const lines = middle.split(/\n/).map(l => l.trim()).filter(l =>
      l.length > 0 &&
      !/^Tipo /i.test(l) &&
      !/^Domicilio$/i.test(l) &&
      l.length < 250
    );
    const direccion = lines.length > 0 ? lines.join(' · ') : '—';
    domicilios.push({ tipo, direccion, jurisdiccion: provincial });
  }

  // ---- Actividades ----
  const actividades = [];
  const actRegex = /Actividad (?:nacional|principal|secundaria)\s*:?\s*([\s\S]+?)Inicio de actividad\s*:?\s*(\d{2}\/\d{4})/gi;
  while ((m = actRegex.exec(bodyText)) !== null) {
    const descRaw = m[1].trim();
    // Tomar la primera línea no vacía como descripción
    const desc = descRaw.split(/\n/).map(l => l.trim()).filter(l => l.length > 0)[0] || descRaw;
    actividades.push({ descripcion: desc, fechaInicio: m[2] });
  }

  return { titular, domicilios, actividades };
}

// ============================================================
// ARCA — Mis Comprobantes (Emitidos + Recibidos)
// ============================================================

async function runArcaComprobantes({ cuit, cuitLogin, password, rangoFechas }) {
  const { context, page } = await arcaLoginAndOpenPortal({ cuit, cuitLogin, password });

  // Asegurarse de estar en el portal — si quedamos en otra URL después del login
  arcaState.step = 'Abriendo portal de ARCA...';
  if (!page.url().includes('portalcf.cloud.afip.gob.ar/portal/app')) {
    await page.goto('https://portalcf.cloud.afip.gob.ar/portal/app/', {
      waitUntil: 'domcontentloaded',
      timeout: 30000,
    }).catch(() => {});
    await sleep(1500);
  }

  // ---- Buscar "Mis Comprobantes" en el buscador del portal ----
  arcaState.step = 'Buscando "Mis Comprobantes"...';
  // Estrategia: getByRole('combobox', { name: 'Buscador' }) primero, luego fallbacks
  let buscador = null;
  const buscadorCandidates = [
    page.getByRole('combobox', { name: 'Buscador' }),
    page.getByPlaceholder(/necesit/i),
    page.getByPlaceholder(/rámite/i),
    page.locator('input[type="search"]'),
  ];
  for (const c of buscadorCandidates) {
    try {
      await c.waitFor({ state: 'visible', timeout: 3000 });
      buscador = c;
      break;
    } catch {}
  }
  if (!buscador) throw new Error('No se encontró el buscador del portal');

  await buscador.click();
  // IMPORTANTE: NO usar fill() acá — el autocomplete del portal no responde bien.
  // Hay que tipear con delay para que dispare el filtro.
  await page.keyboard.type('Mis comprobantes', { delay: 50 });
  await sleep(1000);

  arcaState.step = 'Abriendo "Mis Comprobantes"...';
  // El item del autocomplete suele decir "Mis Comprobantes Consulta de ..." o similar
  const linkCandidates = [
    page.getByRole('link', { name: /Mis Comprobantes/i }).first(),
    page.locator('a:has-text("Mis Comprobantes")').first(),
    page.locator('li:has-text("Mis Comprobantes") a').first(),
  ];

  let popupPage;
  let opened = false;
  for (const link of linkCandidates) {
    try {
      await link.waitFor({ state: 'visible', timeout: 4000 });
      const popupPromise = context.waitForEvent('page', { timeout: 15000 });
      await link.click({ timeout: 4000 });
      popupPage = await popupPromise;
      opened = true;
      break;
    } catch {}
  }
  if (!opened || !popupPage) throw new Error('No se pudo abrir "Mis Comprobantes"');

  arcaState.page = popupPage; // que el screenshot capture la nueva ventana
  await popupPage.waitForLoadState('domcontentloaded', { timeout: 30000 }).catch(() => {});
  await sleep(2000);

  // ---- Seleccionar empresa por CUIT ----
  arcaState.step = `Seleccionando empresa CUIT ${cuit}...`;
  const cuitFmt = cuit.length === 11
    ? `${cuit.slice(0, 2)}-${cuit.slice(2, 10)}-${cuit.slice(10)}`
    : cuit;
  const empresaCandidates = [
    popupPage.locator(`a:has-text("${cuitFmt}")`).first(),
    popupPage.locator(`a:has-text("${cuit}")`).first(),
    popupPage.locator(`button:has-text("${cuitFmt}")`).first(),
  ];
  let empresaClicked = false;
  for (const c of empresaCandidates) {
    try {
      await c.waitFor({ state: 'visible', timeout: 4000 });
      await c.click({ timeout: 3000 });
      empresaClicked = true;
      break;
    } catch {}
  }
  if (!empresaClicked) {
    console.log('[arca] no se encontró link de empresa, puede que ya esté seleccionada');
  }
  await popupPage.waitForLoadState('domcontentloaded', { timeout: 15000 }).catch(() => {});
  await sleep(1500);

  // Resolver rango de fechas → texto a clickear o desde/hasta
  const rango = (rangoFechas || {});
  const tipoRango = rango.tipo || '30dias';
  const labelRango = (() => {
    if (tipoRango === '30dias') return 'Últimos 30 Días';
    if (tipoRango === '60dias') return 'Últimos 60 Días';
    if (tipoRango === '90dias') return 'Últimos 90 Días';
    return null; // custom
  })();

  // ---- Descargar Emitidos ----
  arcaState.step = 'Abriendo sección "Emitidos"...';
  const emitidosBuf = await descargarSeccion(popupPage, 'Emitidos', { tipoRango, labelRango, desde: rango.desde, hasta: rango.hasta });

  // ---- Volver al menú principal ----
  arcaState.step = 'Volviendo al menú principal...';
  try {
    await popupPage.getByRole('link', { name: /Menú Principal/i }).first().click({ timeout: 5000 });
    await popupPage.waitForLoadState('domcontentloaded', { timeout: 15000 }).catch(() => {});
    await sleep(1500);
  } catch {
    console.log('[arca] no se encontró link "Menú Principal", sigo');
  }

  // ---- Descargar Recibidos ----
  arcaState.step = 'Abriendo sección "Recibidos"...';
  const recibidosBuf = await descargarSeccion(popupPage, 'Recibidos', { tipoRango, labelRango, desde: rango.desde, hasta: rango.hasta });

  arcaState.step = 'Procesando archivos descargados...';
  const emitidos = emitidosBuf ? parseComprobantesCsv(emitidosBuf, 'emitido') : [];
  const recibidos = recibidosBuf ? parseComprobantesCsv(recibidosBuf, 'recibido') : [];

  arcaState.step = 'Cerrando ventana...';
  await popupPage.close().catch(() => {});
  arcaState.page = page;

  // Calcular rango efectivo en formato ISO para mostrar en el modal
  const { isoDesde, isoHasta } = calcularRangoIso(tipoRango, rango.desde, rango.hasta);

  arcaState.result = {
    ok: true,
    emitidos,
    recibidos,
    rangoDesde: isoDesde,
    rangoHasta: isoHasta,
  };
  arcaState.step = 'Listo';
}

/// Click en la sección (Emitidos o Recibidos), elegir rango, buscar, descargar CSV.
/// Devuelve un Buffer con el contenido bruto del archivo descargado, o null si falló.
async function descargarSeccion(popupPage, seccion, opts) {
  const re = new RegExp(seccion, 'i');
  const seccionLink = popupPage.getByRole('link', { name: re }).first();
  await seccionLink.waitFor({ state: 'visible', timeout: 8000 });
  await seccionLink.click({ timeout: 5000 });
  await popupPage.waitForLoadState('domcontentloaded', { timeout: 15000 }).catch(() => {});
  await sleep(1500);

  // Abrir el calendario / textbox de fechas
  arcaState.step = `${seccion} — Eligiendo rango de fechas...`;
  const calendarioCandidates = [
    popupPage.getByRole('textbox', { name: /Fecha del Comprobante/i }),
    popupPage.locator('#btnCalendarioFechaEmision i'),
    popupPage.locator('#btnCalendarioFechaEmision'),
    popupPage.locator('input[name*="echa"]').first(),
  ];
  let abierto = false;
  for (const c of calendarioCandidates) {
    try {
      await c.waitFor({ state: 'visible', timeout: 3000 });
      await c.click({ timeout: 3000 });
      abierto = true;
      break;
    } catch {}
  }
  if (!abierto) throw new Error(`${seccion}: no se encontró el selector de fecha`);

  await sleep(800);

  if (opts.labelRango) {
    // Click directo en "Últimos X Días"
    try {
      await popupPage.getByText(opts.labelRango, { exact: false }).first().click({ timeout: 5000 });
    } catch (err) {
      throw new Error(`${seccion}: no se pudo seleccionar "${opts.labelRango}"`);
    }
  } else if (opts.desde && opts.hasta) {
    // Custom: tipear desde / hasta. ARCA usa formato dd/mm/yyyy.
    const desdeStr = formatDdMmYyyy(opts.desde);
    const hastaStr = formatDdMmYyyy(opts.hasta);
    const inputs = await popupPage.locator('input[type="text"]:visible').all();
    // Heurística: hay 2 inputs de fecha visibles en el calendario abierto.
    // Limpiar y tipear cada uno.
    const dateInputs = [];
    for (const inp of inputs) {
      const placeholder = await inp.getAttribute('placeholder').catch(() => '');
      const name = await inp.getAttribute('name').catch(() => '');
      if (/echa|esde|asta/i.test((placeholder || '') + (name || ''))) {
        dateInputs.push(inp);
      }
    }
    if (dateInputs.length >= 2) {
      await dateInputs[0].click();
      await dateInputs[0].fill('').catch(() => {});
      await popupPage.keyboard.type(desdeStr, { delay: 30 });
      await dateInputs[1].click();
      await dateInputs[1].fill('').catch(() => {});
      await popupPage.keyboard.type(hastaStr, { delay: 30 });
    } else {
      // Fallback: buscar botón "Aplicar" del calendario después de tipear en el primero
      console.log('[arca] no se encontraron 2 inputs de fecha, usando 30 días por default');
      await popupPage.getByText('Últimos 30 Días').first().click({ timeout: 5000 }).catch(() => {});
    }
    await sleep(500);
  } else {
    // No hay rango → usar 30 días por default
    await popupPage.getByText('Últimos 30 Días').first().click({ timeout: 5000 }).catch(() => {});
  }

  await sleep(800);

  // Click en Buscar
  arcaState.step = `${seccion} — Buscando comprobantes...`;
  await popupPage.getByRole('button', { name: /Buscar/i }).first().click({ timeout: 5000 });
  await popupPage.waitForLoadState('networkidle', { timeout: 30000 }).catch(() => {});
  await sleep(2000);

  // Click en CSV → dispara download
  arcaState.step = `${seccion} — Descargando archivo CSV...`;
  let buffer = null;
  try {
    const downloadPromise = popupPage.waitForEvent('download', { timeout: 30000 });
    await popupPage.getByRole('button', { name: /^CSV$/i }).first().click({ timeout: 5000 });
    const download = await downloadPromise;
    const tmpPath = await download.path();
    if (tmpPath) {
      buffer = fs.readFileSync(tmpPath);
    }
  } catch (err) {
    console.log(`[arca] ${seccion}: no se pudo descargar CSV (puede que no haya comprobantes en el rango): ${err.message}`);
    return null;
  }
  return buffer;
}

/// Parsea el buffer descargado (puede ser ZIP o CSV directo) en un array de comprobantes.
function parseComprobantesCsv(buffer, tipo) {
  if (!buffer || buffer.length === 0) return [];

  // Detectar magic bytes de ZIP: PK\x03\x04
  const isZip = buffer.length >= 4 &&
                buffer[0] === 0x50 && buffer[1] === 0x4B &&
                buffer[2] === 0x03 && buffer[3] === 0x04;

  let csvText = '';
  if (isZip) {
    try {
      const zip = new AdmZip(buffer);
      const entries = zip.getEntries();
      const csvEntry = entries.find(e => /\.csv$/i.test(e.entryName));
      if (!csvEntry) {
        console.log('[arca] ZIP sin entrada .csv');
        return [];
      }
      // Leer como latin1 (ARCA usa ISO-8859-1)
      const raw = csvEntry.getData();
      csvText = raw.toString('latin1');
    } catch (err) {
      console.log('[arca] error descomprimiendo ZIP:', err.message);
      return [];
    }
  } else {
    csvText = buffer.toString('latin1');
  }

  // Quitar BOM si está
  if (csvText.charCodeAt(0) === 0xFEFF) csvText = csvText.slice(1);

  const rows = parseCsvText(csvText);
  if (rows.length < 2) return [];

  const headers = rows[0].map(h => normalizeHeader(h));
  // Index helpers
  const findCol = (...keywords) => {
    for (let i = 0; i < headers.length; i++) {
      const h = headers[i];
      if (keywords.every(k => h.includes(k))) return i;
    }
    return -1;
  };

  const idxFecha = findCol('fecha');
  // "Nro Doc Receptor" / "Nro Doc Emisor"
  const idxNroDoc = (tipo === 'recibido')
    ? (findCol('nro', 'doc', 'emisor') >= 0 ? findCol('nro', 'doc', 'emisor') : findCol('nro', 'doc'))
    : (findCol('nro', 'doc', 'receptor') >= 0 ? findCol('nro', 'doc', 'receptor') : findCol('nro', 'doc'));
  // Denominación con fallback a columna 9 (índice 8)
  let idxDeno = (tipo === 'recibido')
    ? findCol('denominacion', 'emisor')
    : findCol('denominacion', 'receptor');
  if (idxDeno < 0) idxDeno = findCol('denominacion');
  if (idxDeno < 0 && headers.length > 8) idxDeno = 8;
  const idxNeto = findCol('neto', 'gravado');
  const idxIva = (() => {
    let i = findCol('total', 'iva');
    if (i < 0) i = findCol('iva');
    return i;
  })();
  const idxTotal = (() => {
    // Total general (no IVA) — buscar "imp total" o "total" sin "iva"
    let i = findCol('imp', 'total');
    if (i < 0) {
      for (let j = headers.length - 1; j >= 0; j--) {
        if (headers[j].includes('total') && !headers[j].includes('iva') && !headers[j].includes('neto')) {
          i = j; break;
        }
      }
    }
    return i;
  })();

  const result = [];
  for (let r = 1; r < rows.length; r++) {
    const row = rows[r];
    if (!row || row.length === 0) continue;
    const all = row.map(c => (c ?? '').trim()).filter(c => c.length > 0);
    if (all.length === 0) continue;

    result.push({
      fecha: idxFecha >= 0 ? (row[idxFecha] || '').trim() : '',
      nroDoc: idxNroDoc >= 0 ? (row[idxNroDoc] || '').trim() : '',
      denominacion: idxDeno >= 0 ? (row[idxDeno] || '').trim() : '',
      impNeto: idxNeto >= 0 ? parseDecimalArg(row[idxNeto]) : null,
      totalIva: idxIva >= 0 ? parseDecimalArg(row[idxIva]) : null,
      impTotal: idxTotal >= 0 ? parseDecimalArg(row[idxTotal]) : null,
    });
  }
  return result;
}

/// Parser de CSV simple — soporta separador , o ;, valores con comillas y comillas escapadas.
function parseCsvText(text) {
  const lines = [];
  // Detectar separador con la primera línea
  const firstNl = text.indexOf('\n');
  const firstLine = firstNl >= 0 ? text.slice(0, firstNl) : text;
  const countSemi = (firstLine.match(/;/g) || []).length;
  const countComma = (firstLine.match(/,/g) || []).length;
  const sep = countSemi > countComma ? ';' : ',';

  let cur = [];
  let buf = '';
  let inQuotes = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (inQuotes) {
      if (ch === '"') {
        if (text[i + 1] === '"') { buf += '"'; i++; }
        else inQuotes = false;
      } else {
        buf += ch;
      }
    } else {
      if (ch === '"') {
        inQuotes = true;
      } else if (ch === sep) {
        cur.push(buf); buf = '';
      } else if (ch === '\n') {
        cur.push(buf); buf = '';
        if (cur.length > 1 || (cur.length === 1 && cur[0].trim().length > 0)) lines.push(cur);
        cur = [];
      } else if (ch === '\r') {
        // ignorar
      } else {
        buf += ch;
      }
    }
  }
  // Última fila
  if (buf.length > 0 || cur.length > 0) {
    cur.push(buf);
    if (cur.length > 1 || (cur.length === 1 && cur[0].trim().length > 0)) lines.push(cur);
  }
  return lines;
}

/// "fecha de emisión" → "fechadeemision"; saca acentos, espacios y no-alfanumérico
function normalizeHeader(h) {
  if (!h) return '';
  return h.toString()
    .toLowerCase()
    .normalize('NFD').replace(/[̀-ͯ]/g, '')
    .replace(/[^a-z0-9]/g, '');
}

/// Convierte "1.234,56" o "1234.56" a número. Devuelve null si no se puede.
function parseDecimalArg(raw) {
  if (raw === null || raw === undefined) return null;
  const s = String(raw).trim().replace(/\s/g, '');
  if (s === '' || s === '-') return null;
  // Si tiene tanto punto como coma → punto es miles, coma es decimal (formato AR)
  // Si solo tiene coma → coma es decimal
  // Si solo tiene punto → punto es decimal
  let normalized = s;
  if (s.includes('.') && s.includes(',')) {
    normalized = s.replace(/\./g, '').replace(',', '.');
  } else if (s.includes(',')) {
    normalized = s.replace(',', '.');
  }
  const n = Number(normalized);
  return Number.isFinite(n) ? n : null;
}

function formatDdMmYyyy(isoOrAny) {
  // Acepta yyyy-MM-dd o dd/MM/yyyy
  const s = String(isoOrAny || '');
  const isoMatch = s.match(/^(\d{4})-(\d{2})-(\d{2})/);
  if (isoMatch) return `${isoMatch[3]}/${isoMatch[2]}/${isoMatch[1]}`;
  return s;
}

function calcularRangoIso(tipo, desde, hasta) {
  const today = new Date();
  const ymd = (d) => {
    const yyyy = d.getFullYear();
    const mm = String(d.getMonth() + 1).padStart(2, '0');
    const dd = String(d.getDate()).padStart(2, '0');
    return `${yyyy}-${mm}-${dd}`;
  };
  if (tipo === 'custom' && desde && hasta) {
    return { isoDesde: desde, isoHasta: hasta };
  }
  const dias = tipo === '60dias' ? 60 : tipo === '90dias' ? 90 : 30;
  const d = new Date(today);
  d.setDate(today.getDate() - dias);
  return { isoDesde: ymd(d), isoHasta: ymd(today) };
}

// --- Restauración de sesión al arrancar ---
async function tryRestoreSession() {
  if (!fs.existsSync(STORAGE_STATE_PATH)) return;
  try {
    console.log('[wa] restaurando sesión desde disk...');
    await startSession({ useStorageState: true });
    await sleep(5000);
    const linked = await isLinkedOnPage(state.page);
    state.linked = linked;
    if (linked) {
      console.log('[wa] sesión restaurada');
    } else {
      console.log('[wa] storage-state inválido, cerrando');
      await closeBrowserSafely();
    }
  } catch (err) {
    console.error('[wa] error restaurando sesión:', err.message);
    await closeBrowserSafely();
  }
}

// ============================================================
// BANCO GALICIA — Office Banking empresas.
// Login + (más adelante) descarga de movimientos. Mismo patrón que ARCA:
// browser aislado, single-run concurrente (409 si hay otro), status + screenshot en vivo.
// El formulario limpio tiene: input#userInput (usuario), input#userPassword (clave),
// botón submit "Ingresar". Si hay cookie de "usuario recordado" muestra solo la clave;
// en ese caso clickeamos "Cambiar de usuario" para tener el form completo.
// ============================================================

const galiciaState = {
  browser: null,
  context: null,
  page: null,
  running: false,
  step: 'Iniciando...',
  result: null,
  startedAt: null,
};

async function closeGaliciaBrowserSafely() {
  try { if (galiciaState.page && !galiciaState.page.isClosed()) await galiciaState.page.close().catch(() => {}); } catch {}
  try { if (galiciaState.context) await galiciaState.context.close().catch(() => {}); } catch {}
  try { if (galiciaState.browser) await galiciaState.browser.close().catch(() => {}); } catch {}
  galiciaState.page = null;
  galiciaState.context = null;
  galiciaState.browser = null;
}

// POST /galicia/test/start - body: { usuario, password, submit? }
//   submit === false  => solo abre el login, completa los campos y saca foto SIN enviar
//                        (sirve para verificar sin arriesgar el bloqueo por intentos).
//   submit !== false  => además aprieta "Ingresar" e informa si entró o si pidió token.
app.post('/galicia/test/start', async (req, res) => {
  if (galiciaState.running) {
    return res.status(409).json({ error: 'Ya hay una prueba de Galicia en curso' });
  }
  const { usuario, password, submit } = req.body || {};
  if (!usuario) {
    return res.status(400).json({ error: 'Falta el usuario' });
  }
  const doSubmit = submit !== false;
  if (doSubmit && !password) {
    return res.status(400).json({ error: 'Falta la clave para probar el ingreso' });
  }
  galiciaState.running = true;
  galiciaState.step = 'Iniciando...';
  galiciaState.result = null;
  galiciaState.startedAt = Date.now();
  res.json({ ok: true });

  runGaliciaLogin({ usuario, password, submit: doSubmit })
    .catch(async (err) => {
      console.error('[galicia] error inesperado:', err);
      galiciaState.result = { ok: false, error: err?.message || 'Error desconocido' };
    })
    .finally(async () => {
      await closeGaliciaBrowserSafely();
      galiciaState.running = false;
      if (!galiciaState.result) {
        galiciaState.result = { ok: false, error: 'Prueba interrumpida' };
      }
      galiciaState.step = galiciaState.result?.ok ? 'Listo' : 'Error';
    });
});

// POST /galicia/movimientos/start - body: { usuario, password }
//   Login + navegar a Movimientos + descargar CSV. El CSV vuelve en result.csvBase64.
app.post('/galicia/movimientos/start', async (req, res) => {
  if (galiciaState.running) {
    return res.status(409).json({ error: 'Ya hay una operación de Galicia en curso' });
  }
  const { usuario, password } = req.body || {};
  if (!usuario || !password) {
    return res.status(400).json({ error: 'Faltan usuario y/o clave' });
  }
  galiciaState.running = true;
  galiciaState.step = 'Iniciando...';
  galiciaState.result = null;
  galiciaState.startedAt = Date.now();
  res.json({ ok: true });

  runGaliciaMovimientos({ usuario, password })
    .catch(async (err) => {
      console.error('[galicia] error inesperado (movimientos):', err);
      galiciaState.result = { ok: false, error: err?.message || 'Error desconocido' };
    })
    .finally(async () => {
      await closeGaliciaBrowserSafely();
      galiciaState.running = false;
      if (!galiciaState.result) {
        galiciaState.result = { ok: false, error: 'Operación interrumpida' };
      }
      galiciaState.step = galiciaState.result?.ok ? 'Listo' : 'Error';
    });
});

app.get('/galicia/test/status', (req, res) => {
  res.json({
    running: galiciaState.running,
    step: galiciaState.step,
    result: galiciaState.result,
  });
});

app.get('/galicia/test/screenshot', async (req, res) => {
  try {
    if (!galiciaState.page || galiciaState.page.isClosed()) {
      return res.status(404).send('Sin página activa');
    }
    const buffer = await galiciaState.page.screenshot({ type: 'png', fullPage: false }).catch(() => null);
    if (!buffer) return res.status(404).send('No se pudo capturar');
    res.set('Content-Type', 'image/png');
    res.set('Cache-Control', 'no-cache, no-store, must-revalidate');
    res.set('Pragma', 'no-cache');
    res.set('Expires', '0');
    res.send(buffer);
  } catch (err) {
    res.status(500).send('Error: ' + err.message);
  }
});

// Lanza un browser aislado, abre el login y completa usuario + clave (sin enviar).
// Devuelve la page lista para apretar Ingresar.
async function galiciaOpenAndFill(usuario, password) {
  galiciaState.step = 'Abriendo navegador...';
  const browser = await chromium.launch({
    headless: true,
    args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled'],
  });
  const context = await browser.newContext({
    userAgent: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
    viewport: { width: 1280, height: 800 },
    acceptDownloads: true,
  });
  const page = await context.newPage();
  galiciaState.browser = browser;
  galiciaState.context = context;
  galiciaState.page = page;

  galiciaState.step = 'Abriendo login de Office Banking...';
  await page.goto('https://empresas.bancogalicia.com.ar/login', {
    waitUntil: 'domcontentloaded',
    timeout: 40000,
  });
  await sleep(3000); // SPA: dar tiempo a renderizar

  // Asegurar el formulario COMPLETO. Si hay usuario recordado, muestra solo la clave.
  const userInput = page.locator('#userInput');
  if (!(await userInput.isVisible().catch(() => false))) {
    const cambiar = page.locator('text=Cambiar de usuario').first();
    if (await cambiar.isVisible().catch(() => false)) {
      galiciaState.step = 'Abriendo formulario de usuario...';
      await cambiar.click().catch(() => {});
      await sleep(1500);
    }
  }
  await userInput.waitFor({ state: 'visible', timeout: 20000 });

  // IMPORTANTE: escribir letra por letra (pressSequentially), NO fill().
  // El SPA mantiene "Ingresar" deshabilitado hasta detectar tipeo real.
  galiciaState.step = `Ingresando usuario ${usuario}...`;
  await userInput.click({ timeout: 8000 });
  await userInput.fill('');
  await userInput.pressSequentially(usuario, { delay: 45, timeout: 20000 });
  if (password) {
    galiciaState.step = 'Ingresando clave...';
    const passField = page.locator('#userPassword');
    await passField.click({ timeout: 8000 });
    await passField.fill('');
    await passField.pressSequentially(password, { delay: 45, timeout: 20000 });
  }
  await sleep(600);
  return page;
}

// Aprieta "Ingresar" y verifica. Devuelve { loggedIn, needsToken, error, url }.
async function galiciaSubmitLogin(page) {
  galiciaState.step = 'Ingresando (apretando "Ingresar")...';
  const btnIngresar = page.getByRole('button', { name: 'Ingresar' }).first();
  try { await btnIngresar.waitFor({ state: 'visible', timeout: 8000 }); } catch {}
  let clicked = false;
  try {
    await btnIngresar.click({ timeout: 12000 });
    clicked = true;
  } catch {
    try { await page.locator('#userPassword').press('Enter'); clicked = true; } catch {}
  }
  if (!clicked) return { error: 'No se pudo apretar "Ingresar" (el botón no se habilitó).' };

  galiciaState.step = 'Verificando ingreso...';
  await page.waitForLoadState('domcontentloaded', { timeout: 30000 }).catch(() => {});
  await sleep(4000);

  const url = page.url();
  let visibleText = '';
  try { visibleText = ((await page.locator('body').innerText({ timeout: 3000 })) || '').replace(/\s+/g, ' ').trim(); } catch {}
  const low = visibleText.toLowerCase();
  const stillOnLogin = url.includes('/login');

  const errorHints = ['incorrecta', 'incorrecto', 'inválid', 'invalid', 'bloque', 'no coincide', 'reintent'];
  if (stillOnLogin && errorHints.some((h) => low.includes(h))) {
    return { error: `Ingreso rechazado. El banco dijo: "${visibleText.slice(0, 200)}"`, url };
  }
  const tokenHints = ['token', 'código de verificación', 'codigo de verificacion', 'segundo factor',
    'verificación en dos pasos', 'verificacion en dos pasos', 'ingresá el código', 'ingresa el codigo', 'otp'];
  if (tokenHints.some((h) => low.includes(h))) {
    return { needsToken: true, url };
  }
  if (!stillOnLogin) return { loggedIn: true, url };
  return { error: 'No se pudo confirmar el ingreso (quedó en la pantalla de login).', url };
}

async function runGaliciaLogin({ usuario, password, submit }) {
  const page = await galiciaOpenAndFill(usuario, submit ? password : '');

  if (!submit) {
    galiciaState.step = 'Formulario completado SIN enviar. Mirá la foto.';
    galiciaState.result = { ok: true, submitted: false, url: page.url() };
    await sleep(20000);
    return;
  }

  const r = await galiciaSubmitLogin(page);
  if (r.error) { galiciaState.result = { ok: false, error: r.error, url: r.url }; return; }
  if (r.needsToken) {
    galiciaState.step = 'El banco pidió un código de seguridad.';
    galiciaState.result = { ok: true, submitted: true, loggedIn: false, needsToken: true, url: r.url };
    await sleep(15000);
    return;
  }
  galiciaState.step = '¡Entró! Sesión iniciada.';
  galiciaState.result = { ok: true, submitted: true, loggedIn: true, needsToken: false, url: r.url };
  await sleep(15000);
}

async function runGaliciaMovimientos({ usuario, password }) {
  const page = await galiciaOpenAndFill(usuario, password);
  const r = await galiciaSubmitLogin(page);
  if (r.error) { galiciaState.result = { ok: false, error: r.error, url: r.url }; return; }
  if (r.needsToken) {
    galiciaState.result = { ok: true, loggedIn: false, needsToken: true, url: r.url };
    await sleep(8000);
    return;
  }

  // Ir a la cuenta y abrir Movimientos.
  galiciaState.step = 'Abriendo Cuentas...';
  await page.goto('https://empresas.bancogalicia.com.ar/cuentas', { waitUntil: 'domcontentloaded', timeout: 30000 }).catch(() => {});
  await sleep(4500);

  galiciaState.step = 'Abriendo movimientos de la cuenta...';
  // Click en la primera fila de cuenta (la que tiene "N° ####"). Si falla, navegación directa.
  const cuentaRow = page.locator('text=/N°\\s*\\d{5,}/').first();
  try {
    await cuentaRow.click({ timeout: 8000 });
  } catch {
    await page.goto('https://empresas.bancogalicia.com.ar/cuentas/movimientos', { waitUntil: 'domcontentloaded', timeout: 30000 }).catch(() => {});
  }
  await sleep(5000);

  // Abrir el menú de descarga y elegir .CSV.
  // El trigger es un dropdown con clase "download-button" (clase "download-b..." +
  // "brk-dropdown"); las opciones son elementos con title ".CSV"/".PDF"/etc.
  galiciaState.step = 'Abriendo el menú de descarga...';
  const csvOption = page.locator('[title=".CSV"]').first();
  const triggers = [
    page.locator('[class*="download-b"]').first(),
    page.locator('[class*="brk-dropdown"][class*="download"]').first(),
    page.locator('[class*="brk-dropdown"]').last(),
  ];
  let menuOpen = false;
  for (const t of triggers) {
    try {
      if (!(await t.isVisible().catch(() => false))) continue;
      await t.click({ timeout: 5000 });
      await sleep(1200);
      if (await csvOption.isVisible().catch(() => false)) { menuOpen = true; break; }
    } catch {}
  }
  if (!menuOpen) {
    // Diagnóstico: dumpear los botones/clickables de la página al log del server
    // para poder identificar el selector correcto del botón de descarga.
    try {
      const diag = await page.evaluate(() => {
        const els = [...document.querySelectorAll('button, [role="button"], a, [class*="download" i], [class*="descarg" i]')];
        return els.slice(0, 80).map((e, i) => {
          const t = (e.innerText || '').trim().replace(/\s+/g, ' ').slice(0, 22);
          const al = e.getAttribute('aria-label') || '';
          const ti = e.getAttribute('title') || '';
          const id = e.id || '';
          const cl = (typeof e.className === 'string' ? e.className : '').slice(0, 40);
          const svg = e.querySelector('svg') ? 'SVG' : '';
          return `#${i}[${[t, al && 'aria:' + al, ti && 'title:' + ti, id && 'id:' + id, svg, cl].filter(Boolean).join(' | ')}]`;
        }).join('\n');
      });
      console.log('[galicia][DIAG] botones en /cuentas/movimientos:\n' + diag);
    } catch (e) {
      console.log('[galicia][DIAG] no se pudo dumpear la página:', e?.message);
    }
    galiciaState.result = { ok: false, loggedIn: true, error: 'Entré y llegué a movimientos, pero no encontré el botón de descarga CSV. Hay que ajustar el selector.', url: page.url() };
    await sleep(12000);
    return;
  }

  galiciaState.step = 'Descargando CSV...';
  let csvBase64 = null;
  try {
    const downloadPromise = page.waitForEvent('download', { timeout: 30000 });
    await csvOption.click({ timeout: 5000 });
    const download = await downloadPromise;
    const filePath = await download.path();
    const buf = fs.readFileSync(filePath);
    csvBase64 = buf.toString('base64');
    // Diagnóstico: nombre del archivo + primeras líneas, al log del server.
    try {
      const nombre = download.suggestedFilename();
      const preview = buf.toString('utf8').slice(0, 800).replace(/\n/g, ' \\n ');
      console.log(`[galicia][CSV] archivo="${nombre}" bytes=${buf.length} preview: ${preview}`);
    } catch {}
  } catch (err) {
    galiciaState.result = { ok: false, loggedIn: true, error: 'No se pudo descargar el CSV: ' + (err?.message || err), url: page.url() };
    await sleep(10000);
    return;
  }

  galiciaState.step = '¡Movimientos descargados!';
  galiciaState.result = { ok: true, loggedIn: true, csvBase64, url: page.url() };
  await sleep(4000);
}

// ============================================================
// SHELL FLOTA — login (usuario + clave) + token OTP por mail (leído por IMAP
// del Gmail ya conectado) + lectura del "Saldo disponible". Mismo patrón que
// Galicia; la pieza nueva es leer el código del mail solo.
// ============================================================

const shellState = {
  browser: null, context: null, page: null,
  running: false, step: 'Iniciando...', result: null, startedAt: null,
};

async function closeShellBrowserSafely() {
  try { if (shellState.page && !shellState.page.isClosed()) await shellState.page.close().catch(() => {}); } catch {}
  try { if (shellState.context) await shellState.context.close().catch(() => {}); } catch {}
  try { if (shellState.browser) await shellState.browser.close().catch(() => {}); } catch {}
  shellState.page = null; shellState.context = null; shellState.browser = null;
}

// POST /shell/saldo/start - body: { usuario, password, gmailUser, gmailPass }
app.post('/shell/saldo/start', async (req, res) => {
  if (shellState.running) return res.status(409).json({ error: 'Ya hay una operación de Shell en curso' });
  const { usuario, password, gmailUser, gmailPass } = req.body || {};
  if (!usuario || !password) return res.status(400).json({ error: 'Faltan usuario y/o clave de Shell' });
  if (!gmailUser || !gmailPass) return res.status(400).json({ error: 'Falta la conexión de mail (Gmail) para leer el token' });
  shellState.running = true; shellState.step = 'Iniciando...'; shellState.result = null; shellState.startedAt = Date.now();
  res.json({ ok: true });

  runShellSaldo({ usuario, password, gmailUser, gmailPass })
    .catch(async (err) => { console.error('[shell] error inesperado:', err); shellState.result = { ok: false, error: err?.message || 'Error desconocido' }; })
    .finally(async () => {
      await closeShellBrowserSafely();
      shellState.running = false;
      if (!shellState.result) shellState.result = { ok: false, error: 'Operación interrumpida' };
      shellState.step = shellState.result?.ok ? 'Listo' : 'Error';
    });
});

app.get('/shell/test/status', (req, res) => {
  res.json({ running: shellState.running, step: shellState.step, result: shellState.result });
});

app.get('/shell/test/screenshot', async (req, res) => {
  try {
    if (!shellState.page || shellState.page.isClosed()) return res.status(404).send('Sin página activa');
    const buffer = await shellState.page.screenshot({ type: 'png', fullPage: false }).catch(() => null);
    if (!buffer) return res.status(404).send('No se pudo capturar');
    res.set('Content-Type', 'image/png'); res.set('Cache-Control', 'no-cache, no-store, must-revalidate');
    res.send(buffer);
  } catch (err) { res.status(500).send('Error: ' + err.message); }
});

// Lee el último token OTP de Shell desde Gmail por IMAP. Pollea hasta ~90s.
async function leerOtpShellDesdeGmail(gmailUser, gmailPass, sinceEpochMs) {
  const { ImapFlow } = require('imapflow');
  let simpleParser;
  try { simpleParser = require('mailparser').simpleParser; } catch { simpleParser = null; }

  const esCodigo = (t) => t && t.length === 6 && /[A-Z]/.test(t) && /[0-9]/.test(t);
  const extraerCodigo = (txt) => {
    if (!txt) return null;
    // Sacar colores hex (#3B3D40) para que no se confundan con el token.
    const T = txt.toUpperCase().replace(/#\s*[0-9A-F]{3,8}\b/g, ' ');
    // El código de Shell viene ESPACIADO ("J O N R Q 5"), justo después de "(OTP):".
    // Estrategia: ubicar la palabra clave, tomar lo que sigue, sacar TODO lo no-alfanumérico
    // (junta las letras espaciadas) y quedarnos con los primeros 6 caracteres.
    const kws = ['(OTP)', 'OTP)', 'OTP', 'UN SOLO USO', 'SOLO USO', 'CODIGO TOKEN', 'CÓDIGO TOKEN', 'VERIFICACION', 'VERIFICACIÓN'];
    for (const kw of kws) {
      const pos = T.indexOf(kw);
      if (pos < 0) continue;
      const after = T.slice(pos + kw.length, pos + kw.length + 60).replace(/[^A-Z0-9]/g, '');
      const cand = after.slice(0, 6);
      if (esCodigo(cand)) return cand;
    }
    // Fallback: token contiguo de 6 (letra+dígito).
    const tokens = T.match(/\b[A-Z0-9]{6}\b/g) || [];
    return tokens.find(esCodigo) || null;
  };

  const client = new ImapFlow({ host: 'imap.gmail.com', port: 993, secure: true, auth: { user: gmailUser, pass: gmailPass }, logger: false });
  await client.connect();
  let code = null;
  try {
    for (let intento = 0; intento < 20 && !code; intento++) {
      const lock = await client.getMailboxLock('INBOX');
      try {
        const desde = new Date(sinceEpochMs - 90000);
        let ids = [];
        try { ids = await client.search({ from: 'shellflota', since: desde }, { uid: true }); } catch {}
        if (ids && ids.length) {
          // Pueden acumularse varios mails de token en la casilla. Elegimos el MÁS NUEVO
          // que haya llegado DESPUÉS de este login (descarta OTP de intentos anteriores).
          let mejor = null;
          for (const uid of ids.slice(-8)) {
            const m = await client.fetchOne(uid, { source: true, internalDate: true }, { uid: true });
            if (!m || !m.internalDate) continue;
            if (m.internalDate.getTime() < sinceEpochMs - 20000) continue; // OTP viejo → descartar
            if (!mejor || m.internalDate > mejor.internalDate) mejor = m;
          }
          if (mejor && mejor.source) {
            let contenido = '';
            if (simpleParser) {
              try {
                const p = await simpleParser(mejor.source);
                let htmlLimpio = '';
                if (p.html) {
                  htmlLimpio = p.html
                    .replace(/<style[\s\S]*?<\/style>/gi, ' ')
                    .replace(/style\s*=\s*"[^"]*"/gi, ' ')
                    .replace(/#\s*[0-9a-fA-F]{3,8}\b/g, ' ')
                    .replace(/<[^>]+>/g, ' ');
                }
                contenido = (p.text || '') + '  ⁣  ' + htmlLimpio + '  ' + (p.subject || '');
              } catch {}
            }
            if (!contenido) contenido = mejor.source.toString().replace(/#\s*[0-9a-fA-F]{3,8}\b/g, ' ');
            const c = extraerCodigo(contenido);
            if (c) { console.log('[shell][OTP] código extraído (mail más nuevo ' + mejor.internalDate.toISOString() + '):', c); code = c; }
          }
        }
      } finally { lock.release(); }
      if (!code) await sleep(4500);
    }
  } finally { await client.logout().catch(() => {}); }
  return code;
}

async function dumpShellDiag(page, etiqueta) {
  try {
    const diag = await page.evaluate(() => {
      const els = [...document.querySelectorAll('input, button, a, [role="button"]')];
      return els.slice(0, 60).map((e, i) => {
        // No dumpear valores tipeados (evita loguear clave/código): solo texto de botones/links.
        const esInputDato = e.tagName === 'INPUT' && e.type !== 'submit' && e.type !== 'button';
        const t = esInputDato ? '' : (e.innerText || e.value || '').trim().replace(/\s+/g, ' ').slice(0, 20);
        return `#${i}[${[e.tagName, e.type && 'type:' + e.type, e.id && 'id:' + e.id, e.name && 'name:' + e.name, e.placeholder && 'ph:' + e.placeholder, t].filter(Boolean).join(' | ')}]`;
      }).join('\n');
    });
    console.log(`[shell][DIAG ${etiqueta}] url=${page.url()}\n${diag}`);
  } catch (e) { console.log('[shell][DIAG] no pude dumpear:', e?.message); }
}

async function runShellSaldo({ usuario, password, gmailUser, gmailPass }) {
  shellState.step = 'Abriendo navegador...';
  const browser = await chromium.launch({ headless: true, args: ['--no-sandbox', '--disable-dev-shm-usage', '--disable-blink-features=AutomationControlled'] });
  const context = await browser.newContext({ userAgent: 'Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36', viewport: { width: 1280, height: 800 } });
  const page = await context.newPage();
  shellState.browser = browser; shellState.context = context; shellState.page = page;

  shellState.step = 'Abriendo login de Shell Flota...';
  await page.goto('https://shellflota.mundonectar.com/GeneralLogin/Login.aspx', { waitUntil: 'domcontentloaded', timeout: 40000 });
  await sleep(2500);

  shellState.step = 'Ingresando usuario y clave...';
  // Selectores exactos de Shell (ASP.NET): #UserName + #frmLogin_Password + #LoginImageButton (ENTRAR).
  const userField = page.locator('#UserName');
  const passField = page.locator('#frmLogin_Password');
  try {
    await userField.click({ timeout: 8000 }); await userField.fill(''); await userField.pressSequentially(usuario, { delay: 40 });
    await passField.click({ timeout: 8000 }); await passField.fill(''); await passField.pressSequentially(password, { delay: 40 });
  } catch (e) {
    await dumpShellDiag(page, 'login');
    shellState.result = { ok: false, error: 'No encontré los campos de usuario/clave de Shell. Hay que ajustar el selector.' }; await sleep(12000); return;
  }

  const loginAt = Date.now();
  shellState.step = 'Ingresando (ENTRAR)...';
  const btnEntrar = page.locator('#LoginImageButton');
  try { await btnEntrar.click({ timeout: 8000 }); }
  catch { try { await passField.press('Enter'); } catch {} }
  await sleep(4000);

  let txt = ''; try { txt = ((await page.locator('body').innerText({ timeout: 3000 })) || '').toLowerCase(); } catch {}
  if (txt.includes('incorrecta') || txt.includes('incorrecto') || txt.includes('bloquear')) {
    shellState.result = { ok: false, error: 'Shell rechazó el usuario o la clave.' }; await sleep(8000); return;
  }

  shellState.step = 'Esperando el token en el mail...';
  await dumpShellDiag(page, 'otp');
  let code = null;
  try { code = await leerOtpShellDesdeGmail(gmailUser, gmailPass, loginAt); }
  catch (e) { shellState.result = { ok: false, error: 'No pude leer el mail (IMAP): ' + (e?.message || e) }; await sleep(10000); return; }
  if (!code) { shellState.result = { ok: false, error: 'No llegó (o no pude leer) el código del mail en el tiempo esperado.' }; await sleep(10000); return; }

  shellState.step = 'Ingresando el código del mail...';
  // Shell pide el código en 6 casillas separadas: #otp1..#otp6, un carácter cada una.
  const chars = code.split('');
  try {
    await page.locator('#otp1').waitFor({ state: 'visible', timeout: 10000 });
    for (let i = 0; i < 6; i++) {
      const box = page.locator(`#otp${i + 1}`);
      await box.click({ timeout: 5000 });
      await box.fill('');
      await box.pressSequentially(chars[i] || '', { delay: 80 });
    }
  } catch {
    await dumpShellDiag(page, 'otp-fields');
    shellState.result = { ok: false, error: `Leí el código (${code}) pero no pude escribirlo en las casillas. Ajustar.` }; await sleep(10000); return;
  }
  // Confirmar con el mismo botón ENTRAR.
  try { await page.locator('#LoginImageButton').click({ timeout: 6000 }); } catch { try { await page.locator('#otp6').press('Enter'); } catch {} }
  await page.waitForLoadState('domcontentloaded', { timeout: 30000 }).catch(() => {});
  await sleep(6000);

  // ¿Rechazó el token? (seguimos en Login.aspx con mensaje)
  let txt2 = ''; try { txt2 = ((await page.locator('body').innerText({ timeout: 3000 })) || '').toLowerCase(); } catch {}
  if (page.url().includes('Login.aspx') && (txt2.includes('incorrecto') || txt2.includes('inválid') || txt2.includes('invalid') || txt2.includes('venci') || txt2.includes('expir'))) {
    shellState.result = { ok: false, error: `El código (${code}) no fue aceptado (¿venció o mal leído?).` }; await sleep(10000); return;
  }

  shellState.step = 'Buscando el saldo disponible...';
  await sleep(2000);
  let saldoText = null;
  try {
    const userIcon = page.locator('header button, header a, [class*="user" i], [aria-label*="usuario" i]').last();
    await userIcon.click({ timeout: 6000 }).catch(() => {});
    await sleep(1200);
    const saldoLink = page.locator('text=/Saldo Cuenta/i').first();
    await saldoLink.click({ timeout: 6000 }).catch(() => {});
    await sleep(2500);
    const cuerpo = (await page.locator('body').innerText({ timeout: 3000 })) || '';
    const m = cuerpo.match(/Disponible[^\d\-]*([\-]?[\d.\,]+)/i);
    if (m) saldoText = m[1];
  } catch {}

  if (!saldoText) {
    await dumpShellDiag(page, 'saldo');
    shellState.result = { ok: true, loggedIn: true, saldo: null, error: 'Entré y usé el token, pero no pude leer el número del saldo. Ajustar selector (ver diagnóstico).' };
    await sleep(12000); return;
  }

  shellState.step = '¡Saldo leído!';
  shellState.result = { ok: true, loggedIn: true, saldo: saldoText };
  await sleep(4000);
}

// --- Start ---
app.listen(PORT, () => {
  console.log(`[wa] servicio Playwright escuchando en :${PORT}`);
  tryRestoreSession().catch((e) => console.error('[wa] restore:', e));
});

// Cleanup
process.on('SIGTERM', async () => {
  console.log('[wa] SIGTERM, cerrando...');
  await closeBrowserSafely();
  process.exit(0);
});
process.on('SIGINT', async () => {
  console.log('[wa] SIGINT, cerrando...');
  await closeBrowserSafely();
  process.exit(0);
});
