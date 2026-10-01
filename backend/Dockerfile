FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY TreeEditor.Api/TreeEditor.Api.csproj TreeEditor.Api/
RUN dotnet restore TreeEditor.Api/TreeEditor.Api.csproj
COPY TreeEditor.Api/ TreeEditor.Api/
RUN dotnet publish TreeEditor.Api/TreeEditor.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "TreeEditor.Api.dll"]
