using System.Globalization;
using Api.Data;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace Api.Services;

/// <summary>
/// 17/09/2026 — El reporte de la cuenta de un repartidor, en PDF y en Excel.
///
/// Pedido de él: "necesito que se puedan descargar reportes por día, semana, mes o período
/// personalizado, tanto para él como para nosotros" y "mientras más info traiga, mejor".
/// El PDF es EL MISMO para los dos (lo confirmó): el repartidor ve también quién de la oficina
/// cargó cada cosa. La oficina además lo baja en Excel.
///
/// Dos decisiones que importan:
///
/// • **Arranca con lo que se le debía al empezar el período.** Un reporte de una semana suelta,
///   sin ese arrastre, no cerraría nunca con el "Te debemos" de la pantalla: el saldo viene
///   corriendo desde el principio de los tiempos. Por eso la cuenta se arma SIEMPRE completa y
///   recién al final se recorta a las fechas pedidas.
///
/// • **Las fechas son días de reparto (fecha argentina).** Viajes_Entregas.Fecha ya es el día que
///   salió a repartir, aunque MeLi confirme la entrega a la noche. Las horas (EntregadoAt,
///   CreatedAt) sí vienen en UTC y se pasan a hora argentina acá, no en la pantalla.
/// </summary>
public class ViajesCuentaReporteService
{
    private readonly AppDbContext _db;
    private static readonly CultureInfo Es = new("es-AR");

    static ViajesCuentaReporteService()
    {
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public ViajesCuentaReporteService(AppDbContext db) => _db = db;

    // ─────────────────────────────────────────────────────────────────────────────
    // Los datos del reporte
    // ─────────────────────────────────────────────────────────────────────────────

    public record ReporteEntrega(string Cliente, string? Direccion, DateTime? EntregadoAt,
        decimal Tarifa, string Origen, bool Liquidado);

    /// <summary>Tipo: entregas · extra · registro · pago.</summary>
    public record ReporteMov(DateTime Fecha, string Tipo, string Que, decimal Suma, decimal Pago,
        decimal Saldo, DateTime? Hora, string? CargadoPor, string? Medio,
        bool PideConfirmacion, bool? Confirmado, DateTime? ConfirmadoAt, bool Liquidado,
        DateTime? Desde, DateTime? Hasta, List<ReporteEntrega> Entregas);

    public record ReporteAviso(DateTime Fecha, string Texto, string? Respuesta, DateTime? RespuestaAt);

    public record Reporte(string Empleado, DateTime Desde, DateTime Hasta, string Periodo,
        decimal Tarifa, decimal SaldoInicial, decimal Ganado, decimal Pagado, decimal SaldoFinal,
        int Entregas, int DiasQueSalio, decimal PromedioEntregas, decimal PendienteDeCobro,
        List<ReporteMov> Movimientos, List<ReporteAviso> Avisos, string Negocio);

    /// <summary>Arma la cuenta entera y la recorta al período pedido.</summary>
    public async Task<Reporte?> ArmarAsync(int empleadoId, DateTime desde, DateTime hasta, string periodo)
    {
        var emp = await _db.ViajesEmpleados.FindAsync(empleadoId);
        if (emp is null) return null;

        desde = desde.Date;
        hasta = hasta.Date;

        var ents = await _db.ViajesEntregas.Where(x => x.EmpleadoId == empleadoId).ToListAsync();
        var pagos = await _db.ViajesPagos.Where(x => x.EmpleadoId == empleadoId).ToListAsync();
        var regs = await _db.ViajesRegistros.Where(x => x.EmpleadoId == empleadoId).ToListAsync();
        var tiposCaja = await _db.CafeCajas.ToDictionaryAsync(c => c.Id, c => c.Tipo);

        var filas = new List<ReporteMov>();

        // Un renglón por día de reparto, con todas las entregas adentro.
        foreach (var g in ents.Where(x => x.StopId != null).GroupBy(x => x.Fecha))
        {
            var detalle = g.OrderBy(x => x.EntregadoAt ?? DateTime.MaxValue).ThenBy(x => x.Id)
                .Select(x => new ReporteEntrega(
                    !string.IsNullOrWhiteSpace(x.Cliente) ? x.Cliente!
                        : (!string.IsNullOrWhiteSpace(x.Direccion) ? x.Direccion! : "entrega"),
                    x.Direccion, x.EntregadoAt, x.Tarifa, OrigenEnCriollo(x.Origen),
                    x.LiquidadoPagoId != null))
                .ToList();

            filas.Add(new ReporteMov(g.Key, "entregas",
                $"{g.Count()} entrega{(g.Count() == 1 ? "" : "s")}",
                g.Sum(x => x.Tarifa), 0m, 0m, null, null, null, false, null, null,
                g.All(x => x.LiquidadoPagoId != null),
                g.Min(x => x.EntregadoAt), g.Max(x => x.EntregadoAt), detalle));
        }

        // Lo cargado a mano, agrupado por su motivo.
        foreach (var g in ents.Where(x => x.StopId == null).GroupBy(x => new { x.Fecha, Det = x.Detalle ?? "Ajuste" }))
            filas.Add(new ReporteMov(g.Key.Fecha, "extra", g.Key.Det, g.Sum(x => x.Tarifa), 0m, 0m,
                g.Min(x => x.CreatedAt),
                g.Select(x => x.CargadoPor).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)),
                null, false, null, null, g.All(x => x.LiquidadoPagoId != null),
                null, null, new List<ReporteEntrega>()));

