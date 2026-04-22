# Unter https://aka.ms/customizecontainer erfahren Sie, wie Sie Ihren Debugcontainer anpassen und wie Visual Studio dieses Dockerfile verwendet, um Ihre Images für ein schnelleres Debuggen zu erstellen.

# Abhängig vom Betriebssystem der Hostcomputer, die die Container erstellen oder ausführen, muss das in der FROM-Anweisung angegebene Image möglicherweise geändert werden.
# Weitere Informationen finden Sie unter https://aka.ms/egmqtttroubleshoot.

# Diese Stufe wird verwendet, wenn sie von VS im Schnellmodus ausgeführt wird (Standardeinstellung für Debugkonfiguration).
FROM mcr.microsoft.com/dotnet/runtime:10.0-nanoserver-ltsc2022 AS base
WORKDIR /app


# Diese Stufe wird zum Erstellen des Dienstprojekts verwendet.
FROM mcr.microsoft.com/dotnet/sdk:10.0-windowsservercore-ltsc2022 AS build
# Installieren Sie Visual Studio Build Tools, die für die Veröffentlichung erforderlich sind.
# Hinweis: Die Verwendung des Visual Studio Build Tools erfordert eine gültige Visual Studio-Lizenz.
RUN curl -SL --output vs_buildtools.exe https://aka.ms/vs/17/release/vs_buildtools.exe
RUN vs_buildtools.exe --installPath C:\BuildTools --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 Microsoft.VisualStudio.Component.Windows10SDK.19041 --quiet --wait --norestart --nocache
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["Testpprojekt/Testpprojekt.csproj", "Testpprojekt/"]
RUN dotnet restore "./Testpprojekt/Testpprojekt.csproj"
COPY . .
WORKDIR "/src/Testpprojekt"
RUN dotnet build "./Testpprojekt.csproj" -c %BUILD_CONFIGURATION% -o /app/build

# Diese Stufe wird verwendet, um das Dienstprojekt zu veröffentlichen, das in die letzte Phase kopiert werden soll.
FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./Testpprojekt.csproj" -c %BUILD_CONFIGURATION% -o /app/publish /p:UseAppHost=true

# Diese Stufe wird in der Produktion oder bei Ausführung von VS im regulären Modus verwendet (Standard, wenn die Debugkonfiguration nicht verwendet wird).
FROM mcr.microsoft.com/dotnet/runtime:10.0-nanoserver-ltsc2022 AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["Testpprojekt.exe"]