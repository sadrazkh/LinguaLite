FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY LinguaLite.csproj ./
RUN dotnet restore ./LinguaLite.csproj

COPY . ./
RUN dotnet publish ./LinguaLite.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# pg_dump and pg_restore keep PostgreSQL backups portable and restorable from the admin panel.
RUN apt-get update && apt-get install -y --no-install-recommends postgresql-client && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://+:80
ENV OPENROUTER_MODEL=google/gemma-4-31b-it:free

COPY --from=build /app/publish ./

EXPOSE 80
ENTRYPOINT ["dotnet", "LinguaLite.dll"]
