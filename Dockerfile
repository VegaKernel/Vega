FROM --platform=${BUILDPLATFORM} mcr.microsoft.com/dotnet/sdk:11.0.100-preview.7 AS builder
RUN apt-get update && apt-get install -y gettext
WORKDIR /Vega
COPY . ./
RUN dotnet build Vega.sln -v m
WORKDIR /Vega/src/Vega.Runtime
ARG TARGETPLATFORM
RUN case "${TARGETPLATFORM}" in \
    "linux/amd64") export ARCH="linux-x64" ;; \
    "linux/arm64") export ARCH="linux-arm64" ;; \
    "linux/arm/v7") export ARCH="linux-arm" ;; \
    "windows/amd64") export ARCH="win-x64" ;; \
    *) echo "Error: Unsupported platform ${TARGETPLATFORM}" && exit 1 ;; \
  esac && dotnet publish -o output/ -r "${ARCH}" -v m -f net11.0 -c Release -p:PublishSingleFile=true --self-contained false

FROM mcr.microsoft.com/dotnet/runtime:11.0.0-preview.7 AS linux_base
FROM mcr.microsoft.com/dotnet/runtime:11.0.0-preview.7-nanoserver-ltsc2025 AS windows_base
FROM ${TARGETOS}_base AS final
WORKDIR /server
COPY --from=builder /Vega/src/Vega.Runtime/output ./
VOLUME ["/tshock", "/worlds", "/plugins"]
EXPOSE 7777 7878
ENTRYPOINT ["./Vega.Server", "-configpath", "/tshock", "-logpath", "/tshock/logs", "-crashdir", "/tshock/crashes", "-worldselectpath", "/worlds", "-additionalplugins", "/plugins"]
