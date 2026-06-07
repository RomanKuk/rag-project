FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY src/DocumentQA.Domain/DocumentQA.Domain.csproj src/DocumentQA.Domain/
COPY src/DocumentQA.Application/DocumentQA.Application.csproj src/DocumentQA.Application/
COPY src/DocumentQA.Infrastructure/DocumentQA.Infrastructure.csproj src/DocumentQA.Infrastructure/
COPY src/DocumentQA.Api/DocumentQA.Api.csproj src/DocumentQA.Api/
RUN dotnet restore src/DocumentQA.Api/DocumentQA.Api.csproj
COPY src/ src/
RUN dotnet publish src/DocumentQA.Api/DocumentQA.Api.csproj -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "DocumentQA.Api.dll"]
