using SkiaSharp;

namespace Api.Services;

/// <summary>
/// 2026-10-01 — Saca el fondo VERDE o AZUL (tela chroma) de una foto de producto y la deja sobre blanco
/// puro, cuadrada a 1200 px, lista para la portada de MercadoLibre. Sin IA ni servicios de afuera.
///
/// Pasos: endereza la foto del celu (EXIF) → mira las 4 esquinas para saber si la tela es verde o azul
/// (o se fuerza a mano) → calcula qué tan "de la tela" es cada pixel (transparencia con borde suave) →
/// le saca el reflejo verde/azul al contorno del producto → lo recorta, lo centra con 10% de margen y
/// lo pega sobre #FFFFFF → JPG calidad 92.
///
/// ⚠ Se usa SkiaSharp (ya estaba en el proyecto para FotoPackService) y no ImageSharp: ImageSharp v3 pide
/// licencia paga a empresas que facturan más de USD 1M al año.
/// ⚠ Un producto con partes del MISMO verde/azul que la tela pierde esas partes; para eso las zonas
/// encerradas por el producto (que no tocan el borde de la foto) se sacan sólo si son casi idénticas a la tela.
/// </summary>
public static class FotoChromaService
{
    /// <param name="Color">"auto" | "verde" | "azul".</param>
    /// <param name="Tolerancia">0–100. Más alto = se come más tonos parecidos a la tela (sombras, arrugas).</param>
    /// <param name="Suavizado">0–100. Ancho del borde suave del contorno.</param>
    public record Opciones(string? Color = "auto", int Tolerancia = 50, int Suavizado = 40);

    public record Resultado(byte[]? Jpeg, string? ColorUsado, string? Aviso, string? Error);

    public const int Lado = 1200;
    private const double Margen = 0.10;     // por lado
    private const int MaxTrabajo = 2000;    // las fotos del celu vienen de 4000 px; no hace falta tanto

