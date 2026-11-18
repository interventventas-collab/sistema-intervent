-- 18/11/2026 (3ª): categorías de Facturas recibidas en dos niveles ("Grupo › Detalle").
-- 1) Pasa lo que ya estaba elegido al esquema nuevo. 2) Precarga los proveedores que se pueden adivinar
--    SOLO si todavía no tienen categoría (no pisa lo que eligió el dueño en el esquema nuevo).
SET NOCOUNT ON;

UPDATE Compras_ProveedorConfig SET Categoria = N'Mercado Libre › Comisiones', UpdatedAt = SYSUTCDATETIME() WHERE Categoria = N'Comisiones MeLi/MP' AND Cuit <> '30715686054';
UPDATE Compras_ProveedorConfig SET Categoria = N'Mercado Libre › Envíos',     UpdatedAt = SYSUTCDATETIME() WHERE Categoria IN (N'Comisiones MeLi/MP', N'Envíos') AND Cuit = '30715686054';
UPDATE Compras_ProveedorConfig SET Categoria = N'Vehículos › Peajes',          UpdatedAt = SYSUTCDATETIME() WHERE Categoria = N'Peajes';
UPDATE Compras_ProveedorConfig SET Categoria = N'Local y oficina › Alarma',    UpdatedAt = SYSUTCDATETIME() WHERE Categoria = N'Servicios' AND Cuit = '33716307749';
UPDATE Compras_ProveedorConfig SET Categoria = N'Teléfono e internet',         UpdatedAt = SYSUTCDATETIME() WHERE Categoria IN (N'Telefonia', N'Telefonía');
UPDATE Compras_ProveedorConfig SET Categoria = N'Bancos e impuestos',          UpdatedAt = SYSUTCDATETIME() WHERE Categoria = N'Impuestos y bancos';
UPDATE Compras_ProveedorConfig SET Categoria = N'Vehículos › Reparaciones y repuestos', UpdatedAt = SYSUTCDATETIME() WHERE Categoria = N'Mantenimiento' AND Cuit IN ('30715009826','30688219570');
UPDATE Compras_ProveedorConfig SET Categoria = N'Local y oficina › Mantenimiento', UpdatedAt = SYSUTCDATETIME() WHERE Categoria = N'Mantenimiento';
UPDATE Compras_ProveedorConfig SET Categoria = N'Local y oficina › Equipos y herramientas', UpdatedAt = SYSUTCDATETIME() WHERE Categoria = N'Insumos';
-- "Seguros" a secas queda como Otros hasta que el dueño elija si es del auto o del local.

DECLARE @p TABLE (Cuit NVARCHAR(30), Cat NVARCHAR(60));
INSERT INTO @p VALUES
 ('30703088534', N'Mercado Libre › Comisiones'),       -- Mercadolibre SRL
 ('30715686054', N'Mercado Libre › Envíos'),           -- Meli Log
 ('30561625960', N'Mercadería'),                       -- Colombraro
 ('30714497436', N'Mercadería'),                       -- Global Food
 ('30504726173', N'Mercadería'),                       -- Estisol
 ('30704462782', N'Mercadería'),                       -- Nipper
 ('30649291507', N'Mercadería'),                       -- Polisol
 ('30627028101', N'Mercadería'),                       -- Aliafor
 ('30645830446', N'Mercadería'),                       -- Good Food
 ('30623608677', N'Vehículos › Combustible'),          -- Edenred (Shell Flota)
 ('30715458744', N'Vehículos › Combustible'),          -- Sur Carburantes
 ('30715009826', N'Vehículos › Reparaciones y repuestos'), -- Go Tire
 ('30688219570', N'Vehículos › Reparaciones y repuestos'), -- M R Motors
 ('30708832304', N'Vehículos › VTV y patentes'),       -- Inspección Técnica San Justo
 ('30574876474', N'Vehículos › Peajes'),               -- Autopistas Urbanas
 ('30719454190', N'Vehículos › Peajes'),               -- Rutas Sur Atlántico
 ('30714112836', N'Vehículos › Peajes'),               -- AUBASA
 ('30663498513', N'Vehículos › Peajes'),               -- Grupo Concesionario del Oeste
 ('30677237119', N'Vehículos › Peajes'),               -- Autopistas del Sol
 ('33716307749', N'Local y oficina › Alarma'),         -- Verisure
 ('30719494710', N'Local y oficina › Imprenta y librería'), -- Impresión Grafik
 ('30716304198', N'Local y oficina › Imprenta y librería'), -- RD Printer
 ('30577207190', N'Local y oficina › Limpieza'),       -- Coordinación Ecológica (CEAMSE)
 ('30526874249', N'Local y oficina › Equipos y herramientas'), -- Frávega
 ('30534051049', N'Local y oficina › Equipos y herramientas'), -- Electro Universo
 ('30714333182', N'Local y oficina › Equipos y herramientas'), -- Kromacolor
 ('30657715383', N'Local y oficina › Equipos y herramientas'), -- Maderera Llavallol
 ('30678814357', N'Teléfono e internet'),              -- Telefónica
 ('30500001735', N'Bancos e impuestos');               -- Banco Galicia

UPDATE c SET Categoria = p.Cat, UpdatedAt = SYSUTCDATETIME(), UpdatedBy = N'precarga 18/11'
FROM Compras_ProveedorConfig c JOIN @p p ON p.Cuit = c.Cuit
WHERE c.Categoria IS NULL OR c.Categoria NOT LIKE N'%›%' AND c.Categoria NOT IN (N'Mercadería', N'Teléfono e internet', N'Bancos e impuestos', N'Otros', N'Vehículos', N'Local y oficina', N'Mercado Libre');

INSERT INTO Compras_ProveedorConfig (Cuit, Categoria, UpdatedAt, UpdatedBy)
SELECT p.Cuit, p.Cat, SYSUTCDATETIME(), N'precarga 18/11' FROM @p p
WHERE NOT EXISTS (SELECT 1 FROM Compras_ProveedorConfig c WHERE c.Cuit = p.Cuit);

SELECT Categoria, COUNT(*) n FROM Compras_ProveedorConfig GROUP BY Categoria ORDER BY Categoria;
