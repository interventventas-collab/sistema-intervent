-- ─────────────────────────────────────────────────────────────────────────────
-- 09/09/2026 — COBRO REDIRIGIDO A UN PROVEEDOR.
-- Hasta hoy el redirigido solo podia terminar en un EMPLEADO (o en "la privada").
-- Osmar: "quiero poder pasarle plata a un proveedor".
--
--   Cafe_Proveedores.AceptaRedirigido: el tilde de la ficha. Sin esto habria que
--   elegir entre los 560 proveedores activos en cada cobranza. Ademas es un control:
--   la plata solo puede terminar en uno que el habilito a mano.
--
--   Cafe_CobranzasMedios.RedirigidoProveedorId: a que proveedor se le paso.
--   Cafe_CobranzasMedios.RedirigidoCompraId: contra que factura. NULL = "a cuenta"
--   (el caso normal HOY, porque todavia no carga las compras).
--
-- RedirigidoPagoId se reusa para el id del Cafe_PagosProveedor; se distingue por
-- RedirigidoDestino = 'proveedor'.
--
-- ⚠ Correr A MANO en PROD antes de publicar. Es idempotente y NO corta nada.
-- ─────────────────────────────────────────────────────────────────────────────
IF COL_LENGTH('Cafe_Proveedores','AceptaRedirigido') IS NULL
    ALTER TABLE Cafe_Proveedores ADD AceptaRedirigido BIT NOT NULL DEFAULT 0;
GO
IF COL_LENGTH('Cafe_CobranzasMedios','RedirigidoProveedorId') IS NULL
    ALTER TABLE Cafe_CobranzasMedios ADD RedirigidoProveedorId INT NULL;
GO
IF COL_LENGTH('Cafe_CobranzasMedios','RedirigidoCompraId') IS NULL
    ALTER TABLE Cafe_CobranzasMedios ADD RedirigidoCompraId INT NULL;
GO