        // Los viajes que se tipeaban a mano (modo viejo).
        foreach (var r in regs)
            filas.Add(new ReporteMov(r.Fecha, "registro",
                $"{r.CantidadCABA + r.CantidadPCIA} viajes cargados a mano"
                    + (string.IsNullOrWhiteSpace(r.Anotaciones) ? "" : $" · {r.Anotaciones}"),
                (decimal)r.CantidadCABA * r.TarifaCABA + (decimal)r.CantidadPCIA * r.TarifaPCIA, 0m, 0m,
                r.UpdatedAt ?? r.CreatedAt, r.CargadoPor, null, false, null, null, false,
                null, null, new List<ReporteEntrega>()));

        foreach (var p in pagos)
            filas.Add(new ReporteMov(p.Fecha, "pago",
                "Pago" + (string.IsNullOrWhiteSpace(p.Descripcion) ? "" : " · " + p.Descripcion),
                0m, p.Importe, 0m, p.CreatedAt, p.CargadoPor,
                MedioEnCriollo(p.CajaId, tiposCaja, p.Descripcion),
                p.PideConfirmacion, p.Confirmado, p.ConfirmadoAt, false,
                null, null, new List<ReporteEntrega>()));

        // El saldo corre desde el principio: si no, el número no cerraría con el de la pantalla.
        var orden = filas.OrderBy(f => f.Fecha)
            .ThenBy(f => f.Tipo == "entregas" ? 0 : f.Tipo == "pago" ? 2 : 1).ToList();
        var conSaldo = new List<ReporteMov>(orden.Count);
        decimal acum = 0m;
        foreach (var f in orden)
        {
            acum += f.Suma - f.Pago;
            conSaldo.Add(f with { Saldo = acum });
        }

        // Lo que se le debía al empezar el período = el saldo del último movimiento anterior.
        var saldoInicial = conSaldo.Where(x => x.Fecha < desde).Select(x => x.Saldo).LastOrDefault();
        var delPeriodo = conSaldo.Where(x => x.Fecha >= desde && x.Fecha <= hasta).ToList();

        var ganado = delPeriodo.Sum(x => x.Suma);
        var pagado = delPeriodo.Sum(x => x.Pago);
        var entregas = delPeriodo.Where(x => x.Tipo == "entregas").Sum(x => x.Entregas.Count);
        var dias = delPeriodo.Where(x => x.Tipo == "entregas").Select(x => x.Fecha).Distinct().Count();
        var pendiente = delPeriodo.SelectMany(x => x.Entregas).Where(e => !e.Liquidado).Sum(e => e.Tarifa);

