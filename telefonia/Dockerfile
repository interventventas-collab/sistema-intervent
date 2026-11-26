FROM alpine:3.20
RUN apk add --no-cache asterisk asterisk-srtp asterisk-curl asterisk-sounds-en asterisk-sounds-moh tzdata espeak-ng sox
COPY conf/ /etc/asterisk-plantilla/
COPY arrancar.sh /arrancar.sh
COPY aplicar.sh /aplicar.sh
RUN chmod +x /arrancar.sh /aplicar.sh
ENV TZ=America/Argentina/Buenos_Aires
CMD ["/arrancar.sh"]
