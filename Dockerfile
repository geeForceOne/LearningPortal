# Build from the repository root:
#   docker build -t learningportal .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY LearningPortal.slnx ./
COPY src/LearningPortal.Core/LearningPortal.Core.csproj src/LearningPortal.Core/
COPY src/LearningPortal.Web/LearningPortal.Web.csproj src/LearningPortal.Web/
RUN dotnet restore src/LearningPortal.Web/LearningPortal.Web.csproj

COPY src/ src/
# Built into the app for its release notes page.
COPY CHANGELOG.md ./
# The version shown in the app; the publish workflow passes the release tag's number.
ARG VERSION=dev
RUN dotnet publish src/LearningPortal.Web/LearningPortal.Web.csproj -c Release -o /app -p:InformationalVersion=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Everything persistent (database, uploaded files, Data Protection keys) goes to /data,
# which docker-compose mounts as a volume. Nothing is written inside the image.
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__DataDirectory=/data
VOLUME /data
# Runs as root, like MeuralManager, so a host folder or an existing volume mounted at /data works
# without first changing its owner. (The base image's non-root "app" user couldn't write to
# root-owned mounts and the app failed at startup.)
EXPOSE 8080

ENTRYPOINT ["dotnet", "LearningPortal.Web.dll"]