    public static Resultado Procesar(byte[] original, Opciones? op = null)
    {
        op ??= new Opciones();
        using var src = DecodificarDerecha(original);
        if (src is null) return new Resultado(null, null, null, "No pude leer la imagen.");

        using var b = Achicar(src, MaxTrabajo);
        int w = b.Width, h = b.Height;
        var px = b.Pixels;

        // ── 1. Color de la tela ──
        var (verde, key, avisoColor) = ElegirColor(px, w, h, (op.Color ?? "auto").Trim().ToLowerInvariant());
        if (verde is null) return new Resultado(null, null, null, avisoColor);
        bool esVerde = verde.Value;
        var colorUsado = esVerde ? "verde" : "azul";

        // "Cuánto le gana" el canal de la tela a los otros dos, en la tela misma.
        double keyDom = Dominancia(key, esVerde);
        if (keyDom < 25)
            return new Resultado(null, colorUsado, null, $"El fondo casi no tiene {colorUsado}: así no se puede separar el producto.");
        double keySat = keyDom / Math.Max(1, (int)Canal(key, esVerde));

        // ── 2. Transparencia (alpha) de cada pixel ──
        double tol = Math.Clamp(op.Tolerancia, 0, 100) / 100.0;
        double suav = Math.Clamp(op.Suavizado, 0, 100) / 100.0;
        double hi = 0.75 - 0.35 * tol;          // a partir de acá es tela (alpha 0)
        double lo = hi - (0.15 + 0.25 * suav);  // por debajo es producto (alpha 1)

        // 2026-10-06 (fotos reales con la tela del depósito): se mide CUÁNTO le gana el verde a los otros
        // canales en números absolutos, comparado con la tela. Antes se medía relativo al brillo y los
        // productos NEGROS con reflejo verde (vasos y bolsas de café FRIKAF) se confundían con la tela y
        // desaparecían. Además el pixel tiene que ser "tan verde de color" como la tela (rel): así un gris
        // verdoso no cuenta como tela aunque tenga bastante verde.
        var t = new float[w * h];       // tela con el criterio normal
        var tEnc = new float[w * h];    // tela con el criterio estricto (zonas encerradas por el producto)
        for (int i = 0; i < px.Length; i++)
        {
            var c = px[i];
            double d = Dominancia(c, esVerde);
            if (d <= 0) continue;
            double abs = d / keyDom;
            double rel = d / Math.Max(1, (int)Canal(c, esVerde)) / keySat;
            if (rel > 0.55) t[i] = (float)abs;
            if (rel > 0.70) tEnc[i] = (float)abs;
        }

        float AlphaDe(float v, double h0, double l0) =>
            v >= h0 ? 0f : v <= l0 ? 1f : (float)((h0 - v) / (h0 - l0));

        var alpha = new float[w * h];
        for (int i = 0; i < t.Length; i++) alpha[i] = AlphaDe(t[i], hi, lo);

        // ── 3. Zonas de "tela" encerradas por el producto: sólo se sacan si son casi la tela exacta ──
        var conectado = RellenoDesdeBorde(alpha, w, h);
        double hiEnc = hi + 0.25, loEnc = lo + 0.25;
        for (int i = 0; i < alpha.Length; i++)
            if (!conectado[i] && alpha[i] < 1f) alpha[i] = AlphaDe(tEnc[i], hiEnc, loEnc);

        // ── 4. Borrar manchitas sueltas (arrugas o pelusas de la tela que quedaron) ──
        LimpiarManchitas(alpha, w, h, minArea: Math.Max(50, w * h / 1000));

        // ── 5. Suavizar el contorno (anti-alias) ──
        int radio = Math.Max(1, (int)Math.Round(Math.Min(w, h) / 900.0 * (0.5 + suav)));
        alpha = BlurCaja(alpha, w, h, radio);
        // "Apretar" el borde: lo casi transparente del contorno es pelusa de la tela (el tejido y la sombra
        // pegada al producto), no producto. Sin esto quedaba un halo oscuro y deshilachado alrededor.
        for (int i = 0; i < alpha.Length; i++) alpha[i] = Math.Clamp((alpha[i] - 0.2f) / 0.8f, 0f, 1f);

        // ── 6. Recorte al contenido ──
        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (alpha[y * w + x] > 0.5f)
                {
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        if (maxX < 0) return new Resultado(null, colorUsado, null, "No encontré el producto: toda la foto parece fondo. Probá bajando la tolerancia.");

        // ── 7. Sacar el reflejo verde/azul (spill) ──
        // En el contorno, todo. Adentro del producto, sólo en los colores CASI NEUTROS (blancos, grises,
        // negros con un tinte verde de rebote de la tela: diferencia chica); un verde de verdad (etiqueta
        // de matcha, pistacho) queda como está. ⚠ Un verde muy clarito (ej. syrup de pistacho transparente)
        // puede salir algo blanqueado: es el precio de que los negros salgan negros y no verdosos.
        int banda = Math.Max(3, Math.Min(w, h) / 150);
        var dist = DistanciaAlFondo(alpha, w, h, banda);
        for (int i = 0; i < px.Length; i++)
        {
            if (alpha[i] <= 0f) continue;
            // 1 en el borde (y en lo semitransparente), 0 de la banda para adentro.
            float kBorde = Math.Max(1f - alpha[i], 1f - dist[i] / (float)banda);
            double dPx = Dominancia(px[i], esVerde);
            float kNeutro = dPx <= 0 ? 0f : (float)Math.Clamp(1 - (dPx - 20) / 35.0, 0, 1);
            // El reflejo es un gris/negro/blanco con verde encima: los otros dos canales quedan PAREJOS.
            // Un verde lima de etiqueta (Matcha) tiene rojo y azul muy distintos → no es reflejo, no se toca.
            var cc = px[i];
            int dispar = esVerde ? Math.Abs(cc.Red - cc.Blue) : Math.Abs(cc.Red - cc.Green);
            kNeutro *= (float)Math.Clamp(1 - (dispar - 15) / 25.0, 0, 1);
            float k = Math.Max(kBorde, kNeutro);
            if (k <= 0f) continue;
            px[i] = SacarReflejo(px[i], esVerde, Math.Min(1f, k));
        }

        // ── 8. Componer sobre blanco, centrado con margen ──
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var conAlpha = new SKBitmap(info);
        var salida = new SKColor[px.Length];
        for (int i = 0; i < px.Length; i++)
            salida[i] = px[i].WithAlpha((byte)Math.Round(alpha[i] * 255));
        conAlpha.Pixels = salida;

        int cw = maxX - minX + 1, ch = maxY - minY + 1;
        double util = Lado * (1 - 2 * Margen);
        double esc = Math.Min(util / cw, util / ch);
        float dw = (float)(cw * esc), dh = (float)(ch * esc);
        var destino = SKRect.Create((Lado - dw) / 2f, (Lado - dh) / 2f, dw, dh);

        using var surface = SKSurface.Create(new SKImageInfo(Lado, Lado));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        using (var img = SKImage.FromBitmap(conAlpha))
        using (var p = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
            canvas.DrawImage(img, new SKRect(minX, minY, maxX + 1, maxY + 1), destino, p);

        using var snap = surface.Snapshot();
        using var data = snap.Encode(SKEncodedImageFormat.Jpeg, 92);

        var avisos = new List<string>();
        if (avisoColor is not null) avisos.Add(avisoColor);
        int tocaBorde = (minX <= 1 ? 1 : 0) + (minY <= 1 ? 1 : 0) + (maxX >= w - 2 ? 1 : 0) + (maxY >= h - 2 ? 1 : 0);
        if (tocaBorde > 0)
            avisos.Add("El producto (o algo que no es la tela) toca el borde de la foto: puede haber quedado cortado o con un pedazo de otra cosa. Conviene sacarla de un poco más lejos.");
        if (esc > 2.5)
            avisos.Add("El producto salió muy chico en la foto original: al agrandarlo puede verse borroso. Conviene sacarla más cerca.");

        return new Resultado(data.ToArray(), colorUsado, avisos.Count == 0 ? null : string.Join(" ", avisos), null);
    }

    /// <summary>Foto original enderezada y achicada (para el "antes" de la vista previa).</summary>
    public static byte[]? Miniatura(byte[] original, int max = 900)
    {
        using var src = DecodificarDerecha(original);
        if (src is null) return null;
        using var chica = Achicar(src, max);
        using var img = SKImage.FromBitmap(chica);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    // ───────────────────────── helpers ─────────────────────────

    private static byte Canal(SKColor c, bool verde) => verde ? c.Green : c.Blue;

    private static double Dominancia(SKColor c, bool verde) =>
        verde ? c.Green - Math.Max(c.Red, c.Blue) : c.Blue - Math.Max(c.Red, c.Green);

    /// <summary>Le baja el canal de la tela hasta el más alto de los otros dos, en proporción k, y devuelve
    /// parte de lo sacado repartido en los 3 canales para que el borde no quede oscuro.</summary>
    private static SKColor SacarReflejo(SKColor c, bool verde, float k)
    {
        int r = c.Red, g = c.Green, b = c.Blue;
        if (verde)
        {
            int tope = (Math.Max(r, b) + (r + b) / 2) / 2;
            if (g <= tope) return c;
            int sacar = (int)((g - tope) * k);
            g -= sacar; int devolver = sacar / 3;
            r += devolver; g += devolver; b += devolver;
        }
        else
        {
            int tope = (Math.Max(r, g) + (r + g) / 2) / 2;
            if (b <= tope) return c;
            int sacar = (int)((b - tope) * k);
            b -= sacar; int devolver = sacar / 3;
            r += devolver; g += devolver; b += devolver;
        }
        return new SKColor((byte)Math.Min(255, r), (byte)Math.Min(255, g), (byte)Math.Min(255, b), c.Alpha);
    }

    /// <summary>Mira 4 cuadraditos en las esquinas (5% del lado). Devuelve si la tela es verde, el color
    /// "promedio" de la tela y un aviso/error.</summary>
    private static (bool? Verde, SKColor Key, string? Aviso) ElegirColor(SKColor[] px, int w, int h, string pedido)
    {
        int lado = Math.Max(4, Math.Min(w, h) / 20);
        var esquinas = new[] { (0, 0), (w - lado, 0), (0, h - lado), (w - lado, h - lado) }
            .Select(e => Mediana(px, w, e.Item1, e.Item2, lado)).ToList();

        var verdes = esquinas.Where(c => Dominancia(c, true) > 20).ToList();
        var azules = esquinas.Where(c => Dominancia(c, false) > 20).ToList();

        static SKColor Prom(List<SKColor> l) => new(
            (byte)l.Average(c => c.Red), (byte)l.Average(c => c.Green), (byte)l.Average(c => c.Blue));

        if (pedido == "verde" || pedido == "azul")
        {
            bool v = pedido == "verde";
            var l = v ? verdes : azules;
            if (l.Count > 0)
                return (v, Prom(l), l.Count < 3 ? $"Sólo {l.Count} de las 4 esquinas son {(v ? "verdes" : "azules")}: revisá que la tela cubra toda la foto." : null);
            // Ninguna esquina sirve: se usa un tono típico de tela chroma y se avisa.
            return (v, v ? new SKColor(40, 170, 70) : new SKColor(30, 70, 190),
                $"Ninguna esquina de la foto es {pedido}: usé un {pedido} típico de tela chroma, el resultado puede no ser bueno.");
        }

        if (verdes.Count == 0 && azules.Count == 0)
            return (null, default, "No reconocí el fondo: las esquinas de la foto no son ni verdes ni azules. Si la tela es de un tono raro, elegí el color a mano.");
        bool esVerde = verdes.Count >= azules.Count;
        var lista = esVerde ? verdes : azules;
        var color = esVerde ? "verde" : "azul";
        return (esVerde, Prom(lista), lista.Count < 3 ? $"Sólo {lista.Count} de las 4 esquinas son {(esVerde ? "verdes" : "azules")}: revisá que la tela cubra toda la foto." : null);
    }

    private static SKColor Mediana(SKColor[] px, int w, int x0, int y0, int lado)
    {
        var rs = new List<byte>(); var gs = new List<byte>(); var bs = new List<byte>();
        for (int y = y0; y < y0 + lado; y++)
            for (int x = x0; x < x0 + lado; x++)
            {
                var c = px[y * w + x];
                rs.Add(c.Red); gs.Add(c.Green); bs.Add(c.Blue);
            }
        rs.Sort(); gs.Sort(); bs.Sort();
        int m = rs.Count / 2;
        return new SKColor(rs[m], gs[m], bs[m]);
    }

    /// <summary>Marca los pixeles "de tela" (alpha &lt; 0.5) que se conectan con el borde de la foto.</summary>
    private static bool[] RellenoDesdeBorde(float[] alpha, int w, int h)
    {
        var vis = new bool[w * h];
        var cola = new Queue<int>();
        void Sembrar(int i) { if (!vis[i] && alpha[i] < 0.5f) { vis[i] = true; cola.Enqueue(i); } }
        for (int x = 0; x < w; x++) { Sembrar(x); Sembrar((h - 1) * w + x); }
        for (int y = 0; y < h; y++) { Sembrar(y * w); Sembrar(y * w + w - 1); }
        while (cola.Count > 0)
        {
            int i = cola.Dequeue(), x = i % w, y = i / w;
            if (x > 0) Sembrar(i - 1);
            if (x < w - 1) Sembrar(i + 1);
            if (y > 0) Sembrar(i - w);
            if (y < h - 1) Sembrar(i + w);
        }
        return vis;
    }

    /// <summary>Pone en transparente los pedacitos de "producto" más chicos que minArea.</summary>
    private static void LimpiarManchitas(float[] alpha, int w, int h, int minArea)
    {
        var etiqueta = new int[w * h];
        var cola = new Queue<int>();
        var miembros = new List<int>();
        int id = 0;
        for (int s = 0; s < alpha.Length; s++)
        {
            if (etiqueta[s] != 0 || alpha[s] < 0.5f) continue;
            id++;
            miembros.Clear();
            etiqueta[s] = id; cola.Enqueue(s);
            while (cola.Count > 0)
            {
                int i = cola.Dequeue(), x = i % w, y = i / w;
                miembros.Add(i);
                void Ver(int j) { if (etiqueta[j] == 0 && alpha[j] >= 0.5f) { etiqueta[j] = id; cola.Enqueue(j); } }
                if (x > 0) Ver(i - 1);
                if (x < w - 1) Ver(i + 1);
                if (y > 0) Ver(i - w);
                if (y < h - 1) Ver(i + w);
            }
            if (miembros.Count < minArea)
                foreach (var i in miembros) alpha[i] = 0f;
        }
    }

    /// <summary>Blur de caja separable (2 pasadas = casi gaussiano) sobre la transparencia.</summary>
    private static float[] BlurCaja(float[] a, int w, int h, int r)
    {
        var tmp = new float[a.Length];
        var res = new float[a.Length];
        for (int pasada = 0; pasada < 2; pasada++)
        {
            var src = pasada == 0 ? a : res;
            for (int y = 0; y < h; y++)
            {
                float suma = 0; int n = 0; int fila = y * w;
                for (int x = -r; x < w; x++)
                {
                    int entra = x + r, sale = x - r - 1;
                    if (entra < w) { suma += src[fila + entra]; n++; }
                    if (sale >= 0) { suma -= src[fila + sale]; n--; }
                    if (x >= 0) tmp[fila + x] = suma / n;
                }
            }
            for (int x = 0; x < w; x++)
            {
                float suma = 0; int n = 0;
                for (int y = -r; y < h; y++)
                {
                    int entra = y + r, sale = y - r - 1;
                    if (entra < h) { suma += tmp[entra * w + x]; n++; }
                    if (sale >= 0) { suma -= tmp[sale * w + x]; n--; }
                    if (y >= 0) res[y * w + x] = suma / n;
                }
            }
        }
        // Lo que ya era 100% producto adentro no se tiene que "lavar" por el blur.
        for (int i = 0; i < a.Length; i++) if (a[i] >= 1f && res[i] > 0.98f) res[i] = 1f;
        return res;
    }

    /// <summary>Distancia (en pixeles, aproximada, tope = banda) de cada pixel al fondo más cercano.</summary>
    private static float[] DistanciaAlFondo(float[] alpha, int w, int h, int banda)
    {
        var d = new float[w * h];
        for (int i = 0; i < d.Length; i++) d[i] = alpha[i] < 0.5f ? 0f : banda;
        const float diag = 1.4142f;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x; float v = d[i];
                if (v == 0) continue;
                if (x > 0) v = Math.Min(v, d[i - 1] + 1);
                if (y > 0) v = Math.Min(v, d[i - w] + 1);
                if (x > 0 && y > 0) v = Math.Min(v, d[i - w - 1] + diag);
                if (x < w - 1 && y > 0) v = Math.Min(v, d[i - w + 1] + diag);
                d[i] = v;
            }
        for (int y = h - 1; y >= 0; y--)
            for (int x = w - 1; x >= 0; x--)
            {
                int i = y * w + x; float v = d[i];
                if (v == 0) continue;
                if (x < w - 1) v = Math.Min(v, d[i + 1] + 1);
                if (y < h - 1) v = Math.Min(v, d[i + w] + 1);
                if (x < w - 1 && y < h - 1) v = Math.Min(v, d[i + w + 1] + diag);
                if (x > 0 && y < h - 1) v = Math.Min(v, d[i + w - 1] + diag);
                d[i] = v;
            }
        return d;
    }

    /// <summary>Decodifica respetando la orientación EXIF (las fotos del celu vienen "acostadas" con una
    /// marca que dice cómo girarlas).</summary>
    private static SKBitmap? DecodificarDerecha(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(info);
        var r = codec.GetPixels(info, bmp.GetPixels());
        if (r != SKCodecResult.Success && r != SKCodecResult.IncompleteInput) { bmp.Dispose(); return null; }

        var origen = codec.EncodedOrigin;
        if (origen == SKEncodedOrigin.TopLeft) return bmp;

        bool rota = origen is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop
                           or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int nw = rota ? bmp.Height : bmp.Width, nh = rota ? bmp.Width : bmp.Height;
        var res = new SKBitmap(new SKImageInfo(nw, nh, SKColorType.Rgba8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(res))
        {
            switch (origen)
            {
                case SKEncodedOrigin.TopRight: c.Scale(-1, 1, nw / 2f, 0); break;
                case SKEncodedOrigin.BottomRight: c.RotateDegrees(180, nw / 2f, nh / 2f); break;
                case SKEncodedOrigin.BottomLeft: c.Scale(1, -1, 0, nh / 2f); break;
                case SKEncodedOrigin.LeftTop: c.Scale(-1, 1, nw / 2f, 0); c.Translate(nw, 0); c.RotateDegrees(90); break;
                case SKEncodedOrigin.RightTop: c.Translate(nw, 0); c.RotateDegrees(90); break;
                case SKEncodedOrigin.RightBottom: c.Scale(-1, 1, nw / 2f, 0); c.Translate(0, nh); c.RotateDegrees(270); break;
                case SKEncodedOrigin.LeftBottom: c.Translate(0, nh); c.RotateDegrees(270); break;
            }
            c.DrawBitmap(bmp, 0, 0);
        }
        bmp.Dispose();
        return res;
    }

    private static SKBitmap Achicar(SKBitmap b, int max)
    {
        var esc = Math.Min(1.0, (double)max / Math.Max(b.Width, b.Height));
        var info = new SKImageInfo(Math.Max(1, (int)(b.Width * esc)), Math.Max(1, (int)(b.Height * esc)),
            SKColorType.Rgba8888, SKAlphaType.Premul);
        var r = new SKBitmap(info);
        using var c = new SKCanvas(r);
        c.Clear(SKColors.Transparent);
        using var p = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
        c.DrawBitmap(b, new SKRect(0, 0, info.Width, info.Height), p);
        return r;
    }
}
