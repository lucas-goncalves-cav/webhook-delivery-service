# Builds both the API and the worker from one context. The target is selected
# with --build-arg PROJECT=... so the two images share every cached layer up to
# the publish step.
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG PROJECT=src/Api/WebhookDelivery.Api.csproj
WORKDIR /source

COPY *.sln ./
COPY src/Domain/*.csproj src/Domain/
COPY src/Application/*.csproj src/Application/
COPY src/Infrastructure/*.csproj src/Infrastructure/
COPY src/Api/*.csproj src/Api/
COPY src/Worker/*.csproj src/Worker/
COPY tests/UnitTests/*.csproj tests/UnitTests/
COPY tests/IntegrationTests/*.csproj tests/IntegrationTests/
RUN dotnet restore

COPY . .
RUN dotnet publish $PROJECT -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
ARG ENTRYPOINT_DLL=WebhookDelivery.Api.dll
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ENTRYPOINT_DLL=$ENTRYPOINT_DLL
WORKDIR /app

EXPOSE 8080

COPY --from=build /app ./

# The dll name is a build argument, so one Dockerfile serves both services.
ENTRYPOINT ["sh", "-c", "exec dotnet $ENTRYPOINT_DLL"]
