# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/backend/ ./

RUN dotnet restore FrostAura.Foresight.Api/FrostAura.Foresight.Api.csproj
RUN dotnet publish FrostAura.Foresight.Api/FrostAura.Foresight.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish ./

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "FrostAura.Foresight.Api.dll"]
