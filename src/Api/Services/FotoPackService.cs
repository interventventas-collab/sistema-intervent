using SkiaSharp;

namespace Api.Services;

/// <summary>
/// 2026-09-28 — Arma la foto de un PACK (3 cajas, 6 cajas...) a partir de la foto de UNA unidad.
///
/// Idea de Osmar: la publicación "Pack 3 Chocolate Submarino" tenía que tener una foto con 3 cajas, y
/// sacarla era un lío. Acá se recorta la unidad del fondo blanco y se repite en escalera (hasta 3 en una
/// fila; de 4 en adelante en dos filas, así las cajas no quedan chiquitas), con una sombra suave, en un
/// cuadrado de 1200 px sobre blanco — lo que MeLi pide para la portada.
///
/// ⚠ No se escribe "x3" encima: MeLi no permite texto en la foto de portada.
/// ⚠ Sólo queda prolijo si la foto original tiene fondo blanco (o casi). Si no, se avisa.
/// No toca MercadoLibre: devuelve la imagen y la pantalla la agrega como foto NUEVA, que se sube con
/// el "Guardar en MercadoLibre" de siempre.
/// </summary>
public static class FotoPackService
{
    public record Resultado(byte[]? Jpeg, string? Aviso, string? Error);

    private const int Lado = 1200;
    private const int UmbralBlanco = 240;   // un pixel con los 3 canales ≥ esto cuenta como fondo

    public static Resultado Armar(byte[] original, int cantidad)
    {
        if (cantidad < 2 || cantidad > 12)
            return new Resultado(null, null, "La cantidad tiene que ser entre 2 y 12.");

        using var src = SKBitmap.Decode(original);
        if (src is null) return new Resultado(null, null, "No pude leer la imagen.");

        // Trabajar en un tamaño razonable: las fotos de MeLi llegan a 1920 px y no hace falta tanto.
        using var chica = Achicar(src, 1200);
        var (unidad, bordeBlanco) = RecortarDelFondo(chica);
        if (unidad is null) return new Resultado(null, null, "No encontré el producto en la foto.");

        using (unidad)
        {
            var jpeg = Componer(unidad, cantidad);
            var aviso = bordeBlanco < 0.6
                ? "Ojo: esta foto no tiene fondo blanco, así que no se pudo recortar el producto y quedan las fotos pegadas. Conviene elegir una con fondo blanco."
                : null;
            return new Resultado(jpeg, aviso, null);
        }
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

    /// <summary>Saca el blanco que está PEGADO a los bordes (relleno desde afuera), así un logo blanco
    /// adentro de la caja no queda agujereado. Devuelve la unidad recortada a su contorno y qué parte
    /// del borde de la foto era blanco (para avisar si el fondo no servía).</summary>
    private static (SKBitmap? Unidad, double BordeBlanco) RecortarDelFondo(SKBitmap b)
    {
        int w = b.Width, h = b.Height;
        var px = b.Pixels;   // copia
        bool EsBlanco(int i)
        {
            var c = px[i];
            return c.Alpha < 20 || (c.Red >= UmbralBlanco && c.Green >= UmbralBlanco && c.Blue >= UmbralBlanco);
        }

        var fondo = new bool[w * h];
        var cola = new Queue<int>();
        int bordeTotal = 0, bordeBlancos = 0;
        void Sembrar(int x, int y)
        {
            var i = y * w + x;
            bordeTotal++;
            if (!EsBlanco(i)) return;
            bordeBlancos++;
            if (fondo[i]) return;
            fondo[i] = true;
            cola.Enqueue(i);
        }
        for (int x = 0; x < w; x++) { Sembrar(x, 0); Sembrar(x, h - 1); }
        for (int y = 0; y < h; y++) { Sembrar(0, y); Sembrar(w - 1, y); }

        while (cola.Count > 0)
        {
            var i = cola.Dequeue();
            int x = i % w, y = i / w;
            void Ver(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;
                var j = ny * w + nx;
                if (fondo[j] || !EsBlanco(j)) return;
                fondo[j] = true;
                cola.Enqueue(j);
            }
            Ver(x + 1, y); Ver(x - 1, y); Ver(x, y + 1); Ver(x, y - 1);
        }

        int minX = w, minY = h, maxX = -1, maxY = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var i = y * w + x;
                if (fondo[i]) { px[i] = SKColors.Transparent; continue; }
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        var bordeBlanco = bordeTotal == 0 ? 0 : (double)bordeBlancos / bordeTotal;
        if (maxX < 0) return (null, bordeBlanco);

        using var sinFondo = new SKBitmap(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        sinFondo.Pixels = px;

        var cw = maxX - minX + 1; var ch = maxY - minY + 1;
        var rec = new SKBitmap(new SKImageInfo(cw, ch, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var c = new SKCanvas(rec);
        c.Clear(SKColors.Transparent);
        c.DrawBitmap(sinFondo, new SKRect(minX, minY, maxX + 1, maxY + 1), new SKRect(0, 0, cw, ch));
        return (rec, bordeBlanco);
    }

    /// <summary>Hasta 3: una fila en escalera. De 4 en adelante: dos filas (la de atrás más arriba y
    /// corrida), cada una en escalera. Se dibuja de atrás para adelante.</summary>
    private static byte[] Componer(SKBitmap u, int n)
    {
        int filas = n <= 3 ? 1 : 2;
        int atras = n / filas;                 // fila de atrás (la más corta si es impar)
        int adelante = n - (filas == 2 ? atras : 0);
        var porFila = filas == 1 ? new[] { n } : new[] { adelante, atras };   // [0] = adelante

        // Corrimientos, en fracción del tamaño de UNA unidad.
        double dx = porFila[0] <= 3 ? 0.16 : 0.13;          // entre cajas de la misma fila
        double dy = porFila[0] <= 3 ? 0.13 : 0.09;
        double filaDx = 0.10, filaDy = 0.32;                // la fila de atrás: más arriba y a la derecha

        // Caja que ocupa todo, en unidades de ancho/alto de la unidad.
        double anchoU = 1 + dx * (porFila.Max() - 1) + (filas == 2 ? filaDx : 0);
        double altoU = 1 + dy * (porFila.Max() - 1) + (filas == 2 ? filaDy : 0);
        var esc = Math.Min(Lado * 0.9 / (u.Width * anchoU), Lado * 0.9 / (u.Height * altoU));
        int tw = (int)(u.Width * esc), th = (int)(u.Height * esc);

        double x0 = (Lado - anchoU * tw) / 2;
        double yAbajo = (Lado + altoU * th) / 2 - th;   // arriba-izquierda de la caja de más adelante

        using var surface = SKSurface.Create(new SKImageInfo(Lado, Lado));
        var c = surface.Canvas;
        c.Clear(SKColors.White);

        using var sombra = new SKPaint
        {
            ImageFilter = SKImageFilter.CreateDropShadowOnly(8, 10, 10, 10, new SKColor(0, 0, 0, 50)),
            FilterQuality = SKFilterQuality.High,
            IsAntialias = true,
        };
        using var pintura = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };

        for (int f = filas - 1; f >= 0; f--)
        {
            double fx = x0 + (f == 1 ? filaDx * tw : 0);
            double fy = yAbajo - (f == 1 ? filaDy * th : 0);
            for (int i = porFila[f] - 1; i >= 0; i--)
            {
                var r = SKRect.Create((float)(fx + dx * tw * i), (float)(fy - dy * th * i), tw, th);
                c.DrawBitmap(u, r, sombra);
                c.DrawBitmap(u, r, pintura);
            }
        }

        using var img = surface.Snapshot();
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }
}
