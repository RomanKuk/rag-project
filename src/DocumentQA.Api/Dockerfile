FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/DocumentQA.Core/DocumentQA.Core.csproj src/DocumentQA.Core/
COPY src/DocumentQA.Ingestion/DocumentQA.Ingestion.csproj src/DocumentQA.Ingestion/
COPY src/DocumentQA.Api/DocumentQA.Api.csproj src/DocumentQA.Api/
RUN dotnet restore src/DocumentQA.Api/DocumentQA.Api.csproj
COPY src/ src/
RUN dotnet publish src/DocumentQA.Api/DocumentQA.Api.csproj -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DocumentQA.Api.dll"]
