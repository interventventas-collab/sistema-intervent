#!/usr/bin/env python3
"""Arma src/Web/wwwroot/data/localidades-ar.json: listado OFICIAL de localidades
(Georef, datos.gob.ar) + los codigos postales de data/cps-ar.json (el oficial no trae CP).
CABA viene por barrio. Las localidades de cps-ar.json que no estan en Georef se suman igual.
Formato: [[nombre, provincia, partido, "cp1 cp2 ..."], ...]
Uso: python3 scripts/armar-localidades-ar.py [geo_all.json]   (sin argumento lo baja)"""
import json, sys, unicodedata, urllib.request, os

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DATA = os.path.join(RAIZ, 'src/Web/wwwroot/data')

def n(s):
    return unicodedata.normalize('NFD', s or '').encode('ascii', 'ignore').decode().lower().strip()

if len(sys.argv) > 1:
    geo = json.load(open(sys.argv[1]))
else:
    url = 'https://apis.datos.gob.ar/georef/api/localidades?max=5000&campos=nombre,provincia.nombre,departamento.nombre'
    geo = json.load(urllib.request.urlopen(url, timeout=60))
cps = json.load(open(os.path.join(DATA, 'cps-ar.json')))

provs = {n(v['p']): v['p'] for v in cps.values()}
def prov(p):
    k = n(p)
    if k in provs: return provs[k]
    for kk, vv in provs.items():
        if k.startswith(kk): return vv   # "Tierra del Fuego, Antartida..." -> "Tierra del Fuego"
    return p

cp_por = {}
for cp, v in cps.items():
    cp_por.setdefault((n(v['l']), n(v['p'])), []).append(cp)

filas, vistos = [], set()
for x in geo['localidades']:
    p = prov(x['provincia']['nombre'])
    partido = (x.get('departamento') or {}).get('nombre') or ''
    if p == 'Ciudad Autónoma de Buenos Aires': partido = ''
    clave = (n(x['nombre']), n(p))
    if clave in vistos: continue
    vistos.add(clave)
    filas.append([x['nombre'], p, partido, ' '.join(sorted(cp_por.get(clave, [])))])

for (ln, pn), cpl in cp_por.items():
    if (ln, pn) in vistos: continue
    v = cps[cpl[0]]
    if v['l'] == v['p']: continue        # "Ciudad Autonoma de Buenos Aires" generico
    vistos.add((ln, pn))
    filas.append([v['l'], v['p'], '', ' '.join(sorted(cpl))])

filas.sort(key=lambda f: (n(f[0]), n(f[1])))
json.dump(filas, open(os.path.join(DATA, 'localidades-ar.json'), 'w'), ensure_ascii=False, separators=(',', ':'))
print(len(filas), 'localidades;', sum(1 for f in filas if f[3]), 'con CP')
