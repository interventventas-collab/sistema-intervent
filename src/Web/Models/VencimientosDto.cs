namespace Web.Models;

// 2026-11-04: "Asignar vencimiento" (ver Api/Controllers/CafeVencimientosController.cs)
public class VencFilaDto
{
    public int Id { get; set; }
    public decimal Cantidad { get; set; }
    public DateTime FechaVence { get; set; }
    public decimal Quedan { get; set; }
}

public class VencProductoDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public string? Marca { get; set; }
    public string? Ubicacion { get; set; }
    public bool EsCafe { get; set; }
    public decimal Stock { get; set; }
    public List<VencFilaDto> Filas { get; set; } = new();
    public DateTime? ProximoVence { get; set; }
    public decimal ProximoQuedan { get; set; }
    public DateTime? SiguienteVence { get; set; }
    public decimal SinVencimiento { get; set; }
    public decimal Asignado { get; set; }
}

public class VencResumenDto
{
    public int ProductoId { get; set; }
    public DateTime? ProximoVence { get; set; }
    public decimal ProximoQuedan { get; set; }
    public DateTime? SiguienteVence { get; set; }
    public bool EsCafe { get; set; }
    public decimal SinVencimiento { get; set; }
    public List<VencFechaQuedanDto> Fechas { get; set; } = new();

    /// <summary>"3 vencen 15/01/2027 · 3 vencen 20/06/2027" (café en kg).</summary>
    public string Detalle() => string.Join(" · ", Fechas.Select(f => $"{Cant(f.Quedan)} {(f.Quedan == 1 && !EsCafe ? "vence" : "vencen")} {f.FechaVence:dd/MM/yyyy}"));

    /// <summary>De dónde salen N unidades (o kg): "3 del 15/01/2027 y 3 del 20/06/2027". Null si no hay fechas.</summary>
    public string? SalePara(decimal cantidad)
    {
        if (Fechas.Count == 0 || cantidad <= 0) return null;
        var partes = new List<string>();
        var resto = cantidad;
        foreach (var f in Fechas)
        {
            if (resto <= 0) break;
            var toma = Math.Min(resto, f.Quedan);
            partes.Add($"{Cant(toma)} del {f.FechaVence:dd/MM/yyyy}");
            resto -= toma;
        }
        var txt = partes.Count == 1 ? $"Sale el que vence {Fechas[0].FechaVence:dd/MM/yyyy}" : "Sale " + string.Join(" y ", partes);
        if (resto > 0) txt += $" (faltan {Cant(resto)} sin fecha)";
        return txt;
    }

    /// <summary>Unidades (o kg en café) que lleva un renglón de venta según su formato.
    /// PACK_100 / PACK_10E → 100 / 10 por cantidad; BULTO → UxB; café 1KG/MEDIO/CUARTO → 1 / 0,5 / 0,25 kg.</summary>
    public static decimal UnidadesDe(string? formato, decimal cantidad, int? uxb, bool esCafe)
    {
        var f = (formato ?? "").Trim().ToUpperInvariant();
        if (esCafe) return cantidad * (f switch { "MEDIO" => 0.5m, "CUARTO" => 0.25m, _ => 1m });
        if (f.StartsWith("PACK_"))
        {
            var dig = new string(f.Substring(5).TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(dig, out var n) && n > 0) return cantidad * n;
        }
        if (f == "BULTO" && uxb is > 0) return cantidad * uxb.Value;
        return cantidad;
    }

    /// <summary>Igual que SalePara pero para el armado: "Sacar el que vence…" / "Sacar 3 del … y 3 del …".</summary>
    public string? SacarPara(decimal cantidad)
    {
        var t = SalePara(cantidad);
        return t is null ? null : "Sacar " + t.Substring("Sale ".Length);
    }

    public string Cant(decimal v) => EsCafe ? $"{v:0.##} kg" : (v / 1.000000000000000000000000000000000m).ToString("0.##");
}

public class VencFechaQuedanDto
{
    public DateTime FechaVence { get; set; }
    public decimal Quedan { get; set; }
}

public class VencAlertaDto
{
    public int ProductoId { get; set; }
    public string? Sku { get; set; }
    public string Nombre { get; set; } = "";
    public bool EsCafe { get; set; }
    public DateTime FechaVence { get; set; }
    public decimal Quedan { get; set; }
    public int Dias { get; set; }
}
