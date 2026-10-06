# Build from the repository root: docker build -f samples/Billing/Dockerfile .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
# MinVer reads git tags; there is no .git in the image, and the sample needs no version.
RUN dotnet publish samples/Billing -c Release -o /app -p:MinVerSkip=true

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
# Npgsql probes for Kerberos (GSSAPI) at startup and logs an error when the library is missing.
RUN apk add --no-cache krb5-libs
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=5s --timeout=3s --retries=12 CMD wget -qO- http://localhost:8080/health >/dev/null || exit 1
ENTRYPOINT ["dotnet", "Billing.dll"]
