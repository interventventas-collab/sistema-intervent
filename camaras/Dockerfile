# Tunel de camaras: WireGuard (las PCs de cada local se conectan aca) + go2rtc (lee el video
# de las camaras a traves del tunel, solo cuando alguien lo mira). 2026-11-28.
FROM alpine:3.20
RUN apk add --no-cache wireguard-tools iproute2 curl ca-certificates \
 && curl -fsSL -o /usr/local/bin/go2rtc https://github.com/AlexxIT/go2rtc/releases/download/v1.9.15/go2rtc_linux_amd64 \
 && chmod +x /usr/local/bin/go2rtc
COPY arrancar.sh /arrancar.sh
RUN chmod +x /arrancar.sh
CMD ["/arrancar.sh"]
