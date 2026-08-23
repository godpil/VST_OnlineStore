# Architektur- und Ablaufdiagramme

Die editierbare diagrams.net-Datei `online-store-architecture.drawio` enthält
fünf Seiten:

1. **Systemkontext** – Systemgrenze, Kunde, Entwicklung/Betrieb,
   Zahlungsanbieter-Stubs und externe Infrastruktur einschließlich der
   verwendeten Schnittstellen.
2. **Komponenten** – StoreProxy, ShopService, fachliche Services, StoreBackend,
   RabbitMQ-Kanäle, PostgreSQL, E-Mail-Outbox und OpenTelemetry.
3. **Happy Path** – erfolgreicher Bestell-, Zahlungs- und Rechnungsablauf.
4. **fail1** – unzureichender Bestand; die Zahlung wird nicht gestartet.
5. **fail2** – Zahlungsablehnung; die Bestandsreservierung wird kompensiert.

Die DrawIO-Datei ist die verbindliche und editierbare Quelle. Alle Seiten lassen
sich gemeinsam mit diagrams.net öffnen, anzeigen und bearbeiten.

## Optionale SVG-Exporte

Die folgenden SVG-Dateien sind abgeleitete Ansichten für Browser, Markdown und
Präsentationen. Nach Änderungen an der DrawIO-Quelle müssen sie neu exportiert
werden.

- [Systemkontext](system-context.svg)
- [Komponentendiagramm](component-diagram.svg)
- [Sequenzdiagramm: Happy Path](sequence-happy-path.svg)
- [Sequenzdiagramm: fail1 – unzureichender Bestand](sequence-fail1.svg)
- [Sequenzdiagramm: fail2 – Zahlungsablehnung](sequence-fail2.svg)

## Quellen

- `online-store-architecture.drawio`: aktueller Dokumentationssatz mit allen
  fünf Diagrammseiten

Die ältere Datei `HappyPath.drawio` bleibt als historischer Entwurf unverändert,
ist aber nicht Bestandteil des aktuellen Dokumentationssatzes.
