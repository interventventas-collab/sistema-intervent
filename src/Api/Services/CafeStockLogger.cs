using Api.Data;
using Api.Models;
using Microsoft.EntityFrameworkCore;

namespace Api.Services;

/// <summary>
/// Loguea cada cambio de stock en CafeProducto contra la tabla Stock_Movimientos.
/// Se llama DESPUÉS de aplicar el cambio en CafeProducto.StockUnidades (con el stock antes/después ya conocidos).
/// Si falla el log, NO debe hacer rollback del cambio de stock — solo loguea el error y sigue.
/// La idea es que sea lo más no-invasivo posible: si el logger explota, el stock se mantiene correcto.
/// </summary>
public class CafeStockLogger
{
    private readonly AppDbContext _db;
    private readonly ILogger<CafeStockLogger> _log;

    public CafeStockLogger(AppDbContext db, ILogger<CafeStockLogger> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>
    /// Registra un movimiento de stock. NO modifica el stock — eso lo hizo el caller antes de llamar acá.
    /// </summary>
    /// <param name="productoId">Id de Cafe_Productos</param>
    /// <param name="tipoMov">VENTA_NUESTRA | VENTA_MELI | AJUSTE_ADMIN | SINCRO_CONTABILIUM | COMPRA_PROVEEDOR | CANCELACION | SUMA | RESTA | SET</param>
    /// <param name="stockAntes">Valor antes del cambio</param>
    /// <param name="stockDespues">Valor después del cambio</param>
    /// <param name="operadorId">FK opcional a Stock_Operadores</param>
    /// <param name="operadorNombre">Snapshot del nombre del operador (no se borra si después se desactiva)</param>
    /// <param name="comentario">Descripción libre — ej "Venta #1234 a Cliente X", "Orden MeLi 9876543210"</param>
    /// <param name="saveChanges">Si true, hace SaveChanges. Si false, solo agrega al contexto (caller hace el save).</param>
    public async Task LogAsync(int productoId, string tipoMov, int stockAntes, int stockDespues,
        int? operadorId = null, string? operadorNombre = null, string? comentario = null,
        bool saveChanges = true)
    {
        try
        {
            // El delta se calcula y la cantidad va siempre positiva (convención del modelo existente)
            var delta = stockDespues - stockAntes;
            var cantidad = Math.Abs(delta);

            var mov = new StockMovimiento
            {
                ProductoId = productoId,
                OperadorId = operadorId,
                OperadorNombreSnap = string.IsNullOrWhiteSpace(operadorNombre) ? null : operadorNombre.Trim(),
                TipoMov = tipoMov,
                Cantidad = cantidad,
                StockAntes = stockAntes,
                StockDespues = stockDespues,
                Comentario = string.IsNullOrWhiteSpace(comentario) ? null : comentario.Trim(),
                CreatedAt = DateTime.UtcNow
            };
            _db.StockMovimientos.Add(mov);
            if (saveChanges) await _db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // No relanzamos: el cambio de stock ya pasó, perder el log es mejor que romper la venta
            _log.LogError(ex, "Failed to log stock movement for product {ProductoId} ({TipoMov})", productoId, tipoMov);
        }
    }
}
