-- ─────────────────────────────────────────────────────────────────────────────
-- 09/09/2026 — El costo en DOLARES guardaba solo 2 decimales y para productos
-- de centavos eso rompe la cuenta.
--
-- Medido en PROD: VT120 ($41), VT180 ($46) y VT240 ($52) quedaron los TRES en
-- US$ 0,03 — el mismo numero para tres productos distintos. Al volver a pesos,
-- 0,03 x 1530 = $45,90 contra los $41 reales del VT120: 12% de error solo por
-- el redondeo.
--
-- Peor: el aviso de "dolar desactualizado" salta al 15% de desvio, y el error
-- del redondeo en estos productos llega al 18%. O sea que el aviso podia
-- prenderse (o quedarse callado) por el redondeo, no por el precio.
--
-- 6 decimales: US$ 0,026797 x 1530 = $41,00 exacto.
-- La COTIZACION queda en 2 decimales: un dolar a 1530,00 no necesita mas.
--
-- ⚠ Correr A MANO en PROD. Ampliar la precision NO pierde datos y NO corta nada.
-- ─────────────────────────────────────────────────────────────────────────────
IF EXISTS (SELECT 1 FROM sys.columns c JOIN sys.types t ON t.user_type_id=c.user_type_id
           WHERE c.object_id=OBJECT_ID('Cafe_Productos') AND c.name='CostoUsd' AND c.scale < 6)
    ALTER TABLE Cafe_Productos ALTER COLUMN CostoUsd DECIMAL(18,6) NULL;
GO
