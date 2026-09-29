#!/usr/bin/env python3
"""Genera caddy/static/frikaf/catalogo.json para la web frikaf.com.ar (tienda por marcas).

Lee la base de PRODUCCION (solo SELECT) via `docker exec aiml-sqlserver-prod sqlcmd`.
Caddy sirve la carpeta caddy/static/frikaf montada, asi que al regenerar el JSON la web
se actualiza sola, sin rebuild ni corte.

Reglas de precio (pedido del usuario 28/09/2026), SIEMPRE por 1 unidad, con IVA:
  - CAFE: lista MAYORISTA (tipo OTRO) del kg: PrecioOtro (o futuro vigente) ?? PrecioBar ?? Pvp1..., + IVA.
  - Resto: 10% menos que MercadoLibre por unidad y 10% menos que el OEM -> el menor de los dos.
    MeLi: solo publicaciones activas de 1 unidad (sin packs).
  - Sin referencia, o si la regla deja el precio por debajo del costo + IVA -> sin precio ("Consulta").

Uso: python3 scripts/generar_catalogo_frikaf.py
"""
import json, os, re, subprocess, sys, datetime

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "caddy", "static", "frikaf", "catalogo.json")

SQL = r"""
SET NOCOUNT ON;
WITH comp AS (
  SELECT c.MeliItemId, c.CafeProductoId pid, c.Cantidad, ISNULL(c.Formato,'') fmt FROM MeliItemComponentes c
  UNION
  SELECT i.MeliItemId, i.CafeProductoId, 1, ISNULL(i.CafeFormato,'') FROM MeliItems i WHERE i.CafeProductoId IS NOT NULL
    AND NOT EXISTS (SELECT 1 FROM MeliItemComponentes c WHERE c.MeliItemId=i.MeliItemId)
),
ml AS (
  SELECT comp.pid, SUM(o.Quantity*comp.Cantidad) u FROM MeliOrders o JOIN comp ON comp.MeliItemId=o.ItemId
  WHERE o.DateCreated>=DATEADD(day,-90,GETDATE()) AND o.Status='paid' GROUP BY comp.pid
),
sis AS (
  SELECT i.ProductoId pid, SUM(i.Cantidad) u FROM Cafe_VentaItems i JOIN Cafe_Ventas v ON v.Id=i.VentaId
  WHERE v.Fecha>=DATEADD(day,-90,GETDATE()) AND i.ProductoId IS NOT NULL GROUP BY i.ProductoId
),
pu AS (
  SELECT comp.pid, mi.Price unit, mi.Thumbnail,
   ROW_NUMBER() OVER (PARTITION BY comp.pid ORDER BY mi.SoldQuantity DESC, mi.Price) rn
  FROM comp JOIN MeliItems mi ON mi.MeliItemId=comp.MeliItemId
  WHERE mi.Status='active' AND comp.Cantidad=1 AND comp.fmt IN ('','UNIT','1KG')
    AND NOT EXISTS (SELECT 1 FROM MeliItemComponentes c2 WHERE c2.MeliItemId=comp.MeliItemId AND c2.CafeProductoId<>comp.pid)
),
th AS (
  SELECT comp.pid, mi.Thumbnail, ROW_NUMBER() OVER (PARTITION BY comp.pid ORDER BY mi.SoldQuantity DESC) rn
  FROM comp JOIN MeliItems mi ON mi.MeliItemId=comp.MeliItemId WHERE ISNULL(mi.Thumbnail,'')<>''
)
SELECT p.Id, p.Sku, ISNULL(m.Nombre,p.Marca), p.Nombre, p.Categoria, p.Costo, p.IvaPct,
  p.PrecioOtro, p.PrecioBar, p.Pvp1, p.Pvp2, p.PrecioPorKg,
  CASE WHEN p.FechaAplicaPreciosFuturos IS NOT NULL AND CAST(GETDATE() AS date)>=p.FechaAplicaPreciosFuturos THEN p.PrecioOtroFuturo END,
  CASE WHEN p.FechaAplicaPreciosFuturos IS NOT NULL AND CAST(GETDATE() AS date)>=p.FechaAplicaPreciosFuturos THEN p.PrecioBarFuturo END,
  o.PvpConIva * CASE WHEN ISNULL(p.MultiplicadorOem,0)<=0 THEN 1 ELSE p.MultiplicadorOem END,
  ISNULL(sis.u,0), ISNULL(ml.u,0), pu.unit, COALESCE(pu.Thumbnail, th.Thumbnail, o.ImagenUrl, '')
FROM Cafe_Productos p
LEFT JOIN Cafe_Marcas m ON m.Id=p.MarcaId
LEFT JOIN Cafe_Oems o ON o.Id=p.OemId AND o.PvpConIva>0
LEFT JOIN sis ON sis.pid=p.Id LEFT JOIN ml ON ml.pid=p.Id
LEFT JOIN pu ON pu.pid=p.Id AND pu.rn=1
LEFT JOIN th ON th.pid=p.Id AND th.rn=1
WHERE p.IsActive=1;
"""


