FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY AzumahCloud.Api.csproj ./
RUN dotnet restore
COPY . .
RUN dotnet publish AzumahCloud.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
RUN useradd -r -u 10001 appuser && chown -R appuser /app
USER appuser
ENV ASPNETCORE_ENVIRONMENT=Production \
    LanAccess__Enabled=false \
    ForwardedHeaders__Enabled=true
CMD ["sh", "-c", "export Kestrel__Endpoints__Http__Url=http://0.0.0.0:${PORT:-8080}; exec dotnet AzumahCloud.Api.dll"]