        var avisos = await _db.ViajesReportes
            .Where(a => a.EmpleadoId == empleadoId)
            .OrderBy(a => a.Id).ToListAsync();
        var avisosPeriodo = avisos
            .Where(a => ArDate(a.CreatedAt) >= desde && ArDate(a.CreatedAt) <= hasta)
            .Select(a => new ReporteAviso(a.CreatedAt, a.Texto, a.Respuesta, a.RespuestaAt))
            .ToList();

        var cfg = await _db.CafeSettings.FirstOrDefaultAsync();

        // Lo último arriba, igual que en las dos pantallas.
        delPeriodo.Reverse();

        return new Reporte(emp.Nombre, desde, hasta, periodo, emp.TarifaViaje,
            saldoInicial, ganado, pagado, saldoInicial + ganado - pagado,
            entregas, dias, dias == 0 ? 0 : Math.Round((decimal)entregas / dias, 1),
            pendiente, delPeriodo, avisosPeriodo,
            cfg?.NegocioNombre ?? "Frikaf");
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // PDF — el mismo para el repartidor y para la oficina
    // ─────────────────────────────────────────────────────────────────────────────

    public byte[] Pdf(Reporte r)
    {
        return Document.Create(doc =>
        {
            doc.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(22);
                page.DefaultTextStyle(t => t.FontSize(9));

                page.Header().Column(col =>
                {
                    col.Item().Row(row =>
                    {
                        row.RelativeItem().Column(c =>
                        {
                            c.Item().Text($"Cuenta de {r.Empleado}").FontSize(15).SemiBold();
                            c.Item().Text(r.Periodo).FontSize(10);
                            c.Item().Text($"Del {r.Desde:dd/MM/yyyy} al {r.Hasta:dd/MM/yyyy}").FontSize(9).FontColor("#6b7280");
                        });
                        row.ConstantItem(170).Column(c =>
                        {
                            c.Item().AlignRight().Text(r.Negocio).SemiBold().FontSize(11);
                            c.Item().AlignRight().Text($"Emitido {AhoraAr():dd/MM/yyyy HH:mm}")
                                .FontSize(8).FontColor("#6b7280");
                            c.Item().AlignRight().Text($"${Plata(r.Tarifa)} por entrega")
                                .FontSize(8).FontColor("#6b7280");
                        });
                    });
                    col.Item().PaddingTop(8).BorderBottom(0.8f).BorderColor("#d1d5db");
                });

                page.Content().PaddingTop(10).Column(col =>
                {
                    // ── Resumen: de dónde sale el número final ──
                    col.Item().Background("#f3f4f6").Padding(10).Column(c =>
                    {
                        Renglon(c, "Venía debiéndosele del período anterior", Firmado(r.SaldoInicial), false);
                        Renglon(c, $"Ganó en el período ({r.Entregas} entrega{(r.Entregas == 1 ? "" : "s")})",
                            $"$ {Plata(r.Ganado)}", false);
                        Renglon(c, "Se le pagó en el período", $"− $ {Plata(r.Pagado)}", false);
                        c.Item().PaddingTop(4).BorderTop(0.8f).BorderColor("#9ca3af").PaddingTop(4);
                        Renglon(c, r.SaldoFinal >= 0 ? "SE LE DEBE" : "COBRÓ DE MÁS",
                            $"$ {Plata(Math.Abs(r.SaldoFinal))}", true);
                    });

                    col.Item().PaddingTop(6).Text(t =>
                    {
                        t.DefaultTextStyle(s => s.FontSize(8.5f).FontColor("#4b5563"));
                        t.Span($"Salió {r.DiasQueSalio} día{(r.DiasQueSalio == 1 ? "" : "s")}");
                        t.Span($"  ·  promedio {r.PromedioEntregas.ToString("0.#", Es)} entregas por día");
                        if (r.PendienteDeCobro > 0)
                            t.Span($"  ·  de este período quedan sin liquidar $ {Plata(r.PendienteDeCobro)}");
                    });

                    if (r.Movimientos.Count == 0)
                    {
                        col.Item().PaddingTop(20).AlignCenter()
                            .Text("En este período no hubo movimientos.").FontColor("#6b7280");
                    }

                    // ── Movimiento por movimiento, con las entregas adentro ──
                    foreach (var m in r.Movimientos)
                    {
                        col.Item().PaddingTop(10).BorderBottom(0.5f).BorderColor("#e5e7eb").PaddingBottom(3).Row(row =>
                        {
                            row.ConstantItem(62).Text($"{m.Fecha.ToString("ddd dd/MM", Es)}").SemiBold().FontSize(9);
                            row.RelativeItem().Text(m.Que).SemiBold().FontSize(9.5f);
                            row.ConstantItem(84).AlignRight().PaddingRight(10)
                                .Text(m.EsPagoMov() ? $"− $ {Plata(m.Pago)}" : $"$ {Plata(m.Suma)}").SemiBold();
                            row.ConstantItem(104).AlignRight()
                                .Text((m.Saldo >= 0 ? "quedó debiendo $ " : "cobró de más $ ") + Plata(Math.Abs(m.Saldo)))
                                .FontSize(8).FontColor("#6b7280");
                        });

                        // La letra chica del renglón: cuándo, quién, cómo se pagó, si lo confirmó.
                        var aclara = new List<string>();
                        if (m.Hora is { } h) aclara.Add($"cargado {ArTime(h):dd/MM HH:mm}");
                        if (!string.IsNullOrWhiteSpace(m.CargadoPor)) aclara.Add($"lo cargó {m.CargadoPor}");
                        if (!string.IsNullOrWhiteSpace(m.Medio)) aclara.Add(m.Medio!);
                        if (m.Desde is { } d1 && m.Hasta is { } d2)
                            aclara.Add($"entregadas {ArTime(d1):HH:mm} → {ArTime(d2):HH:mm}");
                        if (m.Tipo == "entregas")
                            aclara.Add(m.Liquidado ? "ya liquidadas" : "todavía sin liquidar");
                        if (m.PideConfirmacion)
                            aclara.Add(m.Confirmado switch
                            {
                                true => $"él confirmó que la recibió{(m.ConfirmadoAt is { } c1 ? $" el {ArTime(c1):dd/MM HH:mm}" : "")}",
                                false => $"él dice que NO le llegó{(m.ConfirmadoAt is { } c2 ? $" el {ArTime(c2):dd/MM HH:mm}" : "")}",
                                _ => "sin responder todavía"
                            });
                        if (aclara.Count > 0)
                            col.Item().PaddingTop(2).PaddingLeft(62)
                                .Text(string.Join("  ·  ", aclara)).FontSize(8).FontColor("#6b7280");

                        if (m.Entregas.Count == 0) continue;

                        col.Item().PaddingTop(4).PaddingLeft(62).Table(tab =>
                        {
                            tab.ColumnsDefinition(c =>
                            {
                                c.ConstantColumn(18);    // número
                                c.RelativeColumn(2.2f);  // cliente
                                c.RelativeColumn(3f);    // dirección
                                c.ConstantColumn(42);    // hora
                                c.ConstantColumn(52);    // de dónde salió
                                c.ConstantColumn(58);    // importe
                            });

                            tab.Header(hd =>
                            {
                                void Th(string txt, bool der = false)
                                {
                                    var cell = hd.Cell().BorderBottom(0.5f).BorderColor("#d1d5db").PaddingBottom(2);
                                    (der ? cell.AlignRight() : cell)
                                        .Text(txt).FontSize(7.5f).SemiBold().FontColor("#6b7280");
                                }
                                Th(""); Th("Cliente"); Th("Dirección"); Th("Hora"); Th("Origen"); Th("Le sumó", true);
                            });

                            var n = 0;
                            foreach (var e in m.Entregas)
                            {
                                n++;
                                tab.Cell().PaddingVertical(1.5f).Text($"{n}.").FontSize(8).FontColor("#9ca3af");
                                tab.Cell().PaddingVertical(1.5f).PaddingRight(4).Text(e.Cliente).FontSize(8.5f);
                                tab.Cell().PaddingVertical(1.5f).PaddingRight(4)
                                    .Text(e.Direccion ?? "").FontSize(8).FontColor("#4b5563");
                                tab.Cell().PaddingVertical(1.5f)
                                    .Text(e.EntregadoAt is { } ea ? ArTime(ea).ToString("HH:mm") : "")
                                    .FontSize(8).FontColor("#4b5563");
                                tab.Cell().PaddingVertical(1.5f).Text(e.Origen).FontSize(7.5f).FontColor("#6b7280");
                                tab.Cell().PaddingVertical(1.5f).AlignRight()
                                    .Text($"$ {Plata(e.Tarifa)}").FontSize(8.5f);
                            }
                        });
                    }

                    // ── Lo que avisó el repartidor en el período ──
                    if (r.Avisos.Count > 0)
                    {
                        col.Item().PaddingTop(16).Text("Avisos que mandó").SemiBold().FontSize(11);
                        foreach (var a in r.Avisos)
                        {
                            col.Item().PaddingTop(5).BorderLeft(2).BorderColor("#d1d5db").PaddingLeft(6).Column(c =>
                            {
                                c.Item().Text($"{ArTime(a.Fecha):dd/MM HH:mm} · {a.Texto}").FontSize(8.5f);
                                if (!string.IsNullOrWhiteSpace(a.Respuesta))
                                    c.Item().Text($"Se le contestó{(a.RespuestaAt is { } ra ? $" el {ArTime(ra):dd/MM HH:mm}" : "")}: {a.Respuesta}")
                                        .FontSize(8).FontColor("#4b5563");
                                else
                                    c.Item().Text("Sin contestar").FontSize(8).FontColor("#b91c1c");
                            });
                        }
                    }
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(s => s.FontSize(7.5f).FontColor("#9ca3af"));
                    t.Span("Página ");
                    t.CurrentPageNumber();
                    t.Span(" de ");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // Excel — sólo para la oficina, para poder hacer cuentas
    // ─────────────────────────────────────────────────────────────────────────────

    public byte[] Excel(Reporte r)
    {
        using var wb = new XLWorkbook();

        // ── Resumen ──
        var hr = wb.Worksheets.Add("Resumen");
        hr.Cell("A1").Value = $"Cuenta de {r.Empleado}";
        hr.Cell("A1").Style.Font.SetBold().Font.SetFontSize(14);
        hr.Cell("A2").Value = $"{r.Periodo} · del {r.Desde:dd/MM/yyyy} al {r.Hasta:dd/MM/yyyy}";
        hr.Cell("A3").Value = $"Emitido {AhoraAr():dd/MM/yyyy HH:mm}";

        var fila = 5;
        void Res(string t, object v)
        {
            hr.Cell(fila, 1).Value = t;
            hr.Cell(fila, 2).Value = XLCellValue.FromObject(v);
            if (v is decimal) hr.Cell(fila, 2).Style.NumberFormat.Format = "#,##0";
            fila++;
        }
        Res("Venía debiéndosele", r.SaldoInicial);
        Res("Ganó en el período", r.Ganado);
        Res("Se le pagó en el período", r.Pagado);
        Res(r.SaldoFinal >= 0 ? "Se le debe" : "Cobró de más", Math.Abs(r.SaldoFinal));
        hr.Cell(fila - 1, 1).Style.Font.SetBold();
        hr.Cell(fila - 1, 2).Style.Font.SetBold();
        fila++;
        Res("Entregas", r.Entregas);
        Res("Días que salió", r.DiasQueSalio);
        Res("Promedio de entregas por día", r.PromedioEntregas);
        Res("Tarifa por entrega", r.Tarifa);
        Res("De este período, sin liquidar", r.PendienteDeCobro);
        hr.Columns(1, 2).AdjustToContents();

        // ── Movimientos ──
        var hm = wb.Worksheets.Add("Movimientos");
        Encabezados(hm, "Fecha", "Qué pasó", "Tipo", "Le sumó", "Se le pagó", "Quedó debiendo",
            "Cargado", "Lo cargó", "Cómo se pagó", "Confirmación", "Liquidado");
        var f = 2;
        foreach (var m in Enumerable.Reverse(r.Movimientos))   // acá va en orden, de viejo a nuevo
        {
            hm.Cell(f, 1).Value = m.Fecha;
            hm.Cell(f, 1).Style.DateFormat.Format = "dd/MM/yyyy";
            hm.Cell(f, 2).Value = m.Que;
            hm.Cell(f, 3).Value = m.Tipo switch
            {
                "entregas" => "entregas del mapa",
                "extra" => "cargado a mano",
                "registro" => "viajes a mano",
                _ => "pago"
            };
            if (m.Suma != 0) hm.Cell(f, 4).Value = m.Suma;
            if (m.Pago != 0) hm.Cell(f, 5).Value = m.Pago;
            hm.Cell(f, 6).Value = m.Saldo;
            if (m.Hora is { } h) { hm.Cell(f, 7).Value = ArTime(h); hm.Cell(f, 7).Style.DateFormat.Format = "dd/MM/yyyy HH:mm"; }
            hm.Cell(f, 8).Value = m.CargadoPor ?? "";
            hm.Cell(f, 9).Value = m.Medio ?? "";
            hm.Cell(f, 10).Value = !m.PideConfirmacion ? "" : m.Confirmado switch
            {
                true => "dijo que la recibió",
                false => "dice que NO le llegó",
                _ => "sin responder"
            };
            hm.Cell(f, 11).Value = m.Tipo == "entregas" ? (m.Liquidado ? "sí" : "no") : "";
            f++;
        }
        hm.Range(2, 4, Math.Max(2, f - 1), 6).Style.NumberFormat.Format = "#,##0";
        hm.Columns(1, 11).AdjustToContents();

        // ── Entregas, una por renglón ──
        var he = wb.Worksheets.Add("Entregas");
        Encabezados(he, "Fecha", "N°", "Cliente", "Dirección", "Hora", "De dónde salió", "Le sumó", "Liquidada");
        f = 2;
        foreach (var m in Enumerable.Reverse(r.Movimientos).Where(x => x.Entregas.Count > 0))
        {
            var n = 0;
            foreach (var e in m.Entregas)
            {
                n++;
                he.Cell(f, 1).Value = m.Fecha;
                he.Cell(f, 1).Style.DateFormat.Format = "dd/MM/yyyy";
                he.Cell(f, 2).Value = n;
                he.Cell(f, 3).Value = e.Cliente;
                he.Cell(f, 4).Value = e.Direccion ?? "";
                if (e.EntregadoAt is { } ea) { he.Cell(f, 5).Value = ArTime(ea); he.Cell(f, 5).Style.DateFormat.Format = "HH:mm"; }
                he.Cell(f, 6).Value = e.Origen;
                he.Cell(f, 7).Value = e.Tarifa;
                he.Cell(f, 8).Value = e.Liquidado ? "sí" : "no";
                f++;
            }
        }
        he.Range(2, 7, Math.Max(2, f - 1), 7).Style.NumberFormat.Format = "#,##0";
        he.Columns(1, 8).AdjustToContents();

        // ── Avisos ──
        var ha = wb.Worksheets.Add("Avisos");
        Encabezados(ha, "Cuándo", "Qué avisó", "Qué se le contestó", "Cuándo se le contestó");
        f = 2;
        foreach (var a in r.Avisos)
        {
            ha.Cell(f, 1).Value = ArTime(a.Fecha);
            ha.Cell(f, 1).Style.DateFormat.Format = "dd/MM/yyyy HH:mm";
            ha.Cell(f, 2).Value = a.Texto;
            ha.Cell(f, 3).Value = a.Respuesta ?? "";
            if (a.RespuestaAt is { } ra) { ha.Cell(f, 4).Value = ArTime(ra); ha.Cell(f, 4).Style.DateFormat.Format = "dd/MM/yyyy HH:mm"; }
            f++;
        }
        ha.Columns(1, 4).AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    /// <summary>
    /// Traduce el botón que se apretó a fechas de verdad, en día argentino. Las semanas arrancan
    /// el lunes. "rango" es el "elegir fechas" y usa las que vinieron.
    /// </summary>
    public static (DateTime desde, DateTime hasta, string titulo) Periodo(string? codigo, DateTime? d, DateTime? h)
    {
        var hoy = AhoraAr().Date;
        var lunes = hoy.AddDays(-(((int)hoy.DayOfWeek + 6) % 7));
        var primero = new DateTime(hoy.Year, hoy.Month, 1);

        switch ((codigo ?? "").ToLowerInvariant())
        {
            case "hoy": return (hoy, hoy, "Hoy");
            case "ayer": return (hoy.AddDays(-1), hoy.AddDays(-1), "Ayer");
            case "semana": return (lunes, hoy, "Esta semana");
            case "semana-pasada": return (lunes.AddDays(-7), lunes.AddDays(-1), "Semana pasada");
            case "mes": return (primero, hoy, "Este mes");
            case "mes-pasado":
                var pm = primero.AddMonths(-1);
                return (pm, primero.AddDays(-1), Mayus(pm.ToString("MMMM yyyy", Es)));
            default:
                var desde = (d ?? hoy).Date;
                var hasta = (h ?? hoy).Date;
                if (hasta < desde) (desde, hasta) = (hasta, desde);
                return (desde, hasta, "Período elegido");
        }
    }

    private static string Mayus(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];

    /// <summary>El nombre del archivo, para que en la carpeta se entienda de quién y de cuándo es.</summary>
    public static string NombreArchivo(Reporte r, string ext)
    {
        var quien = new string(r.Empleado.Where(c => char.IsLetterOrDigit(c) || c == ' ').ToArray())
            .Trim().Replace(' ', '-');
        return $"cuenta-{quien}-{r.Desde:yyyyMMdd}-a-{r.Hasta:yyyyMMdd}.{ext}";
    }

    // ─────────────────────────────────────────────────────────────────────────────

    private static void Encabezados(IXLWorksheet hoja, params string[] titulos)
    {
        for (var i = 0; i < titulos.Length; i++)
        {
            var c = hoja.Cell(1, i + 1);
            c.Value = titulos[i];
            c.Style.Font.SetBold();
            c.Style.Fill.BackgroundColor = XLColor.FromHtml("#e5e7eb");
        }
        hoja.SheetView.FreezeRows(1);
    }

    private static void Renglon(ColumnDescriptor c, string texto, string plata, bool fuerte)
    {
        c.Item().PaddingVertical(1).Row(row =>
        {
            var t = row.RelativeItem().Text(texto);
            var p = row.ConstantItem(120).AlignRight().Text(plata);
            if (fuerte) { t.SemiBold().FontSize(11); p.SemiBold().FontSize(13); }
        });
    }

    private static string Firmado(decimal v) => v >= 0 ? $"$ {Plata(v)}" : $"− $ {Plata(Math.Abs(v))}";

    private static string Plata(decimal v) => v.ToString("N0", Es);

    /// <summary>Todo el sistema guarda las horas en UTC; en pantalla y en papel van en hora argentina.</summary>
    private static DateTime ArTime(DateTime utc) => utc.AddHours(-3);
    private static DateTime ArDate(DateTime utc) => utc.AddHours(-3).Date;
    private static DateTime AhoraAr() => DateTime.UtcNow.AddHours(-3);

    private static string OrigenEnCriollo(string? origen) => (origen ?? "").ToLowerInvariant() switch
    {
        "flex" => "Flex",
        "me1" => "MercadoLibre",
        "venta_cafe" => "venta",
        "alquiler" => "alquiler",
        "visita" => "visita",
        _ => "a mano"
    };

    /// <summary>"en efectivo", "por transferencia"… sale del tipo de caja de la que salió la plata.</summary>
    private static string MedioEnCriollo(int? cajaId, Dictionary<int, string> tipos, string? descripcion)
    {
        if (cajaId.HasValue && tipos.TryGetValue(cajaId.Value, out var tipo))
            return tipo switch
            {
                "EFECTIVO" => "en efectivo",
                "BANCO" => "por transferencia",
                "BILLETERA_VIRTUAL" => "por billetera virtual",
                "CHEQUES_CARTERA" => "con cheque",
                "V_PRIVADO" => "redirigido",
                _ => ""
            };
        if (!string.IsNullOrWhiteSpace(descripcion) &&
            descripcion.StartsWith("Cobranza redirigida", StringComparison.OrdinalIgnoreCase))
            return "cobranza que se quedó él";
        return "";
    }
}

internal static class ReporteMovExt
{
    public static bool EsPagoMov(this ViajesCuentaReporteService.ReporteMov m) => m.Tipo == "pago";
}
