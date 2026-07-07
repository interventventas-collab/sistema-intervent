using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Api.Models;

[Table("Cafe_Productos")]
public class CafeProducto
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Required, MaxLength(200)]
    public string Nombre { get; set; } = string.Empty;

    [MaxLength(50)]
    public string? Sku { get; set; }

    [MaxLength(100)]
    public string? Barcode { get; set; }

    [Required, MaxLength(20)]
    public string Categoria { get; set; } = "CAFE"; // CAFE | OTROS

    [MaxLength(100)]
    public string? Marca { get; set; }

    /// <summary>FK a Cafe_Marcas. Reemplaza progresivamente el campo de texto Marca.</summary>
    public int? MarcaId { get; set; }

    [ForeignKey(nameof(MarcaId))]
    public CafeMarca? MarcaNav { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal Costo { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioPorKg { get; set; }

    /// <summary>PVP 1 — clientes BAR. Se guarda SIN IVA. El precio con IVA se calcula con IvaPct.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? Pvp1 { get; set; }

    /// <summary>PVP 2 — otros clientes. Se guarda SIN IVA. El precio con IVA se calcula con IvaPct.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? Pvp2 { get; set; }

    /// <summary>IVA % aplicable al producto. Default 21, opcional 10.5 para alimentos.</summary>
    [Column(TypeName = "decimal(5,2)")]
    public decimal IvaPct { get; set; } = 21m;

    /// <summary>Solo OTROS: % sobre costo para clientes BAR. NULL = BAR paga PVP (Pvp2).
    /// LEGACY: queda por compatibilidad con productos viejos, pero el modelo nuevo (PrecioBar/PrecioOtro)
    /// es el que se usa cuando están cargados.</summary>
    [Column(TypeName = "decimal(7,2)")]
    public decimal? BarPctSobreCosto { get; set; }

    /// <summary>SOLO productos categoría OTROS — modelo nuevo de precios directos.
    /// Precio sin IVA que paga un cliente tipo OTRO (consumidor final / venta por fuera).
    /// Si está cargado (no null), el motor de precios lo usa directo, ignorando la matriz
    /// Cafe_ReglasPrecios y la lógica de Pvp2. Si es null, cae al modelo legacy.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioOtro { get; set; }

    /// <summary>SOLO productos categoría OTROS — modelo nuevo de precios directos.
    /// Precio sin IVA que paga un cliente tipo BAR. Si está cargado, se usa directo.
    /// Si es null, cae al modelo legacy (PVP2 con matriz BAR -50% o costo×BarPct).</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioBar { get; set; }

    /// <summary>2026-06-10: Flag explicito "este producto NO tiene precio diferenciado para BAR".
    /// Cuando es true, el motor de precios IGNORA PrecioBar (y la matriz BAR legacy) y le cobra
    /// el PrecioOtro a TODOS los clientes (BAR y OTRO). Sirve para productos donde el OEM/proveedor
    /// fija un unico precio sin distincion de tipo de cliente — tipico en linea blanca/rodados.
    /// Default false (mantener comportamiento legacy: BAR usa PrecioBar, OTRO usa PrecioOtro).</summary>
    public bool SinPrecioBar { get; set; } = false;

    /// <summary>Unidades por bulto (informativo, solo OTROS).</summary>
    public int? UxB { get; set; }

    /// <summary>Precio del bulto completo para clientes BAR. Si el cliente lleva >= UxB,
    /// el sistema le cobra (bultosCompletos × PrecioBulto + sueltas × PrecioBar) en vez de
    /// cantidad × PrecioBar. Permite descuento por volumen sin duplicar SKUs. Solo OTROS.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioBulto { get; set; }

    /// <summary>Precio del bulto completo para clientes OTRO. Misma lógica que PrecioBulto pero
    /// para tipoCliente != BAR.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? PrecioBultoOtro { get; set; }

    /// <summary>2026-07-07: formato que sale PREDETERMINADO al cargar este producto en una venta.
    /// Valores: null/"UNIT" = Suelto (default historico), "PACK_{N}" = un pack prearmado, "BULTO",
    /// o para CAFE "1KG"/"MEDIO"/"CUARTO". Si apunta a un pack que ya no existe, el front vuelve a
    /// Suelto. Pedido de Osmar: ej. los vasos casi siempre se venden por pack de 100, no por unidad.</summary>
    [Column(TypeName = "nvarchar(20)")]
    public string? FormatoPorDefecto { get; set; }

    // ─── Precios FUTUROS (cambio programado) — pedido del usuario 2026-05-20 ───
    // Permiten cargar los nuevos precios HOY pero que el sistema los use recien a partir
    // de FechaAplicaPreciosFuturos. Util para entregar la lista nueva al cliente con
    // anticipacion sin afectar las ventas del periodo actual.
    [Column(TypeName = "date")]
    public DateTime? FechaAplicaPreciosFuturos { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioPorKgFuturo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioBarFuturo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioOtroFuturo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioBultoFuturo { get; set; }
    [Column(TypeName = "decimal(18,2)")] public decimal? PrecioBultoOtroFuturo { get; set; }

    /// <summary>True cuando hoy ya pasó la FechaAplicaPreciosFuturos y existe al menos un precio
    /// futuro cargado — el motor de precios usa los futuros en vez de los actuales.</summary>
    [NotMapped]
    public bool UsaPreciosFuturos => FechaAplicaPreciosFuturos.HasValue
        && DateTime.Today >= FechaAplicaPreciosFuturos.Value.Date
        && (PrecioPorKgFuturo.HasValue || PrecioBarFuturo.HasValue || PrecioOtroFuturo.HasValue
            || PrecioBultoFuturo.HasValue || PrecioBultoOtroFuturo.HasValue);

    /// <summary>Precio efectivo (futuro si ya aplica, sino el actual). Usado por el motor de precios.</summary>
    [NotMapped]
    public decimal? PrecioPorKgEfectivo => UsaPreciosFuturos && PrecioPorKgFuturo.HasValue ? PrecioPorKgFuturo : PrecioPorKg;
    [NotMapped]
    public decimal? PrecioBarEfectivo => UsaPreciosFuturos && PrecioBarFuturo.HasValue ? PrecioBarFuturo : PrecioBar;
    [NotMapped]
    public decimal? PrecioOtroEfectivo => UsaPreciosFuturos && PrecioOtroFuturo.HasValue ? PrecioOtroFuturo : PrecioOtro;
    [NotMapped]
    public decimal? PrecioBultoEfectivo => UsaPreciosFuturos && PrecioBultoFuturo.HasValue ? PrecioBultoFuturo : PrecioBulto;
    [NotMapped]
    public decimal? PrecioBultoOtroEfectivo => UsaPreciosFuturos && PrecioBultoOtroFuturo.HasValue ? PrecioBultoOtroFuturo : PrecioBultoOtro;

    /// <summary>FK opcional al OEM origen del proveedor. 1 OEM puede alimentar a N variantes.</summary>
    public int? OemId { get; set; }

    [ForeignKey(nameof(OemId))]
    public CafeOem? OemNav { get; set; }

    [Column(TypeName = "decimal(18,3)")]
    public decimal StockGramos { get; set; }

    public int StockUnidades { get; set; }

    /// <summary>Reserva interna específica para este producto al pushear stock a MeLi.
    /// Si tiene valor: MeLi recibe (stock - StockMinimoMeLi). Si es null: se usa el global
    /// AppSettings["meli.stock_push.reserva_interna"]. Si pongo 0 → MeLi ve el stock real (sin reserva).
    /// Si pongo 2 → MeLi ve (stock-2), o sea quedan 2 reservadas para mí.</summary>
    public int? StockMinimoMeLi { get; set; }

    [MaxLength(500)]
    public string? Notas { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Si false, el producto NO se muestra en el buscador de productos del modal Nueva Venta.
    /// Util para productos que solo existen como componentes de combos MeLi (no se venden sueltos).
    /// Se setea automaticamente por el clone de Contabilium para componentes-solo. Default true.</summary>
    public bool IsVisibleEnVentas { get; set; } = true;

    /// <summary>Origen del producto cuando fue importado/creado por un job automatico.
    /// Ej: "CONTABILIUM_CLONE_2026_05_22". Null = producto creado a mano.</summary>
    [MaxLength(80)]
    public string? ImportSource { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ─── Push event-driven sistema → MeLi (2026-05-22) ───
    // Se setean para que el job de respaldo (MeliStockPushBackgroundService) pueda
    // identificar productos cuyo stock cambio y todavia no se pushearon a MeLi.

    /// <summary>Ultima vez que se pusheo el stock de este producto a MeLi (exitosamente).
    /// Null = nunca se pusheo. Usado por el job de respaldo: si StockChangedAt > LastPushedToMeli
    /// (o LastPushedToMeli es null), hay que pushear.</summary>
    public DateTime? LastPushedToMeli { get; set; }

    /// <summary>Marca la ultima vez que se modifico el stock (StockGramos o StockUnidades).
    /// Lo setean los services que descuentan/devuelven stock (ventas, ordenes MeLi, ajustes manuales).
    /// El job de respaldo lo compara contra LastPushedToMeli para decidir si push.</summary>
    public DateTime? StockChangedAt { get; set; }

    /// <summary>2026-05-30 — Marca la ultima vez que se modifico el precio (PrecioOtro o IvaPct).
    /// Lo setea CafeProductosController al guardar cambios. El servicio MeliPriceAutoPushService
    /// (event-driven + background de respaldo) lo usa para detectar publicaciones MeLi "claimed"
    /// (SyncPrecio=true) que necesitan re-push.</summary>
    public DateTime? PriceChangedAt { get; set; }

    /// <summary>2026-05-30 — Multiplicador del OEM cuando el producto referencia un OEM.
    /// Lógica: si OemId != null y OemNav.PvpConIva != null, el precio del producto =
    /// OEM.PvpConIva × MultiplicadorOem (default 1). Si es null o 0, se asume 1.
    /// Ejemplo: OEM 9172 ($38.528) con multiplicador 1 → producto $38.528.
    /// Ejemplo: pack de 2 → multiplicador 2 → producto $77.056.
    /// Si el producto NO tiene OEM, se ignora este campo y se usa PrecioOtro como antes.</summary>
    [Column(TypeName = "decimal(10,4)")]
    public decimal? MultiplicadorOem { get; set; }

    /// <summary>Packs prearmados (formatos extra "Pack x N") que aparecen en el dropdown
    /// de Formato en el modal de venta. Solo aplica a categoria OTROS.</summary>
    public ICollection<CafeProductoPack> Packs { get; set; } = new List<CafeProductoPack>();
}