def sa_password():
    for line in open(os.path.join(ROOT, ".env"), encoding="utf-8"):
        if line.startswith("SQL_SA_PASSWORD="):
            return line.split("=", 1)[1].strip().strip('"')
    sys.exit("No encuentro SQL_SA_PASSWORD en .env")


def query():
    cmd = ["docker", "exec", "-i", "aiml-sqlserver-prod", "/opt/mssql-tools18/bin/sqlcmd", "-C",
           "-S", "localhost", "-U", "sa", "-P", sa_password(), "-d", "AIml",
           "-W", "-h", "-1", "-s", "\t", "-i", "/dev/stdin"]
    r = subprocess.run(cmd, input=SQL, capture_output=True, text=True)
    if r.returncode != 0 or "Msg " in r.stdout:
        sys.exit("Fallo la consulta:\n" + r.stdout[-2000:] + r.stderr[-2000:])
    return [l.split("\t") for l in r.stdout.splitlines() if l.count("\t") == 18]


def num(x):
    try:
        return float(x)
    except (TypeError, ValueError):
        return None


def categoria(marca, nombre, cat):
    n = nombre.upper()
    if cat == "CAFE" and not re.search("VASO|TAPA|AZUCAR|EDULC", n):
        return "Café"
    if marca == "Mascardi" or re.search(r"\bSILLA|SILLON|SILLÓN|BANQUITO|\bMESA\b|REPOSERA", n):
        return "Muebles"
    if marca in ("Venturo", "Aliafor", "Patroll", "Pegasso") or re.search("DISCO|MECHA|BROCA|FLAP|COPA DIAMANT|PUNTAS ATORN", n):
        return "Herramientas"
    if marca == "Vertice" or re.search("ORINAL|CHATA", n):
        return "Salud e higiene"
    if re.search("VASO|TAPA .*(CC|ONZ|OZ)|TAPA VASO|POTE|COLLAR|PORTAVASO|SORBET|REMOS|AGITADOR|REVOLVED|SERVILLET|BAJALENGUA", n):
        return "Vasos y descartables"
    if re.search("AZUCAR|AZÚCAR|EDULCOR|STEVIA", n):
        return "Azúcar y edulcorante"
    if re.search(r"\bTE\b|\bTÉ\b|MATE COCIDO|INFUS|YERBA", n):
        return "Té e infusiones"
    if re.search("CHOCOLATE|GALLET|ALFAJOR|YOGUR|CREMA|LECHE|SNACK|BUDIN|MAGDAL", n):
        return "Alimentos y snacks"
    if re.search("CAFETERA|FILTRO|EMBUDO|JUNTA|POCILLO|MOLINILLO|PAVA|TAZA|DESCALCIF|GOMA BAJO", n):
        return "Cafeteras y accesorios"
    if marca == "Colombraro" or re.search("RECIP|CESTO|ORGANIZ|BALDE|CANASTO|FUENTE|BOWL|PERCHA", n):
        return "Hogar y bazar"
    return "Otros"


def redondear(v):
    return round(v / 10) * 10 if v < 2000 else round(v / 100) * 100


def lindo(nombre):
    nombre = re.sub(r"\s+", " ", nombre).strip()
    return nombre.title() if nombre.isupper() else nombre


def main():
    productos, sin_precio = [], 0
    for r in query():
        (pid, sku, marca, nombre, cat, costo, iva, p_otro, p_bar, pvp1, pvp2, pkg,
         p_otro_fut, p_bar_fut, oem, v_sis, v_ml, ml_unit, img) = r
        marca = (marca if marca and marca != "NULL" else "Sin marca").replace("®", "").strip()
        iva = num(iva) or 21.0
        f_iva = 1 + iva / 100
        costo_c_iva = (num(costo) or 0) * f_iva

        if cat == "CAFE":
            # Lista MAYORISTA = tipo OTRO, formato 1 kg (misma cadena que CafePricingService).
            base = next((x for x in (num(p_otro_fut) or num(p_otro), num(p_bar_fut) or num(p_bar),
                                      num(pvp1), num(pvp2), num(pkg)) if x), None)
            precio = base * f_iva if base else None
        else:
            refs = [x * 0.9 for x in (num(ml_unit), num(oem)) if x]
            precio = min(refs) if refs else None
            if precio and costo_c_iva > 0 and precio < costo_c_iva:
                precio = None  # la regla lo deja a perdida: mejor consultar
        ml = num(ml_unit)
        if precio:
            precio = redondear(precio)
            if not ml or ml <= precio:
                ml = None
        else:
            sin_precio += 1
            ml = None
        productos.append({
            "id": int(pid), "sku": sku if sku != "NULL" else "", "m": marca, "n": lindo(nombre),
            "c": categoria(marca, nombre, cat), "p": precio, "ml": round(ml) if ml else None,
            "v": int((num(v_sis) or 0) + (num(v_ml) or 0)), "img": img if img and img != "NULL" else "",
            "k": "1 kg" if cat == "CAFE" else "",
        })
    data = {"generado": datetime.datetime.now().strftime("%Y-%m-%d %H:%M"), "productos": productos}
    with open(OUT, "w", encoding="utf-8") as f:
        json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
    print(f"{len(productos)} productos ({sin_precio} sin precio) -> {OUT}")


if __name__ == "__main__":
    main()
