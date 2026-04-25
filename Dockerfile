# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o out

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

COPY --from=build /app/out .

# 🔥 Quan trọng cho Render
ENV ASPNETCORE_URLS=http://+:8080

ENTRYPOINT ["dotnet", "FamilyGuardian.Api.dll"]