FROM mcr.microsoft.com/playwright:v1.42.0-jammy

WORKDIR /app

COPY package.json ./
RUN npm install --omit=dev

COPY index.js ./

RUN mkdir -p /data/whatsapp-session

ENV NODE_ENV=production
ENV PORT=3001

EXPOSE 3001

CMD ["node", "index.js"]
