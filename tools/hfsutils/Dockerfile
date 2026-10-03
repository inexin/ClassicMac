# hfsutils and hfsprogs (fsck.hfs; Debian dropped hfsprogs, Ubuntu universe keeps it) for HfsUtilsInteropTests: build with
#   docker build -t classicmac-hfsutils tools/hfsutils
# and run the tests with CLASSICMAC_HFSUTILS=docker:classicmac-hfsutils. Nothing from these GPL tools is copied into
# ClassicMac; they only read and write test images.
FROM ubuntu:22.04
RUN apt-get update && apt-get install -y --no-install-recommends hfsutils hfsprogs && rm -rf /var/lib/apt/lists/*
