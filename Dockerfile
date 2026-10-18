# Stage 1: Build & Publish the Workbench application
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source

# Copy project files and restore dependencies
COPY src/Workbench/Workbench.csproj src/Workbench/
RUN dotnet restore src/Workbench/Workbench.csproj

# Copy the full application source and publish in Release mode
COPY src/Workbench/ src/Workbench/
WORKDIR /source/src/Workbench
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 2: Final runtime container
# IMPORTANT: The final runtime uses the full .NET 10 SDK image (not just ASP.NET runtime)
# because the ValidationRunner invokes 'dotnet build' and 'dotnet run' on generated candidates at runtime.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS final
WORKDIR /app

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 \
    ASPNETCORE_URLS=http://+:8080 \
    PORT=8080 \
    PUBLIC_DEMO=true \
    DATA_ROOT=/app/data

COPY --from=build /app/publish .

# Pre-create data directory for SQLite database and candidate runs
RUN mkdir -p /app/data

EXPOSE 8080

ENTRYPOINT ["dotnet", "Workbench.dll"]
