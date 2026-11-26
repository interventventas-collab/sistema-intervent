FROM alpine:3.20
RUN apk add --no-cache asterisk asterisk-srtp asterisk-sounds-en asterisk-sounds-moh tzdata
COPY conf/ /etc/asterisk-plantilla/
COPY arrancar.sh /arrancar.sh
RUN chmod +x /arrancar.sh
ENV TZ=America/Argentina/Buenos_Aires
CMD ["/arrancar.sh"]
