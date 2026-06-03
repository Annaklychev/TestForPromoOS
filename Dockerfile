FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY global.json ./
COPY TestForPromoOS/TestForPromoOS.csproj TestForPromoOS/
RUN dotnet restore TestForPromoOS/TestForPromoOS.csproj

COPY TestForPromoOS/ TestForPromoOS/
RUN dotnet publish TestForPromoOS/TestForPromoOS.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "TestForPromoOS.dll"]
