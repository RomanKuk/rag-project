FROM node:22-alpine AS build
WORKDIR /app
COPY package*.json ./
RUN npm install
COPY . .
RUN npm run build:prod
# RUN npm run build -- --configuration development

FROM nginx:alpine AS final
COPY --from=build /app/dist/document-qa-ui/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
