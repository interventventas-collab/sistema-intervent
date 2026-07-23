-- 2026-07-23: triggers XC/XP/XF en los pedidos por WhatsApp (pedido Osmar).
-- Agrega la columna TipoSolicitado a WhatsAppPedidosRecibidos:
--   PEDIDO (##/#nro, default) | COTIZACION (XC) | PRESUPUESTO (XP) | FACTURA (XF)
-- Idempotente: se puede correr las veces que haga falta.
--
-- Como correrlo (misma receta que patch_whatsapp_meta_canal.sql):
--   DEV : sudo docker compose exec -T sqlserver /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i /dev/stdin < db/patch_whatsapp_pedidos_tipo.sql
--   PROD: sudo docker compose -f docker-compose.prod.yml exec -T sqlserver-prod /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SQL_SA_PASSWORD" -C -d AIml -i /dev/stdin < db/patch_whatsapp_pedidos_tipo.sql

IF NOT EXISTS (
    SELECT 1 FROM sys.columns
    WHERE object_id = OBJECT_ID('dbo.WhatsAppPedidosRecibidos') AND name = 'TipoSolicitado'
)
BEGIN
    ALTER TABLE dbo.WhatsAppPedidosRecibidos
        ADD TipoSolicitado nvarchar(20) NOT NULL CONSTRAINT DF_WhatsAppPedidos_TipoSolicitado DEFAULT 'PEDIDO';
    PRINT 'Columna TipoSolicitado agregada.';
END
ELSE
    PRINT 'Columna TipoSolicitado ya existia. Nada que hacer.';
