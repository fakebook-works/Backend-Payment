FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY fakebookPayment/fakebookPayment.csproj fakebookPayment/
RUN dotnet restore fakebookPayment/fakebookPayment.csproj
COPY fakebookPayment/ fakebookPayment/
RUN dotnet publish fakebookPayment/fakebookPayment.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:5016
EXPOSE 5016
USER app
ENTRYPOINT ["dotnet", "fakebookPayment.dll"]

