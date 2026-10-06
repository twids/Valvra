# Build context must be the isolated demo publish directory, never the repository
# or a production installation. See docs/DEMO.md.
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --chown=app:app . .
USER app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Valvra.Demo.dll"]
