FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /app
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/PayNex.Cloud.Api/PayNex.Cloud.Api.csproj src/PayNex.Cloud.Api/
RUN dotnet restore src/PayNex.Cloud.Api/PayNex.Cloud.Api.csproj
COPY . .
RUN dotnet publish src/PayNex.Cloud.Api/PayNex.Cloud.Api.csproj -c Release -o /out

FROM base AS final
WORKDIR /app
COPY --from=build /out .
ENTRYPOINT ["dotnet", "PayNex.Cloud.Api.dll"]
