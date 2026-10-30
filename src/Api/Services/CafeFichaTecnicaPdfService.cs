using Api.Controllers;
using Api.Models;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using System.Globalization;
using System.Text;

namespace Api.Services;

/// <summary>
/// 2026-10-30: PDF de la ficha técnica de un café. Siempre el mismo diseño (negro + amarillo Frikaf):
/// fotos arriba, nombre + sello de perfil, descripción, datos, rueda de sabores, aroma/sabor/retrogusto
/// y balance. Lo que está vacío o marcado "no aplica" NO sale y el resto se acomoda solo.
/// En un blend los datos de origen salen por cada parte (Composición).
/// </summary>
public class CafeFichaTecnicaPdfService
{
    private static readonly CultureInfo Es = new("es-AR");
    private const string Amarillo = "#FACC15";
    private const string Gris = "#6B7280";

    /// <summary>Familias de la rueda de sabores, en el orden en que van alrededor de la rueda.
    /// La pantalla (FichaTecnicaCatalogo en Web) usa las mismas keys y colores.</summary>
    public static readonly (string Key, string Label, string Color)[] Rueda =
    {
        ("floral", "Floral", "#D63384"),
        ("afrutado", "Afrutado", "#DC2626"),
        ("acido", "Ácido / fermentado", "#D4A017"),
        ("verde", "Verde / vegetal", "#6B7A2A"),
        ("otros", "Otros", "#9CA3AF"),
        ("tostado", "Tostado", "#B45309"),
        ("especiado", "Especiado", "#C2410C"),
        ("cacao", "Nueces / cacao", "#5B3A24"),
        ("dulce", "Dulce", "#EA580C"),
    };

