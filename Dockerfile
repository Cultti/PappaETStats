FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src

# Restore first so the layer is cached when only sources change.
COPY PappaETStats.sln ./
COPY server/PappaETStats.Server/PappaETStats.Server.csproj server/PappaETStats.Server/
COPY server/PappaETStats.SkillRating/PappaETStats.SkillRating.csproj server/PappaETStats.SkillRating/
RUN dotnet restore server/PappaETStats.Server/PappaETStats.Server.csproj

COPY server/ server/

RUN dotnet publish server/PappaETStats.Server/PappaETStats.Server.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

# Enable non-invariant globalization and timezone database in Alpine runtime.
RUN apk add --no-cache icu-libs icu-data-full tzdata
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false
ENV TZ=Europe/Helsinki

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

# SQLite database and demo storage live here; mount a volume to persist them.
RUN mkdir -p /app/App_Data && chown -R $APP_UID:$APP_UID /app/App_Data
VOLUME ["/app/App_Data"]

USER $APP_UID

ENTRYPOINT ["dotnet", "PappaETStats.Server.dll"]
