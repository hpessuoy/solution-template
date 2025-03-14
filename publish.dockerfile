FROM pulumi/pulumi-dotnet:latest AS base

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

COPY ./artifacts/app ./app/

WORKDIR /app