# Build from the repository root:
#   docker build -t learningportal .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY LearningPortal.slnx ./
COPY src/LearningPortal.Core/LearningPortal.Core.csproj src/LearningPortal.Core/
COPY src/LearningPortal.Web/LearningPortal.Web.csproj src/LearningPortal.Web/
RUN dotnet restore src/LearningPortal.Web/LearningPortal.Web.csproj

COPY src/ src/
RUN dotnet publish src/LearningPortal.Web/LearningPortal.Web.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .

# Everything persistent (database, uploaded files, Data Protection keys) goes to /data,
# which docker-compose mounts as a volume. Nothing is written inside the image.
ENV ASPNETCORE_URLS=http://+:8080 \
    Storage__DataDirectory=/data
RUN mkdir -p /data && chown -R $APP_UID /data
VOLUME /data
USER $APP_UID
EXPOSE 8080

ENTRYPOINT ["dotnet", "LearningPortal.Web.dll"]
