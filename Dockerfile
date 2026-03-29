FROM ubuntu:24.04
ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
        python3 python3-pip python3-venv \
        curl ca-certificates tar bash \
        git build-essential wget \
        sudo apt-transport-https gnupg \
        unzip zip \
        procps && \
    rm -rf /var/lib/apt/lists/*
WORKDIR /workspace
