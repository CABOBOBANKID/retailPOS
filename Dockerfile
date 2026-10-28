FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AzumahCloud.Api.csproj ./
RUN dotnet restore
COPY . .
RUN mkdir -p wwwroot/icons \
 && for f in index.html app.js app.css service-worker.js lan.html lan.js lan.css Restock_Template.xlsx; do \
      if [ -f "$f" ] && [ ! -f "wwwroot/$f" ]; then cp "$f" "wwwroot/$f"; fi; done \
 && for f in icon-192.png icon-512.png icon.svg; do \
      if [ -f "$f" ] && [ ! -f "wwwroot/icons/$f" ]; then cp "$f" "wwwroot/icons/$f"; fi; done \
 && rm -f wwwroot/manifest.webmanifest \
 && test -f wwwroot/index.html && test -f wwwroot/app.js
RUN dotnet publish AzumahCloud.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
RUN test -f /app/wwwroot/index.html || (echo 'BUILD ERROR: wwwroot/index.html is missing from the repository' && ls -la /app && exit 1)
RUN useradd -r -u 10001 appuser && chown -R appuser /app
USER appuser
ENV ASPNETCORE_ENVIRONMENT=Production \
    LanAccess__Enabled=false \
    ForwardedHeaders__Enabled=true
CMD ["sh", "-c", "export Kestrel__Endpoints__Http__Url=http://0.0.0.0:${PORT:-8080}; exec dotnet AzumahCloud.Api.dll"]
