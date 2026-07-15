-- Limpieza de movimientos duplicados del extracto bancario causados por el bug del
-- hash-con-saldo (el robot Galicia re-importaba el mismo movimiento con distinto saldo).
--
-- CRITERIO:
--   * Grupo = misma Fecha + Descripcion + Debitos + Creditos.
--   * Solo se tocan grupos que aparecen en MAS DE UN archivo de importacion
--     (senal de re-importacion; los repetidos dentro de un mismo archivo son reales).
--   * Copias REALES a conservar = MAX de veces que la clave aparecio en UN SOLO archivo
--     (una bajada del extracto lista cada movimiento real una vez).
--   * Se conservan primero las filas ASOCIADAS a venta/cobranza y luego las mas recientes.
--
-- SEGURIDAD: antes de borrar deja una copia de las filas eliminadas en una tabla de backup.
-- Re-ejecutable: recrea su propia tabla de backup en cada corrida.

SET NOCOUNT ON;

IF OBJECT_ID('dbo.Cafe_ExtractoMovimientos_BackupDupLimpieza') IS NOT NULL
    DROP TABLE dbo.Cafe_ExtractoMovimientos_BackupDupLimpieza;

;WITH dup AS (
    SELECT CAST(Fecha AS date) F, Descripcion, Debitos, Creditos
    FROM dbo.Cafe_ExtractoMovimientos
    GROUP BY CAST(Fecha AS date), Descripcion, Debitos, Creditos
    HAVING COUNT(*) > 1 AND COUNT(DISTINCT ArchivoOrigen) > 1
),
realcount AS (
    SELECT d.F, d.Descripcion, d.Debitos, d.Creditos,
        (SELECT MAX(v) FROM (
            SELECT COUNT(*) v
            FROM dbo.Cafe_ExtractoMovimientos m2
            WHERE CAST(m2.Fecha AS date) = d.F AND m2.Descripcion = d.Descripcion
              AND m2.Debitos = d.Debitos AND m2.Creditos = d.Creditos
            GROUP BY m2.ArchivoOrigen) t) rc
    FROM dup d
),
ranked AS (
    SELECT m.Id, rc.rc,
        ROW_NUMBER() OVER (
            PARTITION BY rc.F, rc.Descripcion, rc.Debitos, rc.Creditos
            ORDER BY CASE WHEN m.VentaIdAsociada IS NOT NULL OR m.CobranzaUsadaId IS NOT NULL THEN 0 ELSE 1 END,
                     m.ImportadoAt DESC, m.Id DESC) rn
    FROM dbo.Cafe_ExtractoMovimientos m
    JOIN realcount rc ON CAST(m.Fecha AS date) = rc.F AND m.Descripcion = rc.Descripcion
        AND m.Debitos = rc.Debitos AND m.Creditos = rc.Creditos
)
SELECT m.*
INTO dbo.Cafe_ExtractoMovimientos_BackupDupLimpieza
FROM dbo.Cafe_ExtractoMovimientos m
JOIN ranked r ON m.Id = r.Id
WHERE r.rn > r.rc;

DECLARE @n INT = @@ROWCOUNT;

;WITH dup AS (
    SELECT CAST(Fecha AS date) F, Descripcion, Debitos, Creditos
    FROM dbo.Cafe_ExtractoMovimientos
    GROUP BY CAST(Fecha AS date), Descripcion, Debitos, Creditos
    HAVING COUNT(*) > 1 AND COUNT(DISTINCT ArchivoOrigen) > 1
),
realcount AS (
    SELECT d.F, d.Descripcion, d.Debitos, d.Creditos,
        (SELECT MAX(v) FROM (
            SELECT COUNT(*) v
            FROM dbo.Cafe_ExtractoMovimientos m2
            WHERE CAST(m2.Fecha AS date) = d.F AND m2.Descripcion = d.Descripcion
              AND m2.Debitos = d.Debitos AND m2.Creditos = d.Creditos
            GROUP BY m2.ArchivoOrigen) t) rc
    FROM dup d
),
ranked AS (
    SELECT m.Id, rc.rc,
        ROW_NUMBER() OVER (
            PARTITION BY rc.F, rc.Descripcion, rc.Debitos, rc.Creditos
            ORDER BY CASE WHEN m.VentaIdAsociada IS NOT NULL OR m.CobranzaUsadaId IS NOT NULL THEN 0 ELSE 1 END,
                     m.ImportadoAt DESC, m.Id DESC) rn
    FROM dbo.Cafe_ExtractoMovimientos m
    JOIN realcount rc ON CAST(m.Fecha AS date) = rc.F AND m.Descripcion = rc.Descripcion
        AND m.Debitos = rc.Debitos AND m.Creditos = rc.Creditos
)
DELETE m
FROM dbo.Cafe_ExtractoMovimientos m
JOIN ranked r ON m.Id = r.Id
WHERE r.rn > r.rc;

PRINT 'Filas duplicadas eliminadas: ' + CAST(@n AS varchar) + ' (backup en Cafe_ExtractoMovimientos_BackupDupLimpieza)';
