FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/GoHome.Web/GoHome.Web.csproj src/GoHome.Web/
RUN dotnet restore src/GoHome.Web/GoHome.Web.csproj
COPY src/ src/
RUN dotnet publish src/GoHome.Web/GoHome.Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 \
    ConnectionStrings__Default="Data Source=/data/gohome.db" \
    DataProtection__KeysPath=/data/keys \
    Storage__Path=/data/uploads \
    TZ=Asia/Bishkek
VOLUME /data
EXPOSE 8080
ENTRYPOINT ["dotnet", "GoHome.Web.dll"]
