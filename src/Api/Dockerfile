FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
# git: para operaciones internas. zbar-tools: lector de QR de respaldo (zbarimg) para las facturas
# cuyos QR ZXing no logra leer (son más densos); zbar los lee sin problema.
RUN apt-get update && apt-get install -y --no-install-recommends git zbar-tools && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 80
ENTRYPOINT ["dotnet", "Api.dll"]
