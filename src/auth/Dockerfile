FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS build
WORKDIR /src
COPY AnimeVE.Auth.csproj .
RUN dotnet restore
COPY Program.cs .
RUN dotnet publish -c Release --no-restore -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4
WORKDIR /app
COPY --from=build /out .
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0
USER $APP_UID
ENTRYPOINT ["dotnet", "AnimeVE.Auth.dll"]
