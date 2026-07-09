FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# git: operaciones internas. zbar-tools (zbarimg) + poppler-utils (pdftoppm): lector de QR de respaldo.
# El renderizador propio (PDFium) no lee bien algunos QR densos; poppler + zbar los lee sin problema.
RUN apt-get update && apt-get install -y --no-install-recommends git zbar-tools poppler-utils && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 80
ENTRYPOINT ["dotnet", "Api.dll"]
