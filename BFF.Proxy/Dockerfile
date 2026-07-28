FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# Defaults to Production. Override with `-e ASPNETCORE_ENVIRONMENT=Development` for local/dev
# use (relaxed config via appsettings.Development.json) - the app logs a startup warning when
# running in a non-Production environment since it is not a secure configuration.
ENV ASPNETCORE_ENVIRONMENT=Production

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["BFF.Proxy.csproj", "./"]
RUN dotnet restore "BFF.Proxy.csproj"
COPY . .
WORKDIR "/src/"
RUN dotnet build "./BFF.Proxy.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./BFF.Proxy.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "BFF.Proxy.dll"]
