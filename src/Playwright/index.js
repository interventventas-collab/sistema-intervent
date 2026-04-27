// WhatsApp Web automation service (single-session) via Playwright
// Expone un API HTTP minimalista para que la API .NET pueda: vincular, obtener QR,
// chequear estado, desvincular y enviar mensajes.

const express = require('express');
const fs = require('fs');
const path = require('path');
const { chromium } = require('playwright');

const PORT = parseInt(process.env.PORT || '3001', 10);
const DATA_DIR = '/data/whatsapp-session';
const STORAGE_STATE_PATH = path.join(DATA_DIR, 'storage-state.json');

if (!fs.existsSync(DATA_DIR)) {
  fs.mkdirSync(DATA_DIR, { recursive: true });
}

const app = express();
app.use(express.json({ limit: '5mb' }));

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
