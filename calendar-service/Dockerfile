FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /App

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a Debug build (so PDBs are emitted unoptimized for VS attach-to-process debugging)
RUN dotnet publish -c Debug -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /App
ENV DOTNET_ENVIRONMENT=Development
ENV ASPNETCORE_ENVIRONMENT=Development
COPY --from=build ./App/out .

# ASP.NET Core 8 listens on 8080 by default in the aspnet:8.0 image.
EXPOSE 8080

ENTRYPOINT ["dotnet", "calendar-service.dll"]