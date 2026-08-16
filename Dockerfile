FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ChatServer/ChatServer.csproj ChatServer/
RUN dotnet restore ChatServer/ChatServer.csproj
COPY ChatServer/ ChatServer/
RUN dotnet publish ChatServer/ChatServer.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "ChatServer.dll"]
