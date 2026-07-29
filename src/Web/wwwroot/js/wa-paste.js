// 2026-07-29: Pegar capturas de pantalla (Ctrl+V / Win+V) en el chat de WhatsApp.
// Escucha el evento "paste"; si en el portapapeles hay una imagen, la lee y se la
// pasa a Blazor (.NET) para subirla y enviarla como adjunto, igual que el clip 📎.
window.waPaste = {
    _handler: null,
    register: function (dotNetRef) {
        // Si ya habia uno registrado (re-render / volver a entrar), lo sacamos primero.
        this.unregister();
        const handler = function (e) {
            const items = (e.clipboardData && e.clipboardData.items) || [];
            for (let i = 0; i < items.length; i++) {
                const it = items[i];
                if (it.kind === 'file' && it.type && it.type.indexOf('image/') === 0) {
                    const file = it.getAsFile();
                    if (!file) continue;
                    e.preventDefault(); // que no pegue "ruido" de texto en el cuadro
                    const reader = new FileReader();
                    reader.onload = function () {
                        const dataUrl = reader.result || '';
                        const comma = dataUrl.indexOf(',');
                        const b64 = comma >= 0 ? dataUrl.substring(comma + 1) : '';
                        let ext = (it.type.split('/')[1] || 'png').split('+')[0];
                        const name = 'captura-' + new Date().getTime() + '.' + ext;
                        dotNetRef.invokeMethodAsync('RecibirImagenPegada', b64, name, it.type);
                    };
                    reader.readAsDataURL(file);
                    return; // primera imagen encontrada, listo
                }
            }
        };
        this._handler = handler;
        document.addEventListener('paste', handler);
    },
    unregister: function () {
        if (this._handler) {
            document.removeEventListener('paste', this._handler);
            this._handler = null;
        }
    }
};
