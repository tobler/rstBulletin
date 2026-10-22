# rstBulletin – Build: docker build -t rstbulletin .
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
# Restore erst mit allen Quellen: Ob das Paket mit blazor.web.js gebraucht wird, erkennt das SDK an den .razor-Dateien
COPY src/ src/
RUN dotnet restore src/VbForum.Web/VbForum.Web.csproj -a $TARGETARCH
# Nur die Linux-Binaries der Zielarchitektur (spart ~350 MB SkiaSharp-Natives für Windows/macOS)
RUN dotnet publish src/VbForum.Web/VbForum.Web.csproj -c Release -a $TARGETARCH --self-contained false -o /app --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

# Laufzeitdaten (Cookie-Schlüssel, Logo, Bild-Cache, ggf. dbsettings.json) → als Volume mounten
ENV VbForum__DataPath=/data \
    ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true
RUN mkdir -p /data /app/wwwroot/images/smilies && chown -R $APP_UID /data /app/wwwroot/images/smilies
VOLUME /data

USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD ["dotnet", "VbForum.Web.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "VbForum.Web.dll"]
