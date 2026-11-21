FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY PawPals.Server/PawPals.Server.csproj PawPals.Server/
RUN dotnet restore PawPals.Server/PawPals.Server.csproj
COPY PawPals.Server/ PawPals.Server/
COPY PawPals.Desktop/CatBehavior.cs PawPals.Desktop/
RUN dotnet publish PawPals.Server/PawPals.Server.csproj -c Release -o /out --no-restore -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_ENVIRONMENT=Production
ENV PORT=10000
ENV PawPals__RequirePostgres=true
USER $APP_UID
EXPOSE 10000
ENTRYPOINT ["dotnet", "PawPals.Server.dll"]
