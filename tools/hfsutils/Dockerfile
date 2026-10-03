# hfsutils and hfsprogs (fsck.hfs) for HfsUtilsInteropTests: build with
#   docker build -t classicmac-hfsutils tools/hfsutils
# and run the tests with CLASSICMAC_HFSUTILS=docker:classicmac-hfsutils. Nothing from these GPL tools is copied into
# ClassicMac; they only read and write test images.
FROM debian:bookworm-slim
RUN apt-get update && apt-get install -y --no-install-recommends hfsutils hfsprogs && rm -rf /var/lib/apt/lists/*