    static CafeFichaTecnicaPdfService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public byte[] Generar(CafeFichaTecnica f)
    {
        var noAplica = new HashSet<string>((f.NoAplica ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        string? V(string key, string? val) => noAplica.Contains(key) || string.IsNullOrWhiteSpace(val) ? null : val.Trim();
        var blend = f.Tipo == "BLEND";

        var fotoCafe = noAplica.Contains("fotoCafe") ? null : Leer(f.FotoCafeArchivo);
        var fotoEtiqueta = noAplica.Contains("fotoEtiqueta") ? null : Leer(f.FotoEtiquetaArchivo);

        // Renglón debajo del nombre
        var sub = new List<string>();
        if (blend) sub.Add("Blend");
        else
        {
            if (V("region", f.Region) is string reg) sub.Add(reg);
            if (V("pais", f.Pais) is string pais) sub.Add(pais);
        }
        if (V("especie", f.Especie) is string esp) sub.Add(esp);

        // Datos en grilla de 3
        var datos = new List<(string, string)>();
        if (!blend)
        {
            if (V("variedad", f.Variedad) is string va) datos.Add(("Variedad", va));
            if (V("proceso", f.Proceso) is string pr) datos.Add(("Proceso", pr));
            if (V("altura", f.Altura) is string al) datos.Add(("Altura", al));
            if (V("finca", f.Finca) is string fi) datos.Add(("Finca / productor", fi));
        }
        if (V("tueste", f.Tueste) is string tu) datos.Add(("Tueste", tu));
        if (V("idealPara", f.IdealPara) is string id) datos.Add(("Ideal para", id));
        if (V("puntaje", f.Puntaje) is string pu) datos.Add(("Puntaje SCA", pu));

        var componentes = blend ? CafeFichasTecnicasController.LeerComponentes(f.ComponentesJson) : new();
        var rueda = noAplica.Contains("rueda") ? new List<string>()
            : (f.Rueda ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var renglones = new List<(string, string)>();
        if (V("fraganciaAroma", f.FraganciaAroma) is string fa) renglones.Add(("Fragancia y aroma", fa));
        if (V("sabor", f.Sabor) is string sa) renglones.Add(("Sabor", sa));
        if (V("retrogusto", f.Retrogusto) is string re) renglones.Add(("Retrogusto", re));
        var balance = noAplica.Contains("balance") ? new List<(string, decimal)>()
            : new[] { ("Cuerpo", f.Cuerpo), ("Acidez", f.Acidez), ("Dulzor", f.Dulzor) }
                .Where(b => b.Item2.HasValue).Select(b => (b.Item1, b.Item2!.Value)).ToList();

        var actualizada = f.UpdatedAt.AddHours(-3).ToString("dd/MM/yyyy", Es);

        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(0);
                page.DefaultTextStyle(t => t.FontSize(10).FontColor("#111111"));

                page.Header().Background("#111111").PaddingVertical(14).PaddingHorizontal(32).Row(r =>
                {
                    r.RelativeItem().Text("FRIKAF").FontColor(Amarillo).Bold().FontSize(15).LetterSpacing(0.15f);
                    r.RelativeItem().AlignRight().AlignMiddle().Text("FICHA TÉCNICA · CAFÉ DE ESPECIALIDAD").FontColor("#FFFFFF").FontSize(9);
                });

                page.Content().ScaleToFit().Column(col =>
                {
                    if (fotoCafe is not null || fotoEtiqueta is not null)
                    {
                        col.Item().Height(210).Row(r =>
                        {
                            if (fotoCafe is not null)
                                r.RelativeItem().Background("#2B1D14").AlignCenter().AlignMiddle().Image(fotoCafe).FitArea();
                            if (fotoEtiqueta is not null)
                                r.RelativeItem().Background("#F5EFE6").Padding(14).AlignCenter().AlignMiddle().Image(fotoEtiqueta).FitArea();
                        });
                    }

                    col.Item().PaddingHorizontal(32).PaddingTop(20).Column(body =>
                    {
                        body.Spacing(4);
                        body.Item().Row(r =>
                        {
                            r.RelativeItem().Column(c =>
                            {
                                c.Item().Text(f.Nombre).FontSize(24).Bold();
                                if (sub.Count > 0) c.Item().Text(string.Join(" · ", sub)).FontColor(Gris).FontSize(11);
                            });
                            if (V("perfil", f.Perfil) is string perfil)
                                r.AutoItem().AlignTop().Background(Amarillo).PaddingVertical(5).PaddingHorizontal(10)
                                    .Text(perfil.ToUpperInvariant()).Bold().FontSize(12).LetterSpacing(0.08f);
                        });

                        if (V("descripcion", f.Descripcion) is string desc)
                            body.Item().PaddingTop(8).Text(desc).FontSize(11).LineHeight(1.4f);

                        if (componentes.Count > 0)
                        {
                            Seccion(body, "Composición");
                            body.Item().Table(t =>
                            {
                                t.ColumnsDefinition(cd => { for (var i = 0; i < Math.Min(componentes.Count, 3); i++) cd.RelativeColumn(); });
                                foreach (var c in componentes)
                                {
                                    t.Cell().PaddingRight(10).PaddingBottom(8).BorderLeft(3).BorderColor("#6B4429").PaddingLeft(8).Column(cc =>
                                    {
                                        var tit = c.Pais ?? "—";
                                        if (c.Pct is decimal pct) tit += $" · {pct.ToString("0.#", Es)}%";
                                        cc.Item().Text(tit).Bold().FontSize(12);
                                        void L(string lbl, string? v) { if (!string.IsNullOrWhiteSpace(v)) cc.Item().Text(tx => { tx.Span(lbl + "  ").FontColor(Gris).FontSize(9); tx.Span(v); }); }
                                        L("Región", c.Region); L("Variedad", c.Variedad); L("Proceso", c.Proceso); L("Finca", c.Finca); L("Altura", c.Altura);
                                    });
                                }
                            });
                        }

                        if (datos.Count > 0)
                        {
                            Seccion(body, null);
                            body.Item().Table(t =>
                            {
                                t.ColumnsDefinition(cd => { cd.RelativeColumn(); cd.RelativeColumn(); cd.RelativeColumn(); });
                                foreach (var (lbl, val) in datos)
                                    t.Cell().PaddingBottom(8).PaddingRight(8).Column(c =>
                                    {
                                        c.Item().Text(lbl.ToUpperInvariant()).FontSize(8).FontColor(Gris).LetterSpacing(0.05f);
                                        c.Item().Text(val).Bold().FontSize(11);
                                    });
                            });
                        }

                        if (renglones.Count > 0)
                        {
                            Seccion(body, null);
                            foreach (var (lbl, val) in renglones)
                                body.Item().BorderBottom(0.5f).BorderColor("#E5E7EB").PaddingVertical(5).Row(r =>
                                {
                                    r.ConstantItem(130).Text(lbl.ToUpperInvariant()).FontSize(8).FontColor(Gris).LetterSpacing(0.05f);
                                    r.RelativeItem().Text(val).Bold().FontSize(11);
                                });
                        }

                        // Rueda y balance lado a lado (así la ficha entra en una hoja)
                        if (rueda.Count > 0 || balance.Count > 0)
                        {
                            Seccion(body, null);
                            body.Item().Row(r =>
                            {
                                if (rueda.Count > 0)
                                    r.RelativeItem().Column(c =>
                                    {
                                        c.Item().PaddingBottom(6).Text("RUEDA DE SABORES").FontSize(8).FontColor(Gris).LetterSpacing(0.06f);
                                        c.Item().Row(rr =>
                                        {
                                            rr.ConstantItem(115).Height(115).Svg(RuedaSvg(rueda));
                                            rr.RelativeItem().PaddingLeft(12).AlignMiddle().Column(lc =>
                                            {
                                                foreach (var x in Rueda.Where(x => rueda.Contains(x.Key)))
                                                    lc.Item().PaddingVertical(1).Row(lr =>
                                                    {
                                                        lr.ConstantItem(10).AlignMiddle().Height(8).Width(8).Background(x.Color);
                                                        lr.RelativeItem().Text(x.Label).Bold().FontSize(11);
                                                    });
                                            });
                                        });
                                    });
                                if (balance.Count > 0)
                                    r.ConstantItem(70 * balance.Count + 10).Column(c =>
                                    {
                                        c.Item().PaddingBottom(6).Text("BALANCE (1 A 5)").FontSize(8).FontColor(Gris).LetterSpacing(0.06f);
                                        c.Item().Width(70 * balance.Count).Height(100).Svg(BalanceSvg(balance));
                                    });
                            });
                        }
                    });
                });

                page.Footer().BorderTop(0.5f).BorderColor("#E5E7EB").PaddingVertical(8).PaddingHorizontal(32).Row(r =>
                {
                    r.RelativeItem().Text("frikaf.com.ar").FontSize(8).FontColor(Gris);
                    r.RelativeItem().AlignRight().Text($"Actualizada {actualizada}").FontSize(8).FontColor(Gris);
                });
            });
        }).GeneratePdf();
    }

    private static void Seccion(ColumnDescriptor body, string? titulo)
    {
        body.Item().PaddingTop(10).LineHorizontal(1.5f).LineColor("#111111");
        if (titulo is not null)
            body.Item().PaddingTop(4).PaddingBottom(4).Text(titulo.ToUpperInvariant()).FontSize(8).FontColor(Gris).LetterSpacing(0.06f);
        else
            body.Item().Height(4);
    }

    private static byte[]? Leer(string? archivo)
    {
        if (string.IsNullOrEmpty(archivo)) return null;
        try
        {
            var p = Path.Combine(CafeFichasTecnicasController.FotosDir, archivo);
            return File.Exists(p) ? File.ReadAllBytes(p) : null;
        }
        catch { return null; }
    }

    /// <summary>Rueda: las familias marcadas salen más grandes y a color; las otras, apagadas.</summary>
    public static string RuedaSvg(ICollection<string> marcadas)
    {
        var sb = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"-75 -75 150 150\">");
        var n = Rueda.Length;
        for (var i = 0; i < n; i++)
        {
            var (key, _, color) = Rueda[i];
            var on = marcadas.Contains(key);
            double a0 = i * 2 * Math.PI / n - Math.PI / 2, a1 = (i + 1) * 2 * Math.PI / n - Math.PI / 2;
            double R = on ? 72 : 60, r = 28;
            string P(double a, double rr) => $"{(Math.Cos(a) * rr).ToString("0.##", CultureInfo.InvariantCulture)},{(Math.Sin(a) * rr).ToString("0.##", CultureInfo.InvariantCulture)}";
            sb.Append($"<path d=\"M{P(a0, R)} A{R},{R} 0 0 1 {P(a1, R)} L{P(a1, r)} A{r},{r} 0 0 0 {P(a0, r)} Z\" fill=\"{color}\" fill-opacity=\"{(on ? "1" : "0.22")}\" stroke=\"#FFFFFF\" stroke-width=\"1.5\"/>");
        }
        return sb.Append("</svg>").ToString();
    }

    private static string BalanceSvg(List<(string Label, decimal Valor)> barras)
    {
        var w = 70 * barras.Count;
        var sb = new StringBuilder($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {w} 100\">");
        for (var i = 0; i < barras.Count; i++)
        {
            var (lbl, v) = barras[i];
            var h = (double)v / 5 * 72;
            var x = i * 70 + 10;
            sb.Append($"<rect x=\"{x}\" y=\"{(78 - h).ToString("0.#", CultureInfo.InvariantCulture)}\" width=\"50\" height=\"{h.ToString("0.#", CultureInfo.InvariantCulture)}\" fill=\"{(i == 1 ? Amarillo : "#111111")}\"/>");
            sb.Append($"<text x=\"{x + 25}\" y=\"94\" font-size=\"10\" font-family=\"Arial, sans-serif\" text-anchor=\"middle\" fill=\"{Gris}\">{lbl} {v.ToString("0.#", Es)}</text>");
        }
        return sb.Append("</svg>").ToString();
    }
}
