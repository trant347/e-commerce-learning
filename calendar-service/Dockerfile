FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /App

# Copy everything
COPY . ./
# Restore as distinct layers
RUN dotnet restore
# Build and publish a release
RUN dotnet publish -c Release -o out

# Build runtime image
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
WORKDIR /App
COPY --from=build ./App/out .

# Expose the port your application listens on (e.g., 80 or 5000).
EXPOSE 80

ENTRYPOINT ["dotnet", "calendar-service.dll"]