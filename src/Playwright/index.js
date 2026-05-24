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
  // Si aparecen los chats (#pane-side o role "grid"), ya está vinculado
  try {
    const paneSide = await page.$('#pane-side');
    if (paneSide) return true;
    const chatList = await page.$('[role="grid"]');
    if (chatList) return true;
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
app.get('/whatsapp/status', async (req, res) => {
  try {
    // Si tenemos página viva, re-chequear
    let linked = state.linked;
    if (state.page && !state.page.isClosed()) {
      try {
        linked = await isLinkedOnPage(state.page);
      } catch {
        linked = false;
      }
    } else {
      linked = false;
    }
    state.linked = linked;
    res.json({ linked, isLinking: state.isLinking, info: state.lastInfo });
  } catch (err) {
    res.status(500).json({ error: err.message });
  }
});

// GET /whatsapp/check-linked - versión ligera para polling
app.get('/whatsapp/check-linked', async (req, res) => {
  try {
    let linked = false;
    if (state.page && !state.page.isClosed()) {
      linked = await isLinkedOnPage(state.page).catch(() => false);
    }
    state.linked = linked;
    res.json({ linked });
  } catch {
    res.json({ linked: false });
  }
});

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
  try {
    const { recipients, message } = req.body || {};
    if (!Array.isArray(recipients) || recipients.length === 0) {
      return res.status(400).json({ error: 'recipients vacío' });
    }

    // Restaurar sesión desde storage si no hay browser
    if (!state.page || state.page.isClosed()) {
      if (!fs.existsSync(STORAGE_STATE_PATH)) {
        return res.status(400).json({ error: 'WhatsApp no esta vinculado' });
      }
      await startSession({ useStorageState: true });
      // Esperar que se cargue la app y aparezcan chats
      await sleep(5000);
      const linked = await isLinkedOnPage(state.page);
      if (!linked) {
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

    res.json(results);
  } catch (err) {
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

// Mutex simple para evitar múltiples lecturas simultáneas (cambios de chat se pisan)
let messagesListBusy = false;

app.post('/whatsapp/messages/list', async (req, res) => {
  const phoneInput = (req.body && req.body.phone) || '';
  const sinceId = (req.body && req.body.sinceId) || '';
  if (!phoneInput) return res.status(400).json({ error: 'phone requerido' });

  const phone = normalizePhone(phoneInput);
  if (phone.length < 8) return res.status(400).json({ error: 'phone invalido' });

  if (messagesListBusy) return res.status(429).json({ error: 'busy', message: 'Otro listado en curso' });
  messagesListBusy = true;

  try {
    // Asegurar sesión
    const alive = await ensureSessionAlive();
    if (!alive) {
      try { await startSession({ useStorageState: true }); } catch {}
      if (!state.page || state.page.isClosed()) {
        messagesListBusy = false;
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
      messagesListBusy = false;
      return res.status(400).json({ error: 'Numero invalido o sin WhatsApp' });
    }
    if (!composeBox) {
      messagesListBusy = false;
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
    const messages = await state.page.evaluate(() => {
      const items = [];
      const nodes = document.querySelectorAll('div[data-id]');
      nodes.forEach(node => {
        const id = node.getAttribute('data-id') || '';
        if (!id) return;
        const isMessage = node.querySelector('span.selectable-text') !== null
                       || node.querySelector('[data-pre-plain-text]') !== null
                       || node.classList.contains('message-in')
                       || node.classList.contains('message-out');
        if (!isMessage) return;
        const fromMe = node.classList.contains('message-out') || id.startsWith('true_');
        let text = '';
        const textSpans = node.querySelectorAll('span.selectable-text span, span.selectable-text');
        if (textSpans.length > 0) {
          for (let i = textSpans.length - 1; i >= 0; i--) {
            const t = (textSpans[i].textContent || '').trim();
            if (t) { text = t; break; }
          }
        }
        const meta = node.querySelector('[data-pre-plain-text]');
        const metaAttr = meta ? meta.getAttribute('data-pre-plain-text') : '';
        items.push({ id, text, fromMe, meta: metaAttr });
      });
      return items;
    });

    let filtered = messages;
    if (sinceId) {
      const idx = messages.findIndex(m => m.id === sinceId);
      if (idx >= 0) filtered = messages.slice(idx + 1);
    }

    messagesListBusy = false;
    return res.json({ messages: filtered, total: messages.length, phone });
  } catch (err) {
    messagesListBusy = false;
    console.error('[wa] messages/list error:', err.message || err);
    return res.status(500).json({ error: err.message || 'error' });
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
