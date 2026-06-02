FROM node:alpine


WORKDIR /usr/app

COPY ./package.json .

RUN npm install

COPY . .

EXPOSE 3000

CMD ["sh", "-c", "NODE_ENV=production node --require ./tracing.js ./bin/www"]


